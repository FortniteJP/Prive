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
