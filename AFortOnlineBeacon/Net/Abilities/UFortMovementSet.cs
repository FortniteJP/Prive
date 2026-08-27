namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     The PlayerState's MovementSet, and the one attribute set this server sends VALUES for.
///
///     A client console query settled why a player could only crawl:
///
///         GetAll FortMovementComp_CharacterAthena MaxWalkSpeed
///         ... PlayerPawn_Athena_C_2147478982.CharMoveComp.MaxWalkSpeed = 0.000000
///
///     Zero, not one - the 1.0 uu/s that was actually measured is UE's own floor underneath it. That
///     CDO default is zero on purpose: Fortnite always overrides MaxWalkSpeed from these attributes,
///     so nothing feeding them leaves the character with no speed at all.
///
///     The client's log DOES print "Property RunSpeed new value is: 410" - but that is attribute
///     initialisation from a curve table, and it is evidently not what the per-player set instance
///     ends up holding, or MaxWalkSpeed would not be zero. So the values are sent explicitly here.
///
///     The numbers are the client's own reported defaults, not invented: WalkSpeed 200,
///     RunSpeed 410, SprintSpeed 550, CrouchedRunSpeed 290, CrouchedSprintSpeed 420,
///     BackwardSpeedMultiplier 0.65. Each is overridable by environment variable so a wrong guess
///     costs a restart rather than a rebuild.
/// </summary>
public class UFortMovementSet : UFortAttributeSet {
    private static float Env(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    public float WalkSpeed { get; set; } = Env("WALK_SPEED", 200.0f);
    public float RunSpeed { get; set; } = Env("RUN_SPEED", 410.0f);
    public float SprintSpeed { get; set; } = Env("SPRINT_SPEED", 550.0f);
    public float CrouchedRunSpeed { get; set; } = Env("CROUCHED_RUN_SPEED", 290.0f);
    public float CrouchedSprintSpeed { get; set; } = Env("CROUCHED_SPRINT_SPEED", 420.0f);
    public float BackwardSpeedMultiplier { get; set; } = Env("BACKWARD_SPEED_MULTIPLIER", 0.65f);
    public float SpeedMultiplier { get; set; } = Env("SPEED_MULTIPLIER", 1.0f);
}
