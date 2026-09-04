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
    /// <summary>
    ///     ALWAYS RELEVANT, and this was learned the hard way (2026-09-04, live): after distance
    ///     culling landed, the warmup timer expired and NOBODY WAS PUT ON THE BUS.
    ///
    ///     The bus enters from OUTSIDE the map and flies thousands of units up, so it is never within
    ///     any sane cull radius of a player standing on the warmup island - and no login-time code
    ///     opens its channel, it arrives through UNetDriver's newly-relevant sweep. Culled, it has no
    ///     NetGUID; with no NetGUID, AGameModeBase's ClientSetViewTarget(aircraft) names an object the
    ///     client cannot resolve, and the whole boarding sequence quietly does nothing.
    ///
    ///     It is also simply what the real game does: every player sees and hears the battle bus from
    ///     anywhere on the map, whatever the distance, which is the definition of bAlwaysRelevant.
    /// </summary>
    public AFortAthenaAircraft() => bAlwaysRelevant = true;

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
    ///     THE MAP'S OWN AIRCRAFT SETTINGS, read out of the shipped 10.40 data with Tools/PakReader -
    ///     no longer the round numbers this class used to carry.
    ///
    ///     Where each one comes from, so it can be re-derived for another version:
    ///
    ///         pakreader actors FortniteGame/Content/Athena/Maps/Athena_Terrain.umap MapInfo
    ///             -> DefaultMapInfo_4 at (32256, -25600, 1536)   = <see cref="MapCenter"/>
    ///         pakreader props  FortniteGame/Content/Athena/Maps/Athena_Terrain.umap
    ///             -> AircraftSpawnZone  Min=(-160000,-160000) Max=(160000,160000)
    ///                AircraftDropZone   Min=(-110000,-110000) Max=(110000,110000)
    ///         pakreader props  FortniteGame/Content/Athena/Balance/MapInfos/DefaultMapInfo
    ///             -> AircraftHeight / AircraftSpeed / AircraftDeviationAngle /
    ///                AircraftDistanceFromMidLine, each an FScalableFloat naming a curve row
    ///         pakreader rows   FortniteGame/Content/Athena/Balance/DataTables/AthenaGameData
    ///             -> Default.Aircraft.Height 80000, .Speed 7500, .DeviationAngle 45,
    ///                .DistanceFromMidLine 0
    ///
    ///     THE TWO ZONE BOXES ARE CENTRED ON THE MAP INFO ACTOR, not on the world origin. That is a
    ///     claim worth its evidence, because getting it wrong shifts the entire flight path by
    ///     (32256, -25600). The same map info carries
    ///     `CachedPlayableBoundsForClients Origin=(31488,-23808) BoxExtent=(114432,118016,75000)
    ///     SphereRadius=159757` - and 159757 is the spawn zone's 160000, while 114432/118016 is the
    ///     drop zone's 110000. Two perfectly symmetric +/-N boxes whose N matches the playable area's
    ///     own extents ARE those extents; a world-space box would have had to be authored off-centre
    ///     to land on the same island.
    ///
    ///     That actor's location is also the centre the storm's first circle was seen using in the
    ///     live capture (FortSafeZoneSystem.InitialCentre) - independent confirmation that this is
    ///     what the game treats as the middle of the map.
    /// </summary>
    public static readonly FVector MapCenter = new() { X = 32256f, Y = -25600f, Z = 1536f };

    /// <summary>AFortAthenaMapInfo::AircraftSpawnZone half-extent - the box the flight begins and ends on.</summary>
    public const float SpawnZoneExtent = 160000f;

    /// <summary>AFortAthenaMapInfo::AircraftDropZone half-extent - the box the doors are open inside.</summary>
    public const float DropZoneExtent = 110000f;

    /// <summary>Default.Aircraft.Height - the bus's world Z. Far higher than the 15000 this used to guess.</summary>
    public const float DefaultHeight = 80000f;

    /// <summary>Default.Aircraft.Speed, cm/s. Was 4000.</summary>
    public const float DefaultSpeed = 7500f;

    /// <summary>
    ///     Plans the flight the way the game does: a straight line on the given heading THROUGH THE
    ///     MAP CENTRE, entering and leaving on the spawn zone box, with the drop window set to
    ///     exactly the stretch of that line lying inside the drop zone box.
    ///
    ///     That shape is the native one, and it is readable straight off the client binary's own log
    ///     strings for InitializeFlightPath - "Failed to project path onto spawn zone", "...onto drop
    ///     zone volume", "...onto drop zone box". Projecting a path onto a zone is what the four
    ///     times ARE: flight start and end are where the line meets the spawn zone, drop start and
    ///     end are where it meets the drop zone. None of the four is a chosen duration any more -
    ///     they all fall out of the geometry and the speed.
    ///
    ///     WHAT IS STILL NOT NATIVE, stated plainly rather than papered over:
    ///
    ///     * The HEADING. A real server picks it, and `Default.Aircraft.DeviationAngle` = 45 is
    ///       clearly part of how - but the exec that would have named its own arguments
    ///       (`SetAircraftFlightPath(StartDegrees, OffsetFactor)`) is compiled out of the shipping
    ///       build: the thunk registered for it tail-calls 0x140382DA0, which is a bare `ret`. The
    ///       real InitializeFlightPath IS in the binary (0x141161D70, found from the log strings
    ///       above) but is ~12 KB of inlined float code. Not read. So the heading here is uniformly
    ///       random, or AIRCRAFT_YAW to pin it.
    ///     * The DROP ZONE VOLUME. The map sets AircraftDropVolume = IslandZoneVolume_1, a brush,
    ///       and the native code prefers it over the box - which is why there are two separate log
    ///       strings for the two. This uses the box the volume is inscribed in, so the doors open a
    ///       little early and shut a little late compared with a real match.
    ///     * `AircraftDistanceFromMidLine` is 0 in 10.40, so a line through the centre is exactly
    ///       right for THIS version. On a version where it is non-zero the line is offset sideways
    ///       by that much, and this would need the offset adding perpendicular to the heading.
    /// </summary>
    public void PlanFlightAcrossMap(float matchTimeSeconds, float yawDegrees, float height, float speed) {
        var radians = yawDegrees * MathF.PI / 180f;
        var dirX = MathF.Cos(radians);
        var dirY = MathF.Sin(radians);

        // Distance from the map centre out to a box edge along +/- the heading. A ray leaving the
        // centre of an axis-aligned box exits through whichever side it reaches first, which is the
        // SMALLER of the two per-axis crossings; a heading lying exactly along one axis never
        // crosses the other, hence the guard rather than a plain divide.
        static float ToBoxEdge(float extent, float dirX, float dirY) {
            var tx = MathF.Abs(dirX) > 1e-6f ? extent / MathF.Abs(dirX) : float.PositiveInfinity;
            var ty = MathF.Abs(dirY) > 1e-6f ? extent / MathF.Abs(dirY) : float.PositiveInfinity;
            return MathF.Min(tx, ty);
        }

        var toSpawnEdge = ToBoxEdge(SpawnZoneExtent, dirX, dirY);
        var toDropEdge = ToBoxEdge(DropZoneExtent, dirX, dirY);

        FlightSpeed = speed;
        PlanFlight(matchTimeSeconds,
            new FVector {
                X = MapCenter.X - dirX * toSpawnEdge,
                Y = MapCenter.Y - dirY * toSpawnEdge,
                Z = height
            },
            yawDegrees,
            flightDuration: toSpawnEdge * 2f / speed,
            dropStartOffset: (toSpawnEdge - toDropEdge) / speed,
            dropEndOffset: (toSpawnEdge + toDropEdge) / speed);
    }

    /// <summary>
    ///     Fills in one straight flight path across the map, in the units the client expects.
    ///
    ///     The caller picks the geometry; <see cref="PlanFlightAcrossMap"/> is the one that
    ///     reproduces the game's own. This only writes the fields.
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
