namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     One seat of a vehicle, as its Blueprint configures it - the server side of
///     `FAthenaCarPlayerSlot`.
///
///     THE FIELD ORDER HERE IS NOT THE WIRE ORDER, and an earlier version of this comment said it
///     was. The wire order lives in NativeRepLayouts.VehicleSeatProps, machine-checked against the
///     10.40 SDK by `python Tools/RepHandles/verify_cs_handles.py` - 31 handles per element, whose
///     COUNT is the divisor for every seat's handles. This type is just the server's own copy of the
///     data, so its fields can be in whatever order reads best.
///
///     WHAT IS ACTUALLY REPLICATED IS <see cref="Player" /> ALONE. The rest is baked out of the
///     Blueprint by Tools/VehicleSeats and never sent - the client's own copy of the Blueprint
///     already holds it, and `PrepReceivedArray` leaves an array it does not have to resize
///     completely alone. So the load-bearing thing this table supplies is the seat COUNT: send a
///     different one and the client resizes, throwing away exactly those sockets and offsets.
/// </summary>
public sealed class FVehicleSeat {
    public string SeatSocket { get; init; } = "";
    public string SeatChoiceSocket { get; init; } = "";
    public string SeatIndicatorSocket { get; init; } = "";

    public string SeatChoiceDisplayTextNamespace { get; init; } = "";
    public string SeatChoiceDisplayTextKey { get; init; } = "";
    public string SeatChoiceDisplayTextSource { get; init; } = "";

    public string SeatCollision { get; init; } = "";
    public string[] ExitSockets { get; init; } = System.Array.Empty<string>();

    public float ShootingConeYawConstraint { get; init; }
    public float ShootingConePitchConstraint { get; init; }

    public string SoundOnEnter { get; init; } = "";
    public string SoundOnExit { get; init; } = "";

    public bool bCanEmote { get; init; }
    public bool bForceCrouch { get; init; }
    public bool bIsSelectable { get; init; }
    public bool bPlayEnterSoundForTransition { get; init; }
    public bool bPlayExitSoundForTransition { get; init; }
    public bool bUseGroundMotion { get; init; }
    public bool bUseVehicleIsOnGround { get; init; }

    public Core.Math.FVector ActorSpaceCameraOffset { get; init; } = new();
    public Core.Math.FVector VehicleSpaceCameraOffset { get; init; } = new();
    public float SlopeCompensationCameraOffset { get; init; }
    public Core.Math.FVector StandingFiringOffset { get; init; } = new();
    public Core.Math.FVector CrouchingFiringOffset { get; init; } = new();
    public Core.Math.FVector EmoteOffset { get; init; } = new();

    /// <summary>Who is sitting here - the only member this server ever changes. Null for empty.</summary>
    public APawn? Player { get; set; }

    public float PlayerEntryTime { get; set; }

    public bool bConstrainPawnToSeatTransform { get; init; }
    public bool bOffsetPlayerRelativeAttachLocation { get; init; }
    public bool bUseExitTimer { get; init; }
}

/// <summary>
///     Every vehicle's seats, baked from its Blueprint by Tools/VehicleSeats.
/// </summary>
internal static partial class FortVehicleSeats {
    /// <summary>
    ///     A FRESH COPY per vehicle instance, because the occupant is per instance. Handing out the
    ///     baked objects themselves would have two carts share a driver - the kind of aliasing bug
    ///     that looks like a replication fault and is not one.
    /// </summary>
    public static FVehicleSeat[] For(string? className) {
        if (className == null || !Seats.TryGetValue(className, out var seats)) return System.Array.Empty<FVehicleSeat>();

        var copy = new FVehicleSeat[seats.Length];
        for (var i = 0; i < seats.Length; i++) {
            var seat = seats[i];
            copy[i] = new FVehicleSeat {
                SeatSocket = seat.SeatSocket,
                SeatChoiceSocket = seat.SeatChoiceSocket,
                SeatIndicatorSocket = seat.SeatIndicatorSocket,
                SeatChoiceDisplayTextNamespace = seat.SeatChoiceDisplayTextNamespace,
                SeatChoiceDisplayTextKey = seat.SeatChoiceDisplayTextKey,
                SeatChoiceDisplayTextSource = seat.SeatChoiceDisplayTextSource,
                SeatCollision = seat.SeatCollision,
                ExitSockets = seat.ExitSockets,
                ShootingConeYawConstraint = seat.ShootingConeYawConstraint,
                ShootingConePitchConstraint = seat.ShootingConePitchConstraint,
                SoundOnEnter = seat.SoundOnEnter,
                SoundOnExit = seat.SoundOnExit,
                bCanEmote = seat.bCanEmote,
                bForceCrouch = seat.bForceCrouch,
                bIsSelectable = seat.bIsSelectable,
                bPlayEnterSoundForTransition = seat.bPlayEnterSoundForTransition,
                bPlayExitSoundForTransition = seat.bPlayExitSoundForTransition,
                bUseGroundMotion = seat.bUseGroundMotion,
                bUseVehicleIsOnGround = seat.bUseVehicleIsOnGround,
                ActorSpaceCameraOffset = seat.ActorSpaceCameraOffset,
                VehicleSpaceCameraOffset = seat.VehicleSpaceCameraOffset,
                SlopeCompensationCameraOffset = seat.SlopeCompensationCameraOffset,
                StandingFiringOffset = seat.StandingFiringOffset,
                CrouchingFiringOffset = seat.CrouchingFiringOffset,
                EmoteOffset = seat.EmoteOffset,
                bConstrainPawnToSeatTransform = seat.bConstrainPawnToSeatTransform,
                bOffsetPlayerRelativeAttachLocation = seat.bOffsetPlayerRelativeAttachLocation,
                bUseExitTimer = seat.bUseExitTimer
            };
        }

        return copy;
    }
}
