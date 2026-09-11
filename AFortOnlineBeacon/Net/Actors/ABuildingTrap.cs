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
}
