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

    private sealed class FState : FWorldSubsystem {
        public readonly List<ABuildingTrap> Traps = new();
    }

    private static FState StateOf(UWorld world) => world.GetSubsystem<FState>();

    /// <summary>The live traps in a world - for the trigger logic.</summary>
    public static IReadOnlyList<ABuildingTrap> TrapsIn(UWorld world) => StateOf(world).Traps;

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
        foreach (var existing in state.Traps) {
            if (existing.IsPendingKillPending() || existing.bDestroyed) continue;
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
        trap.InitializeLevelActorHitPoints(TrapMaxHealth);
        trap.TrapData = UAssetRegistry.GetOrCreate(ctx.Placed.ItemPath);
        trap.AttachedTo = attachedTo;
        trap.PlacedBy = ctx.PlayerState;
        trap.PlacerTeam = ctx.PlayerState.TeamIndex;
        trap.SetReplicates(true);

        state.Traps.Add(trap);

        // The cost: one off the stack, and the tool goes when the stack does.
        FortConsumableSystem.ConsumeOne(ctx.PlayerState, ctx.Tool, ctx.Held.ItemPath[(ctx.Held.ItemPath.LastIndexOf('.') + 1)..]);

        Console.WriteLine($"FortTrapSystem: {rpc} - {ctx.PlayerState.GetFName()} placed " +
                          $"{ctx.Placed.ActorClass![(ctx.Placed.ActorClass.LastIndexOf('.') + 1)..]} ({ctx.Surface}) at {location} " +
                          $"rot {rotation.Pitch:F0}/{rotation.Yaw:F0}/{rotation.Roll:F0} on " +
                          $"{attachedTo?.GetFName().ToString() ?? "nothing"}");
    }

    /// <summary>Driven from AGameModeBase.TickPhases.</summary>
    public static void Tick(UWorld world, float now) {
        var state = StateOf(world);

        for (var i = state.Traps.Count - 1; i >= 0; i--) {
            var trap = state.Traps[i];

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
            }
        }
    }
}
