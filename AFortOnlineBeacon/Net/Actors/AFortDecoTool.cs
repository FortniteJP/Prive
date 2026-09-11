using AFortOnlineBeacon.Core.Objects;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     /Script/FortniteGame.FortDecoTool - what a player holds while carrying a TRAP (TrapTool_C, or
///     TrapTool_ContextTrap_Athena_C for the one-item-three-surfaces traps). Spawned from the trap
///     item's WeaponActorClass exactly like a rifle; see FortTraps.
///
///     THE GHOST IS THIS ACTOR'S ItemDefinition. It is the deco tool's counterpart of the building
///     tool's DefaultMetadata: OnRep_ItemDefinition is what makes the client build its placement
///     preview, so a tool that never sends it is a pair of empty hands. Project-Reboot-3.0 gets it for
///     free from the native AFortPawn::PickUpActor(nullptr, DecoItemDefinition); here it is set by
///     APawn.EquipInventoryItem.
///
///     Its OWN layout (NativeRepLayouts.DecoTool): handles 1-35 are AFortWeapon's, then
///     36 ItemDefinition, 37 CarriedActor and - on the context tool only - 38
///     ContextTrapItemDefinition (rep_handles.py AFortTrapTool / AFortDecoTool_ContextTrap). A plain
///     TrapTool_C's class ends at 37, so 38 must never reach one: it goes out only while
///     <see cref="ContextTrapItemDefinition" /> is set.
/// </summary>
public class AFortDecoTool : AFortWeapon {
    /// <summary>AFortDecoTool::ItemDefinition - handle 36. The trap item being held.</summary>
    public UObject? ItemDefinition { get; set; }

    /// <summary>
    ///     AFortDecoTool_ContextTrap::ContextTrapItemDefinition - handle 38. The CONTEXT item (e.g.
    ///     TID_ContextTrap_Athena), which the client resolves to the floor/wall/ceiling variant for
    ///     whatever it is aiming at. Set only when the held item really is a context item: the mounted
    ///     turret uses the context tool with a plain item, and a plain item in this pointer would be
    ///     rejected by the client's type check anyway.
    /// </summary>
    public UObject? ContextTrapItemDefinition { get; set; }
}
