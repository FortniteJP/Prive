using AFortOnlineBeacon.Core.Math;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     The battle bus - AFortAthenaAircraft, spawned as `/Game/Athena/Aircraft/AthenaAircraft.AthenaAircraft_C`.
///
///     Confirmed against the PR3.0 capture (PriveDev/PacketProxy/decoded_new.txt, packet #357): a real
///     server exports exactly that Blueprint as the archetype and spawns the aircraft as an ORDINARY
///     dynamic actor, with `bSerializeLocation=True bSerializeRotation=False bSerializeScale=False
///     bSerializeVelocity=True` in its spawn header - so nothing about it needs a special channel or a
///     sub-object block, only the right class path and the right RepLayout.
///
///     THE CLIENT FLIES IT, NOT THIS SERVER. Every field below is a flight PLAN, not a position: the
///     client simulates the bus from FlightStartLocation along FlightStartRotation at FlightSpeed and
///     interpolates against the match clock. That is why this actor never needs a tick here and why
///     its replicated Location barely matters - and it is also why the times have to be consistent
///     with the GameState's own AircraftStartTime, or the bus will be somewhere the client does not
///     expect when the doors open.
///
///     The times are all in the same seconds-since-match-start units as
///     AGameState.ReplicatedWorldTimeSeconds, which is what the client compares them against.
/// </summary>
public class AFortAthenaAircraft : AActor {
    /// <summary>AFortAircraft::JumpFlashCount - wire handle 16. Bumped when a player jumps; purely cosmetic (the flash on the bus).</summary>
    public int JumpFlashCount { get; set; }

    /// <summary>
    ///     FlightInfo.FlightStartLocation - wire handle 17. Where the bus enters the map. RepLayout
    ///     recurses into FAircraftFlightInfo member by member rather than giving the struct one
    ///     handle, so these six are handles 17-22 and not one atomic blob.
    /// </summary>
    public FVector FlightStartLocation { get; set; } = new();

    /// <summary>FlightInfo.FlightStartRotation - wire handle 18. The bus flies along this heading, so its Yaw IS the flight path.</summary>
    public FRotator FlightStartRotation { get; set; } = new();

    /// <summary>FlightInfo.FlightSpeed - wire handle 19, in cm/s.</summary>
    public float FlightSpeed { get; set; } = 4000f;

    /// <summary>FlightInfo.TimeTillFlightEnd - wire handle 20, seconds from FlightStartTime.</summary>
    public float TimeTillFlightEnd { get; set; }

    /// <summary>FlightInfo.TimeTillDropStart - wire handle 21. Before this, the doors are shut and jumping is refused.</summary>
    public float TimeTillDropStart { get; set; }

    /// <summary>FlightInfo.TimeTillDropEnd - wire handle 22. After this, anyone still aboard is dropped automatically.</summary>
    public float TimeTillDropEnd { get; set; }

    /// <summary>Wire handle 23 - the absolute match time the flight begins.</summary>
    public float FlightStartTime { get; set; }

    /// <summary>Wire handle 24.</summary>
    public float FlightEndTime { get; set; }

    /// <summary>Wire handle 25.</summary>
    public float DropStartTime { get; set; }

    /// <summary>Wire handle 26.</summary>
    public float DropEndTime { get; set; }

    /// <summary>
    ///     Wire handle 27 - the server's own clock reading for the flight, which the client uses to
    ///     correct for however long the spawn bunch took to arrive. Sent equal to FlightStartTime.
    /// </summary>
    public float ReplicatedFlightTimestamp { get; set; }

    /// <summary>Wire handle 28. One bus, so always 0 - AFortGameStateAthena.Aircrafts is indexed by this.</summary>
    public int AircraftIndex { get; set; }

    /// <summary>
    ///     Fills in one straight flight path across the map, in the units the client expects.
    ///
    ///     The numbers are DELIBERATELY simple and are not claimed to match a real match's bus: a
    ///     real playlist picks the path from the safe-zone plan, which this server has no equivalent
    ///     of yet. What matters for the client is only that the four times are ordered and that the
    ///     path is long enough to still be over the island when the doors open.
    /// </summary>
    public void PlanFlight(float matchTimeSeconds, FVector start, float yaw, float flightDuration,
                           float dropStartOffset, float dropEndOffset) {
        FlightStartLocation = start;
        FlightStartRotation = new FRotator { Yaw = yaw };

        TimeTillFlightEnd = flightDuration;
        TimeTillDropStart = dropStartOffset;
        TimeTillDropEnd = dropEndOffset;

        FlightStartTime = matchTimeSeconds;
        FlightEndTime = matchTimeSeconds + flightDuration;
        DropStartTime = matchTimeSeconds + dropStartOffset;
        DropEndTime = matchTimeSeconds + dropEndOffset;
        ReplicatedFlightTimestamp = matchTimeSeconds;

        SetActorLocation(new FVector { X = start.X, Y = start.Y, Z = start.Z });
        SetActorRotation(new FRotator { Yaw = yaw });
    }

    /// <summary>
    ///     Where the bus is at <paramref name="now"/>, on the same straight line the CLIENT
    ///     interpolates - see the class comment for why this actor never ticks.
    ///
    ///     Needed because a player leaving the bus gets a brand new pawn and it has to appear where
    ///     the bus actually is, not where the bus started. Clamped to the flight window at both ends
    ///     so a jump before the doors open, or a stale aircraft after the flight, still gives a
    ///     point on the path rather than one extrapolated off it.
    /// </summary>
    public FVector LocationAt(float now) {
        var elapsed = Math.Clamp(now - FlightStartTime, 0f, TimeTillFlightEnd);
        var radians = FlightStartRotation.Yaw * MathF.PI / 180f;
        var travelled = FlightSpeed * elapsed;

        return new FVector {
            X = FlightStartLocation.X + MathF.Cos(radians) * travelled,
            Y = FlightStartLocation.Y + MathF.Sin(radians) * travelled,
            Z = FlightStartLocation.Z
        };
    }
}
