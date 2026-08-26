#!/usr/bin/env python
"""Cross-check NativeRepLayouts.cs handle numbering against Tools/RepHandles/rep_handles.py.

A single missing or extra Reserved() entry silently shifts every later handle and misdecodes every
property after it on the client, with no error raised on either side - the same failure mode
Tools/NetFieldVerify guards against for ClassNetCache. Run both after touching either table.

Usage: python Tools/RepHandles/verify_cs_handles.py
Exits non-zero on a mismatch.
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
]

ENTRY_RE = re.compile(
    r'Reserved\("([^"]+)"'
    r'|Name = "([^"]+)",[^\n]*\n\s*Kind = ERepPropertyKind\.(\w+)')


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
    return got.split(".")[0] == exp.split(".")[0]


def main():
    src = io.open(CS, encoding="utf-8-sig").read()
    bad = 0
    for var, prefixes, ue_class in TABLES:
        names = []
        for p in prefixes:
            names += cs_names(src, p)
        names += cs_names(src, var)

        lines = subprocess.run([sys.executable, "Tools/RepHandles/rep_handles.py", ue_class],
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
