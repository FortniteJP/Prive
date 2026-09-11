using AFortOnlineBeacon.Core.Math;
using AFortOnlineBeacon.Net.Actors;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     The storm: the circle the client draws, and the damage for standing outside it.
///
///     UNUSUALLY FOR THIS PROJECT, NOTHING HERE IS GUESSED. The wire side came out of a real 10.40
///     server's own bunch, hand-decoded handle by handle (see AFortSafeZoneIndicator). The phase plan
///     came out of the shipped curve table via Tools/PakReader (see Phases below). And the two AGREE:
///     the capture's live numbers fall exactly where the curve table says they should, to the second.
///     The only thing still chosen rather than sourced is which random centre each circle moves to,
///     which a real match picks at random anyway.
///
///     THE CLIENT DRAWS AND INTERPOLATES THE CIRCLE. A phase is expressed by writing the Next pair
///     plus the two shrink times and letting the client lerp; this server recomputes the current
///     radius on its own only so the damage test agrees with what the player can see.
///
///     Off by default (SAFEZONE_ENABLED=1), for the same reason the battle bus is: a storm that closes
///     wrongly kills the player, which is a far worse failure than not having one, and neither can be
///     tested from here.
///
///     FIRST RUN: `SAFEZONE_ENABLED=1 SAFEZONE_TIME_SCALE=10 AIRCRAFT_ENABLED=1`. The scale is not a
///     nicety - at 1x the first circle does not move for over five minutes after landing, which is
///     indistinguishable from a storm that is simply broken, and that is exactly how the first live
///     run of this was wasted. At 10x the whole ten-circle plan runs in about two minutes with every
///     radius, centre and damage number still exactly as shipped.
/// </summary>
internal static class FortSafeZoneSystem {
    /// <summary>
    ///     One circle. NOT PLACEHOLDERS ANY MORE - every number below is Fortnite 10.40's own, read
    ///     out of the shipped curve table with Tools/PakReader:
    ///
    ///         pakreader rows FortniteGame/Content/Athena/Playlists/AthenaCompositeGameData
    ///
    ///     which resolves to `Athena/Balance/DataTables/AthenaGameData`. Each row is a curve whose
    ///     TIME is the phase index and whose VALUE is that phase's setting:
    ///
    ///         Default.SafeZone.Count          10
    ///         Default.SafeZone.StartingRadius 185000
    ///         Default.SafeZone.StartDelay     60
    ///         Default.SafeZone.WaitTime       0, 200, 120, 90, 80, 50, 30, 0,  0,  0
    ///         Default.SafeZone.ShrinkTime     0, 180, 120, 90, 70, 60, 60, 55, 45, 75
    ///         Default.SafeZone.Radius    150000, 80000, 40000, 20000, 10000, 5000, 2500, 1650, 1090, 0
    ///         Default.SafeZone.Damage      .01,  .01,  .01,  .02,  .05,  .08,  .1,  .1,  .1,  .1
    ///
    ///     CROSS-VALIDATED against the live capture, which is why this is trustworthy rather than just
    ///     sourced: the real server's indicator carried Radius 185000 (= StartingRadius),
    ///     NextNextRadius 80000 (= Radius[1]) and SafeZoneStartShrinkTime 260.7586 - and
    ///     StartDelay 60 + WaitTime[1] 200 is exactly 260. Two independent sources agreeing to the
    ///     second is what says the model below is the right shape and not merely the right numbers.
    ///
    ///     Index 0 is the opening hold, not a shrink: its WaitTime and ShrinkTime are both 0 and its
    ///     Radius (150000) is overridden by StartingRadius. The sequence a match actually plays is
    ///     index 1 onward.
    ///
    ///     DAMAGE IS A FRACTION OF MAX HEALTH per tick, not a flat number - 0.01 is the familiar
    ///     1-point-per-second first circle on a 100 HP player.
    /// </summary>
    private readonly record struct FSafeZonePhase(float WaitSeconds, float ShrinkSeconds, float Radius, float DamageFraction);

    private static readonly FSafeZonePhase[] Phases = {
        new(  0f,   0f, 150000f, 0.01f), // index 0 - the opening hold; radius overridden by StartingRadius
        new(200f, 180f,  80000f, 0.01f),
        new(120f, 120f,  40000f, 0.01f),
        new( 90f,  90f,  20000f, 0.02f),
        new( 80f,  70f,  10000f, 0.05f),
        new( 50f,  60f,   5000f, 0.08f),
        new( 30f,  60f,   2500f, 0.10f),
        new(  0f,  55f,   1650f, 0.10f),
        new(  0f,  45f,   1090f, 0.10f),
        new(  0f,  75f,      0f, 0.10f)
    };

    /// <summary>
    ///     Where the first circle sits. The RADIUS is the game's own Default.SafeZone.StartingRadius.
    ///
    ///     THE CENTRE IS NOT RANDOM, which is a correction to what this used to say. It was written
    ///     up as "a real match's randomly chosen first centre", read off the capture; it is in fact
    ///     the location of the map's own AFortAthenaMapInfo actor - `pakreader actors
    ///     FortniteGame/Content/Athena/Maps/Athena_Terrain.umap MapInfo` prints
    ///     `DefaultMapInfo_4  32256.0,-25600.0,1536.0`, all three components identical to the
    ///     captured value. So the opening circle is centred on the middle of the map, and only the
    ///     circles that shrink inside it move.
    ///
    ///     That actor is the same centre the battle bus flies through - see
    ///     AFortAthenaAircraft.MapCenter, which relies on this agreement as its evidence.
    /// </summary>
    private static readonly FVector InitialCentre = new() { X = 32256f, Y = -25600f, Z = 1536f };

    private const float InitialRadius = 185000f;

    /// <summary>Default.SafeZone.StartDelay - how long the opening circle holds before phase 1 begins.</summary>
    private const float StartDelaySeconds = 60f;

    /// <summary>This world's share of FortSafeZoneSystem's state - see FWorldSubsystem.</summary>
    private sealed class FSafeZoneState : FWorldSubsystem {
        public bool Enabled => Options.Get("SAFEZONE_ENABLED") is "1";

        public int _phaseIndex;

        /// <summary>
        ///     The window phase <see cref="_phaseIndex"/> shrinks in, decided AS SOON AS THE PREVIOUS
        ///     PHASE ENDS rather than when this one starts, because that is what the indicator has to be
        ///     carrying for the client's countdown to be right during the hold.
        ///
        ///     Each one is anchored to the PREVIOUS phase's finish, never to `now`. Anchoring to `now`
        ///     adds however late the tick was to every phase and accumulates that over all ten of them,
        ///     which is a storm that drifts steadily further from the schedule the longer a match runs.
        /// </summary>
        public float _shrinkStart;

        public float _shrinkFinish;

        public bool _shrinking;

        /// <summary>Whether the forecast circle for <see cref="_phaseIndex"/> is up - see AnnounceNext.</summary>
        public bool _announced;

        public float _nextDamageTime;

        /// <summary>
        ///     Divides every wait and every shrink, so the whole storm can be watched end to end in a
        ///     short session. It exists because the real timeline makes this feature effectively
        ///     untestable: the first shrink is 260 s in and the full run is over twenty minutes, so a
        ///     tester who quits after five minutes sees a storm that never did ANYTHING and cannot tell
        ///     that from one that is broken - which is exactly what happened on the first live run.
        ///
        ///     Only the CLOCK is scaled. Radii, centres and damage fractions stay exactly as shipped, so
        ///     a compressed run still exercises the same geometry and the same wire values.
        /// </summary>
        public float TimeScale {
            get {
                var scale = Options.Float("SAFEZONE_TIME_SCALE", 1f);
                return scale > 0f ? scale : 1f;
            }
        }

        /// <summary>
        ///     This world's storm dice. PER WORLD for two reasons, both real: System.Random is not
        ///     thread-safe, so two worlds drawing from one would eventually corrupt it; and SAFEZONE_SEED
        ///     exists to make a storm REPRODUCIBLE, which it could not be if another world were pulling
        ///     numbers out of the same sequence in between.
        /// </summary>
        public Random Rng = null!;

        protected internal override void Initialize() {
            Rng = new Random(Options.Int("SAFEZONE_SEED", 1337));
        }
    }

    private static FSafeZoneState StateOf(UWorld world) => world.GetSubsystem<FSafeZoneState>();

    /// <summary>Damage is applied on a whole-second cadence, so a 1 dps phase really is 1 per second.</summary>
    private const float DamageIntervalSeconds = 1f;

    /// <summary>
    ///     THE STORM DOES NOT EXIST DURING WARMUP, and that is not a nicety - it is the difference
    ///     between this system being usable and it killing everyone the moment it is switched on.
    ///
    ///     The opening circle is centred on the map (32256, -25600) with radius 185000, and the 121
    ///     warmup starts sit 172078 to 197233 from that centre - so 50 OF THEM, 41%, ARE OUTSIDE THE
    ///     FIRST CIRCLE. A storm that ticks from server start therefore damages four players in ten
    ///     while they stand on an island they cannot leave yet, and does it before anything else in
    ///     the match has happened. Real Fortnite has the same geometry and never notices, because the storm is armed
    ///     when the aircraft phase begins - by which time nobody is on the island any more.
    ///
    ///     So: armed when the SAFE ZONE PHASE BEGINS - the end of the bus flight - and it only
    ///     BITES once the phase has actually left Warmup.
    ///
    ///     ARMED AT THE END OF THE FLIGHT, NOT AT ITS START, which is a correction to the previous
    ///     round. Anchoring to AircraftStartTime is defensible from the shipped numbers (the flight
    ///     is 43-60 s and the first shrink is 260 s in, so at 1x the ordering is fine either way) and
    ///     the live capture cannot separate the two, because that server's phases were being driven
    ///     by hand. What settles it is SAFEZONE_TIME_SCALE: at 10x, an AircraftStartTime anchor puts
    ///     the first shrink at t=86 while the bus does not land until t=105, so the storm starts
    ///     closing over players who are still in the air. An anchor that can order itself before the
    ///     drop is wrong whatever the capture says.
    ///
    ///     The no-bus fallback matters for AIRCRAFT_ENABLED=0, where players stay on the island
    ///     forever: there they get a visible circle and a live countdown, and no damage. It is gated
    ///     on the env var rather than on the phase because AGameModeBase flips Warmup -> Aircraft on
    ///     the same tick warmup ends, and whichever of the two ticks first would otherwise decide it.
    /// </summary>
    public static void Tick(UWorld world, float now) {
        var state = StateOf(world);

        if (!state.Enabled) return;
        if (world.GameState is not { } gameState) return;

        var indicator = gameState.SafeZoneIndicator;
        if (indicator == null) {
            var noBus = world.Options.Get("AIRCRAFT_ENABLED") is not "1";
            var armed = gameState.GamePhase is EAthenaGamePhase.SafeZones or EAthenaGamePhase.EndGame
                        || (noBus && gameState.AircraftStartTime > 0f && now >= gameState.AircraftStartTime);
            if (!armed) return;

            indicator = Spawn(world, gameState, now);
            if (indicator == null) return;
        }

        AdvancePhase(world, gameState, indicator, now);
        indicator.UpdateCurrentRadius(now);

        var biting = gameState.GamePhase is EAthenaGamePhase.Aircraft or EAthenaGamePhase.SafeZones
                                          or EAthenaGamePhase.EndGame;
        MarkStormState(world, indicator, now, biting);
        if (biting) ApplyStormDamage(world, indicator, now);
    }

    /// <summary>
    ///     Tells every pawn whether it is in the circle, and whether the wall is close.
    ///
    ///     Separate from the damage tick and run EVERY tick, because these two are what the client
    ///     DRAWS from - the storm vignette and audio - and a player who crosses the wall should see
    ///     it immediately rather than up to a second later when the damage cadence next comes round.
    ///     Damage is on a one-second cadence because the curve table's numbers are per second; being
    ///     inside or outside is not.
    /// </summary>
    private static void MarkStormState(UWorld world, AFortSafeZoneIndicator indicator, float now, bool biting) {
        var centre = indicator.CurrentCentre(now);
        var radius = indicator.Radius;

        // "Near the edge" as a fraction of the current radius rather than a fixed distance: the
        // final circles are 1090 units across, where any fixed warning band would cover the whole
        // thing. NOT a sourced number - the shipped data has no row for it.
        var edgeBand = radius * world.Options.Float("SAFEZONE_EDGE_BAND", 0.1f);
        var inner = MathF.Max(0f, radius - edgeBand);

        foreach (var connection in world.NetDriver?.ClientConnections ?? Enumerable.Empty<UNetConnection>()) {
            if (connection.PlayerController?.Pawn is not { } pawn) continue;

            if (!biting) {
                pawn.bIsInsideSafeZone = true;
                pawn.bIsInAnyStorm = false;
                pawn.bIsNearSafeZoneEdge = false;
                continue;
            }

            var location = pawn.GetActorLocation();
            var dx = location.X - centre.X;
            var dy = location.Y - centre.Y;
            var distanceSquared = dx * dx + dy * dy;

            var inside = distanceSquared <= radius * radius;
            pawn.bIsInsideSafeZone = inside;
            pawn.bIsInAnyStorm = !inside;
            pawn.bIsNearSafeZoneEdge = inside && distanceSquared >= inner * inner;
        }
    }

    private static AFortSafeZoneIndicator? Spawn(UWorld world, AGameState gameState, float now) {
        var state = StateOf(world);

        var indicator = world.SpawnActor<AFortSafeZoneIndicator>(
            Core.Objects.GUClassArray.StaticClass<AFortSafeZoneIndicator>(),
            new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient });
        if (indicator == null) return null;

        var centre = new FVector {
            X = world.Options.Float("SAFEZONE_CENTER_X", InitialCentre.X),
            Y = world.Options.Float("SAFEZONE_CENTER_Y", InitialCentre.Y),
            Z = world.Options.Float("SAFEZONE_CENTER_Z", InitialCentre.Z)
        };
        var radius = world.Options.Float("SAFEZONE_START_RADIUS", InitialRadius);

        indicator.SetRole(ENetRole.ROLE_Authority);
        indicator.SetReplicates(true);

        // The map's preview circle wants to know where the FIRST shrink is heading before it starts,
        // which is exactly what the real capture carried (NextNextRadius 80000 while Next was still
        // 185000). Pick it now so the preview is right from the first frame.
        var (nextCentre, nextRadius) = PickNext(world, centre, radius, 1);
        indicator.NextNextCenter = nextCentre;
        indicator.NextNextRadius = nextRadius;

        gameState.SafeZoneIndicator = indicator;

        // ZERO UNTIL THE FIRST CIRCLE IS ANNOUNCED. SafeZonePhase is not a label - it is a
        // NOTIFICATION, and Announce is the only place allowed to move it. See Announce.
        gameState.SafeZonePhase = 0;
        // WHEN THE SAFE ZONES START, WHICH IS NOT WHEN THE FIRST ONE SHRINKS. This used to be set
        // to the first shrink (StartDelay + WaitTime[1] = 260), and the reported symptom was that the
        // map's forecast circle never appeared.
        //
        // Both readings decompose the capture's SafeZoneStartShrinkTime of 260.7586 the same way, so
        // the capture cannot separate them - but they are different properties and only one reading
        // makes the field's own name true. Default.SafeZone.StartDelay delays the safe zone SYSTEM;
        // each phase then waits its own WaitTime before closing. So SafeZonesStartTime is
        // `+ StartDelay` (60) and the first shrink is that plus WaitTime[1] (200).
        //
        // And that ordering is the whole point of a forecast circle: with SafeZonesStartTime at 260
        // the safe zones "start" at the very moment the storm begins to move, which leaves no window
        // in which a circle can be forecast at all. At 60 there are 200 seconds of knowing where to
        // go before anything moves, which is what the real game gives you.
        //
        // From `now`, i.e. the tick the safe zone phase was noticed, which costs at most one tick of
        // lateness ONCE. It does not accumulate: every later phase is anchored to the previous
        // phase's scheduled finish rather than to whenever it was noticed - see _shrinkStart.
        gameState.SafeZonesStartTime = now + world.Options.Float("SAFEZONE_START_DELAY", StartDelaySeconds) / state.TimeScale;

        state._phaseIndex = 1;
        state._shrinking = false;
        state._announced = false;
        state._shrinkStart = gameState.SafeZonesStartTime + Phases[1].WaitSeconds / state.TimeScale;
        state._shrinkFinish = state._shrinkStart + Phases[1].ShrinkSeconds / state.TimeScale;

        // And tell the client about that window NOW, while the circle is still holding - see
        // AFortSafeZoneIndicator.HoldUntil for why the countdown is wrong without it.
        indicator.HoldUntil(centre, radius, state._shrinkStart);

        Console.WriteLine($"FortSafeZoneSystem: storm armed - centre {centre} radius {radius}, " +
                          $"safe zones start t={gameState.SafeZonesStartTime:F1}, " +
                          $"first shrink t={state._shrinkStart:F1}..{state._shrinkFinish:F1}s toward radius {nextRadius} " +
                          $"at {nextCentre}");
        return indicator;
    }

    private static void AdvancePhase(UWorld world, AGameState gameState, AFortSafeZoneIndicator indicator, float now) {
        var state = StateOf(world);

        if (state._phaseIndex >= Phases.Length) return;

        if (!state._shrinking) {
            // THE FORECAST GOES UP FIRST, at SafeZonesStartTime, and the storm does not move until
            // _shrinkStart - which for phase 1 is a further WaitTime[1] away. See
            // AFortSafeZoneIndicator.AnnounceNext for why the client is happy to draw a circle it is
            // not yet closing to.
            if (!state._announced && now >= gameState.SafeZonesStartTime) {
                Announce(world, gameState, indicator);
                Console.WriteLine($"FortSafeZoneSystem: forecast circle for phase {state._phaseIndex} is up - " +
                                  $"radius {indicator.NextRadius} at {indicator.NextCenter}, " +
                                  $"storm starts moving at t={state._shrinkStart:F1}");
            }

            if (now < state._shrinkStart) return;

            // The wait is over: open the window. The times used are the ADVERTISED ones, not `now` -
            // the client has been counting down to exactly these, and moving them by a tick's
            // lateness would make the circle jump.
            indicator.SetShrinkWindow(state._shrinkStart, state._shrinkFinish);
            state._shrinking = true;

            Console.WriteLine($"FortSafeZoneSystem: phase {state._phaseIndex} closing to radius " +
                              $"{indicator.NextRadius} at {indicator.NextCenter} over " +
                              $"t={state._shrinkStart:F1}..{state._shrinkFinish:F1}s " +
                              $"({Phases[state._phaseIndex].DamageFraction:P0} of max health per second outside)");
            return;
        }

        if (now < state._shrinkFinish) return;

        // The shrink finished. Hold at the new circle - and schedule the next window straight away,
        // anchored to this one's finish rather than to `now`.
        state._shrinking = false;
        state._phaseIndex++;

        if (state._phaseIndex >= Phases.Length) {
            gameState.SafeZonePhase = (byte) Math.Min(byte.MaxValue, state._phaseIndex);
            indicator.HoldAt(indicator.NextCenter, indicator.NextRadius, now);
            Console.WriteLine("FortSafeZoneSystem: final circle reached, storm is done closing");
            return;
        }

        state._shrinkStart = state._shrinkFinish + Phases[state._phaseIndex].WaitSeconds / state.TimeScale;
        var previousFinish = state._shrinkFinish;
        state._shrinkFinish = state._shrinkStart + Phases[state._phaseIndex].ShrinkSeconds / state.TimeScale;

        // A new hold - a zero-length window at the next shrink - and the next forecast circle goes
        // up straight away. Only the FIRST one waits, and what it waits for is
        // Default.SafeZone.StartDelay, which is the whole reason SafeZonesStartTime exists as a
        // separate number from the shrink times.
        var reached = indicator.NextRadius;
        indicator.SetShrinkWindow(state._shrinkStart, state._shrinkStart);
        Announce(world, gameState, indicator);

        Console.WriteLine($"FortSafeZoneSystem: phase {state._phaseIndex} holding at radius {reached} " +
                          $"until t={state._shrinkStart:F1} (waited from {previousFinish:F1}), then closing to " +
                          $"{indicator.NextRadius} at {indicator.NextCenter} until t={state._shrinkFinish:F1}");
    }

    /// <summary>
    ///     Puts the forecast circle up, picks the one after it so the preview is never empty, and
    ///     MOVES SafeZonePhase - which is the part that makes the client look.
    ///
    ///     SafeZonePhase (handle 157) is not a label the HUD prints, it is a NOTIFICATION.
    ///     `AFortGameStateAthena::OnRep_SafeZonePhase` (0x141219FE0 in the dump) reads
    ///     `byte [this+0x1DA9]`, finds a subsystem off the world and calls a virtual on it - so the
    ///     circle is redrawn when the phase number CHANGES, not merely because Next now holds a new
    ///     value. There is no OnRep on LastCenter/NextCenter/NextRadius at all; those are polled or
    ///     read on demand.
    ///
    ///     That is the whole of why only the FIRST forecast circle was missing. Every later announce
    ///     happened at the end of a shrink, where SafeZonePhase was being incremented anyway, so the
    ///     notification went out and the map redrew. The first one happened mid-hold with
    ///     SafeZonePhase already sitting at 1 from spawn - the value never changed, OnRep never fired,
    ///     and nothing re-read the circle that had just been announced.
    ///
    ///     So: 0 at spawn, and only this method ever writes it. An announce and a phase change are
    ///     the same event and must not be able to drift apart again.
    ///
    ///     (Third bug in a row from the same root - see APawn.bIsInAnyStorm and
    ///     FortDamageSystem.Kill. On this client, "the value is correct on the wire" and "the client
    ///     acts on it" are separate questions, and the second one is always about an OnRep.)
    /// </summary>
    private static void Announce(UWorld world, AGameState gameState, AFortSafeZoneIndicator indicator) {
        var state = StateOf(world);

        indicator.AnnounceNext();

        var (afterCentre, afterRadius) = PickNext(world, indicator.NextCenter, indicator.NextRadius, state._phaseIndex + 1);
        indicator.NextNextCenter = afterCentre;
        indicator.NextNextRadius = afterRadius;

        gameState.SafeZonePhase = (byte) Math.Min(byte.MaxValue, state._phaseIndex);
        state._announced = true;
    }

    /// <summary>
    ///     Where the next circle goes: a random point far enough inside the current one that the new
    ///     circle is fully contained. Seeded (SAFEZONE_SEED) so a match is reproducible - a storm
    ///     that moves differently every run is not something a single tester can chase a bug through.
    ///
    ///     CONTAINMENT IS THE WHOLE RULE IN 10.40, which is worth stating because the fields that
    ///     would have added more to it are all zero this version. AFortAthenaMapInfo's
    ///     FFortSafeZoneDefinition points at these curve rows, and Tools/PakReader reads them as:
    ///
    ///         Default.SafeZone.ForceDistanceMin     0
    ///         Default.SafeZone.ForceDistanceMax     0
    ///         Default.SafeZone.RejectRadius         0
    ///         Default.SafeZone.RejectOuterDistance  0
    ///
    ///     so there is no minimum drift, and no rejection annulus, to reproduce.
    ///
    ///     THE ONE PART NOT REPRODUCED: SafeZoneVolumeDefinitions. The map places three brushes
    ///     (SafeZoneVolume0/1/2_S7) with rejection chances 0.6 / 0.2 / 0 - the areas a circle is
    ///     discouraged from centring on, ocean most likely. Reproducing them means brush geometry,
    ///     which this server has no notion of, so a circle here can centre somewhere a real match's
    ///     would usually have rerolled away from.
    /// </summary>
    private static (FVector Centre, float Radius) PickNext(UWorld world, FVector centre, float radius, int phaseIndex) {
        var nextRadius = phaseIndex < Phases.Length ? Phases[phaseIndex].Radius : 0f;
        var drift = MathF.Max(0f, radius - nextRadius);

        var angle = (float) (StateOf(world).Rng.NextDouble() * System.Math.PI * 2.0);
        var distance = (float) (StateOf(world).Rng.NextDouble() * drift);

        return (new FVector {
            X = centre.X + MathF.Cos(angle) * distance,
            Y = centre.Y + MathF.Sin(angle) * distance,
            Z = centre.Z
        }, nextRadius);
    }

    /// <summary>
    ///     Everyone outside the circle takes the current phase's damage, once a second.
    ///
    ///     The position used is the pawn's, which on this server is whatever the client last reported
    ///     through ServerMoveNoBase - so this is as trustworthy as movement is, i.e. not against a
    ///     modified client. That is the same trade the rest of this project already makes.
    ///
    ///     Goes through FortDamageSystem.ApplyDamage with EDeathCause.OutsideSafeZone, which was
    ///     already wired for exactly this: it suppresses the ballistic hit marker for storm damage and
    ///     names the storm in the elimination feed.
    /// </summary>
    private static void ApplyStormDamage(UWorld world, AFortSafeZoneIndicator indicator, float now) {
        var state = StateOf(world);

        if (now < state._nextDamageTime) return;
        state._nextDamageTime = now + DamageIntervalSeconds;

        var phase = state._phaseIndex < Phases.Length ? Phases[state._phaseIndex] : Phases[^1];
        if (phase.DamageFraction <= 0f) return;

        var centre = indicator.CurrentCentre(now);
        var radius = indicator.Radius;

        foreach (var connection in world.NetDriver?.ClientConnections ?? Enumerable.Empty<UNetConnection>()) {
            if (connection.PlayerController is not { PlayerState: { } playerState } pc) continue;
            if (playerState.bIsDead) continue;
            // Still on the battle bus - the storm cannot reach them there.
            if (playerState.bInAircraft) continue;
            if (pc.Pawn is not { } pawn) continue;

            var location = pawn.GetActorLocation();
            var dx = location.X - centre.X;
            var dy = location.Y - centre.Y;
            if (dx * dx + dy * dy <= radius * radius) continue;

            // A FRACTION of the victim's own max health, per the curve table - so a shielded or
            // otherwise non-100-HP player takes the proportion the real game would deal, not a flat
            // number that happens to be right for 100.
            var maxHealth = playerState.HealthSet?.MaxHealth ?? 100f;
            FortDamageSystem.ApplyDamage(playerState, phase.DamageFraction * maxHealth, EDeathCause.OutsideSafeZone);
        }
    }
}
