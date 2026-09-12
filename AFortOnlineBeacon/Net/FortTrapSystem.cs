using AFortOnlineBeacon.Core.Math;
using AFortOnlineBeacon.Core.Objects;
using AFortOnlineBeacon.Net.Actors;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     PLACING a trap, and keeping placed traps honest afterwards.
///
///     Placement is the deco tool's pair of server RPCs (AFortDecoTool, 10.40 SDK):
///
///         ServerSpawnDeco(FVector Location, FRotator Rotation, ABuildingSMActor* AttachedActor,
///                         EBuildingAttachmentType)                    - onto an existing piece
///         ServerCreateBuildingAndSpawnDeco(FVector_NetQuantize10 BuildingLocation, FRotator
///                         BuildingRotation, FVector_NetQuantize10 Location, FRotator Rotation,
///                         EBuildingAttachmentType)                    - onto bare ground: build first
///
///     Both carry the transform the CLIENT's ghost was standing on, already snapped and already
///     offset by the item's GridPlacementOffset - the same contract as ServerCreateBuildingActor, and
///     for the same reason the server takes it verbatim: re-deriving it could only disagree with what
///     the player saw.
///
///     What is spawned is the item's BlueprintClass - for a CONTEXT item (the damage trap, the
///     bouncer, the chiller) the floor/wall/ceiling item that matches the surface, read off the
///     context item itself. See FortTraps.PlacedFor.
///
///     A trap LIVES on what it is attached to: when that piece goes, the trap goes with it (a real
///     ABuildingTrap is destroyed through its AttachedTo's OnDestroyed). Health is 200 -
///     `BuildingDeco.1.FortHealthSet.MaxHealth` in Balance/DataTables/AttributesBuildingProps, the
///     row every trap Blueprint's AttributeInitKeys names - and the ordinary building damage path
///     delivers hits to it, since it IS a building actor.
/// </summary>
internal static class FortTrapSystem {
    /// <summary>`BuildingDeco.1.FortHealthSet.MaxHealth`, the AttributeInitKeys row every trap uses.</summary>
    private const int TrapMaxHealth = 200;

    /// <summary>Two placements closer than this are one placement sent twice.</summary>
    private const float DuplicateDistance = 8f;

    /// <summary>The Wood tier-1 pieces an auto-created attachment is built as. See CreateAttachment.</summary>
    private const string AutoFloorClass = "/Game/Building/ActorBlueprints/Player/Wood/L1/PBWA_W1_Floor.PBWA_W1_Floor_C";

    private const string AutoWallClass = "/Game/Building/ActorBlueprints/Player/Wood/L1/PBWA_W1_Solid.PBWA_W1_Solid_C";

    /// <summary>One placed trap and what its behaviour is waiting on.</summary>
    private sealed class FTrapRuntime {
        public required ABuildingTrap Trap;
        public required FTrapBehaviour? Behaviour;
        public float ArmedAt;
        public float? FireAt;
        public float ReloadUntil;
        public bool ReloadEndPending;

        /// <summary>Who was inside last tick - a launch fires on ENTERING, not on standing there.</summary>
        public HashSet<APawn> Inside = new();

        public float NextHealAt;
        public int HealsGiven;

        /// <summary>When `Abilities.Traps.Cooldown` comes off the trap's component, or null if it is not on.</summary>
        public float? CooldownUntil;

        /// <summary>Launch pad: pawns that stepped on and when the server stops waiting for the client's own launch.</summary>
        public Dictionary<APawn, float> PendingLaunch = new();
    }

    /// <summary>A poison dart's damage over time on one player.</summary>
    private sealed class FPoisoned {
        public required APlayerState Victim;
        public required APlayerState? Instigator;
        public required float PerTick;
        public float NextTickAt;
        public int TicksLeft;
    }

    private sealed class FState : FWorldSubsystem {
        public readonly List<FTrapRuntime> Traps = new();
        public readonly List<FPoisoned> Poisoned = new();

        /// <summary>Players under a bouncer's low gravity, and when it ends.</summary>
        public readonly Dictionary<APawn, float> LowGravityUntil = new();
    }

    private static FState StateOf(UWorld world) => world.GetSubsystem<FState>();

    /// <summary>The live traps in a world.</summary>
    public static IEnumerable<ABuildingTrap> TrapsIn(UWorld world) => StateOf(world).Traps.Select(entry => entry.Trap);

    // ---------------------------------------------------------------- the data behind the behaviours

    /// <summary>`Default.Launchpad.LaunchStrength` (AthenaGameData) - the launch pad's upward speed.</summary>
    private const float LaunchPadStrength = 4500f;

    /// <summary>`Default.BouncePad.Floor.Player.*`: ZVelocity, MaxLateralVelocity, MaxVelocity.</summary>
    private const float FloorBounceZ = 1500f, FloorBounceMaxLateral = 800f, FloorBounceSpeed = 1600f;

    /// <summary>`Default.BouncePad.Wall.Player.*`: Min/MaxLateralVelocity (both 1600) and MinZVelocity.</summary>
    private const float WallBounceLateral = 1600f, WallBounceZ = 800f;

    /// <summary>`Default.BouncePad.LowGravity.*`: GravityZScale and Duration - GE_Trap_BouncePad_LowGravity.</summary>
    private const float BounceLowGravityScale = 0.4f, BounceLowGravityDuration = 2f;

    /// <summary>`Default.TrapCampFire.*`: HealPerTick, HealTimeInterval, MaxHeals.</summary>
    private const float CampfireHealPerTick = 2f, CampfireHealInterval = 1f;
    private const int CampfireMaxHeals = 25;

    /// <summary>`Default.PoisonDartTrap.*`: TickInterval and Duration (DamagePerTick is the behaviour's Damage).</summary>
    private const float PoisonTickInterval = 1f, PoisonDuration = 7f;

    /// <summary>A Fortnite player's capsule, for the trigger-box test.</summary>
    private const float PawnRadius = 40f, PawnHalfHeight = 96f;

    /// <summary>
    ///     THE TAG A TRAP'S CLIENT-SIDE LIFE HANGS OFF. ABuildingTrap's constructor stores
    ///     FFortGameplayTags+0x418 at trap+0xC48 (0x1413B30E1), and the tag registration at 0x1415DA716
    ///     names it `Abilities.Traps.Cooldown`. The trap then registers a NewOrRemoved tag event for it
    ///     on its own ASC (0x1413D424F) whose handler (0x1413C2F70) does nothing while the count is
    ///     above zero and, when it reaches zero, calls OnFinishedBuilding the FIRST time (flag
    ///     trap+0xBD8) and OnReloadEnd + a trigger re-check every time after.
    ///
    ///     OnFinishedBuilding is what shows the damage trap's spikes AND what arms a trap's trigger on
    ///     the client - the launch pad's trigger has no authority check and launches the local pawn
    ///     natively once armed, skydive and glider included. OnReloadEnd is what brings the spikes
    ///     back after a shot. Both were missing because nothing ever put this tag on and took it off.
    /// </summary>
    private const string CooldownTag = "Abilities.Traps.Cooldown";

    /// <summary>
    ///     A trap with no ArmTime still needs its tag on for a moment, so it can come OFF and arm the trap.
    ///     A full second, not less: the tag has to have REACHED the client before it is taken off -
    ///     a removal of a tag the client never saw changes no count and fires nothing - and the
    ///     trap's component and attribute set go out a round trip after the actor opens.
    /// </summary>
    private const float MinimumArmSeconds = 1f;

    /// <summary>How long the server waits for the client's own launch before doing it itself.</summary>
    private const float LaunchPadClientGrace = 0.25f, BouncerClientGrace = 0.15f;

    private const string ActivateCue = "GameplayCue.Abilities.Activation.Traps.ActivateTrap";
    private const string ReloadBeginCue = "GameplayCue.Abilities.Activation.Traps.ReloadBegin";
    private const string ReloadEndCue = "GameplayCue.Abilities.Activation.Traps.ReloadEnd";

    /// <summary>ServerSpawnDeco - onto a piece that already stands.</summary>
    public static void SpawnDeco(AFortDecoTool tool, FVector location, FRotator rotation, UObject? attachedActor,
                                 EBuildingAttachmentType surface) {
        if (!Resolve(tool, surface, "ServerSpawnDeco", out var ctx)) return;

        var attachedTo = attachedActor as ABuildingActor;
        if (attachedActor != null && attachedTo == null) {
            Console.WriteLine($"FortTrapSystem: ServerSpawnDeco named {attachedActor.GetType().Name} " +
                              $"'{attachedActor.GetFName()}' as the surface - not a building this server knows, placing unattached");
        }

        Place(ctx, location, rotation, attachedTo, "ServerSpawnDeco");
    }

    /// <summary>ServerCreateBuildingAndSpawnDeco - onto bare ground, building the piece under it first.</summary>
    public static void CreateBuildingAndSpawnDeco(AFortDecoTool tool, FVector buildingLocation, FRotator buildingRotation,
                                                  FVector location, FRotator rotation, EBuildingAttachmentType surface) {
        if (!Resolve(tool, surface, "ServerCreateBuildingAndSpawnDeco", out var ctx)) return;

        if (!ctx.Held.AutoCreateAttachmentBuilding) {
            Console.WriteLine($"FortTrapSystem: {ctx.Held.ItemPath} may not build under itself " +
                              "(bAutoCreateAttachmentBuilding is false) - ignoring");
            return;
        }

        var attachedTo = CreateAttachment(ctx, buildingLocation, buildingRotation.Yaw, surface);
        if (attachedTo == null) return;

        Place(ctx, location, rotation, attachedTo, "ServerCreateBuildingAndSpawnDeco");
    }

    private readonly record struct FPlaceContext(
        UWorld World,
        AFortDecoTool Tool,
        APawn Pawn,
        APlayerState PlayerState,
        FTrapDef Held,
        FTrapDef Placed,
        EBuildingAttachmentType Surface);

    private static bool Resolve(AFortDecoTool tool, EBuildingAttachmentType surface, string rpc, out FPlaceContext ctx) {
        ctx = default;

        if (tool.GetWorld() is not { } world) return false;
        if (tool.Owner is not APawn { PlayerState: { } playerState } pawn) {
            Console.WriteLine($"FortTrapSystem: {rpc} on {tool.GetFName()} - the tool has no pawn with a player state, ignoring");
            return false;
        }

        if (FortTraps.For(tool.ItemDefinition) is not { } held) {
            Console.WriteLine($"FortTrapSystem: {rpc} on {tool.GetFName()} - holding " +
                              $"'{tool.ItemDefinition?.GetFName()}', which is not in the trap table, ignoring");
            return false;
        }

        if (FortTraps.PlacedFor(held, surface) is not { ActorClass: not null } placed) {
            Console.WriteLine($"FortTrapSystem: {rpc} - {held.ItemPath} has nothing to place on {surface}, ignoring");
            return false;
        }

        ctx = new FPlaceContext(world, tool, pawn, playerState, held, placed, surface);
        return true;
    }

    /// <summary>
    ///     The piece a trap placed on bare ground sits on: a floor, or a wall for a wall trap - the
    ///     item's own AutoCreateAttachmentBuildingShapes, which is why the launch pad can only ever
    ///     make a floor.
    ///
    ///     WOOD, AND FREE. Which material a real server builds it from, and whether it charges for it,
    ///     is not in any asset this project reads - it is ServerCreateBuildingAndSpawnDeco's native
    ///     body. Wood tier 1 is the piece every player can always build; if a live comparison shows
    ///     otherwise, this is the one place to change.
    ///
    ///     A piece of that kind already standing there (the same send twice, or a race with a build)
    ///     is used rather than doubled.
    /// </summary>
    private static ABuildingActor? CreateAttachment(FPlaceContext ctx, FVector location, float yaw, EBuildingAttachmentType surface) {
        var shape = surface == EBuildingAttachmentType.ATTACH_Wall && ctx.Held.AutoCreateShapes.Contains(EAutoCreateShape.Wall)
            ? EAutoCreateShape.Wall
            : EAutoCreateShape.Floor;

        if (!ctx.Held.AutoCreateShapes.Contains(shape)) {
            Console.WriteLine($"FortTrapSystem: {ctx.Held.ItemPath} may not build a {shape} under itself - ignoring");
            return null;
        }

        var classPath = shape == EAutoCreateShape.Wall ? AutoWallClass : AutoFloorClass;
        var buildingClass = ABuildingActor.ClassForPath(classPath);
        var type = ABuildingActor.BuildingTypeFromClassPath(classPath);

        var structural = BuildingStructuralSupportSystem.Of(ctx.World);
        if (structural.IsOccupied(location, yaw, type)) {
            if (structural.FindAt(location, yaw, type) is { } existing) {
                Console.WriteLine($"FortTrapSystem: a {type} already stands at {location} - placing on it");
                return existing;
            }
        }

        var building = Rpc.NativeRpcHandlers.PlacePlayerBuilding(ctx.World, buildingClass, location, yaw);
        if (building != null) {
            Console.WriteLine($"FortTrapSystem: built a {type} ({classPath[(classPath.LastIndexOf('.') + 1)..]}) at " +
                              $"{location} yaw {yaw:F0} for the trap to sit on");
        }

        return building;
    }

    private static void Place(FPlaceContext ctx, FVector location, FRotator rotation, ABuildingActor? attachedTo, string rpc) {
        var state = StateOf(ctx.World);

        // The client has been seen sending a build twice for one confirm; a trap is placed by the
        // same kind of input.
        //
        // SAME CLASS, SAME FACING, not merely the same spot. A wall trap and a floor trap on the same
        // edge share an ORIGIN - every trap's pivot is the tile edge - so a location-only test
        // refused a wall bouncer standing on a floor bouncer's edge as a "duplicate".
        foreach (var existing in state.Traps.Select(entry => entry.Trap)) {
            if (existing.IsPendingKillPending() || existing.bDestroyed) continue;
            if (!string.Equals(existing.GetClass().NativePackagePath, ctx.Placed.ActorClass, StringComparison.OrdinalIgnoreCase)) continue;
            if (MathF.Abs(FRotator.NormalizeAxis(existing.GetActorRotation().Yaw - rotation.Yaw)) > 1f) continue;
            if (MathF.Abs(existing.GetActorRotation().Pitch - rotation.Pitch) > 1f) continue;
            if (MathF.Abs(existing.GetActorRotation().Roll - rotation.Roll) > 1f) continue;
            if (FVector.DistSquared(existing.GetActorLocation(), location) < DuplicateDistance * DuplicateDistance) {
                Console.WriteLine($"FortTrapSystem: {rpc} - a trap already stands at {location}, ignoring (duplicate send)");
                return;
            }
        }

        var trapClass = GUClassArray.StaticClassForPath<ABuildingTrap>(ctx.Placed.ActorClass!);

        ABuildingTrap? trap;
        try {
            trap = ctx.World.SpawnActor<ABuildingTrap>(trapClass, new FActorSpawnParameters {
                ObjectFlags = EObjectFlags.RF_Transient
            });
        } catch (Exception ex) {
            Console.WriteLine($"FortTrapSystem: could not spawn {ctx.Placed.ActorClass} - {ex.Message}");
            return;
        }

        if (trap == null) return;

        trap.SetRole(ENetRole.ROLE_Authority);
        trap.SetActorLocation(location);
        trap.SetActorRotation(rotation);

        // The attribute set BEFORE the hit points, so they land in it too - see
        // ABuildingActor.EnsureAbilitySystemComponent for why a trap needs both.
        trap.EnsureAbilitySystemComponent(withAttributeSet: true);
        trap.InitializeLevelActorHitPoints(TrapMaxHealth);
        trap.TrapData = UAssetRegistry.GetOrCreate(ctx.Placed.ItemPath);
        trap.AttachedTo = attachedTo;
        trap.PlacedBy = ctx.PlayerState;
        trap.PlacerTeam = ctx.PlayerState.TeamIndex;

        // WHAT IT DOES - see FortTraps.BehaviourFor. Two things follow from the kind before the trap
        // replicates: its layout (the launch pad and campfire have handles the rest do not), and an
        // ability system component, which is the only way a trap's own Blueprint hears its
        // activation cue (GameplayCue.Abilities.Activation.Traps.ActivateTrap and friends are
        // events ON the trap, delivered through an ASC whose avatar is the trap).
        var behaviour = FortTraps.BehaviourFor(ctx.Placed.ActorClass);
        trap.Kind = behaviour?.Kind ?? ETrapKind.None;

        // Lit on placement: the Blueprint's authority path sets IsActive the moment it is placed.
        if (trap.Kind == ETrapKind.Campfire) trap.IsActive = true;

        trap.SetReplicates(true);

        var now = ctx.World.TimeSeconds;
        var armTime = MathF.Max(behaviour?.ArmTime ?? 0f, MinimumArmSeconds);

        // ARMING, the way the client understands it: the cooldown tag on for the arm time, then
        // off - see CooldownTag.
        trap.AbilitySystemComponent?.SetMinimalReplicationTag(CooldownTag, true);

        state.Traps.Add(new FTrapRuntime {
            Trap = trap,
            Behaviour = behaviour,
            ArmedAt = now + armTime,
            CooldownUntil = trap.AbilitySystemComponent != null ? now + armTime : null,
            NextHealAt = now + CampfireHealInterval
        });

        // The cost: one off the stack, and the tool goes when the stack does.
        FortConsumableSystem.ConsumeOne(ctx.PlayerState, ctx.Tool, ctx.Held.ItemPath[(ctx.Held.ItemPath.LastIndexOf('.') + 1)..]);

        Console.WriteLine($"FortTrapSystem: {rpc} - {ctx.PlayerState.GetFName()} placed " +
                          $"{ctx.Placed.ActorClass![(ctx.Placed.ActorClass.LastIndexOf('.') + 1)..]} ({ctx.Surface}) at {location} " +
                          $"rot {rotation.Pitch:F0}/{rotation.Yaw:F0}/{rotation.Roll:F0} on " +
                          $"{attachedTo?.GetFName().ToString() ?? "nothing"}" +
                          (behaviour == null ? " - no behaviour modelled for it" : $" - behaves as {behaviour.Kind}"));
    }

    /// <summary>Driven from AGameModeBase.TickPhases.</summary>
    public static void Tick(UWorld world, float now) {
        var state = StateOf(world);
        if (state.Traps.Count == 0 && state.Poisoned.Count == 0 && state.LowGravityUntil.Count == 0) return;

        var pawns = LivePawns(world);

        for (var i = state.Traps.Count - 1; i >= 0; i--) {
            var entry = state.Traps[i];
            var trap = entry.Trap;

            if (trap.IsPendingKillPending()) {
                state.Traps.RemoveAt(i);
                continue;
            }

            if (trap.bDestroyed) continue;

            // WHAT IT SAT ON IS GONE: so is the trap, through the ordinary destruction path so the
            // client sees bDestroyed before the channel closes.
            if (trap.AttachedTo is { } piece && (piece.bDestroyed || piece.IsPendingKillPending())) {
                Console.WriteLine($"FortTrapSystem: {trap.GetFName()} lost the {piece.GetFName()} it sat on - destroyed with it");
                BuildingStructuralSupportSystem.Of(world).ApplyDamage(trap, Math.Max(1, trap.CurrentHitPoints));
                continue;
            }

            if (entry.CooldownUntil is { } cooldownUntil && now >= cooldownUntil) {
                entry.CooldownUntil = null;
                trap.AbilitySystemComponent?.SetMinimalReplicationTag(CooldownTag, false);
                world.NetDriver?.FlushActorProperties(trap);
            }

            if (entry.Behaviour is { } behaviour && now >= entry.ArmedAt) Behave(world, entry, behaviour, pawns, now);
        }

        TickPoison(state, now);
        TickLowGravity(world, state, now);
    }

    /// <summary>
    ///     A bouncer's GE_Trap_BouncePad_LowGravity: GravityZScale x0.4 for 2 s, refreshed by every
    ///     bounce (StackLimitCount 1, AggregateByTarget). The attribute is the physical half - the
    ///     client's movement reads it - and the aura cues sent with the launch are the visible half.
    /// </summary>
    private static void ApplyBounceLowGravity(UWorld world, APawn pawn, float now) {
        if (world.Options.Get("BOUNCER_LOW_GRAVITY") is "0") return;
        if (pawn.PlayerState?.MovementSet is not { } movementSet) return;

        StateOf(world).LowGravityUntil[pawn] = now + BounceLowGravityDuration;
        if (movementSet.GravityZScale == BounceLowGravityScale) return;

        movementSet.GravityZScale = BounceLowGravityScale;
        movementSet.bGravityZScaleEverChanged = true;
        world.NetDriver?.FlushActorProperties(pawn.PlayerState);
    }

    private static void TickLowGravity(UWorld world, FState state, float now) {
        if (state.LowGravityUntil.Count == 0) return;

        foreach (var (pawn, until) in state.LowGravityUntil.ToArray()) {
            if (now < until && !pawn.IsPendingKillPending()) continue;

            state.LowGravityUntil.Remove(pawn);
            if (pawn.PlayerState?.MovementSet is not { } movementSet) continue;

            movementSet.GravityZScale = 1f;
            world.NetDriver?.FlushActorProperties(pawn.PlayerState);
        }
    }

    /// <summary>Every player pawn that is alive - the only thing a trap reacts to here.</summary>
    private static List<APawn> LivePawns(UWorld world) {
        var pawns = new List<APawn>();
        foreach (var connection in world.NetDriver?.ClientConnections ?? Enumerable.Empty<UNetConnection>()) {
            if (connection.PlayerController?.Pawn is not { } pawn) continue;
            if (pawn.IsPendingKillPending() || pawn.PlayerState is not { bIsDead: false }) continue;
            pawns.Add(pawn);
        }

        return pawns;
    }

    private static void Behave(UWorld world, FTrapRuntime entry, FTrapBehaviour behaviour, List<APawn> pawns, float now) {
        var trap = entry.Trap;
        var friendlyFire = world.Options.Get("TRAP_FRIENDLY_FIRE") is "1";
        var inside = new HashSet<APawn>();

        foreach (var pawn in pawns) {
            if (!Overlaps(trap, behaviour, pawn.GetActorLocation())) continue;

            // A HOSTILE trap ignores its own team - the damage trap, the poison darts and the
            // chiller are for the other side. TRAP_FRIENDLY_FIRE=1 lets a lone tester feel them.
            if (behaviour.EnemiesOnly && !friendlyFire && pawn.PlayerState?.TeamIndex == trap.PlacerTeam) continue;

            inside.Add(pawn);
        }

        switch (behaviour.Kind) {
            case ETrapKind.LaunchPad:
            case ETrapKind.FloorBouncer:
            case ETrapKind.WallBouncer:
                TickLaunchTrap(world, entry, behaviour.Kind, inside, now);
                entry.Inside = inside;
                break;

            case ETrapKind.Damage:
            case ETrapKind.PoisonDart:
            case ETrapKind.Chiller:
                TickTriggered(world, entry, behaviour, inside, now);
                break;

            case ETrapKind.Campfire:
                TickCampfire(world, entry, inside, now);
                break;
        }
    }

    // ---------------------------------------------------------------- launch pad and bouncers

    /// <summary>
    ///     THE CLIENT LAUNCHES ITSELF once its copy of the trap is armed - and for the launch pad that
    ///     is the only launch that comes with the real skydive and glider, because the skydive entry
    ///     is the pawn's LOCAL bPendingSkydiveLaunch, which no server message can set.
    ///
    ///     Why it can now: every trap calls OnPlaced on the client (ABuildingTrap vtable 0x300,
    ///     0x1413B6EE0, `if (Role != Authority) OnPlaced()`), and OnPlaced is where the launch pad and
    ///     the bouncers AddTriggerComponent. What stopped the trigger was its pre-check (0x1413D5F20):
    ///     it refuses without the trap's ability system component - the client's local copy of
    ///     handle 20, which this server did not send for a player-placed piece - and while
    ///     `Abilities.Traps.Cooldown` is on. Both are now handled (see CooldownTag and
    ///     ABuildingActor.EnsureAbilitySystemComponent), and neither the launch pad's trigger
    ///     (0x1412229C0) nor the bouncer's BP_OnTrigger checks for authority.
    ///
    ///     So a pawn stepping on is given a moment to go up by itself; if its own moves show it
    ///     rising, the server only does its own part (ServerLaunchInfo for others, fall-damage
    ///     immunity, the bouncer's low gravity). If not, the server launches it as before. Launching
    ///     from both sides would be two launches, the second with the server's slightly stale idea
    ///     of where the player is. TRAP_SERVER_LAUNCH=0 disables the fallback, =server skips the wait.
    /// </summary>
    private static void TickLaunchTrap(UWorld world, FTrapRuntime entry, ETrapKind kind, HashSet<APawn> inside, float now) {
        var trap = entry.Trap;
        var mode = world.Options.Get("TRAP_SERVER_LAUNCH");
        // The wait is only ever FELT when the client does not launch itself: a client launch shows
        // up as a rising pawn and ends it on the spot. Round 3 (2026-09-11) felt it every time,
        // because every trap's tags went to a second ASC the client never listened to (see
        // ABuildingTrap.HasNativeAbilitySubobjects) - so its trap never armed and never launched.
        // Short now, but not zero: the ServerMove that first puts a pawn in the box can predate its
        // own launch by a move or two, and launching from here as well would stack a second,
        // server-side launch on the client's - which throws the launch pad's skydive away.
        // TRAP_SERVER_LAUNCH=server launches at once without looking (the old behaviour), =0 never.
        var watchClient = mode is not "server";
        var grace = watchClient
            ? kind == ETrapKind.LaunchPad ? LaunchPadClientGrace : BouncerClientGrace
            : 0f;

        foreach (var pawn in inside) {
            if (entry.Inside.Contains(pawn) || entry.PendingLaunch.ContainsKey(pawn)) continue;
            entry.PendingLaunch[pawn] = now + grace;
        }

        foreach (var (pawn, due) in entry.PendingLaunch.ToArray()) {
            var rising = watchClient && LaunchedByClient(kind, pawn.EstimatedVelocity);
            if (!rising && now < due) continue;

            entry.PendingLaunch.Remove(pawn);
            if (pawn.IsPendingKillPending()) continue;

            if (rising) {
                if (kind == ETrapKind.LaunchPad) {
                    pawn.BeginLaunchPadDescent(now);
                    trap.LaunchServerTime = now;
                    trap.LaunchedPawn = pawn;
                    FortProjectileSystem.AfterClientTrapLaunch(world, pawn, lowGravity: false);
                } else {
                    FortProjectileSystem.AfterClientTrapLaunch(world, pawn, lowGravity: true);
                    ApplyBounceLowGravity(world, pawn, now);
                }

                SendCue(world, trap, ActivateCue);
                Console.WriteLine($"FortTrapSystem: {pawn.GetFName()} launched itself off {trap.GetFName()} " +
                                  $"(client-side, rising at {pawn.EstimatedVelocity.Z:F0})");
                continue;
            }

            if (mode is "0") {
                Console.WriteLine($"FortTrapSystem: {pawn.GetFName()} did not launch itself off {trap.GetFName()} " +
                                  "and TRAP_SERVER_LAUNCH=0 - leaving it.");
                continue;
            }

            if (grace > 0f) {
                Console.WriteLine($"FortTrapSystem: {pawn.GetFName()} did not launch itself off {trap.GetFName()} " +
                                  $"within {grace:F2}s - launching it from the server");
            }

            Launch(world, trap, kind, pawn, now);
        }
    }

    private static void Launch(UWorld world, ABuildingTrap trap, ETrapKind kind, APawn pawn, float now) {
        var velocity = pawn.EstimatedVelocity;

        switch (kind) {
            case ETrapKind.LaunchPad: {
                // AFortLauncherAthena's launch, read out of the 10.40 client (0x141214D80):
                //
                //     LaunchCharacter((0, 0, LaunchStrength), bXYOverride=false, bZOverride=true)
                //     bPendingSkydiveLaunch = true   -> the pawn's skydive, via a gameplay event
                //     bIsSkydivingFromLaunchPad = true
                //     SetInGliderRedeploy(false)
                //     [authority] ServerLaunchInfo = { WorldTime, Pawn }
                //
                // XY is KEPT (no XY override), Z is replaced. The skydive is the correction's movement
                // mode here - this server cannot fire the pawn's gameplay event - and
                // LAUNCHPAD_SKYDIVE=0 falls back to a plain fall if that ever misbehaves.
                var launch = new FVector { X = velocity.X, Y = velocity.Y, Z = LaunchPadStrength };
                // FALLING BY DEFAULT. Forcing custom Skydiving through the correction put the client in
                // the skydive's physics without its state - upright, no glider (live, 2026-09-11) -
                // because a correction only sets the movement mode; the pose, the holster and the
                // glider belong to the pawn's own skydive entry, which runs off a gameplay event this
                // server cannot fire. LAUNCHPAD_SKYDIVE=1 brings the forced mode back.
                var mode = world.Options.Get("LAUNCHPAD_SKYDIVE") is "1"
                    ? APawn.PackedMovementModeSkydiving
                    : APawn.PackedMovementModeFalling;

                pawn.BeginLaunchPadDescent(now);
                FortProjectileSystem.LaunchByTrap(world, pawn, launch, horizontalPush: false, lowGravity: false, mode);

                trap.LaunchServerTime = now;
                trap.LaunchedPawn = pawn;
                Console.WriteLine($"FortTrapSystem: {trap.GetFName()} launched {pawn.GetFName()} at {launch} " +
                                  $"({(mode == APawn.PackedMovementModeSkydiving ? "skydiving" : "falling")})");
                break;
            }

            case ETrapKind.FloorBouncer: {
                // Trap_Floor_BouncePad_C's PlayerLaunch: mirror the player's velocity off the pad,
                // keep at most 800 of it sideways, add 1500 up the pad's normal, and send the sum out
                // at EXACTLY 1600 (ClampVectorSize with min = max = PlayerMaxVelocity).
                var launch = FloorBounceVelocity(velocity, AxisOf(trap, 2));

                FortProjectileSystem.LaunchByTrap(world, pawn, launch, horizontalPush: true, lowGravity: true);
                ApplyBounceLowGravity(world, pawn, now);
                Console.WriteLine($"FortTrapSystem: {trap.GetFName()} bounced {pawn.GetFName()} at {launch}");
                break;
            }

            case ETrapKind.WallBouncer: {
                // Trap_Wall_BouncePad_C's PlayerLaunch: straight out of the wall at 1600, and up at
                // the clamp's floor of 800.
                var launch = WallBounceVelocity(AxisOf(trap, 1));

                FortProjectileSystem.LaunchByTrap(world, pawn, launch, horizontalPush: true, lowGravity: true);
                ApplyBounceLowGravity(world, pawn, now);
                Console.WriteLine($"FortTrapSystem: {trap.GetFName()} bounced {pawn.GetFName()} off the wall at {launch}");
                break;
            }
        }

        SendCue(world, trap, ActivateCue);
    }

    /// <summary>
    ///     Whether a pawn's own moves already show the launch this trap gives - each well above what
    ///     running or jumping produces: the pad's 4500 up, the floor bouncer's 1500 up, the wall
    ///     bouncer's 1600 sideways (it only lifts by 800, which a jump can come close to).
    /// </summary>
    private static bool LaunchedByClient(ETrapKind kind, FVector velocity) => kind switch {
        ETrapKind.LaunchPad => velocity.Z > 1500f,
        ETrapKind.FloorBouncer => velocity.Z > 1000f,
        _ => velocity.X * velocity.X + velocity.Y * velocity.Y > 1200f * 1200f
    };

    /// <summary>The floor bouncer's launch for a player moving at <paramref name="velocity"/> - see Launch.</summary>
    internal static FVector FloorBounceVelocity(FVector velocity, FVector up) {
        var along = Dot(velocity, up);
        var mirrored = new FVector {
            X = velocity.X - 2f * along * up.X,
            Y = velocity.Y - 2f * along * up.Y,
            Z = 0f
        };
        var lateral = ClampSize(mirrored, FloorBounceMaxLateral);
        return WithSize(new FVector {
            X = up.X * FloorBounceZ + lateral.X,
            Y = up.Y * FloorBounceZ + lateral.Y,
            Z = up.Z * FloorBounceZ + lateral.Z
        }, FloorBounceSpeed);
    }

    /// <summary>The wall bouncer's launch, out along <paramref name="outward"/> - see Launch.</summary>
    internal static FVector WallBounceVelocity(FVector outward) {
        var flat = WithSize(new FVector { X = outward.X, Y = outward.Y, Z = 0f }, WallBounceLateral);
        return new FVector { X = flat.X, Y = flat.Y, Z = WallBounceZ };
    }

    // ---------------------------------------------------------------- damage trap, poison darts, chiller

    /// <summary>
    ///     A triggered trap's cycle, from its stat row: ARMED ArmTime after placement; somebody steps
    ///     in, and FireDelay later it FIRES on whoever is in it then; then it RELOADS for ReloadTime,
    ///     deaf to everything. The activation and reload cues are what the client animates.
    /// </summary>
    private static void TickTriggered(UWorld world, FTrapRuntime entry, FTrapBehaviour behaviour, HashSet<APawn> inside, float now) {
        var trap = entry.Trap;

        if (entry.FireAt is { } fireAt) {
            if (now < fireAt) return;

            entry.FireAt = null;
            Fire(world, trap, behaviour, inside);
            SendCue(world, trap, ActivateCue);

            entry.ReloadUntil = now + behaviour.ReloadTime;
            entry.ReloadEndPending = true;
            SendCue(world, trap, ReloadBeginCue, ReloadCuesAdded(world));

            // THE RELOAD THE CLIENT ACTS ON - the cooldown tag for the reload time; its removal is
            // OnReloadEnd, which is what raises the spikes again. See CooldownTag.
            if (trap.AbilitySystemComponent is { } abilitySystem) {
                abilitySystem.SetMinimalReplicationTag(CooldownTag, true);
                entry.CooldownUntil = entry.ReloadUntil;
                world.NetDriver?.FlushActorProperties(trap);
            }
            return;
        }

        if (now < entry.ReloadUntil) return;

        if (entry.ReloadEndPending) {
            entry.ReloadEndPending = false;
            if (world.Options.Get("TRAP_RELOAD_END_CUE") is "1") SendCue(world, trap, ReloadEndCue, ReloadCuesAdded(world));
        }

        if (inside.Count > 0) entry.FireAt = now + behaviour.FireDelay;
    }

    private static void Fire(UWorld world, ABuildingTrap trap, FTrapBehaviour behaviour, HashSet<APawn> inside) {
        foreach (var pawn in inside) {
            if (pawn.PlayerState is not { } victim) continue;

            switch (behaviour.Kind) {
                case ETrapKind.Damage:
                    var dealt = FortDamageSystem.ApplyDamage(victim, behaviour.Damage, EDeathCause.Trap, trap.PlacedBy);
                    Console.WriteLine($"FortTrapSystem: {trap.GetFName()} hit {victim.GetFName()} for {dealt:F0}");
                    break;

                case ETrapKind.PoisonDart:
                    Poison(world, victim, trap.PlacedBy, behaviour.Damage);
                    break;

                case ETrapKind.Chiller:
                    // The chill itself - ice-skating movement for Default.IceTrap.Duration (15 s) - is a
                    // gameplay effect on the victim's own movement, which this server has no way to
                    // apply yet (the Chiller grenade has the same gap). The trap still goes off.
                    Console.WriteLine($"FortTrapSystem: {trap.GetFName()} went off on {victim.GetFName()} " +
                                      "- the slippery-feet effect itself is not modelled.");
                    break;
            }
        }

        if (inside.Count == 0) Console.WriteLine($"FortTrapSystem: {trap.GetFName()} fired on nobody (they left in time)");
    }

    private static void Poison(UWorld world, APlayerState victim, APlayerState? instigator, float perTick) {
        var state = StateOf(world);

        // A second volley REFRESHES rather than stacks - the same rule as re-drinking a Slurp.
        state.Poisoned.RemoveAll(entry => entry.Victim == victim);
        state.Poisoned.Add(new FPoisoned {
            Victim = victim,
            Instigator = instigator,
            PerTick = perTick,
            NextTickAt = world.TimeSeconds + PoisonTickInterval,
            TicksLeft = (int) MathF.Round(PoisonDuration / PoisonTickInterval)
        });

        Console.WriteLine($"FortTrapSystem: {victim.GetFName()} is poisoned - {perTick:F0} every {PoisonTickInterval:F0}s " +
                          $"for {PoisonDuration:F0}s");
    }

    private static void TickPoison(FState state, float now) {
        for (var i = state.Poisoned.Count - 1; i >= 0; i--) {
            var entry = state.Poisoned[i];
            if (now < entry.NextTickAt) continue;

            if (entry.Victim.bIsDead || entry.TicksLeft <= 0) {
                state.Poisoned.RemoveAt(i);
                continue;
            }

            FortDamageSystem.ApplyDamage(entry.Victim, entry.PerTick, EDeathCause.Trap, entry.Instigator);
            entry.TicksLeft--;
            entry.NextTickAt += PoisonTickInterval;
        }
    }

    // ---------------------------------------------------------------- campfire

    /// <summary>
    ///     Trap_Floor_Player_Campfire_C's HealTicks, which only runs with authority: every
    ///     HealTimeInterval, BoxOverlapActors(AOE_BoxExtents) at ProximityTraceOrigin and heal every
    ///     living player in it; after MaxHeals ticks, IsActive goes false and the fire goes out.
    ///     Everyone in range is healed, not just the placer's team.
    /// </summary>
    private static void TickCampfire(UWorld world, FTrapRuntime entry, HashSet<APawn> inside, float now) {
        var trap = entry.Trap;
        if (!trap.IsActive || now < entry.NextHealAt) return;

        entry.NextHealAt += CampfireHealInterval;
        entry.HealsGiven++;

        foreach (var pawn in inside) FortDamageSystem.Heal(pawn.PlayerState, health: CampfireHealPerTick);

        if (entry.HealsGiven >= CampfireMaxHeals) {
            trap.IsActive = false;
            Console.WriteLine($"FortTrapSystem: {trap.GetFName()} burned out after {entry.HealsGiven} heals");
        }
    }

    // ---------------------------------------------------------------- shared

    /// <summary>
    ///     A trap's activation cue, on the trap's own ASC, to everyone who has the trap. The trap
    ///     Blueprint's `GameplayCue.Abilities.Activation.Traps.*` events are what play the spikes,
    ///     the bounce and the darts. TRAP_CUES=0 turns it off.
    /// </summary>
    private static void SendCue(UWorld world, ABuildingTrap trap, string cue, bool added = false) {
        if (world.Options.Get("TRAP_CUES") is "0") return;
        if (trap.AbilitySystemComponent is not { } abilitySystem) return;

        foreach (var connection in world.NetDriver?.ClientConnections ?? Enumerable.Empty<UNetConnection>()) {
            connection.FindActorChannel(trap)?.SendAbilitySystemCue(abilitySystem, cue, added, trap.GetActorLocation());
        }
    }

    /// <summary>
    ///     The reload cues' event type. Executed by default: the "OnActive" theory behind sending them
    ///     as Added was wrong - 0x1413C2F70's third argument is a TAG COUNT, not a cue event type (see
    ///     CooldownTag), and Added changed nothing live. TRAP_RELOAD_CUE=added keeps the experiment;
    ///     the ReloadEnd cue itself is now off unless TRAP_RELOAD_END_CUE=1, since the tag's removal
    ///     is what ends a reload on the client.
    /// </summary>
    private static bool ReloadCuesAdded(UWorld world) => world.Options.Get("TRAP_RELOAD_CUE") is "added";

    /// <summary>
    ///     Whether a pawn's capsule overlaps the trap's trigger box, in the trap's own frame. The box
    ///     is grown by the capsule's radius and half-height, which is exact for an upright capsule
    ///     against the axis-aligned faces and a slight over-reach at the corners.
    /// </summary>
    internal static bool Overlaps(ABuildingTrap trap, FTrapBehaviour behaviour, FVector pawnLocation) {
        var origin = trap.GetActorLocation();
        var d = new FVector { X = pawnLocation.X - origin.X, Y = pawnLocation.Y - origin.Y, Z = pawnLocation.Z - origin.Z };

        var local = new FVector { X = Dot(d, AxisOf(trap, 0)), Y = Dot(d, AxisOf(trap, 1)), Z = Dot(d, AxisOf(trap, 2)) };

        return MathF.Abs(local.X - behaviour.Center.X) <= behaviour.Extent.X + PawnRadius
               && MathF.Abs(local.Y - behaviour.Center.Y) <= behaviour.Extent.Y + PawnRadius
               && MathF.Abs(local.Z - behaviour.Center.Z) <= behaviour.Extent.Z + PawnHalfHeight;
    }

    /// <summary>The trap's local X (0, forward), Y (1, right) or Z (2, up) axis in world space - FRotationMatrix's rows.</summary>
    internal static FVector AxisOf(ABuildingTrap trap, int axis) {
        var rotation = trap.GetActorRotation();
        const float toRadians = MathF.PI / 180f;
        var (sp, cp) = MathF.SinCos(rotation.Pitch * toRadians);
        var (sy, cy) = MathF.SinCos(rotation.Yaw * toRadians);
        var (sr, cr) = MathF.SinCos(rotation.Roll * toRadians);

        return axis switch {
            0 => new FVector { X = cp * cy, Y = cp * sy, Z = sp },
            1 => new FVector { X = sr * sp * cy - cr * sy, Y = sr * sp * sy + cr * cy, Z = -sr * cp },
            _ => new FVector { X = -(cr * sp * cy + sr * sy), Y = cy * sr - cr * sp * sy, Z = cr * cp }
        };
    }

    private static float Dot(FVector a, FVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static FVector ClampSize(FVector v, float max) {
        var size = MathF.Sqrt(Dot(v, v));
        return size <= max || size < 1e-4f ? v : new FVector { X = v.X / size * max, Y = v.Y / size * max, Z = v.Z / size * max };
    }

    private static FVector WithSize(FVector v, float size) {
        var length = MathF.Sqrt(Dot(v, v));
        return length < 1e-4f ? new FVector { Z = size } : new FVector { X = v.X / length * size, Y = v.Y / length * size, Z = v.Z / length * size };
    }
}
