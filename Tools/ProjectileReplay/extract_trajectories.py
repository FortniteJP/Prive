#!/usr/bin/env python
"""Pull every projectile's per-substep trajectory out of a real server log.

WHY THIS EXISTS, AND WHY IT COMES FIRST. This server is about to grow a projectile simulator, and
the usual way that goes wrong is that it looks plausible: a grenade arcs, it bounces, nobody can say
whether it is RIGHT. A PR3.0 capture removes the guesswork entirely - `LogProjectileMovement` at
Verbose prints the real server's own position and velocity at every substep, on the real map, for
the real projectile classes:

    Projectile B_Prj_Athena_FragGrenade_C_2147460749:
      (Role: 3, Iteration 1, step 0.033, [0.000 / 0.033] cur/total)
      sim (Pos X=1544.056 Y=55888.668 Z=2067.150, Vel X=-843.896 Y=-3769.873 Z=-1037.253)

The 0906 capture has 319,790 of those lines. That is a better oracle than anything else in this
project has ever had for anything, so the simulator gets built against it rather than against a
description of it.

WHAT THE LOG ALREADY SETTLES, before a line of simulator is written: the real server is NOT running
a physics engine over the map. It runs `UProjectileMovementComponent` - fixed ~0.0333s substeps, a
swept move, an extra sub-iteration spending the remainder of the step after a bounce, and
`ResolvePenetration` when something ends up inside a mesh. Nine tenths of the lines are
`FortPickupAthena`: DROPPED ITEMS, which this server currently just places where they were dropped.

THREE LINE SHAPES are parsed, and all three matter:

  * `sim (Pos ..., Vel ...)`  - one substep's end state. `Iteration` counts sub-iterations within
    one frame's step, and `[cur / total]` says how much of the frame is already spent.
  * `allowing extra iteration after bounce N (t=..., adding ... secs)` - a BOUNCE, with the fraction
    of the step at which it happened. The velocity on the NEXT line is post-bounce, so a bounce is
    the one place the pre/post pair can be read off directly - which is what makes restitution and
    friction measurable rather than guessable.
  * `is stuck inside <component>!` - the real server's own failure mode, worth reproducing rather
    than improving on.

OUTPUT is one TSV row per substep, which fit_motion.py then reads:

    actor <TAB> class <TAB> time <TAB> iteration <TAB> step <TAB> cur <TAB> total <TAB>
    px py pz vx vy vz <TAB> event

`event` is empty, `bounce:<t>` on the substep FOLLOWING a bounce (so the row carries post-bounce
velocity and the fraction it happened at), or `stuck`.

    python extract_trajectories.py <FortniteGame_PR3.0Server.log> <out.tsv> [class-substring]
"""
import re
import sys

# [2026.09.06-13.07.46:554][ 62]LogProjectileMovement: Verbose: Projectile <name>: (Role: 3,
#   Iteration 1, step 0.033, [0.000 / 0.033] cur/total) sim (Pos X=.. Y=.. Z=.., Vel X=.. Y=.. Z=..)
SIM = re.compile(
    r'\[[\d.]+-(?P<h>\d\d)\.(?P<m>\d\d)\.(?P<s>\d\d):(?P<ms>\d\d\d)\]\[[^\]]*\]LogProjectileMovement: '
    r'Verbose: Projectile (?P<actor>\S+?): '
    r'\(Role: \d+, Iteration (?P<iter>\d+), step (?P<step>[\d.]+), '
    r'\[(?P<cur>[\d.]+) / (?P<total>[\d.]+)\] cur/total\) '
    r'sim \(Pos X=(?P<px>-?[\d.]+) Y=(?P<py>-?[\d.]+) Z=(?P<pz>-?[\d.]+), '
    r'Vel X=(?P<vx>-?[\d.]+) Y=(?P<vy>-?[\d.]+) Z=(?P<vz>-?[\d.]+)\)')

BOUNCE = re.compile(
    r'LogProjectileMovement: Verbose: Projectile (?P<actor>\S+?): '
    r'\([^)]*\) allowing extra iteration after bounce (?P<n>\d+) '
    r'\(t=(?P<t>[\d.]+), adding (?P<add>[\d.]+) secs\)')

STUCK = re.compile(r'LogProjectileMovement: Verbose: Projectile (?P<actor>\S+?) is stuck inside')


def class_of(actor):
    """`B_Prj_Athena_FragGrenade_C_2147460749` -> `B_Prj_Athena_FragGrenade_C`.

    The trailing number is UE's per-instance suffix, so stripping it is what groups a class's
    hundreds of instances into one population to fit against.
    """
    return re.sub(r'_\d+$', '', actor)


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    wanted = sys.argv[3] if len(sys.argv) > 3 else None

    # A bounce line comes BEFORE the substep that carries the post-bounce velocity, so it is held
    # here and attached to that actor's next sim row.
    pending = {}
    rows = 0
    bounces = 0
    stucks = 0
    actors = set()

    with open(sys.argv[1], encoding='utf-8', errors='replace') as log, \
            open(sys.argv[2], 'w', encoding='utf-8') as out:
        out.write('actor\tclass\ttime\titer\tstep\tcur\ttotal\tpx\tpy\tpz\tvx\tvy\tvz\tevent\n')

        for line in log:
            # Cheap reject first: this runs over a 550 MB log and only ~0.06% of it matches.
            if 'LogProjectileMovement' not in line:
                continue

            if (hit := BOUNCE.search(line)) is not None:
                actor = hit.group('actor')
                if wanted is None or wanted in actor:
                    pending[actor] = f"bounce:{hit.group('t')}"
                    bounces += 1
                continue

            if (hit := STUCK.search(line)) is not None:
                actor = hit.group('actor')
                if wanted is None or wanted in actor:
                    pending[actor] = 'stuck'
                    stucks += 1
                continue

            if (hit := SIM.search(line)) is None:
                continue

            actor = hit.group('actor')
            if wanted is not None and wanted not in actor:
                continue

            seconds = (int(hit.group('h')) * 3600 + int(hit.group('m')) * 60
                       + int(hit.group('s')) + int(hit.group('ms')) / 1000.0)

            actors.add(actor)
            out.write('\t'.join((
                actor, class_of(actor), f'{seconds:.3f}',
                hit.group('iter'), hit.group('step'), hit.group('cur'), hit.group('total'),
                hit.group('px'), hit.group('py'), hit.group('pz'),
                hit.group('vx'), hit.group('vy'), hit.group('vz'),
                pending.pop(actor, ''))) + '\n')
            rows += 1

    print(f'{rows} substep(s) from {len(actors)} projectile(s) -> {sys.argv[2]}')
    print(f'  {bounces} bounce(s), {stucks} stuck report(s)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
