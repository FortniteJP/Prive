namespace AFortOnlineBeacon.Net.Actors;

public class APawn : AActor {
    /// <summary>
    ///     The view rotation the client last sent with a move (the packed "View" parameter of
    ///     ServerMove*). Real UE feeds this into AController::ControlRotation; here it is kept only
    ///     so the server knows which way the player is facing - a dropped item has to land in front
    ///     of them to be reachable, and the pawn's own Rotation is never updated by the move RPCs.
    /// </summary>
    public FRotator? LastClientViewRotation { get; set; }

    /// <summary>APawn::Controller - wire handle 18, an ObjectRef.</summary>
    public AController? Controller { get; private set; }

    /// <summary>
    ///     APawn::PlayerState - wire handle 17, an ObjectRef. Real UE's APawn::PossessedBy copies it
    ///     down from the possessing controller; see AController.Possess.
    /// </summary>
    public APlayerState? PlayerState { get; set; }

    /// <summary>
    ///     AFortPawn::CurrentWeapon - wire handle 66, an ObjectRef. What the pawn is holding, and
    ///     the single property that turns an equip into something the client can see: the weapon
    ///     actor is spawned and replicated on its own channel (see AFortWeapon), and this is the
    ///     reference that attaches it to a pair of hands.
    ///
    ///     Declared here rather than on a separate AFortPawn class for the same reason APawn already
    ///     stands in for the whole ACharacter -> AFortPawn -> ... -> PlayerPawn_Athena_C chain: this
    ///     project has one C# pawn type, and the handle numbering (NativeRepLayouts.PawnProps) is
    ///     what encodes the real hierarchy.
    /// </summary>
    public AFortWeapon? CurrentWeapon { get; set; }

    /// <summary>
    ///     The part of AFortPawn::EquipWeaponDefinition this server can actually do: spawn the item's
    ///     own weapon actor, fill in the properties the client reads off it, and point the pawn at
    ///     it. Everything else that native call does (ability specs, animation state, the
    ///     server-side firing model) has no counterpart here.
    ///
    ///     No channel is opened by hand. UNetDriver.ServerReplicateActors' relevancy pass gives the
    ///     weapon a channel on the next tick, and it runs BEFORE the per-channel update walk that
    ///     carries the pawn's CurrentWeapon handle - so the reference is never sent ahead of the
    ///     actor it points at. That ordering is the reason this needs no ClientInternalEquipWeapon /
    ///     ClientGivenTo RPC to be visible: CurrentWeapon is RepNotify (verified in the 10.40 SDK -
    ///     AFortPawn::OnRep_CurrentWeapon exists), so arriving IS the equip.
    /// </summary>
    public void EquipInventoryItem(FFortItemEntry item) {
        // Already holding it. The client re-sends this for the slot it is already on (a key repeat,
        // a quickbar refresh), and re-spawning would destroy and re-create the weapon each time.
        if (CurrentWeapon is { } held && held.ItemEntryGuid == item.ItemGuid) {
            Console.WriteLine($"APawn.EquipInventoryItem: already holding ItemGuid={item.ItemGuid}, nothing to do");
            return;
        }

        var world = GetWorld();
        if (world == null) return;

        var weaponClass = FortWeaponActorClasses.ClassFor(item.ItemDefinition);
        if (weaponClass == null) {
            Console.WriteLine($"APawn.EquipInventoryItem: no WeaponActorClass known for " +
                              $"'{item.ItemDefinition.GetFName()}' (see Tools/WeaponClasses), not equipping");
            return;
        }

        // Swapping weapons destroys the old actor, exactly like a claimed pickup: the channel close
        // is what removes it from the client, and leaving it alive would leave a second weapon
        // attached to the pawn forever.
        UnequipCurrentWeapon();

        var weapon = world.SpawnActor<AFortWeapon>(weaponClass, new FActorSpawnParameters {
            ObjectFlags = EObjectFlags.RF_Transient,
            Owner = this,
            Instigator = this
        });

        if (weapon == null) return;

        // Set BEFORE SetReplicates, for the same reason AFortPickup's entry is - once the actor is
        // replicating, a tick can read every one of these from another thread of control.
        weapon.WeaponData = item.ItemDefinition;
        weapon.ItemEntryGuid = item.ItemGuid;
        weapon.WeaponLevel = item.Level;
        weapon.AmmoCount = item.LoadedAmmo;
        weapon.SetOwner(this);
        weapon.SetActorLocation(GetActorLocation());
        // Grant the weapon's fire ability. Without a spec in ActivatableAbilities the client has
        // literally nothing to activate: the FGameplayAbilitySpecHandle it sends in
        // ServerTryActivateAbility is an index into that array, handed out by the server.
        //
        // The ability lives on the PLAYER STATE's component, not the pawn's - AFortPlayerPawn's
        // bInitAbilitySystemComponentFromPlayerState says the pawn borrows it - so a grant outlives
        // the pawn.
        if (Controller?.PlayerState?.AbilitySystemComponent is { } abilitySystem
            && FortWeaponActorClasses.FireAbilityFor(item.ItemDefinition) is { } fireAbility) {
            var spec = abilitySystem.GrantAbility(fireAbility, weapon);
            weapon.GrantedAbilitySpecHandle = spec.Handle;
            Console.WriteLine($"APawn.EquipInventoryItem: granted {fireAbility.GetFName()} as spec handle {spec.Handle}");

            // A magazine the player can empty needs a way to refill it. The reload ability is
            // native (UFortGameplayAbility_Reload), so its CDO resolves by path with no asset to
            // stream - and a real server grants it in the same breath as the fire ability, which
            // the Project-Reboot-3.0 capture shows plainly: packet 1922 exports
            // Default__GA_Ranged_GenericDamage_C and Default__FortGameplayAbility_Reload together
            // the moment a rifle is equipped.
            //
            // Ranged only, decided from the weapon's real class chain rather than its name, so a
            // pickaxe can never be handed one.
            if (FortWeaponNetCaches.IsRanged(weapon)) {
                var reloadSpec = abilitySystem.GrantAbility(
                    UAssetRegistry.GetOrCreate("/Script/FortniteGame.Default__FortGameplayAbility_Reload"), weapon);
                weapon.ReloadAbilitySpecHandle = reloadSpec.Handle;
                Console.WriteLine($"APawn.EquipInventoryItem: granted reload as spec handle {reloadSpec.Handle}");
            }
        }

        weapon.SetRole(ENetRole.ROLE_Authority);
        weapon.SetReplicates(true);

        CurrentWeapon = weapon;

        Console.WriteLine($"APawn.EquipInventoryItem: '{item.ItemDefinition.GetFName()}' as " +
                          $"{weaponClass.NativePackagePath} guid={item.ItemGuid} ammo={weapon.AmmoCount} - " +
                          "waiting for ServerReplicateActors to open its channel");
    }

    /// <summary>
    ///     Drops the pawn's current weapon actor. Clearing CurrentWeapon is what the client sees;
    ///     Destroy() is what stops the actor from lingering on every client that had a channel for
    ///     it (see AActor.Destroy / UNetDriver.ServerReplicateActors' close pass).
    /// </summary>
    public void UnequipCurrentWeapon() {
        if (CurrentWeapon is not { } weapon) return;

        // Take the grant back before the weapon goes. UAbilitySystemComponent::ClearAbility's job:
        // a spec left behind is a handle the client can still name in ServerTryActivateAbility for
        // a weapon that no longer exists, and they accumulate one per swap.
        if (Controller?.PlayerState?.AbilitySystemComponent is { } abilitySystem) {
            if (weapon.GrantedAbilitySpecHandle != UnrealConstants.IndexNone) abilitySystem.ClearAbility(weapon.GrantedAbilitySpecHandle);
            if (weapon.ReloadAbilitySpecHandle != UnrealConstants.IndexNone) abilitySystem.ClearAbility(weapon.ReloadAbilitySpecHandle);
        }

        CurrentWeapon = null;
        weapon.Destroy();
    }

    private FVector? _lastTrackedLocation;
    private float _lastTrackedTime;
    private float _trackedDistance;

    /// <summary>
    ///     Measures how fast the pawn is actually moving, from the ClientLoc every ServerMove
    ///     carries, and prints it once a second.
    ///
    ///     Exists because "still slow" is not a number. The client's own LogNetPlayerMovement lines
    ///     are Verbose and turned out not to be present in every session, so the only reliable place
    ///     to measure is here - and the difference between 1 uu/s and 10 uu/s decides whether a
    ///     speed multiplier is doing anything at all. For reference, Fortnite's RunSpeed is 410.
    /// </summary>
    public void TrackMovementSpeed(FVector location, float now) {
        if (_lastTrackedLocation is { } previous) {
            var dx = location.X - previous.X;
            var dy = location.Y - previous.Y;
            var dz = location.Z - previous.Z;
            _trackedDistance += MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        } else {
            _lastTrackedTime = now;
        }

        _lastTrackedLocation = location;

        var elapsed = now - _lastTrackedTime;
        if (elapsed < 1.0f) return;

        Console.WriteLine($"APawn.TrackMovementSpeed: {_trackedDistance / elapsed:F1} uu/s over {elapsed:F1}s " +
                          $"(Fortnite RunSpeed is 410)");

        _trackedDistance = 0.0f;
        _lastTrackedTime = now;
    }

    public void SetController(AController? controller) => Controller = controller;

    /// <summary>
    ///     Stand-in for FNetworkPredictionData_Server_Character::PendingAdjustment.TimeStamp with
    ///     bAckGoodMove set - the newest client move timestamp we have accepted and still owe the
    ///     client an acknowledgement for. 0 means nothing pending.
    ///
    ///     This matters more than it looks: a client's FSavedMove_Character list is only freed when
    ///     the server acknowledges a timestamp, so a server that never acks makes the client pile up
    ///     moves until it hits its cap and logs
    ///     "CreateSavedMove: Hit limit of 96 saved moves (timing out or very bad ping?)", throwing
    ///     the whole list away and restarting - which is what movement looks like from a server that
    ///     receives ServerMove and says nothing back.
    /// </summary>
    public float PendingAckGoodMoveTimeStamp { get; set; }

    /// <summary>
    ///     UCharacterMovementComponent::ServerLastClientGoodMoveAckTime. Real UE only sends one ack
    ///     per replication pass and additionally throttles by NetworkMinTimeBetweenClientAckGoodMoves
    ///     (0.10s by default) - acking every single move would put an unreliable bunch on the wire at
    ///     the client's full move rate for no benefit, since one ack frees every saved move up to its
    ///     timestamp.
    /// </summary>
    public float ServerLastClientGoodMoveAckTime { get; set; } = float.NegativeInfinity;

    /// <summary>Records a client move timestamp as accepted. Newest wins - an ack is cumulative.</summary>
    public void MarkGoodMove(float timeStamp) {
        if (timeStamp > PendingAckGoodMoveTimeStamp) PendingAckGoodMoveTimeStamp = timeStamp;
    }
}
