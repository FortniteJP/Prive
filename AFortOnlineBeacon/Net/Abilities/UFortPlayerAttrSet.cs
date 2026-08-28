namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     UFortPlayerAttrSet - the player's stamina, and the second attribute set this server sends
///     VALUES for rather than merely introducing.
///
///     Same failure as walk speed, one system over. The client reads stamina through the ability
///     system, so a set the server never fills reads as zero; and in Fortnite a jump COSTS stamina
///     (AFortPlayerController::JumpStaminaCost, wire handle 54, is an EFortJumpStaminaCost). Zero
///     stamina therefore refuses the jump outright, client-side, before anything reaches the server -
///     which is exactly the shape the bug had: walking and crouching are free and both worked, while
///     the jump flag never once appeared in a client move.
///
///     The behaviour the user described independently is the same mechanism seen from the other
///     side: jumping repeatedly in a real match lowers each successive jump, because each one spends
///     stamina that has not finished regenerating.
///
///     The NUMBERS here are not ground truth. Fortnite's own defaults live in a curve table this
///     server has not read; what is derived is the wire layout (Tools/RepHandles
///     rep_handles.py UFortPlayerAttrSet: Stamina 1, StaminaRegenRate 10, StaminaRegenDelay 19,
///     MaxStamina 28, nine handles each). All four are env-overridable so a value can be tried
///     against a live client without a rebuild.
/// </summary>
public class UFortPlayerAttrSet : UFortAttributeSet {
    private static float Env(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    /// <summary>Current stamina. Wire handles 1 (BaseValue) and 2 (CurrentValue).</summary>
    public float Stamina { get; set; } = Env("STAMINA", 100.0f);

    /// <summary>Wire handles 10 / 11.</summary>
    public float StaminaRegenRate { get; set; } = Env("STAMINA_REGEN_RATE", 10.0f);

    /// <summary>Wire handles 19 / 20.</summary>
    public float StaminaRegenDelay { get; set; } = Env("STAMINA_REGEN_DELAY", 1.0f);

    /// <summary>Wire handles 28 / 29.</summary>
    public float MaxStamina { get; set; } = Env("MAX_STAMINA", 100.0f);
}
