using AFortOnlineBeacon.Runtime;
namespace AFortOnlineBeacon.Net.Actors;

public class APawn : AActor {
    /// <summary>
    ///     EXEMPT FROM DISTANCE CULLING, on purpose. AActor.IsNetRelevantFor gained the real
    ///     distance tail, and the engine default radius (15000 units) would cull other players at
    ///     150 m - which is well inside sniper range and, more to the point, saves nothing: a match
    ///     holds tens of pawns, not the thousands of pickups that made culling worth having. Losing a
    ///     player is total, so the trade is entirely one-sided.
    ///
    ///     Real Fortnite reaches the same answer by a different road - its ReplicationGraph puts
    ///     player pawns in an always-relevant-for-team / large-cell node rather than the spatial grid
    ///     the small props live in.
    /// </summary>
    public APawn() => NetCullDistanceSquared = float.MaxValue;

    /// <summary>
    ///     The view rotation the client last sent with a move (the packed "View" parameter of
    ///     ServerMove*). Real UE feeds this into AController::ControlRotation; here it is kept only
    ///     so the server knows which way the player is facing - a dropped item has to land in front
    ///     of them to be reachable, and the pawn's own Rotation is never updated by the move RPCs.
    /// </summary>
    /// <summary>
    ///     AFortPawn::PushMomentum - wire handle 65, an FVector_NetQuantize with OnRep_PushMomentum.
    ///     Zero means "not being pushed"; see FortProjectileSystem's knockback for who sets it and
    ///     why it has to be set back.
    /// </summary>
    public FVector PushMomentum { get; private set; } = new();

    public void SetPushMomentum(FVector momentum) => PushMomentum = momentum;

    public FRotator? LastClientViewRotation { get; set; }

    /// <summary>
    ///     APawn::RemoteViewPitch - wire handle 16, and THE ONLY WAY ANYONE ELSE SEES YOU LOOK UP OR
    ///     DOWN. A pawn's own rotation carries yaw alone (a capsule does not pitch), so an onlooker
    ///     has no other source for where a player is aiming vertically; real UE fills this in
    ///     APawn::Tick from the control rotation and replicates it COND_SkipOwner - the owner already
    ///     knows where it is looking.
    ///
    ///     It was `Reserved` here - declared, correctly numbered, never sent - which is exactly the
    ///     silent-property failure [[rep-handle-derivation]] is about: yaw looked right, so nothing
    ///     seemed wrong, and every other player's head stayed level no matter where they aimed.
    ///
    ///     A uint8 over the full turn: `(uint8)(Pitch * 255.f / 360.f)`, straight from Pawn.cpp. The
    ///     client's inverse is the same scale, so a wrap at 255 -> 0 is a wrap at 360 -> 0 and needs
    ///     no special case.
    /// </summary>
    public byte RemoteViewPitch { get; private set; }

    /// <summary>Records the view pitch a client reported, in the uint8 form the wire wants.</summary>
    public void SetRemoteViewPitch(float pitchDegrees) {
        var wrapped = pitchDegrees % 360f;
        if (wrapped < 0f) wrapped += 360f;

        RemoteViewPitch = (byte) (int) MathF.Round(wrapped * 255f / 360f);
    }

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
    ///     AFortPlayerPawn::RepAnimMontageInfo (0x1F58) - wire handles 176-186, and THE ONLY WAY AN
    ///     ONLOOKER SEES AN EMOTE.
    ///
    ///     Handle 70 above serves the emoting player's own client; a simulated proxy's OnRep does
    ///     nothing with it (measured: on the second client only the LOCAL pawn ever plays an emote
    ///     montage). GAS shows a montage to everyone else through this struct, which a real server
    ///     fills as a side effect of PlayMontage inside the activated ability - see
    ///     NativeRepLayouts' entry for the whole account.
    ///
    ///     The montage itself is the emote definition's `Animation`, baked in FortEmoteAssets.
    /// </summary>
    public UObject? EmoteMontage { get; set; }

    /// <summary>
    ///     Handle 177, and it starts at ZERO because THE CLIENT'S DOES.
    ///
    ///     `FGameplayAbilityRepAnimMontage`'s constructor (GameplayAbilityTypes.h:217-226) is
    ///     `PlayRate(0.f), Position(0.f), BlendTime(0.f), IsStopped(true)`. A server-side default of
    ///     1.0 looks harmless and is not: these members are in NeverSentAtOpen, so the shadow is
    ///     SEEDED with our value without it ever going on the wire, and a value that then never
    ///     changes is never sent at all. The client kept its own 0 and played the montage at rate
    ///     ZERO - which is a character standing perfectly still while every other part of the emote
    ///     arrives, exactly as reported.
    ///
    ///     THE RULE: a property this server declines to send at open must default to what the CLIENT
    ///     defaults to, or the two disagree silently and forever. FortEmoteSystem sets the real
    ///     values when an emote starts, and the diff carries them because they differ from these.
    /// </summary>
    public float EmoteMontagePlayRate { get; set; }

    public float EmoteMontagePosition { get; set; }

    /// <summary>Handle 179. Zero for the same reason as PlayRate - see above.</summary>
    public float EmoteMontageBlendTime { get; set; }

    /// <summary>
    ///     Handle 181, and the reason the same emote twice works here where handle 70 needed a null
    ///     pushed between the two. The engine TOGGLES this on every fresh PlayMontage precisely so a
    ///     repeat is still a change; a value that only ever goes A -> A is invisible to a RepNotify.
    /// </summary>
    public bool EmoteMontageForcePlayBit { get; set; }

    /// <summary>Handle 183 - true once the emote is over, which is how the onlooker stops it.</summary>
    public bool EmoteMontageIsStopped { get; set; } = true;

    /// <summary>
    ///     Handle 184. False by default because the CLIENT'S is, true while an emote runs.
    ///
    ///     It opts the onlooker out of GAS's position correction, which this server cannot feed:
    ///     that block clears the montage's next section (making the emote stop after one loop) and
    ///     drags the animation back to our constant Position of 0. See NativeRepLayouts.
    /// </summary>
    public bool EmoteMontageSkipPositionCorrection { get; set; }

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
    /// <summary>
    ///     <paramref name="clientInitiated"/> records whether the CLIENT asked for this equip
    ///     (ServerExecuteInventoryItem) or the server decided it. Nothing acts on it today: it was
    ///     added to suppress the ClientInternalEquipWeapon echo for client-initiated equips, and that
    ///     removed building altogether - see UActorChannel.ReplicateEquippedWeapon. Kept because the
    ///     distinction is real and worth having recorded at the call sites.
    /// </summary>
    public void EquipInventoryItem(FFortItemEntry item, bool clientInitiated = false) {
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

        // A TRAP's ghost is the tool's ItemDefinition (handle 36), the deco tool's DefaultMetadata -
        // and on the context tool, ContextTrapItemDefinition (38) too, both the context item itself,
        // exactly what Project-Reboot-3.0's equip leaves on it. See AFortDecoTool.
        if (weapon is AFortDecoTool decoTool) {
            decoTool.ItemDefinition = item.ItemDefinition;
            if (FortTraps.For(item.ItemDefinition) is { IsContext: true }) decoTool.ContextTrapItemDefinition = item.ItemDefinition;
            Console.WriteLine($"APawn.EquipInventoryItem: trap tool holding {item.ItemDefinition?.GetFName()}" +
                              (decoTool.ContextTrapItemDefinition != null ? " (context)" : ""));
        }

        // AFortPawn::ClientInternalEquipWeapon(AFortWeapon*) IS NOT FLAGGED HERE ANY MORE. It used to
        // set a bool on the weapon that UNetDriver consumed when that weapon's channel opened - which
        // meant a RE-equip sent nothing, because the channel is already open and never opens twice.
        // Leaving a building tool for the pickaxe therefore told the client nothing, and the client's
        // build ghost and build-mode arm pose stayed up (found 2026-08-29, and again at spawn on
        // 2026-09-03). It is now driven by CurrentWeapon CHANGING, per connection, in
        // UActorChannel.ReplicateEquippedWeapon - which also keeps the "wait for the weapon's own
        // channel" rule that made the flag necessary in the first place.
        //
        // Grant the weapon's fire ability. Without a spec in ActivatableAbilities the client has
        // literally nothing to activate: the FGameplayAbilitySpecHandle it sends in
        // ServerTryActivateAbility is an index into that array, handed out by the server.
        //
        // The ability lives on the PLAYER STATE's component, not the pawn's - AFortPlayerPawn's
        // bInitAbilitySystemComponentFromPlayerState says the pawn borrows it - so a grant outlives
        // the pawn.
        if (Controller?.PlayerState?.AbilitySystemComponent is { } abilitySystem
            && FortWeaponActorClasses.FireAbilityFor(item.ItemDefinition) is { } fireAbility) {
            // A ReplicateYes ability needs a server-created instance or it is inert - the client
            // makes none of its own for those and ends up activating on the CDO, where every
            // Server_* RPC it sends is executed locally and thrown away. Grenades are the whole
            // affected set today; every healing consumable is ReplicateNo, which is why those have
            // always worked. See UGameplayAbilityInstance.
            var needsInstance = FortConsumables.NeedsReplicatedAbilityInstance(
                item.ItemDefinition?.GetFName().ToString() ?? string.Empty);

            var spec = abilitySystem.GrantAbility(fireAbility, weapon, replicateInstance: needsInstance);
            weapon.GrantedAbilitySpecHandle = spec.Handle;

            // AND THE OTHER BUTTON, when the item has one - see AFortWeapon.SecondaryAbilitySpecHandle.
            if (FortWeaponActorClasses.SecondaryAbilityFor(item.ItemDefinition) is { } secondaryAbility) {
                var secondaryNeedsInstance = FortConsumables.SecondaryNeedsReplicatedAbilityInstance(
                    item.ItemDefinition?.GetFName().ToString() ?? string.Empty);
                var secondarySpec = abilitySystem.GrantAbility(secondaryAbility, weapon,
                                                               replicateInstance: secondaryNeedsInstance);
                weapon.SecondaryAbilitySpecHandle = secondarySpec.Handle;
                Console.WriteLine($"APawn.EquipInventoryItem: granted secondary {secondaryAbility.GetFName()} " +
                                  $"as spec handle {secondarySpec.Handle}");
            }
            Console.WriteLine($"APawn.EquipInventoryItem: granted {fireAbility.GetFName()} as spec handle {spec.Handle}" +
                              (needsInstance ? " (with a replicated ability instance)" : ""));

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
            if (weapon.SecondaryAbilitySpecHandle != UnrealConstants.IndexNone) abilitySystem.ClearAbility(weapon.SecondaryAbilitySpecHandle);
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
    private byte _lastMoveFlags;

    /// <summary>
    ///     Which FSavedMove_Character compressed-move-flag bits this pawn's client has EVER set -
    ///     read by Net.JumpDiagnostics, which reports the absence of bit 0 rather than leaving it as
    ///     a log line that never appears.
    /// </summary>
    public byte SeenMoveFlags => _seenMoveFlags;

    /// <summary>The last movement mode the client reported, or null if it never has.</summary>
    public byte? LastClientMovementMode => _lastMovementMode;

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
    ///     AFortPawn::bMovingEmote (52), bMovingEmoteForwardOnly (53) and EmoteWalkSpeed (71) - the
    ///     three properties that decide whether a dance MOVES.
    ///
    ///     They were Reserved for one reason: the values live on the emote asset
    ///     (UAthenaDanceItemDefinition::bMovingEmote / bMoveForwardOnly / WalkForwardSpeed) and an
    ///     out-of-process server could not read a .uasset. They are baked now - see
    ///     FortEmoteAssets.Generated.cs - and only 22 of 263 dances set them at all, so the default
    ///     (dancing on the spot) is right for nearly everything.
    ///
    ///     Set when an emote starts and cleared when it ends; see FortEmoteSystem.
    /// </summary>
    public bool bMovingEmote { get; set; }

    /// <summary>See <see cref="bMovingEmote"/>. Handle 53.</summary>
    public bool bMovingEmoteForwardOnly { get; set; }

    /// <summary>See <see cref="bMovingEmote"/>. Handle 71, in unreal units per second.</summary>
    public float EmoteWalkSpeed { get; set; }

    /// <summary>
    ///     AFortPlayerPawn::bIsSkydivingFromBus - handle 106, OnRep_IsSkydivingFromBus. Not derivable
    ///     from the movement mode: skydiving from the battle bus and skydiving off a launch pad are
    ///     the same custom mode. The SERVER is the only one that knows which, so it is set in
    ///     NativeRpcHandlers.LeaveAircraft and cleared here when the descent ends.
    /// </summary>
    public bool bIsSkydivingFromBus { get; set; }

    /// <summary>
    ///     ACharacter::ReplicatedBasedMovement - what a player standing on a moving thing is BASED on,
    ///     and how a driver rides a vehicle.
    ///
    ///     NOT AN ATTACHMENT, and that distinction cost this project a round: an earlier attempt at
    ///     vehicles attached the pawn to the vehicle actor, and the client applied the attachment
    ///     without ever entering vehicle state - the pawn sank through the floor. The PR3.0 capture
    ///     shows what the real server does instead, `LogCharacter: Setting base on Server for
    ///     'PlayerPawn_Athena_C_...' to 'FortVehicleSkelMeshComponent ...SkeletalMeshComponent'`. A
    ///     base is a relationship the client's own movement code understands; an attachment is a
    ///     transform it applies to a pawn that is still trying to walk. See [[vehicle-wire-flow]].
    ///
    ///     Handles 19-25, seven of them, because FBasedMovementInfo is not STRUCT_NetSerializeNative
    ///     and FRepLayout recurses into every member - see NativeRepLayouts.PawnProps.
    /// </summary>
    public UObject? MovementBase { get; private set; }

    /// <summary>The bone within the base, "None" for a whole component.</summary>
    public FName MovementBaseBoneName { get; private set; } = new("None");

    /// <summary>Where the pawn sits RELATIVE to its base.</summary>
    public FVector BasedRelativeLocation { get; private set; }

    /// <summary>How the pawn is turned relative to its base.</summary>
    public FRotator BasedRelativeRotation { get; private set; }

    /// <summary>
    ///     `bServerHasBaseComponent` - the client uses it to tell "no base" from "a base it has not
    ///     resolved yet". True exactly when a base is set.
    /// </summary>
    public bool bServerHasBaseComponent => MovementBase != null;

    /// <summary>`bRelativeRotation` - whether the rotation above is relative to the base or absolute.</summary>
    public bool bBasedRelativeRotation { get; private set; }

    /// <summary>`bServerHasVelocity` - nothing here integrates a base's velocity, so this stays false.</summary>
    public bool bServerHasVelocity => false;

    /// <summary>
    ///     Whether this pawn has EVER been in a vehicle or on a base, and therefore whether those
    ///     handle groups stay in its replicated set.
    ///
    ///     ONCE TRUE, NEVER FALSE - the same rule AActor.bAttachmentEverSet follows, and for a reason
    ///     that only shows up when someone gets OUT: the client learns it has left by receiving
    ///     `VehicleStateRep.Vehicle = null`, so dropping the handles from the set the moment the
    ///     vehicle is cleared would remove them before the clearing could be sent. The player would
    ///     be stuck in a seat the server believes is empty.
    /// </summary>
    public bool bVehicleStateEverSet { get; private set; }

    /// <summary>See <see cref="bVehicleStateEverSet"/> - same rule, for the movement base.</summary>
    public bool bMovementBaseEverSet { get; private set; }

    /// <summary>
    ///     AFortPlayerPawn::VehicleStateRep (handles 134-140) - "this pawn is in THAT vehicle, in THIS
    ///     seat", and the thing that actually puts the local player in the seat.
    ///
    ///     WHY THIS AND NOT THE MOVEMENT BASE. The base (19-25) is `COND_SimulatedOnly`
    ///     (Character.cpp:1497), so it can never reach the driver's own connection - it is how
    ///     ONLOOKERS see someone ride along, and a first attempt at vehicles spent a live test
    ///     discovering that. VehicleStateRep carries no condition, is RepNotify, and is on the pawn
    ///     this server already replicates.
    ///
    ///     WHY NOT THE SEAT COMPONENT, which the capture also shows replicating: `PlayerSlots` is a
    ///     `TArray&lt;FAthenaCarPlayerSlot&gt;` and that struct has ~30 members including FText and
    ///     nested arrays, every one of which would have to be written correctly for the array not to
    ///     desync. VehicleStateRep is seven leaves. If the client turns out to need the seat array as
    ///     well, that is the next step and not a cheaper one.
    /// </summary>
    public AActor? VehicleStateVehicle { get; private set; }

    /// <summary>Which seat, 0 being the driver's.</summary>
    public byte VehicleStateSeatIndex { get; private set; }

    /// <summary>The rest of FVehiclePawnState, kept at the values a fresh entry has.</summary>
    public float VehicleStateApexZ { get; private set; }

    public byte VehicleStateExitSocketIndex { get; private set; }

    public bool VehicleStateOverrideExit { get; private set; }

    public FVector VehicleStateSeatTransitionVector { get; private set; }

    public float VehicleStateEntryTime { get; private set; }

    /// <summary>
    ///     Puts this pawn in a vehicle seat, or takes it out with null. Bumps the replicated-property
    ///     set for the same reason SetMovementBase does - the handles are conditional on being in a
    ///     vehicle at all, and a cached set that is never invalidated sends nothing while the server
    ///     logs success.
    /// </summary>
    public void SetVehicleState(AActor? vehicle, byte seatIndex, float entryTime) {
        // The set gains these handles the FIRST time this pawn is ever seated, and keeps them - so
        // the invalidation belongs on that transition and nowhere else. Getting out changes values,
        // not which properties exist.
        var firstTime = vehicle != null && !bVehicleStateEverSet;
        if (vehicle != null) bVehicleStateEverSet = true;

        VehicleStateVehicle = vehicle;
        VehicleStateSeatIndex = seatIndex;
        VehicleStateEntryTime = entryTime;
        VehicleStateApexZ = 0f;
        VehicleStateExitSocketIndex = 0;
        VehicleStateOverrideExit = false;
        VehicleStateSeatTransitionVector = new FVector();

        if (firstTime) MarkReplicatedPropertySetChanged();
    }

    /// <summary>
    ///     Puts this pawn on a base, or takes it off one with null. The relative transform is the
    ///     caller's business: a vehicle seat is an offset in the vehicle's space, and this class has
    ///     no idea where the seats are.
    /// </summary>
    public void SetMovementBase(UObject? newBase, FVector relativeLocation = default,
                                FRotator? relativeRotation = null) {
        var firstBase = newBase != null && !bMovementBaseEverSet;
        if (newBase != null) bMovementBaseEverSet = true;

        MovementBase = newBase;
        BasedRelativeLocation = relativeLocation;
        BasedRelativeRotation = relativeRotation ?? new FRotator();
        bBasedRelativeRotation = relativeRotation != null;

        // WHICH PROPERTIES THIS PAWN REPLICATES JUST CHANGED, and the channel caches that set. Without
        // this the server logs a successful boarding and sends NOTHING - the live log showed the pawn
        // still sending only [ReplicatedMovement] while the player stood beside the vehicle. The
        // attachment group above hit exactly this and its comment says so; the comment did not stop it
        // happening a second time, so the invalidation now lives in the setter itself.
        if (firstBase) MarkReplicatedPropertySetChanged();
    }

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

        // EVERY CHANGE of the flags byte, not just each bit's first appearance.
        //
        // The first-appearance probe above answers "which bits ever arrive" and that has now been
        // answered - 0xC0, i.e. only Custom_2 and Custom_3, with JumpPressed never. What it CANNOT
        // answer is the next question: does pressing jump change the byte AT ALL? If it does, then
        // Fortnite's FSavedMove_FortCharacter::GetCompressedFlags does not put jump in bit 0 the way
        // stock UE does and the whole "JumpPressed never arrives" reading is a false negative. If it
        // does not, the client really is refusing before ACharacter::Jump() and the cause is
        // client-side state.
        //
        // Deliberately noisy and deliberately default-ON while this is open; MOVE_FLAG_TRACE=0 mutes
        // it. Only CHANGES are printed, so holding a key produces one line, not one per tick.
        if (compressedMoveFlags != _lastMoveFlags && WorldOptions.Get("MOVE_FLAG_TRACE") is not "0") {
            Console.WriteLine($"APawn.TrackMoveFlags: flags 0x{_lastMoveFlags:X2} -> 0x{compressedMoveFlags:X2} " +
                              $"(movementMode={clientMovementMode})");
        }

        _lastMoveFlags = compressedMoveFlags;

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

    private float Env(string name, float fallback) =>
        float.TryParse(WorldOptions.Get(name), out var value) ? value : fallback;

    /// <summary>
    ///     How far a player may fall for free. 1152 uu is three Fortnite storeys (a wall is 384),
    ///     which is roughly where the real game starts hurting. FALL_DAMAGE_MIN_DISTANCE overrides.
    /// </summary>
    private float FallDamageMinDistance => Env("FALL_DAMAGE_MIN_DISTANCE", 1152.0f);

    /// <summary>Damage per storey fallen beyond the free distance. FALL_DAMAGE_PER_STOREY overrides.</summary>
    private float FallDamagePerStorey => Env("FALL_DAMAGE_PER_STOREY", 10.0f);

    /// <summary>One Fortnite storey, the unit both constants above are expressed in.</summary>
    private const float StoreyHeight = 384.0f;

    /// <summary>True once this pawn has been seen on the ground at least once - see TrackFallDamage.</summary>
    private bool _hasBeenGrounded;

    /// <summary>The highest Z reached during the fall currently in progress; null when not falling.</summary>
    private float? _fallPeakZ;

    /// <summary>
    ///     No fall damage until this world time - what a SHOCKWAVE grenade grants and an impulse
    ///     grenade does not (`Default.ShockwaveGrenade.AllPlayersTakeFallDamage` is 0 against the
    ///     impulse's 1). Being thrown a hundred metres and then dying to the landing is not what the
    ///     item does.
    /// </summary>
    private float _fallDamageImmuneUntil = float.NegativeInfinity;

    /// <summary>
    ///     Starts the immunity window. The LENGTH is not a row - the table says only whether fall
    ///     damage applies, not for how long - so it is long enough to cover the throw and the landing
    ///     and no longer. SHOCKWAVE_IMMUNITY_SECONDS overrides it.
    /// </summary>
    public void GrantFallDamageImmunity(float timeSeconds) =>
        _fallDamageImmuneUntil = timeSeconds + ShockwaveImmunitySeconds;

    private float ShockwaveImmunitySeconds => Env("SHOCKWAVE_IMMUNITY_SECONDS", 8.0f);

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

        // A SHOCKWAVE'S victim lands unhurt however far it threw them - see GrantFallDamageImmunity.
        // Checked after the peak is cleared so the immunity consumes the fall rather than leaving it
        // pending for the next landing.
        if (GetWorld() is { } world && world.TimeSeconds < _fallDamageImmuneUntil) {
            Console.WriteLine($"APawn.TrackFallDamage: {GetFName()} fell {dropped:F0}uu but is " +
                              "shockwave-immune - no damage.");
            return;
        }

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
        if (timeStamp > LastClientMoveTimeStamp) LastClientMoveTimeStamp = timeStamp;
    }

    /// <summary>
    ///     The newest move timestamp this client has sent, NEVER cleared - unlike
    ///     <see cref="PendingAckGoodMoveTimeStamp" />, which is zeroed the moment it is acked.
    ///
    ///     A correction needs this rather than the pending one, and the difference is the whole
    ///     difference between a correction that works and one that is silently dropped:
    ///     ClientAdjustPosition_Implementation begins with `ClientData->GetSavedMoveIndex(TimeStamp)`
    ///     and RETURNS if that finds nothing (CharacterMovementComponent.cpp:9165). A stale or zero
    ///     timestamp names no saved move, so the client discards the whole correction and logs it at
    ///     Log verbosity, which nothing prints.
    /// </summary>
    public float LastClientMoveTimeStamp { get; private set; }

    /// <summary>
    ///     The last position this client reported RELATIVE to its movement base, or null if it has
    ///     not sent a based move. See NativeRpcHandlers' move handler for why a based move's
    ///     ClientLoc is relative and cannot be used as a world position.
    /// </summary>
    public FVector? LastClientRelativeLocation { get; set; }

    /// <summary>
    ///     When an UNBASED client move last gave this pawn a real world position, in world seconds.
    ///     Negative infinity until one does.
    ///
    ///     This is the only honest measure of whether <see cref="AActor.GetActorLocation" /> means
    ///     anything for a player: a based move carries no world position at all, so the moment
    ///     someone stands on a vehicle this stops advancing and the stored location starts aging.
    /// </summary>
    public float LastUnbasedMoveTime { get; set; } = float.NegativeInfinity;

    /// <summary>
    ///     How closely a camera sample and the pawn's own position must agree in TIME before the gap
    ///     between them is worth calling a boom length. A tenth of a second is about four units of
    ///     drift per metre-per-second of player speed - so at a sprint, tens of units rather than
    ///     hundreds. One second, which this used to be, allows more drift than the boom itself.
    /// </summary>
    public const float BoomSampleSkewSeconds = 0.15f;

    /// <summary>
    ///     How far this client's camera actually sits from its pawn, MEASURED while both were known
    ///     good - the length of the third-person boom, per player and recent.
    ///
    ///     Exists so a movement correction can put the player where the PLAYER is rather than where
    ///     their camera is. The camera is the only world-space position this server still receives
    ///     during a ride (see UActorChannel.SendMovementCorrection), and it hangs a boom's length
    ///     behind and above; walking back along the camera's own forward vector by this distance
    ///     lands on the pawn.
    ///
    ///     WHY A MEASUREMENT AND NOT A CONSTANT. UE pulls the camera IN when it is obstructed, so
    ///     the boom is not a fixed number - it is whatever the client last used. Guessing high in a
    ///     tight space would place the player past themselves and into whatever is in front of them,
    ///     and ClientAdjustPosition teleports without a collision sweep. A measured value cannot
    ///     overshoot, because the client's own camera collision has already swept the segment
    ///     between the pawn and the camera: every point on it is clear.
    /// </summary>
    public float? LastObservedCameraBoom { get; private set; }

    private float _boomObservedAt = float.NegativeInfinity;

    /// <summary>How long a boom measurement stands before it is replaced by a fresh one outright.</summary>
    private const float BoomWindowSeconds = 5f;

    /// <summary>
    ///     Records one camera-to-pawn distance, keeping the SMALLEST seen recently rather than the
    ///     latest.
    ///
    ///     WHY THE MINIMUM. The two ends are not sampled at the same instant - the camera arrives in
    ///     its own RPC and the pawn's position in a move - so any skew between them ADDS to the
    ///     distance, never subtracts: a player running at 410 uu/s puts 410 units of drift into a
    ///     one-second gap. That is not theory, it is what the first live run measured - a 552uu
    ///     "boom" where the real one is about 254 - and it made the correction overshoot past the
    ///     player into whatever was in front of them, which is how someone lands unable to move.
    ///
    ///     Error in this measurement is therefore one-directional, so the minimum is the estimate
    ///     closest to the truth AND the one that fails safely: too short lands between the player
    ///     and their camera, which the camera's own collision sweep has already proven clear. Too
    ///     long lands inside the map.
    ///
    ///     The window exists so a genuinely longer boom can be adopted again - a camera that pulled
    ///     in against a wall and then swung back out would otherwise hold the estimate down forever.
    /// </summary>
    public void ObserveCameraBoom(float distance, float now) {
        if (LastObservedCameraBoom is not { } existing
            || now - _boomObservedAt > BoomWindowSeconds
            || distance < existing) {
            LastObservedCameraBoom = distance;
            _boomObservedAt = now;
        }
    }

    /// <summary>
    ///     The packed movement mode the server wants this client to be in, or null when it has no
    ///     opinion - which is almost always. Consumed once by UActorChannel.SendMovementCorrection.
    ///
    ///     THIS IS THE ONLY WAY TO TELL AN AUTONOMOUS PROXY WHAT MOVEMENT MODE IT IS IN.
    ///     ReplicatedMovementMode is COND_SimulatedOnly, so it never reaches the player who owns the
    ///     pawn; the owning client runs its own movement and only ever defers to the server through
    ///     a correction, where ClientAdjustPosition_Implementation does exactly one thing with this
    ///     byte - `ApplyNetworkMovementMode(ServerMovementMode)`, under the comment "Trust the
    ///     server's movement mode" (CharacterMovementComponent.cpp:9198).
    /// </summary>
    public byte? PendingMovementModeCorrection { get; private set; }


    /// <summary>
    ///     EMovementMode::MOVE_Walking, packed. UCharacterMovementComponent::PackNetworkMovementMode
    ///     (CharacterMovementComponent.cpp:1148) is `MovementMode | (groundBit &lt;&lt; 3)` for a
    ///     non-custom mode, and the ground bit is 0 for Walking - so plain 1. A CUSTOM mode is
    ///     `CustomMovementMode + 16` instead, which is why Driving arrives as 17 and why the two
    ///     encodings can never collide (a non-custom mode maxes out at 15).
    /// </summary>
    public const byte PackedMovementModeWalking = 1;

    /// <summary>
    ///     EMovementMode::MOVE_Falling, packed - plain 3, by the same rule as
    ///     <see cref="PackedMovementModeWalking" />. What a LAUNCH has to put the client into: an
    ///     upward velocity handed to a character that still thinks it is Walking is projected onto
    ///     the floor it is standing on and lost, which is the whole of "the upward impact is weak".
    /// </summary>
    public const byte PackedMovementModeFalling = 3;

    /// <summary>
    ///     Asks for a movement-mode correction on the next update - see
    ///     <see cref="PendingMovementModeCorrection" />.
    /// </summary>
    public void RequestMovementModeCorrection(byte packedMode) => PendingMovementModeCorrection = packedMode;

    /// <summary>Consumes the pending correction, so one request sends exactly one correction.</summary>
    public byte? TakeMovementModeCorrection() {
        var mode = PendingMovementModeCorrection;
        PendingMovementModeCorrection = null;
        return mode;
    }

    /// <summary>
    ///     The velocity this pawn is being LAUNCHED at - a shockwave or impulse grenade's throw -
    ///     waiting for the next correction to carry it. Null when there is none, which is nearly
    ///     always.
    ///
    ///     WHY A CORRECTION AND NOT PushMomentum, which is what this used to be and what the name
    ///     promises. `AFortPawn::OnRep_PushMomentum` was disassembled out of the client
    ///     (0x1419347C0) and it writes exactly two floats:
    ///
    ///         CharacterMovement->Velocity.X = PushMomentum.X;      // [cmc+0xC4]
    ///         CharacterMovement->Velocity.Y = PushMomentum.Y;      // [cmc+0xC8]
    ///         CharacterMovement->AddInputVector(PushMomentum.GetSafeNormal(), true);   // slot 0x548
    ///
    ///     **Velocity.Z (+0xCC) is never touched.** So PushMomentum cannot lift anybody, at any
    ///     magnitude - it is a horizontal shove by construction, and no Z this server sends in it
    ///     will ever arrive. (UMovementComponent::Velocity is at 0xC4 per the 10.40 SDK; slots 0x548
    ///     AddInputVector and 0x400 StopActiveMovement were identified from the exec thunks of those
    ///     same UFUNCTIONs, which call through the vtable at exactly those offsets.)
    ///
    ///     The real game does not use PushMomentum here either. Both grenade projectiles
    ///     (`B_Prj_Athena_ShockGrenade`, `B_Prj_Athena_KnockGrenade`) call **LaunchCharacter** on the
    ///     server, and on a dedicated server that reaches the owning client one way only: a movement
    ///     correction carrying NewVelocity and ServerMovementMode. See
    ///     UActorChannel.SendMovementCorrection.
    /// </summary>
    public FVector? PendingLaunchVelocity { get; private set; }

    /// <summary>
    ///     Launch this pawn on the next correction. Sets the movement mode too, because the two are
    ///     inseparable: ClientAdjustPosition applies `Velocity = NewVelocity` and
    ///     `ApplyNetworkMovementMode(mode)` in that order, and a walking character's next floor
    ///     check would eat the upward half before it ever moved.
    /// </summary>
    public void RequestLaunch(FVector velocity) {
        PendingLaunchVelocity = velocity;
        RequestMovementModeCorrection(PackedMovementModeFalling);
    }

    /// <summary>Consumes the pending launch - one request, one launch.</summary>
    public FVector? TakeLaunchVelocity() {
        var velocity = PendingLaunchVelocity;
        PendingLaunchVelocity = null;
        return velocity;
    }

    /// <summary>
    ///     Whether <see cref="AActor.GetActorLocation" /> is a position the client itself reported
    ///     just now, rather than a stale one from before it boarded something.
    ///
    ///     A correction TELEPORTS without a collision sweep, so naming a stale position is worse
    ///     than sending nothing at all. Half a second is about three metres at a sprint.
    /// </summary>
    public bool HasFreshUnbasedLocation =>
        (GetWorld()?.TimeSeconds ?? 0f) - LastUnbasedMoveTime <= FreshLocationSeconds;

    private const float FreshLocationSeconds = 0.5f;

    private int _exitReportMovesLeft;

    /// <summary>
    ///     Start reporting the next few client moves in detail, because the player has just left a
    ///     vehicle and cannot move afterwards.
    ///
    ///     WHAT THIS IS FOR, precisely. This server never corrects a client's position and never
    ///     refuses a move - there is no ClientAdjustPosition here and TrackMovementSpeed only logs -
    ///     so a player who cannot move is being stopped by their OWN client's state, and there are
    ///     four candidates that need different fixes. The next few moves tell them apart with no
    ///     guessing:
    ///
    ///       no moves at all         -> the client is blocked before movement even runs: a gameplay
    ///                                  effect or ability from boarding that nothing ever removed
    ///                                  (GE_AthenaInVehicle_C is the suspect - see vehicle-wire-flow).
    ///       only based ServerMove   -> the client still thinks it is standing on the vehicle. The
    ///                                  seat is empty and the base is not.
    ///       ServerMoveNoBase, mode 17 -> stuck in EFortCustomMovement::Driving with nothing to
    ///                                  drive (17 = 1 Driving + the 16 custom-mode threshold).
    ///       ServerMoveNoBase, mode 1 -> the client is walking as far as it is concerned, and the
    ///                                  problem is somewhere this report does not reach.
    ///
    ///     Bounded to a handful of moves: this is a question about the first moment after an exit,
    ///     and a per-move log at 60Hz would bury the answer in the noise it creates.
    /// </summary>
    public void BeginVehicleExitReport() {
        _exitReportMovesLeft = 20;
        Console.WriteLine($"APawn.BeginVehicleExitReport: {GetFName()} just left a vehicle - reporting its " +
                          "next 20 client moves. See this method's comment for what each outcome means. " +
                          "SILENCE HERE IS ITSELF THE ANSWER: it means the client sent no moves at all.");
    }

    /// <summary>
    ///     One client move, while <see cref="BeginVehicleExitReport" /> is still watching.
    ///
    ///     STOPS ON SUCCESS RATHER THAN ON A COUNT. The thing worth knowing is whether the client
    ///     left EFortCustomMovement::Driving, so the first non-custom mode ends the report with one
    ///     line saying so - and a client that never leaves it keeps reporting until the budget runs
    ///     out, which is the case that needs the lines. A fixed count printed twenty identical lines
    ///     on success, which is noise that hides the one line that matters.
    /// </summary>
    public void ReportMoveAfterExit(string variant, byte? movementMode, string? movementBase) {
        if (_exitReportMovesLeft <= 0) return;
        _exitReportMovesLeft--;

        var mode = movementMode is { } packed
            ? packed >= 16
                ? $"{packed} (custom {packed - 16}; 1=Driving 3=Parachuting 4=Skydiving)"
                : $"{packed} (1=Walking 3=Falling 6=Custom)"
            : "not carried by this variant";

        // A non-custom mode means the correction landed and the player can move again.
        if (movementMode is { } settled && settled < 16) {
            _exitReportMovesLeft = 0;
            Console.WriteLine($"APawn.ReportMoveAfterExit: {GetFName()} is out of the vehicle and back in " +
                              $"movementMode={mode} - the movement correction landed. ({variant}, " +
                              $"base={movementBase ?? "none"})");
            return;
        }

        Console.WriteLine($"APawn.ReportMoveAfterExit: {GetFName()} {variant}, movementMode={mode}, " +
                          $"base={movementBase ?? "none"} ({_exitReportMovesLeft} more before giving up - " +
                          "still stuck if this runs out)");
    }
}
