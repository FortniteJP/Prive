namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     UFortHealthSet - where a player's health and shield actually live, and the single most
///     important attribute set in the game.
///
///     This is the SAME class a building keeps its health in: real Fortnite's
///     UFortBuildingActorSet derives from it, and so does this project's
///     <see cref="UFortBuildingActorSet"/>. That shared ancestry is not a tidiness point, it is the
///     working hypothesis for the one building feature that does not work - a piece's live-updating
///     health bar. The wire side of the building bar is proven correct (the handle table survives a
///     deliberate one-handle shift test that desyncs instantly when wrong), so whatever makes a GAS
///     health value drive a HUD element is shared between the two, and the player is where it
///     unambiguously must work.
///
///     UNLIKE the building's set, this one is a STABLY NAMED default subobject of the PlayerState -
///     the client's own AFortPlayerState constructor already built a "HealthSet" - so it is
///     referenced by path and needs no class on the wire. See <see cref="UFortAttributeSet"/> for
///     that whole arrangement, and AGameModeBase.Login for where the ten sets are introduced.
///
///     Handles are derived, not guessed: `python Tools/RepHandles/rep_handles.py UFortHealthSet`
///     gives Health at 1/2, MaxHealth at 10/11, CurrentShield at 19/20 and Shield at 28/29 - the
///     nine-handle-per-FFortGameplayAttributeData stride UFortPlayerAttrSet already relies on.
///
///     NAMING TRAP, and it is Fortnite's, not this project's: the CURRENT shield is
///     `CurrentShield` while the MAXIMUM shield is plain `Shield`. Health does it the ordinary way
///     round (Health / MaxHealth). Both names are kept exactly as the SDK has them, because the
///     handle derivation sorts by offset and a renamed member here would silently stop matching
///     Tools/RepHandles output.
/// </summary>
public class UFortHealthSet : UFortAttributeSet {
    /// <summary>Current health. Wire handles 1 (BaseValue) and 2 (CurrentValue).</summary>
    public float Health { get; set; }

    /// <summary>Wire handles 10 / 11.</summary>
    public float MaxHealth { get; set; }

    /// <summary>The shield a player currently has - what damage eats before health. Handles 19 / 20.</summary>
    public float CurrentShield { get; set; }

    /// <summary>The shield CAP, despite the bare name. Handles 28 / 29.</summary>
    public float Shield { get; set; }
}
