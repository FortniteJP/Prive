using AFortOnlineBeacon.Core.Math;
using AFortOnlineBeacon.Net.Actors;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     Where a dropped item actually comes to rest, by simulating the toss the real server simulates.
///
///     THE GAP THIS FILLS. `SpawnDroppedPickup` put every dropped item at a fixed offset in front of
///     the player - `origin + forward * TossDistance`, at `origin.Z + TossHeight` - and left it
///     hanging there. Its own comment said so: "Real UE tosses the item along an arc; with no toss to
///     simulate, the least this server can do is not bury the pickup in the pawn's own capsule."
///     Standing on a ramp, at the edge of a build, or on any slope, the item floated or sank into
///     geometry, and a pickup the client's interaction query cannot reach is a pickup that is gone.
///
///     THE REAL SERVER SIMULATES THESE, and it is not a footnote - it is nine tenths of all the
///     physics it does. Of the 0906 PR3.0 capture's `LogProjectileMovement` lines, 279,874 are
///     `FortPickupAthena`: dropped items falling, bouncing and coming to rest, substep by substep,
///     through the same UProjectileMovementComponent that carries a grenade.
///
///     EVERY CONSTANT BELOW WAS MEASURED FROM THAT CAPTURE by Tools/ProjectileReplay, not chosen.
///     See the tool's own docstrings for how each is recovered and how well it fits.
///
///     WHAT THIS DOES NOT DO: stream the arc. The real server replicates the pickup's position every
///     substep while it flies; this solves the whole trajectory at the moment of the drop and
///     publishes only where it stopped. The visible difference is an item that appears at its resting
///     place instead of being watched into it - which is the same bargain the flight animation makes
///     in the other direction, and it is worth being explicit that it is a bargain. The simulation
///     itself is the real one, so making it stream later is a matter of feeding these steps to a
///     tick rather than of getting the physics right afterwards.
/// </summary>
internal static class FortPickupToss {
    /// <summary>
    ///     Fortnite's world gravity times FortPickupAthena's ProjectileGravityScale, in uu/s^2.
    ///
    ///     MEASURED at 2800.0 (IQR 16.8 over 67 free-flight runs). The same tool reads 2800.0 off
    ///     `CBGA_GreenGlop_WithGrav_C` (IQR 4.2, n=877), 2243.2 off the frag grenade, 839.8 off the
    ///     grenade launcher and 560.0 off the ostrich drop - **all multiples of 280**. Against UE's
    ///     default 980 those are 2.857, 2.289, 0.857 and 0.571; against 1400 they are 2.0, 1.6, 0.6
    ///     and 0.4. So the world gravity is -1400 and these are round scales, which also confirms
    ///     FortProjectileSystem's own 2800 x 0.8 = 2240 for the grenade against an independent
    ///     measurement of 2243.2.
    /// </summary>
    private static float Gravity =>
        float.TryParse(Environment.GetEnvironmentVariable("PICKUP_GRAVITY"), out var g) && g > 0 ? g : 2800f;

    /// <summary>
    ///     The MaxSpeed clamp, in uu/s. MEASURED at 503.7 (IQR 12.2 over 332 clamped substeps) -
    ///     recognised as steps where the velocity's DIRECTION turned while its LENGTH did not, which
    ///     only a clamp does. A dropped item is a slow, short toss, not a throw.
    /// </summary>
    private static float MaxSpeed =>
        float.TryParse(Environment.GetEnvironmentVariable("PICKUP_MAX_SPEED"), out var s) && s > 0 ? s : 500f;

    /// <summary>
    ///     Restitution. MEASURED at 0.534 (IQR 0.071) across 22 bounces off WALLS - the ones whose
    ///     surface normal is horizontal, where gravity plays no part and so neither does any error in
    ///     correcting for it. The all-surfaces median is a wider 0.600, and floor bounces are the
    ///     reason: their answer depends on adding back exactly the right amount of gravity over the
    ///     post-bounce sub-step. The wall figure is the one to believe.
    /// </summary>
    private static float Bounciness =>
        float.TryParse(Environment.GetEnvironmentVariable("PICKUP_BOUNCINESS"), out var b) && b >= 0 ? b : 0.534f;

    /// <summary>
    ///     Tangential loss per bounce, UE's Friction. THE LEAST CERTAIN NUMBER HERE, and worth saying
    ///     so: the fit is bimodal (median 0.113 with an IQR of 0.500), because 45,182 of the capture's
    ///     pickup bounces are on surfaces that match no axis and are refused rather than forced. An
    ///     earlier, looser pass over the same data gave 0.544 with an IQR of 0.002. 0.5 sits between
    ///     them and is the one constant to reach for first if settled items slide too far or too
    ///     little.
    /// </summary>
    private static float BounceFriction =>
        float.TryParse(Environment.GetEnvironmentVariable("PICKUP_FRICTION"), out var f) && f >= 0 ? f : 0.5f;

    /// <summary>
    ///     The substep, in seconds. The capture's every projectile line reads `step 0.033`, which is
    ///     a 30 Hz fixed substep - the same one this has to use, because a bounce's outcome depends
    ///     on where in the step it happened.
    /// </summary>
    private const float SubStep = 1f / 30f;

    /// <summary>
    ///     Below this speed the item is at rest. UE calls it BounceVelocityStopSimulatingThreshold;
    ///     FortProjectileSystem has the same idea and the same reason - without it a pickup on a
    ///     floor jitters against it forever and the loop below never terminates early.
    /// </summary>
    private const float StopSpeed = 20f;

    /// <summary>
    ///     A ceiling on the simulation, in substeps. Ten seconds. A toss settles in well under one,
    ///     so reaching this means the item found somewhere to fall for ever - off the map, or through
    ///     a hole in the baked collision - and the answer is to stop and use where it got to rather
    ///     than to spin. See the note about degrading in [[feedback-guards-degrade-dont-refuse]].
    /// </summary>
    private const int MaxSubSteps = 300;

    /// <summary>
    ///     Simulates the toss and returns where the item settles.
    ///
    ///     Synchronous on purpose - see the class comment. The three collision sources are the same
    ///     three FortProjectileSystem sweeps against, in the same order and for the same reasons:
    ///     player builds, the game's own convex hulls, and the baked walls for meshes that ship no
    ///     hulls.
    /// </summary>
    public static FVector Settle(FVector from, FVector velocity) {
        var position = from;
        var v = Clamp(velocity);
        var bounces = 0;

        for (var step = 0; step < MaxSubSteps; step++) {
            v = new FVector { X = v.X, Y = v.Y, Z = v.Z - Gravity * SubStep };
            v = Clamp(v);

            var next = new FVector {
                X = position.X + v.X * SubStep,
                Y = position.Y + v.Y * SubStep,
                Z = position.Z + v.Z * SubStep
            };

            var contact = FirstContact(position, next);
            if (contact == null) {
                position = next;
                continue;
            }

            var (point, normal) = contact.Value;
            bounces++;

            // Back off ALONG THE NORMAL so the next step starts outside the surface rather than
            // exactly on it, where floating point puts it back inside - the same half-unit
            // FortProjectileSystem uses, and for the failure it was added to stop.
            position = new FVector {
                X = point.X + normal.X * 0.5f,
                Y = point.Y + normal.Y * 0.5f,
                Z = point.Z + normal.Z * 0.5f
            };

            // UE's ComputeBounceResult: the component ALONG the normal is reversed and scaled by
            // Bounciness, everything perpendicular to it is scaled by (1 - Friction). Measuring that
            // rule is what Tools/ProjectileReplay does, so this is the same equation read backwards.
            var approach = v.X * normal.X + v.Y * normal.Y + v.Z * normal.Z;
            var tangentScale = 1f - BounceFriction;

            v = new FVector {
                X = (v.X - normal.X * approach) * tangentScale - normal.X * approach * Bounciness,
                Y = (v.Y - normal.Y * approach) * tangentScale - normal.Y * approach * Bounciness,
                Z = (v.Z - normal.Z * approach) * tangentScale - normal.Z * approach * Bounciness
            };

            if (Speed(v) < StopSpeed) {
                Console.WriteLine($"FortPickupToss: settled at {position} after {step + 1} substep(s), " +
                                  $"{bounces} bounce(s)");
                return position;
            }
        }

        Console.WriteLine($"FortPickupToss: gave up after {MaxSubSteps} substeps at {position} - the item " +
                          "never came to rest, so this is where it got to rather than where it belongs");
        return position;
    }

    private static (FVector Point, FVector Normal)? FirstContact(FVector from, FVector to) {
        var buildHit = BuildingStructuralSupportSystem.SweepToBuild(from, to);
        var hullHit = WorldCollision.Sweep(from, to);
        var wallHit = TerrainWalls.Sweep(from, to);

        (FVector Point, FVector Normal)? best = null;
        var bestDistance = float.MaxValue;

        void Consider(FVector point, FVector normal) {
            var dx = point.X - from.X;
            var dy = point.Y - from.Y;
            var dz = point.Z - from.Z;
            var distance = dx * dx + dy * dy + dz * dz;
            if (distance >= bestDistance) return;

            bestDistance = distance;
            best = (point, normal);
        }

        if (buildHit is { } b) Consider(b.Point, AxisNormal(b.Axis, to, from));
        if (hullHit is { } h) Consider(h.Point, h.Normal);
        if (wallHit is { } w) Consider(w.Point, AxisNormal(w.Axis, to, from));

        return best;
    }

    /// <summary>
    ///     A box sweep reports which AXIS it entered through, not a normal. The normal is that axis
    ///     pointing back the way the item came - the same conversion FortProjectileSystem does so
    ///     that one bounce rule can serve all three collision sources.
    /// </summary>
    private static FVector AxisNormal(int axis, FVector to, FVector from) {
        var sign = axis switch {
            0 => to.X > from.X ? -1f : 1f,
            1 => to.Y > from.Y ? -1f : 1f,
            _ => to.Z > from.Z ? -1f : 1f
        };

        return axis switch {
            0 => new FVector { X = sign },
            1 => new FVector { Y = sign },
            _ => new FVector { Z = sign }
        };
    }

    private static float Speed(FVector v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    private static FVector Clamp(FVector v) {
        var speed = Speed(v);
        if (speed <= MaxSpeed || speed <= 0f) return v;

        var k = MaxSpeed / speed;
        return new FVector { X = v.X * k, Y = v.Y * k, Z = v.Z * k };
    }
}
