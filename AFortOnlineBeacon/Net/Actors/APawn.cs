namespace AFortOnlineBeacon.Net.Actors;

public class APawn : AActor {
    /// <summary>
    ///     The view rotation the client last sent with a move (the packed "View" parameter of
    ///     ServerMove*). Real UE feeds this into AController::ControlRotation; here it is kept only
    ///     so the server knows which way the player is facing - a dropped item has to land in front
    ///     of them to be reachable, and the pawn's own Rotation is never updated by the move RPCs.
    /// </summary>
    public FRotator? LastClientViewRotation { get; set; }

    /// <summary>
    ///     The exact transform NativeRpcHandlers.ServerCreateBuildingActor last spawned something at
    ///     for this pawn, as "X,Y,Z,Yaw" - a duplicate filter, since the client can send this RPC
    ///     more than once for what is really a single confirm and each send would otherwise become
    ///     its own building actor stacked on the last one.
    ///
    ///     This used to be a 0.3s time debounce, from back when the RPC's parameters could not be
    ///     decoded and "same placement" was unknowable. Now that FCreateBuildingActorData decodes,
    ///     the duplicates can be recognised for what they are - byte-identical transforms - which
    ///     also stops the filter from swallowing the genuinely distinct, genuinely fast placements
    ///     that turbo building produces well inside 0.3s.
    /// </summary>
    public string? LastBuildingPlaceKey { get; set; }

    /// <summary>
    ///     The building actor class path the client's own ServerSetPlayerBuildableClass last named
    ///     (see NativeRpcHandlers - arrives on the pawn's BroadcastRemoteClientInfo, routed here via
    ///     its Owner). Cycling pieces while ALREADY in build mode only ever sends this, never a fresh
    ///     ServerExecuteInventoryItem/EquipInventoryItem - so CurrentWeapon.WeaponData reflects
    ///     whichever piece build mode was FIRST entered with, not the current selection, and
    ///     ServerCreateBuildingActor needs this instead once it's been set at least once.
    /// </summary>
    public string? SelectedBuildingActorClassPath { get; set; }

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

        // A building tool's ghost/pencil preview is driven client-side off OnRep_DefaultMetadata -
        // with no value here the client silently draws no ghost (same shape as jump: a client gate
        // fed by a property nothing ever sent). Null for every other weapon.
        if (FortWeaponActorClasses.BuildingMetadataFor(item.ItemDefinition) is { } buildingMetadata) {
            weapon.DefaultMetadata = buildingMetadata;
            Console.WriteLine($"APawn.EquipInventoryItem: set DefaultMetadata={buildingMetadata.GetFName()} for building tool");
        }

        // AFortPawn::ClientInternalEquipWeapon(AFortWeapon*) - originally scoped to building tools
        // only (2026-08-29), on the reasoning that ordinary weapons already worked via
        // CurrentWeapon's RepNotify alone. Widened to every weapon the same day: leaving a building
        // tool for the pickaxe/a gun left the client stuck showing the ghost AND the build-mode arm
        // pose forever, meaning the RPC (or something it triggers) is also what tells the client
        // this equip REPLACES a previous one, and skipping it for a normal weapon left the client
        // with no signal to tear down the OLD equip's state, only to raise the new one. See
        // UNetDriver.OpenChannelsForNewlyRelevantActors for why the actual send is deferred to that
        // weapon's own channel opening, not here (sending immediately here fails: the weapon has no
        // NetGUID resolvable client-side yet, and the client logs "Unable to resolve RPC parameter
        // ... Parameter Weap" and silently drops the call).
        weapon.bNeedsClientInternalEquipWeaponRpc = true;
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

    private byte? _lastMovementMode;
    private bool _reportedJumpPress;
    private byte _seenMoveFlags;

    /// <summary>
    ///     Reports what every client move says about jumping, because nothing else can.
    ///
    ///     A jump in UE is CLIENT-FIRST: the client runs its own CanJump()/DoJump() and only then
    ///     tells the server, by setting FLAG_JumpPressed in the move's compressed flags
    ///     (FSavedMove_Character::GetCompressedFlags). So this one bit separates the two possible
    ///     worlds cleanly, with no console and no guessing:
    ///
    ///       bit never set  -> the CLIENT refused to jump. Its own CanJumpInternal said no, and the
    ///                         cause is a pawn state we are getting wrong (or failing to send).
    ///       bit set        -> the client DID jump and the server is somehow undoing it - which
    ///                         would point at the position this server writes back from ClientLoc.
    ///
    ///     ClientMovementMode is logged on every change for the same reason: CanAttemptJump requires
    ///     IsMovingOnGround(), so a client that thinks it is anything but Walking cannot jump no
    ///     matter what else is right. EMovementMode: 1 Walking, 2 NavWalking, 3 Falling, 4 Swimming,
    ///     5 Flying, 6 Custom.
    /// </summary>
    public void TrackMoveFlags(byte compressedMoveFlags, byte clientMovementMode) {
        // FSavedMove_Character::CompressedFlags. Only the first two are standard input; the Custom
        // ones are whatever the game's own movement component defines (Fortnite uses them for
        // sprint and the like), and they are worth seeing precisely because we do not know which.
        var names = new[] {
            "JumpPressed", "WantsToCrouch", "Reserved_1", "Reserved_2",
            "Custom_0", "Custom_1", "Custom_2", "Custom_3"
        };

        // Report each distinct bit ONCE. The point is not the traffic, it is the answer to a single
        // question: which inputs reach this server at all? A jump that never sets bit 0 while crouch
        // sets bit 1 says the input system is fine and jump specifically is refused client-side;
        // neither bit ever appearing says the moves carry no input flags at all, which is a
        // different problem entirely.
        for (var bit = 0; bit < 8; bit++) {
            var mask = (byte) (1 << bit);
            if ((compressedMoveFlags & mask) == 0 || (_seenMoveFlags & mask) != 0) continue;

            _seenMoveFlags |= mask;
            Console.WriteLine($"APawn.TrackMoveFlags: client move flag {names[bit]} (0x{mask:X2}) seen for the first " +
                              $"time - full flags=0x{compressedMoveFlags:X2}, movementMode={clientMovementMode}");
        }

        if ((compressedMoveFlags & 0x01) != 0 && !_reportedJumpPress) {
            _reportedJumpPress = true;
            Console.WriteLine("APawn.TrackMoveFlags: the client PRESSED JUMP - so ACharacter::Jump() ran and anything " +
                              "still wrong is on this side, not in the client's own CanJump().");
        }

        if (_lastMovementMode == clientMovementMode) return;

        Console.WriteLine($"APawn.TrackMoveFlags: ClientMovementMode {_lastMovementMode?.ToString() ?? "(none)"} -> " +
                          $"{clientMovementMode} (1=Walking 2=NavWalking 3=Falling 4=Swimming 5=Flying 6=Custom)");

        _lastMovementMode = clientMovementMode;
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
