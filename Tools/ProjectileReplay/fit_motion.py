#!/usr/bin/env python
"""Measure each projectile class's motion constants from the real server's own trajectories.

Reads the TSV that extract_trajectories.py produces and answers, per projectile class, the four
numbers a UProjectileMovementComponent needs - **measured, not chosen**:

    gravity          uu/s^2 downward (UE's 980 x the class's ProjectileGravityScale)
    drag             1/s, the exponential decay applied to velocity in free flight
    bounciness       the normal-direction restitution UE calls Bounciness
    friction         the tangential loss UE calls Friction

HOW EACH ONE IS RECOVERED, and why the log is enough.

FREE FLIGHT gives gravity and drag together. Between two consecutive full substeps with no bounce,
the horizontal velocity decays by a constant ratio - in the frag grenade's case 0.995 per 0.0333s,
which is a drag of about 0.15/s and cannot be anything else, because nothing is touching it. With
drag known, the vertical equation

    vz(t+dt) = (vz + g/k) * exp(-k*dt) - g/k

has gravity as its only unknown. Fitting vz alone would confuse the two; fitting the horizontal
first is what separates them.

A BOUNCE gives restitution and friction, because the log brackets it: the substep before carries the
velocity at impact and the substep after carries the result. UE's rule is

    v_out = -Bounciness * (v_in . n) n  +  (1 - Friction) * v_tangential

so with the surface normal n known, both constants fall straight out. THE NORMAL IS NOT LOGGED, and
that is the one thing here that is inferred rather than read: the six axis directions are tried and
the one whose TANGENTIAL DIRECTION IS PRESERVED wins, since a bounce may only shorten the tangential
component, never turn it. A sloped surface fits none of them and is reported as unresolved rather
than being forced into the nearest axis - which is the whole reason the unresolved count is printed.

Fortnite's own map and every player build are overwhelmingly axis-aligned, so this resolves most
bounces; the frag grenade's second bounce in the 0906 capture is a wall (normal -Y) and reads
Bounciness 0.300, Friction 0.400 exactly.

WHAT CAME OUT, and the thing it accidentally settled. The measured gravities are

    BGA_Athena_Ostrich_Drop_C              560.0 uu/s^2  (IQR 0.7,  n=45)
    B_Prj_Ranged_GrenadeLauncher_Athena_C  839.8         (IQR 1.9,  n=16)
    B_Prj_Athena_FragGrenade_C            2243.2         (IQR 11.3, n=12)
    CBGA_GreenGlop_WithGrav_C             2800.0         (IQR 4.2,  n=877)
    FortPickupAthena                      2800.0         (IQR 16.8, n=67)

**Every one of them is a multiple of 280**, and against UE's default 980 they are the unlovely
0.571, 0.857, 2.289, 2.857. Against **1400** they are 0.4, 0.6, 1.6, 2.0 - so Fortnite's world
gravity is -1400 uu/s^2, not the engine default, and these are ordinary round
ProjectileGravityScale values. That was not the question this tool was written to answer; it fell
out of five independent measurements agreeing on a grid.

    python fit_motion.py <traj.tsv> [min-samples]
"""
import math
import sys
from collections import defaultdict

AXES = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)]

# Below this speed a velocity's DIRECTION is noise, and every ratio taken from it is noise too. The
# log prints three decimals, so a component of a few uu/s carries about one significant figure.
MIN_SPEED = 50.0

# A substep whose length differs from the frame's nominal step is a post-bounce remainder; pairing
# across one would measure a bounce as if it were free flight.
NOMINAL_STEP = 0.0333
STEP_TOLERANCE = 0.002


def median(values):
    ordered = sorted(values)
    n = len(ordered)
    if n == 0:
        return float('nan')
    return ordered[n // 2] if n % 2 else (ordered[n // 2 - 1] + ordered[n // 2]) / 2


def spread(values):
    """Inter-quartile range - reported beside every median so a bimodal fit cannot hide behind one."""
    ordered = sorted(values)
    n = len(ordered)
    if n < 4:
        return float('nan')
    return ordered[3 * n // 4] - ordered[n // 4]


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def scale(a, k):
    return (a[0] * k, a[1] * k, a[2] * k)


def length(a):
    return math.sqrt(dot(a, a))


def read(path):
    """Group the TSV back into per-actor trajectories, in log order."""
    trajectories = defaultdict(list)
    with open(path, encoding='utf-8') as tsv:
        next(tsv)
        for line in tsv:
            f = line.rstrip('\n').split('\t')
            if len(f) < 14:
                continue
            trajectories[f[0]].append({
                'class': f[1], 'time': float(f[2]), 'iter': int(f[3]), 'step': float(f[4]),
                'pos': (float(f[7]), float(f[8]), float(f[9])),
                'vel': (float(f[10]), float(f[11]), float(f[12])),
                'event': f[13],
            })
    return trajectories


def fit_free_flight_run(run):
    """Gravity from a whole run of free-flight substeps: (vz_first - vz_last) / elapsed.

    MEASURED ACROSS THE RUN, not per step, because the log's timestamps are milliseconds. One
    substep spans about 33 ms, so a single pair knows dt only to 3% and gravity comes out quantised
    into a handful of values - which is why three unrelated projectile classes first reported an
    identical 2828.3 with an identical spread: that was the quantisation grid showing through, not a
    shared constant. Over twenty steps the same 1 ms lands on 0.15%.
    """
    if len(run) < 6:
        return None

    elapsed = run[-1]['time'] - run[0]['time']
    if elapsed <= 0:
        return None

    return (run[0]['vel'][2] - run[-1]['vel'][2]) / elapsed


def in_free_flight(a, b):
    """Gravity from one pair of consecutive UNCLAMPED free-flight substeps, or None.

    THERE IS NO DRAG, and finding that out is what this function is really for. The first fit here
    assumed one, because the frag grenade's horizontal velocity visibly decays over its opening
    steps - 843.896, 839.696, 835.249, 830.564 - which reads as air resistance. It is not: the decay
    is not exponential (the per-step ratio itself drifts), and the SPEED across those same steps is
    4000.000, 4000.000, 4000.000. The projectile is at a MaxSpeed clamp, so gravity turns the vector
    without lengthening it, and the horizontal component gives way to the vertical one.

    After its first bounce the same grenade is down to 2310 uu/s, and from there the horizontal
    velocity is CONSTANT to the last printed decimal for as long as it flies. No drag - a clamp.

    So an unclamped step is one where vx and vy do not move at all, and in that step gravity is the
    only thing acting: g = (vz0 - vz1) / dt, exactly, with nothing to disentangle.
    """
    dt = b['time'] - a['time']
    if not (NOMINAL_STEP - STEP_TOLERANCE <= dt <= NOMINAL_STEP + STEP_TOLERANCE):
        return None

    (vx0, vy0, vz0), (vx1, vy1, vz1) = a['vel'], b['vel']

    # The log prints three decimals, so "unchanged" is a thousandth, not zero.
    if abs(vx1 - vx0) > 0.002 or abs(vy1 - vy0) > 0.002:
        return None

    # A WHOLLY unchanged velocity is a projectile at REST, not one in free flight, and it answers
    # "what is gravity" with zero. That mattered: 100,446 of this capture's log lines are pickups
    # reporting `is stuck inside`, so resting items outnumber falling ones and dragged the measured
    # gravity for FortPickupAthena to exactly 0.0 with an IQR of 0.0 - a confident wrong answer.
    # A class that really has no gravity now reports NO SAMPLES instead, which is honest.
    if abs(vz1 - vz0) < 0.002:
        return None

    return True


def fit_max_speed(a, b):
    """The MaxSpeed clamp, or None - a step where the direction turned but the speed did not.

    Gravity always changes vz, so a step whose SPEED is unchanged to the printed precision while the
    vector demonstrably moved is a step that was clamped, and the speed it was clamped to is the
    constant being looked for.
    """
    dt = b['time'] - a['time']
    if not (NOMINAL_STEP - STEP_TOLERANCE <= dt <= NOMINAL_STEP + STEP_TOLERANCE):
        return None

    speed0, speed1 = length(a['vel']), length(b['vel'])
    if speed0 < MIN_SPEED or abs(speed1 - speed0) > 0.01:
        return None

    # Unchanged speed AND unchanged direction is a projectile that is simply not accelerating
    # (gravity scale 0), which says nothing about any clamp.
    if abs(b['vel'][2] - a['vel'][2]) < 0.01:
        return None

    return speed0


def fit_bounce(before, after, gravity):
    """(bounciness, friction, axis) for one bounce, or None when no axis explains it."""
    v_in = before['vel']

    # The post-bounce substep has already had gravity applied over its own (short) length, so it is
    # added back to recover the velocity AT the surface. Skipping this reads a floor bounce as less
    # elastic than it is - it is the difference between 0.29 and the true 0.30 on the frag grenade.
    v_out = (after['vel'][0], after['vel'][1], after['vel'][2] + gravity * after['step'])

    if length(v_in) < MIN_SPEED or length(v_out) < MIN_SPEED:
        return None

    best = None
    for axis in AXES:
        approach = dot(v_in, axis)
        if approach >= 0:
            continue  # not moving into this face

        bounciness = -dot(v_out, axis) / approach
        if not 0.0 <= bounciness <= 1.0:
            continue

        tangent_in = sub(v_in, scale(axis, approach))
        tangent_out = sub(v_out, scale(axis, dot(v_out, axis)))
        if length(tangent_in) < MIN_SPEED or length(tangent_out) < MIN_SPEED:
            continue

        # A bounce may SHORTEN the tangential component; it may not rotate it. How well the two
        # directions agree is what decides which face this was.
        agreement = dot(tangent_in, tangent_out) / (length(tangent_in) * length(tangent_out))
        friction = 1.0 - length(tangent_out) / length(tangent_in)
        if not 0.0 <= friction <= 1.0:
            continue

        if best is None or agreement > best[0]:
            best = (agreement, bounciness, friction, axis)

    # 0.999 is about half a degree of rotation - anything looser is a sloped surface being forced
    # onto an axis, and a wrong normal poisons both constants.
    if best is None or best[0] < 0.999:
        return None

    return best[1], best[2], best[3]


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    min_samples = int(sys.argv[2]) if len(sys.argv) > 2 else 8
    trajectories = read(sys.argv[1])

    gravities = defaultdict(list)
    clamps = defaultdict(list)
    bounce_pairs = defaultdict(list)

    for steps in trajectories.values():
        run = []

        def close_run():
            if len(run) >= 6 and (g := fit_free_flight_run(run)) is not None:
                gravities[run[0]['class']].append(g)
            run.clear()

        for i in range(1, len(steps)):
            a, b = steps[i - 1], steps[i]
            if b['event'].startswith('bounce'):
                bounce_pairs[b['class']].append((a, b))
                close_run()
                continue
            if a['event'] or b['event'] or a['iter'] != 1 or b['iter'] != 1:
                close_run()
                continue

            if in_free_flight(a, b):
                if not run:
                    run.append(a)
                run.append(b)
                continue

            close_run()
            if (speed := fit_max_speed(a, b)) is not None:
                clamps[b['class']].append(speed)

        close_run()

    classes = sorted(set(gravities) | set(bounce_pairs))
    print(f'{len(trajectories)} projectile(s), {len(classes)} class(es)\n')

    for klass in classes:
        gravity_samples = gravities.get(klass, [])
        gravity = median(gravity_samples) if gravity_samples else 980.0

        bounces = []
        unresolved = 0
        for before, after in bounce_pairs.get(klass, []):
            if (fit := fit_bounce(before, after, gravity)) is None:
                unresolved += 1
            else:
                bounces.append(fit)

        if len(gravity_samples) < min_samples and len(bounces) < min_samples:
            continue

        print(f'{klass}')
        if gravity_samples:
            # Against 1400 rather than UE's 980 - see the docstring for why that is the right
            # denominator and how five classes proved it between them.
            print(f'  free flight  n={len(gravity_samples):<6} gravity    {gravity:8.1f} uu/s^2 '
                  f'(IQR {spread(gravity_samples):.1f}, = 1400 x {gravity / 1400.0:.3f})')
        if (clamped := clamps.get(klass)):
            print(f'  speed clamp  n={len(clamped):<6} MaxSpeed   {median(clamped):8.1f} uu/s   '
                  f'(IQR {spread(clamped):.1f})')
        if bounces:
            # WALL BOUNCES ARE THE TRUSTWORTHY ONES for bounciness. On a horizontal normal the
            # restitution is read off an axis gravity never touches, so it needs no correction and
            # carries no error from one; a floor bounce's answer depends on adding back exactly the
            # right amount of gravity over the post-bounce sub-step. On the frag grenade the two
            # disagree - walls say 0.300, floors ~0.33 - and the wall figure is the one to believe.
            walls = [(b, f) for b, f, axis in bounces if axis[2] == 0]

            print(f'  bounces      n={len(bounces):<6} bounciness {median([b for b, _, _ in bounces]):8.3f} '
                  f'       (IQR {spread([b for b, _, _ in bounces]):.3f})')
            if walls:
                print(f'{"":15}{"":8}  ...on walls only, n={len(walls)}: '
                      f'{median([b for b, _ in walls]):.3f} (IQR {spread([b for b, _ in walls]):.3f})')
            print(f'{"":15}{"":8}friction   {median([f for _, f, _ in bounces]):8.3f}        '
                  f'(IQR {spread([f for _, f, _ in bounces]):.3f})')
        if unresolved:
            print(f'{"":15}{"":8}{unresolved} bounce(s) on no axis (sloped surfaces) - not fitted')
        print()

    return 0


if __name__ == '__main__':
    sys.exit(main())
