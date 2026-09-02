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
    ///     Where the first circle sits. The RADIUS is the game's own Default.SafeZone.StartingRadius;
    ///     the CENTRE is the one a real server was seen using in the capture, which is a real match's
    ///     randomly chosen first centre rather than a constant - but it is a point on the actual map,
    ///     which is what matters for landing somewhere sensible.
    /// </summary>
    private static readonly FVector InitialCentre = new() { X = 32256f, Y = -25600f, Z = 1536f };

    private const float InitialRadius = 185000f;

    /// <summary>Default.SafeZone.StartDelay - how long the opening circle holds before phase 1 begins.</summary>
    private const float StartDelaySeconds = 60f;

    private static bool Enabled => Environment.GetEnvironmentVariable("SAFEZONE_ENABLED") is "1";

    private static float EnvFloat(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

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
    private static float TimeScale {
        get {
            var scale = EnvFloat("SAFEZONE_TIME_SCALE", 1f);
            return scale > 0f ? scale : 1f;
        }
    }

    private static int _phaseIndex;
    private static float _phaseChangeTime;
    private static bool _shrinking;
    private static float _nextDamageTime;
    private static readonly Random Rng = new(
        int.TryParse(Environment.GetEnvironmentVariable("SAFEZONE_SEED"), out var seed) ? seed : 1337);

    /// <summary>Damage is applied on a whole-second cadence, so a 1 dps phase really is 1 per second.</summary>
    private const float DamageIntervalSeconds = 1f;

    public static void Tick(UWorld world, float now) {
        if (!Enabled) return;
        if (world.GameState is not { } gameState) return;

        var indicator = gameState.SafeZoneIndicator ?? Spawn(world, gameState, now);
        if (indicator == null) return;

        AdvancePhase(gameState, indicator, now);
        indicator.UpdateCurrentRadius(now);
        ApplyStormDamage(world, indicator, now);
    }

    private static AFortSafeZoneIndicator? Spawn(UWorld world, AGameState gameState, float now) {
        var indicator = world.SpawnActor<AFortSafeZoneIndicator>(
            Core.Objects.GUClassArray.StaticClass<AFortSafeZoneIndicator>(),
            new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient });
        if (indicator == null) return null;

        var centre = new FVector {
            X = EnvFloat("SAFEZONE_CENTER_X", InitialCentre.X),
            Y = EnvFloat("SAFEZONE_CENTER_Y", InitialCentre.Y),
            Z = EnvFloat("SAFEZONE_CENTER_Z", InitialCentre.Z)
        };
        var radius = EnvFloat("SAFEZONE_START_RADIUS", InitialRadius);

        indicator.SetRole(ENetRole.ROLE_Authority);
        indicator.SetReplicates(true);
        indicator.HoldAt(centre, radius, now);

        // The map's preview circle wants to know where the FIRST shrink is heading before it starts,
        // which is exactly what the real capture carried (NextNextRadius 80000 while Next was still
        // 185000). Pick it now so the preview is right from the first frame.
        var (nextCentre, nextRadius) = PickNext(centre, radius, 1);
        indicator.NextNextCenter = nextCentre;
        indicator.NextNextRadius = nextRadius;

        gameState.SafeZoneIndicator = indicator;
        gameState.SafeZonePhase = 0;
        // Default.SafeZone.StartDelay, then phase 1's own WaitTime - which is exactly how the real
        // capture's SafeZoneStartShrinkTime of 260.7586 decomposes (60 + 200).
        gameState.SafeZonesStartTime =
            now + (EnvFloat("SAFEZONE_START_DELAY", StartDelaySeconds) + Phases[1].WaitSeconds) / TimeScale;

        _phaseIndex = 1;
        _phaseChangeTime = gameState.SafeZonesStartTime;
        _shrinking = false;

        Console.WriteLine($"FortSafeZoneSystem: storm armed - centre {centre} radius {radius}, " +
                          $"first shrink at t={gameState.SafeZonesStartTime:F1}s toward radius {nextRadius}");
        return indicator;
    }

    private static void AdvancePhase(AGameState gameState, AFortSafeZoneIndicator indicator, float now) {
        if (_phaseIndex >= Phases.Length || now < _phaseChangeTime) return;

        if (!_shrinking) {
            // The wait is over: start closing toward the circle the map has been previewing.
            var phase = Phases[_phaseIndex];
            var target = indicator.NextNextCenter;
            var targetRadius = indicator.NextNextRadius;

            var shrinkSeconds = phase.ShrinkSeconds / TimeScale;
            indicator.BeginShrink(target, targetRadius, now, now + shrinkSeconds);

            // And immediately pick the one AFTER it, so the preview circle is never empty.
            var (afterCentre, afterRadius) = PickNext(target, targetRadius, _phaseIndex + 1);
            indicator.NextNextCenter = afterCentre;
            indicator.NextNextRadius = afterRadius;

            _shrinking = true;
            _phaseChangeTime = now + shrinkSeconds;

            Console.WriteLine($"FortSafeZoneSystem: phase {_phaseIndex} closing to radius {targetRadius} " +
                              $"at {target} over {shrinkSeconds:0.0}s " +
                              $"({phase.DamageFraction:P0} of max health per second outside)");
            return;
        }

        // The shrink finished. Hold at the new circle until the next phase's wait elapses.
        _shrinking = false;
        _phaseIndex++;
        gameState.SafeZonePhase = (byte) Math.Min(byte.MaxValue, _phaseIndex);

        if (_phaseIndex >= Phases.Length) {
            Console.WriteLine("FortSafeZoneSystem: final circle reached, storm is done closing");
            return;
        }

        var waitSeconds = Phases[_phaseIndex].WaitSeconds / TimeScale;
        _phaseChangeTime = now + waitSeconds;
        Console.WriteLine($"FortSafeZoneSystem: phase {_phaseIndex} holding at radius {indicator.NextRadius} " +
                          $"for {waitSeconds:0.0}s");
    }

    /// <summary>
    ///     Where the next circle goes: a random point far enough inside the current one that the new
    ///     circle is fully contained, which is the one rule real Fortnite's circles do obey. Seeded
    ///     (SAFEZONE_SEED) so a match is reproducible - a storm that moves differently every run is
    ///     not something a single tester can chase a bug through.
    /// </summary>
    private static (FVector Centre, float Radius) PickNext(FVector centre, float radius, int phaseIndex) {
        var nextRadius = phaseIndex < Phases.Length ? Phases[phaseIndex].Radius : 0f;
        var drift = MathF.Max(0f, radius - nextRadius);

        var angle = (float) (Rng.NextDouble() * System.Math.PI * 2.0);
        var distance = (float) (Rng.NextDouble() * drift);

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
        if (now < _nextDamageTime) return;
        _nextDamageTime = now + DamageIntervalSeconds;

        var phase = _phaseIndex < Phases.Length ? Phases[_phaseIndex] : Phases[^1];
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
