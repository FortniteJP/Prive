using AFortOnlineBeacon.Runtime;
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
    public float WalkSpeed { get; set; } = 200.0f;
    public float RunSpeed { get; set; } = 410.0f;
    public float SprintSpeed { get; set; } = 550.0f;
    public float CrouchedRunSpeed { get; set; } = 290.0f;
    public float CrouchedSprintSpeed { get; set; } = 420.0f;
    public float BackwardSpeedMultiplier { get; set; } = 0.65f;

    /// <summary>
    ///     UFortMovementSet::JumpHeight - wire handle 64, and the reason a player could not jump at
    ///     all. Exactly the shape the walk-speed bug had: the attribute exists, the client reads
    ///     movement through the ability system, and an attribute this server never sends reads as
    ///     zero no matter what the client's own defaults were.
    ///
    ///     The default here is deliberately 1.0, because whether this is a MULTIPLIER or a raw value
    ///     is not yet known. Its siblings do not settle it - everything ending in Multiplier or Scale
    ///     is a scalar and every speed is raw, and "JumpHeight" is neither. 1.0 is the safe half of
    ///     that bet: if it is a multiplier, jumping simply works; if it is raw, the result is the
    ///     same zero-height jump as today rather than something wild.
    ///
    ///     One reading settles it. With this sent, `GetAll FortMovementComp_CharacterAthena
    ///     JumpZVelocity` in the client console says which it is - a real velocity (~1000) means
    ///     multiplier and this is already right; ~1 or 0 means raw, and the number it wants can be
    ///     set with JUMP_HEIGHT without a rebuild.
    /// </summary>
    public float JumpHeight { get; set; } = 1.0f;
    public float SpeedMultiplier { get; set; } = 1.0f;

    /// <summary>
    ///     Loads this set's tunables from its WORLD's options. Called by AGameModeBase.Login right
    ///     after the set is created: a property initialiser runs in the constructor, before the set
    ///     belongs to any world, so the initialisers above hold only the shipped defaults and this is
    ///     where a per-playlist value gets in.
    /// </summary>
    public void ApplyOptions(FBeaconOptions options) {
        WalkSpeed = options.Float("WALK_SPEED", 200.0f);
        RunSpeed = options.Float("RUN_SPEED", 410.0f);
        SprintSpeed = options.Float("SPRINT_SPEED", 550.0f);
        CrouchedRunSpeed = options.Float("CROUCHED_RUN_SPEED", 290.0f);
        CrouchedSprintSpeed = options.Float("CROUCHED_SPRINT_SPEED", 420.0f);
        BackwardSpeedMultiplier = options.Float("BACKWARD_SPEED_MULTIPLIER", 0.65f);
        JumpHeight = options.Float("JUMP_HEIGHT", 1.0f);
        SpeedMultiplier = options.Float("SPEED_MULTIPLIER", 1.0f);
    }
}
