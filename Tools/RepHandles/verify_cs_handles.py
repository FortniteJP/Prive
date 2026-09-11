#!/usr/bin/env python
"""Cross-check NativeRepLayouts.cs handle numbering against Tools/RepHandles/rep_handles.py.

A single missing or extra Reserved() entry silently shifts every later handle and misdecodes every
property after it on the client, with no error raised on either side - the same failure mode
Tools/NetFieldVerify guards against for ClassNetCache. Run both after touching either table.

Usage: python Tools/RepHandles/verify_cs_handles.py
       python Tools/RepHandles/verify_cs_handles.py --reserved [table-or-substring]
Exits non-zero on a mismatch.

--reserved LISTS THE SILENT PROPERTIES, which is the other half of what this file is for.

A `Reserved("Name")` entry holds a handle's NUMBER without giving it a value, and that is a correct
and necessary thing to be: the number is what makes every property after it land in the right place.
What it is not is visible. Six separate features in one session did nothing because a property they
depended on was Reserved - the placement on the death screen, the building's RepairTime, the pickup's
flight, the pickup's ItemOwner, the vehicle seat's display text, and `bTearOff`, which made dead
bodies vanish on the instant. In every one of them the code read as complete and no error was raised
anywhere.

So when a feature does nothing at all, this is the first place to look:

    python Tools/RepHandles/verify_cs_handles.py --reserved PlayerState
    python Tools/RepHandles/verify_cs_handles.py --reserved            # every table

It prints each table's coverage and then the Reserved entries WITH THEIR HANDLE NUMBERS, which is
what a live client error message names. NOT a list of things to go and implement - see the note on
that question in [[afortonlinebeacon-status]]; inventing a value for a property this server has no
state for has already cost a live bug (an all-zero AttachmentReplication, seen as a 90-degree camera
roll at spawn).
"""
import io
import re
import subprocess
import sys

CS = "AFortOnlineBeacon/Net/Replication/NativeRepLayouts.cs"

# (C# table variable, tables it Concat()s onto in order, UE class the table models)
# A C# table may stop short of the SDK's full list on purpose (nothing past the last property this
# project sends needs a Cmd), so only the overlapping prefix is compared.
TABLES = [
    ("GameStateProps", ["ActorProps"], "AFortGameStateAthena"),
    ("PlayerStateProps", ["ActorProps"], "AFortPlayerStateAthena"),
    ("PlayerControllerProps", ["ActorProps", "ControllerProps"], "AFortPlayerControllerAthena"),
    ("PickupProps", ["ActorProps"], "AFortPickupAthena"),
    ("PawnProps", ["ActorProps"], "AFortPlayerPawnAthena"),
    ("WeaponProps", ["ActorProps"], "AFortWeapon"),
    # A placed piece is really an ABuildingSMActor subclass (PBWA_*_C), but every handle this table
    # covers belongs to ABuildingActor, which sits above it - checking against the derived class is
    # the stricter of the two, since it would also catch anything wrongly inserted in between.
    ("BuildingActorProps", ["ActorProps"], "ABuildingSMActor"),
    # A spray decal. Listed because its FOUR handles sit on top of BuildingActorProps' 67, so every
    # one of them moves if anything below is inserted or removed - exactly the silent renumbering
    # this script exists for. ActorProps first, then BuildingActorProps' own entries, then its own.
    ("SprayDecalProps", ["ActorProps", "BuildingActorProps"], "AFortSprayDecalInstance"),
    # A trap's TOOL: AFortWeapon's first 35 via HandlePrefix ("Table:N" = the running list cut at N
    # handles), then 36-38. Checked against the context tool, the deeper of the two, so 38 is covered.
    ("DecoToolProps", ["ActorProps", "WeaponProps:35"], "AFortDecoTool_ContextTrap"),
    # A placed trap: every building handle, 68, then ABuildingTrap's 69-73.
    ("TrapProps", ["ActorProps", "BuildingActorProps"], "ABuildingTrap"),
    ("TrapLauncherProps", ["ActorProps", "BuildingActorProps", "TrapProps"], "ATrap_Floor_Player_Launch_Pad_C"),
    ("TrapCampfireProps", ["ActorProps", "BuildingActorProps", "TrapProps"], "ATrap_Floor_Player_Campfire_C"),
    # A component, so no ActorProps. MinimalReplicationTags (28) is what a trap's reload rides.
    ("AbilitySystemComponentProps", [], "UFortAbilitySystemComponent"),
    ("VehicleSeatComponentProps", [], "UFortVehicleSeatComponent"),
    # A STRUCT, not a class: FAthenaCarPlayerSlot is the inner of UFortVehicleSeatComponent's
    # PlayerSlots, and this table is the per-element handle space. Its LENGTH is load-bearing in a
    # way no other table's is - it is the divisor in `element * len + member`, so one missing entry
    # does not lose a member, it renumbers every seat after the first.
    ("VehicleSeatProps", [], "FAthenaCarPlayerSlot", "--struct"),
]

ENTRY_RE = re.compile(
    r'Reserved\("([^"]+)"'
    # Kind may sit on the Name line (compact one-line entries) or on the next one,
    # with or without a trailing comment between them - a table must not have to be
    # formatted a particular way to be checkable.
    r'|Name = "([^"]+)",[^\n]*\s*Kind = ERepPropertyKind\.(\w+)')


def cs_names(src, var):
    """Ordered leaf names of one FRepPropertyDef[] literal. StructRecurse parents are skipped:
    they reserve no handle of their own, FRepLayout recurses straight into Children."""
    i = src.index("FRepPropertyDef[] " + var)
    # ActorProps is a plain "= { ... };" literal; the Concat()ed tables end at ").ToArray();".
    # Take whichever terminator comes first or the ActorProps scan runs on into ControllerProps.
    ends = [e for e in (src.find(").ToArray();", i), src.find(chr(10) + "    };", i)) if e != -1]
    end = min(ends)
    out = []
    for m in ENTRY_RE.finditer(src[i:end]):
        if m.group(3) == "StructRecurse":
            continue
        out.append(m.group(1) or m.group(2))
    return out


def leaf(name):
    return name.split(".")[-1].split("[")[0]


def same(got, exp):
    """The C# table sometimes names a custom-delta struct by the parent alone (e.g.
    "DelayedQuickBarActions") where the SDK derivation names the member it recursed into
    ("DelayedQuickBarActions.Items"). Same single handle either way."""
    if leaf(got) == leaf(exp):
        return True

    # THE PARENT-ONLY FALLBACK ONLY APPLIES WHEN ONE SIDE NAMES NO MEMBER. It was written for a
    # custom-delta struct the C# table names by its parent alone; letting it match when BOTH sides
    # name a member makes any two members of one struct compare equal - which is exactly how a
    # same-offset BITFIELD reorder inside FGameplayAbilityRepAnimMontage went unnoticed while the
    # server sent ForcePlayBit into bSkipPlayRate and never sent IsStopped at all.
    # A wrapper struct with a single member is the same one handle either way -
    # FGameplayAbilitySpecHandle is just `int32 Handle`, so naming it by the wrapper is exact.
    if exp == got + ".Handle":
        return True

    if "." in got and "." in exp:
        return False

    return got.split(".")[0] == exp.split(".")[0]


def cs_entries(src, var):
    """Like cs_names, but each entry is (name, is_reserved) so coverage can be reported."""
    i = src.index("FRepPropertyDef[] " + var)
    ends = [e for e in (src.find(").ToArray();", i), src.find(chr(10) + "    };", i)) if e != -1]
    end = min(ends)
    out = []
    for m in ENTRY_RE.finditer(src[i:end]):
        if m.group(3) == "StructRecurse":
            continue
        out.append((m.group(1) or m.group(2), m.group(1) is not None))
    return out


def report_reserved(src, wanted):
    """Print each table's coverage and its Reserved entries, by handle number."""
    for var, prefixes, ue_class, *_ in TABLES:
        if wanted and wanted.lower() not in var.lower() and wanted.lower() not in ue_class.lower():
            continue

        entries = []
        for p in prefixes:
            entries += cs_entries(src, p)
        entries += cs_entries(src, var)

        reserved = [(n, name) for n, (name, res) in enumerate(entries, start=1) if res]
        print("%s (%s): %d handles, %d with getters, %d Reserved"
              % (var, ue_class, len(entries), len(entries) - len(reserved), len(reserved)))

        for handle, name in reserved:
            print("    %4d  %s" % (handle, name))
        print()


def main():
    src = io.open(CS, encoding="utf-8-sig").read()

    if "--reserved" in sys.argv:
        rest = [a for a in sys.argv[1:] if a != "--reserved"]
        report_reserved(src, rest[0] if rest else None)
        return

    bad = 0
    for var, prefixes, ue_class, *extra in TABLES:
        names = []
        for p in prefixes:
            table, _, cut = p.partition(":")
            names += cs_names(src, table)
            if cut:
                names = names[:int(cut)]
        names += cs_names(src, var)

        lines = subprocess.run([sys.executable, "Tools/RepHandles/rep_handles.py", ue_class] + extra,
                               capture_output=True, text=True).stdout.splitlines()
        want = [re.match(r"\s*(\d+)\s+\S+\s+\S+\s+(\S+)", l).group(2)
                for l in lines if re.match(r"\s*\d+\s", l)]

        for n, (got, exp) in enumerate(zip(names, want), start=1):
            # AttachmentReplication is modeled as six same-named placeholders on purpose - the live
            # probe reports the parent name for all six handles, not the member names.
            if got.startswith("AttachmentReplication") and exp.startswith("AttachmentReplication"):
                continue
            if not same(got, exp):
                print("%s handle %d: C# has %r, SDK says %r" % (var, n, got, exp))
                bad += 1
        print("%s: %d C# handles vs %d SDK handles, %d mismatch(es)" % (var, len(names), len(want), bad))
    sys.exit(1 if bad else 0)


main()
