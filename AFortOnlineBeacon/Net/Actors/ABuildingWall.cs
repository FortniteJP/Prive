using AFortOnlineBeacon.Core.Math;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     A wall with a door in it - ABuildingWall, which derives from ABuildingSMActor and adds handles
///     69-74. Every openable door on the Athena map is one of these.
///
///     Like a chest (see ABuildingContainer), these are MAP actors this server never loaded, reached
///     through a stably-named stand-in built from the path the client names in ServerAttemptInteract.
///
///     Opening a door on the wire is one bit: <see cref="bDoorOpen"/> (handle 73) has an
///     `OnRep_bDoorOpen` on the client, which is what swings the mesh. There is also a client-side
///     `bLocalDoorOpen` and a `VerifyDoorOpenMatchesServer` - the client predicts the swing locally
///     and then checks the server agreed, so the flag has to actually come back or the door snaps
///     shut again.
/// </summary>
public class ABuildingWall : ABuildingActor {
    /// <summary>ABuildingWall::bDoorOpen - wire handle 73, with OnRep_bDoorOpen on the client.</summary>
    public bool bDoorOpen { get; private set; }

    /// <summary>
    ///     ABuildingWall::bDoorCollisionDisabled - wire handle 74.
    ///
    ///     DELIBERATELY NEVER SET. It used to track bDoorOpen, on the reasoning that an open door the
    ///     player still collides with is worse than one that never opened - and that made an opened
    ///     door impossible to CLOSE. Fortnite's interaction is a TRACE (the client's own config:
    ///     `PickingInteractDistance = 350`, plus the bFilterInteractTraces* options), and a trace
    ///     needs something to hit. Switching the door's collision off takes it out of that trace, so
    ///     the client has no interact target on it any more: no prompt for the door, and no second
    ///     ServerAttemptInteract - which is exactly what the live test showed, one open and then
    ///     nothing, with the prompt still offering to open something.
    ///
    ///     Whatever this flag is really for, it is not "the door is open" - the swing is
    ///     OnRep_bDoorOpen's job and the mesh moving out of the doorway is what lets a player through.
    /// </summary>
    public bool bDoorCollisionDisabled { get; private set; }

    /// <summary>
    ///     The client sends ServerAttemptInteract REPEATEDLY for one press - the live logs show five
    ///     to eight for a single door, about 200 ms apart. Toggling on every one of them swings the
    ///     door open, shut, open, shut in quick succession, which is what "the door spins" looks like.
    ///
    ///     So a toggle is rate-limited. This is not a guess about the client's intent: one PRESS is
    ///     one state change, and any further attempt arriving within this window is the same press
    ///     still being reported. DOOR_TOGGLE_COOLDOWN overrides it.
    /// </summary>
    private static readonly float ToggleCooldownSeconds =
        float.TryParse(Environment.GetEnvironmentVariable("DOOR_TOGGLE_COOLDOWN"), out var v) ? v : 0.5f;

    private float _lastToggleAt = float.NegativeInfinity;

    /// <summary>
    ///     Toggles the door and reports the new state, or null when the attempt was swallowed as a
    ///     repeat of the press that is already being handled.
    /// </summary>
    public bool? ToggleDoor(float timeSeconds) {
        if (timeSeconds - _lastToggleAt < ToggleCooldownSeconds) return null;

        _lastToggleAt = timeSeconds;
        bDoorOpen = !bDoorOpen;
        DoorDesiredRotOffset = new FRotator { Yaw = bDoorOpen ? DoorOpenYaw : 0f };
        return bDoorOpen;
    }

    /// <summary>
    ///     ABuildingWall::DoorDesiredRotOffset - wire handle 70, an FRotator. How far the door should
    ///     be turned, which is what bDoorOpen on its own does not say. Zero when shut.
    /// </summary>
    public FRotator DoorDesiredRotOffset { get; private set; } = new();

    /// <summary>
    ///     How far an open door swings. Ninety degrees is the obvious value and NOT a measured one -
    ///     the real game picks a direction per door (a door opens away from whoever opened it), which
    ///     this does not attempt. DOOR_OPEN_YAW overrides it; negative swings the other way.
    /// </summary>
    private static readonly float DoorOpenYaw =
        float.TryParse(Environment.GetEnvironmentVariable("DOOR_OPEN_YAW"), out var v) ? v : 90f;
}
