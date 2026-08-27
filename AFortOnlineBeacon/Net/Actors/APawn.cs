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
        if (CurrentWeapon is { } held && held.ItemEntryGuid == item.ItemGuid) return;

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

        CurrentWeapon = null;
        weapon.Destroy();
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
