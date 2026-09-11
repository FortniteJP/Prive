using AFortOnlineBeacon.Core.Objects;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     /Script/FortniteGame.BuildingTrap - a PLACED trap (a launch pad, a damage trap, a campfire),
///     spawned as the trap item's BlueprintClass by FortTrapSystem.
///
///     An ABuildingSMActor subclass, so every building handle up to 68 is its own, and it adds five
///     (rep_handles.py ABuildingTrap):
///
///         69 TrapData          UFortTrapItemDefinition*   which trap this is
///         70 AppliedAlterations                           (never sent)
///         71 AttachedTo        ABuildingSMActor*          the piece it sits on
///         72 TrapLevel         int32
///         73 OriginalTrapLevel int32                      (never sent)
///
///     A Blueprint may append more after 73 (the launch pad's ServerLaunchInfo at 74/75, the
///     campfire's IsActive at 74) - appending never renumbers these.
/// </summary>
public class ABuildingTrap : ABuildingActor {
    public UObject? TrapData { get; set; }

    public ABuildingActor? AttachedTo { get; set; }

    public int TrapLevel { get; set; } = 1;

    /// <summary>The team of whoever placed it - a trap does not fire on its own team.</summary>
    public byte PlacerTeam { get; set; } = byte.MaxValue;

    /// <summary>Who placed it - credited with what it does.</summary>
    public APlayerState? PlacedBy { get; set; }

    /// <summary>What it does, from its class - see FortTraps.BehaviourFor. Also picks its layout.</summary>
    public ETrapKind Kind { get; set; }

    /// <summary>
    ///     AFortLauncherAthena::ServerLaunchInfo (handles 74/75, launch pad only) - when and whom it
    ///     last launched. RepNotify: OnRepLaunchServerInfo plays the launch sound and effect for
    ///     everyone watching. The 10.40 launch writes exactly these two, and only with authority.
    /// </summary>
    public float LaunchServerTime { get; set; }

    public APawn? LaunchedPawn { get; set; }

    /// <summary>
    ///     Trap_Floor_Player_Campfire_C::IsActive (handle 74, campfire only) - lit. Its OnRep runs
    ///     InitCampfireEffects, which draws the fire only while this is true; the server sets it on
    ///     placement and clears it when the heals run out, exactly as the Blueprint's authority
    ///     path does.
    /// </summary>
    public bool IsActive { get; set; }
}
