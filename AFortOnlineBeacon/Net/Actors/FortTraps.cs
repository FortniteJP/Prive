using AFortOnlineBeacon.Core.Objects;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>A building shape a trap may build under itself when placed on bare ground.</summary>
internal enum EAutoCreateShape {
    Floor,
    Wall
}

/// <summary>EBuildingAttachmentType - which surface a deco is being placed on. 4 bits on the wire.</summary>
internal enum EBuildingAttachmentType : byte {
    ATTACH_Floor = 0,
    ATTACH_Wall = 1,
    ATTACH_Ceiling = 2,
    ATTACH_Corner = 3,
    ATTACH_All = 4,
    ATTACH_WallThenFloor = 5,
    ATTACH_FloorAndStairs = 6,
    ATTACH_CeilingAndStairs = 7,
    ATTACH_None = 8
}

/// <summary>
///     One trap item definition, as read from the paks - see Tools/TrapTable/gen_traps.py.
///
///     <c>ToolClass</c> is the WeaponActorClass the player HOLDS (TrapTool_C or the context tool);
///     <c>ActorClass</c> is the trap actor that is PLACED. A context item's own ActorClass is never
///     spawned: the Floor/Wall/CeilingTrap item for the surface is, with that item's ActorClass.
/// </summary>
internal sealed record FTrapDef(
    string ItemPath,
    string? ToolClass,
    string? ActorClass,
    bool IsContext,
    float GridPlacementOffset,
    bool AutoCreateAttachmentBuilding,
    EAutoCreateShape[] AutoCreateShapes,
    string? FloorTrap,
    string? WallTrap,
    string? CeilingTrap,
    string? StatRow);

/// <summary>
///     What a trap item is and how it places - the lookups over FortTraps.Generated.cs.
///
///     A trap is HELD the way a building piece is: its WeaponActorClass is a tool
///     (AFortDecoTool), and the tool's replicated ItemDefinition is what the client draws the ghost
///     from. See AFortDecoTool and FortTrapSystem.
/// </summary>
internal static partial class FortTraps {
    public static FTrapDef? For(UObject? itemDefinition) =>
        itemDefinition == null ? null : Table.GetValueOrDefault(itemDefinition.GetFName().ToString());

    public static FTrapDef? ForPath(string? itemPath) {
        if (itemPath == null) return null;
        var dot = itemPath.LastIndexOf('.');
        return Table.GetValueOrDefault(dot < 0 ? itemPath : itemPath[(dot + 1)..]);
    }

    /// <summary>The tool to hold for this item, or null if it is not a trap (or has no tool).</summary>
    public static string? ToolClassFor(string itemDefinitionName) =>
        Table.GetValueOrDefault(itemDefinitionName)?.ToolClass;

    /// <summary>
    ///     The item that is actually PLACED for a surface: a context trap resolves to its per-surface
    ///     item, anything else is itself. Null when a context trap has nothing for that surface.
    /// </summary>
    public static FTrapDef? PlacedFor(FTrapDef held, EBuildingAttachmentType surface) {
        if (!held.IsContext) return held;

        var path = surface switch {
            EBuildingAttachmentType.ATTACH_Wall => held.WallTrap,
            EBuildingAttachmentType.ATTACH_Ceiling or EBuildingAttachmentType.ATTACH_CeilingAndStairs => held.CeilingTrap,
            _ => held.FloorTrap
        };

        return ForPath(path);
    }
}

/// <summary>What a placed trap DOES - decided by its Blueprint class. See FortTraps.BehaviourFor.</summary>
public enum ETrapKind {
    /// <summary>Nothing modelled: it stands there and can be shot.</summary>
    None,
    LaunchPad,
    FloorBouncer,
    WallBouncer,
    Damage,
    PoisonDart,
    Chiller,
    Campfire
}

/// <summary>
///     One trap class's behaviour. <c>Center</c>/<c>Extent</c> are its trigger box in the ACTOR's
///     local frame (X forward, Y right, Z up; the actor's origin is the tile's edge, which is why
///     nearly every box sits at Y=256), read from the Blueprint's own trigger component - see
///     Tools notes in the trap_placement memory. The timings are the stat row's (AthenaTraps).
/// </summary>
internal sealed record FTrapBehaviour(
    ETrapKind Kind,
    Core.Math.FVector Center,
    Core.Math.FVector Extent,
    float ArmTime = 0f,
    float FireDelay = 0f,
    float ReloadTime = 0f,
    float Damage = 0f,
    bool EnemiesOnly = false);

internal static partial class FortTraps {
    private static Core.Math.FVector V(float x, float y, float z) => new() { X = x, Y = y, Z = z };

    /// <summary>
    ///     Keyed on the placed actor's class name. Boxes: the Blueprint's BoxComponent (RelativeLocation,
    ///     BoxExtent x RelativeScale3D). Two are not boxes in the asset and are marked:
    ///       * the launch pad's trigger is a collision MESH (S_Athena_Launchpad_Collision at (0,256,128),
    ///         Z scale 0.5) - approximated by a box over the pad;
    ///       * the ceiling poison dart has no box of its own in its package - the floor one's, hung
    ///         downward.
    ///     Stat rows: Trap_Floor_Spikes_Athena_R_T03 (arm 1, delay 0.5, reload 5, 150), Trap_Floor_Prj
    ///     (arm 1, delay 0.5, reload 5; the 10 is per TICK - Default.PoisonDartTrap.*), and
    ///     Trap_Context_Freeze (arm 1, delay 0.25, reload 3).
    /// </summary>
    private static readonly Dictionary<string, FTrapBehaviour> Behaviours = new(StringComparer.OrdinalIgnoreCase) {
        // Smaller than the tile AND LOW: 180 reached past what is drawn, and a tall box launched a
        // player standing on a roof built over the pad. Only feet on the pad count now - with the
        // capsule's 96 added, a pawn centre 0..192 above the trap's origin. The server box is only
        // the fallback's anyway: an armed pad's real trigger is the client's own (see
        // FortTrapSystem.TickLaunchPad).
        ["Trap_Floor_Player_Launch_Pad_C"] = new(ETrapKind.LaunchPad, V(0, 256, 48), V(110, 110, 48)),
        ["Trap_Floor_BouncePad_C"] = new(ETrapKind.FloorBouncer, V(0, 256, 61.67f), V(236, 236, 18.75f)),
        ["Trap_Wall_BouncePad_C"] = new(ETrapKind.WallBouncer, V(0, 76.1f, 209), V(230, 40, 162)),

        ["Trap_Athena_Spikes_C"] = new(ETrapKind.Damage, V(0, 256, 58), V(200, 200, 30), 1f, 0.5f, 5f, 150f, true),
        ["Trap_Athena_WallSpikes_C"] = new(ETrapKind.Damage, V(0, 208, 200), V(240, 180, 173), 1f, 0.5f, 5f, 150f, true),
        ["Trap_Athena_CeilingSpikes_C"] = new(ETrapKind.Damage, V(0, 256, -192), V(240, 240, 176), 1f, 0.5f, 5f, 150f, true),

        ["BP_PoisonDartTrap_Floor_C"] = new(ETrapKind.PoisonDart, V(0, 256, 588), V(240, 240, 556), 1f, 0.5f, 5f, 10f, true),
        ["BP_PoisonDartTrap_Wall_C"] = new(ETrapKind.PoisonDart, V(0, 780, 204), V(240, 768, 192), 1f, 0.5f, 5f, 10f, true),
        ["BP_PoisonDartTrap_Ceiling_C"] = new(ETrapKind.PoisonDart, V(0, 256, -588), V(240, 240, 556), 1f, 0.5f, 5f, 10f, true),

        ["Trap_Floor_Ice_Athena_C"] = new(ETrapKind.Chiller, V(0, 256, 58), V(220, 220, 30), 1f, 0.25f, 3f, 0f, true),
        ["Trap_Wall_Ice_Athena_C"] = new(ETrapKind.Chiller, V(0, 208, 200), V(256, 180, 173), 1f, 0.25f, 3f, 0f, true),
        ["Trap_Ceiling_Ice_Athena_C"] = new(ETrapKind.Chiller, V(0, 256, -192), V(256, 256, 176), 1f, 0.25f, 3f, 0f, true),

        // The campfire's AOE_BoxExtents at its ProximityTraceOrigin - who it heals, not a trigger.
        ["Trap_Floor_Player_Campfire_C"] = new(ETrapKind.Campfire, V(0, 256, 192), V(256, 256, 192))
    };

    /// <summary>For the self-test: every class name a behaviour is keyed on.</summary>
    internal static IEnumerable<string> BehaviourClassNames => Behaviours.Keys;

    /// <summary>For the self-test: every actor class the trap table can place.</summary>
    internal static IEnumerable<string> PlaceableActorClasses =>
        Table.Values.Select(def => def.ActorClass).OfType<string>();

    public static FTrapBehaviour? BehaviourFor(string? actorClassPath) {
        if (actorClassPath == null) return null;
        var dot = actorClassPath.LastIndexOf('.');
        return Behaviours.GetValueOrDefault(dot < 0 ? actorClassPath : actorClassPath[(dot + 1)..]);
    }
}
