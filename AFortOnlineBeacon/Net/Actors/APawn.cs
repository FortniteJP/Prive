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
    ///     AFortPawn::LastReplicatedEmoteExecuted - wire handle 70, an ObjectRef to the emote's own
    ///     UFortMontageItemDefinitionBase asset (an EID_/SPID_/TOY_ item definition), and RepNotify.
    ///
    ///     This is the ONLY part of an emote that anybody other than the emoting player ever sees:
    ///     the ability that actually plays the montage is granted on the PlayerState's channel,
    ///     which is owner-only, and confirmed with a client RPC to one connection. See
    ///     <see cref="FortEmoteSystem"/> for the whole flow and NativeRepLayouts for the handle.
    ///
    ///     Null means "not emoting". It is set back to null when the emote ends so that playing the
    ///     same emote twice in a row is still two changes, and therefore two OnReps.
    /// </summary>
    public UObject? LastReplicatedEmoteExecuted { get; set; }

    /// <summary>
    ///     The FGameplayAbilitySpec handle of the emote ability currently granted to this pawn's
    ///     player, or 0 for none. Server-side bookkeeping only - it never reaches the wire itself,
    ///     it is what lets an incoming ServerCancelAbility/ServerEndAbility be recognised as "the
    ///     emote stopped" rather than as some other ability ending. See <see cref="FortEmoteSystem"/>.
    /// </summary>
    public int ActiveEmoteAbilityHandle { get; set; }

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

        // NOT while descending. This line exists to catch a walking speed that disagrees with the
        // movement attributes, and comparing a skydive against RunSpeed reads like a speed-hack
        // warning when it is just gravity - the descent legitimately runs at thousands of uu/s
        // (Default.SkydivingControlActive.TerminalVelocity is 6000).
        if (!bIsSkydiving && !bIsParachuteOpen) {
            Console.WriteLine($"APawn.TrackMovementSpeed: {_trackedDistance / elapsed:F1} uu/s over {elapsed:F1}s " +
                              $"(Fortnite RunSpeed is 410)");
        }

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
    /// <summary>
    ///     ACharacter::ReplicatedMovementMode - wire handle 29, and what a remote player's ANIMATION
    ///     runs off. `OnRep_ReplicatedMovementMode` calls
    ///     UCharacterMovementComponent::ApplyNetworkMovementMode, which is how a simulated proxy
    ///     learns it is walking, falling, or - and this is the one that matters here - in one of
    ///     Fortnite's CUSTOM modes. Skydiving and gliding are custom modes, which is why a remote
    ///     player fell to earth standing bolt upright: with this at its default the proxy believed it
    ///     was walking the whole way down.
    ///
    ///     Relayed verbatim from the owning client rather than derived. ServerMoveNoBase's last
    ///     parameter IS `PackNetworkMovementMode()`'s output, computed by the one machine that knows
    ///     what mode the character is in - so there is nothing to work out and nothing to get wrong,
    ///     including whatever Fortnite packs into the custom bits.
    /// </summary>
    public byte ReplicatedMovementMode { get; set; }

    /// <summary>
    ///     ACharacter::bIsCrouched - wire handle 30. `OnRep_IsCrouched` calls Crouch()/UnCrouch() on
    ///     the proxy, which plays the animation AND resizes the capsule.
    ///
    ///     The capsule is why the symptom was "standing, and sunk into the ground": the replicated
    ///     LOCATION is the crouched actor's, which sits lower, but a proxy still at full height puts
    ///     its feet that much below the floor. Two bugs looking like one.
    ///
    ///     From FSavedMove_Character::GetCompressedFlags bit 1, FLAG_WantsToCrouch - the same byte
    ///     TrackMoveFlags already reads.
    /// </summary>
    public bool bIsCrouched { get; set; }

    /// <summary>
    ///     AFortPlayerPawn::bIsSkydiving - wire handle 103, with an OnRep (OnRep_IsSkydiving). The
    ///     packed movement mode alone is not enough: Fortnite's animation blueprint reads these
    ///     dedicated flags, and each of the three below has its own OnRep, which is what makes them
    ///     the ones the client is actually driven by.
    /// </summary>
    public bool bIsSkydiving { get; set; }

    /// <summary>AFortPlayerPawn::bIsParachuteOpen - handle 104, OnRep_IsParachuteOpen. The glider.</summary>
    public bool bIsParachuteOpen { get; set; }

    /// <summary>
    ///     AFortPlayerPawn::bIsSkydivingFromBus - handle 106, OnRep_IsSkydivingFromBus. Not derivable
    ///     from the movement mode: skydiving from the battle bus and skydiving off a launch pad are
    ///     the same custom mode. The SERVER is the only one that knows which, so it is set in
    ///     NativeRpcHandlers.LeaveAircraft and cleared here when the descent ends.
    /// </summary>
    public bool bIsSkydivingFromBus { get; set; }

    public void TrackMoveFlags(byte compressedMoveFlags, byte clientMovementMode) {
        // The two the OTHER clients need, taken straight from the owning client's own move.
        ReplicatedMovementMode = clientMovementMode;
        bIsCrouched = (compressedMoveFlags & 0x02) != 0;

        // EFortCustomMovement rides INSIDE the packed mode, and the packing is exact rather than
        // guessed - UCharacterMovementComponent::PackNetworkMovementMode (CharacterMovementComponent
        // .cpp:1148) is `CustomMovementMode + CustomModeThr` for a custom mode, and CustomModeThr is
        // `2 << CeilLogTwo(MOVE_MAX)` = 16. So Parachuting (EFortCustomMovement 3) arrives as 19 and
        // Skydiving (4) as 20; anything below 16 is a plain EMovementMode with a ground-mode bit.
        const int customModeThreshold = 16;
        var customMode = clientMovementMode >= customModeThreshold
            ? clientMovementMode - customModeThreshold
            : -1;

        bIsSkydiving = customMode == 4;      // EFortCustomMovement::Skydiving
        bIsParachuteOpen = customMode == 3;  // EFortCustomMovement::Parachuting

        // Back on the ground - whatever started the descent, it is over.
        if (!bIsSkydiving && !bIsParachuteOpen) bIsSkydivingFromBus = false;

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

    private static float Env(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    /// <summary>
    ///     How far a player may fall for free. 1152 uu is three Fortnite storeys (a wall is 384),
    ///     which is roughly where the real game starts hurting. FALL_DAMAGE_MIN_DISTANCE overrides.
    /// </summary>
    private static readonly float FallDamageMinDistance = Env("FALL_DAMAGE_MIN_DISTANCE", 1152.0f);

    /// <summary>Damage per storey fallen beyond the free distance. FALL_DAMAGE_PER_STOREY overrides.</summary>
    private static readonly float FallDamagePerStorey = Env("FALL_DAMAGE_PER_STOREY", 10.0f);

    /// <summary>One Fortnite storey, the unit both constants above are expressed in.</summary>
    private const float StoreyHeight = 384.0f;

    /// <summary>True once this pawn has been seen on the ground at least once - see TrackFallDamage.</summary>
    private bool _hasBeenGrounded;

    /// <summary>The highest Z reached during the fall currently in progress; null when not falling.</summary>
    private float? _fallPeakZ;

    /// <summary>
    ///     Fall damage, worked out from the movement mode and location every client move carries.
    ///
    ///     This is the FIRST damage source this server has that needs no second player, which is the
    ///     whole reason it exists: a solo session cannot shoot anyone, so without it the health and
    ///     death paths could not be exercised against a real client at all.
    ///
    ///     UE computes fall damage from impact VELOCITY, in ACharacter::Landed. Velocity is not
    ///     replicated to a server that runs no physics of its own, so the height dropped stands in
    ///     for it - the two agree closely enough under gravity, and the numbers are placeholders
    ///     regardless (the real curve is in a DataTable, like every other damage value here).
    ///
    ///     The first fall of a session is DELIBERATELY exempt. A player spawns in the air and drops
    ///     onto the terrain, and a drop of unknown height is exactly what this would otherwise
    ///     charge for - the client would take damage, or die outright, before it had control.
    ///     Nothing is tracked until the pawn has been seen standing on something once.
    /// </summary>
    public void TrackFallDamage(FVector location, byte clientMovementMode) {
        const byte falling = 3;

        if (clientMovementMode == falling) {
            // Only the peak matters: a fall that goes up first (a jump, a bounce) is measured from
            // the top, the same way UE's own impact velocity would be.
            if (_hasBeenGrounded) _fallPeakZ = MathF.Max(_fallPeakZ ?? location.Z, location.Z);
            return;
        }

        _hasBeenGrounded = true;

        if (_fallPeakZ is not { } peak) return;
        _fallPeakZ = null;

        var dropped = peak - location.Z;
        if (dropped <= FallDamageMinDistance) return;

        var damage = (dropped - FallDamageMinDistance) / StoreyHeight * FallDamagePerStorey;

        Console.WriteLine($"APawn.TrackFallDamage: {GetFName()} fell {dropped:F0}uu " +
                          $"({dropped / StoreyHeight:F1} storeys) - {damage:F0} damage");

        FortDamageSystem.ApplyDamage(PlayerState, damage, EDeathCause.FallDamage);
    }

    /// <summary>
    ///     AFortPawn::bIsDying - wire handle 47, and the whole of what this server tells a client
    ///     about a dead pawn today. A plain replicated bool, so there is no width risk in sending
    ///     it; the client's own death handling runs off it.
    ///
    ///     Set by FortDamageSystem.Kill and never cleared - this project has no respawn, and Athena
    ///     would not respawn a pawn anyway (a dead BR player becomes a spectator).
    /// </summary>
    public bool bIsDying { get; set; }

    public void SetController(AController? controller) => Controller = controller;

    /// <summary>
    ///     The glider asset, built lazily rather than in a field initializer - the same static
    ///     ordering trap that killed FortFloorLoot's first world tick.
    /// </summary>
    private static UObject? _defaultGlider;

    public static UObject DefaultGlider => _defaultGlider ??=
        UAssetRegistry.GetOrCreate("/Game/Athena/Items/Cosmetics/Gliders/DefaultGlider.DefaultGlider");

    /// <summary>
    ///     AFortPlayerPawn::CosmeticLoadout.Glider - wire handle 145, and the reason the client used
    ///     to crash about a second into a skydive.
    ///
    ///     The pawn resolves "which glider am I using" as GliderOverrideStack.Last() ->
    ///     GliderClass (+0x22C8) -> CosmeticLoadout.Glider (+0x18E8), and the last step is
    ///     dereferenced WITHOUT a null check - the fault was `mov rax,[rcx]` at 0x141962AC7. This
    ///     server set none of the three, so a player who jumped had no glider to open.
    ///
    ///     A real server sends /Game/Athena/Items/Cosmetics/Gliders/DefaultGlider here; the PR3.0
    ///     capture registers it as NetGUID 979 in the pawn's very first property burst, alongside
    ///     DefaultPickaxe and CID_001_Athena_Commando_F_Default. Only the glider is sent here,
    ///     because only the glider is dereferenced blind.
    /// </summary>
    public UObject? CosmeticGlider { get; set; } = DefaultGlider;

    /// <summary>
    ///     AFortPlayerPawn::bIsInAnyStorm - wire handle 126, and THE ONE THAT DRIVES THE SCREEN
    ///     EFFECT. Sending only bIsInsideSafeZone changed nothing on screen, and this is why:
    ///
    ///     Both bools live in the same byte at 0x126C - bIsInAnyStorm is bit 0, bIsInsideSafeZone is
    ///     bit 1 - but only bIsInAnyStorm has an OnRep. `AFortPlayerPawn::OnRep_IsInAnyStorm`
    ///     (0x141972BF0 in the dump, reached from the name table via dumpwork/findfn.py) reads
    ///     `byte [this+0x126C] & 1`, folds the answer into a bit of 0x1131, and tail-calls
    ///     `vtable[0xEF0]`. The post-process it ends up driving is right there in the class next to
    ///     the flags: OutsideSafeZoneBlendSpeed (0x1278), CurrentOutsideSafeZonePPVBlend (0x127C),
    ///     TargetOutsideSafeZonePPVBlend (0x1280), OutsideSafeZonePPComponent (0x1288). A property
    ///     with no OnRep can be perfectly replicated and still light nothing up.
    /// </summary>
    public bool bIsInAnyStorm { get; set; }

    /// <summary>
    ///     AFortPlayerPawn::bIsInsideSafeZone - wire handle 127, the inverse of
    ///     <see cref="bIsInAnyStorm"/>. Sent as well because it is what the rest of the client's
    ///     safe-zone logic reads, but on its own it is invisible - see above.
    ///
    ///     True by default so a pawn that exists before the storm does is not born in the storm:
    ///     with the storm off nothing ever writes either of these, and the defaults are what get
    ///     sent.
    /// </summary>
    public bool bIsInsideSafeZone { get; set; } = true;

    /// <summary>
    ///     AFortPlayerPawn::bIsNearSafeZoneEdge - wire handle 95. The client's cue that the wall is
    ///     close; set inside <see cref="Net.FortSafeZoneSystem"/> along with bIsInsideSafeZone.
    /// </summary>
    public bool bIsNearSafeZoneEdge { get; set; }

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
