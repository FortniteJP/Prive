namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     UFortBuildingActorSet - the attribute set a placed building piece keeps its health in, and
///     therefore what a client needs before it can draw a health bar over one. Derives from
///     UFortHealthSet in real Fortnite, which is where Health/MaxHealth actually live; the two
///     attributes this class adds of its own (BuildTime, RepairTime) are Save-The-World concerns and
///     are not modelled.
///
///     UNLIKE the PlayerState's ten attribute sets, this one is NOT a stably-named default
///     subobject the client already built for itself (see UFortAttributeSet's doc comment for that
///     arrangement). Real Fortnite creates a building's set at runtime - the property carrying it,
///     ABuildingActor::ReplicatedBuildingAttributeSet, is Transient/ExportObject/RepNotify - so it
///     has no stable path and has to travel the way the AbilitySystemComponent does: a sub-object
///     content block naming its own NetGUID plus its CLASS, which the client then constructs.
///     `/Script/FortniteGame.FortBuildingActorSet` is native, so that class reference always
///     resolves (see GUClassArray's mapping and the hazard note there about unresolvable classes).
///
///     Wire handles are derived, not guessed: `python Tools/RepHandles/rep_handles.py
///     UFortHealthSet` gives Health at 1/2 and MaxHealth at 10/11 (each
///     FFortGameplayAttributeData is nine handles wide, of which BaseValue and CurrentValue are the
///     first two - the same nine-wide stride UFortPlayerAttrSet already relies on).
/// </summary>
public class UFortBuildingActorSet : UFortHealthSet {
    // Health (handles 1/2) and MaxHealth (10/11) are INHERITED from UFortHealthSet, exactly as they
    // are in real Fortnite - a building's health and a player's are the same attributes at the same
    // wire handles. See UFortHealthSet for why that shared ancestry matters to the health-bar work.
}
