#!/usr/bin/env python3
"""Verify AFortOnlineBeacon's NativeClassNetCache tables against the real 10.40 Dumper-7 SDK.

NativeClassNetCache is a hand-maintained list of each class's OWN net fields (CPF_Net properties +
FUNC_Net functions). FClassNetCacheMgr assigns FieldNetIndex by walking those name-sorted, per class,
up the inheritance chain - so ONE missing or extra entry silently shifts every index after it and
misdecodes every later RPC on that actor, with no error raised anywhere. That failure mode is
invisible in a log, which is why it is worth checking mechanically.

Usage (from the repo root):
    python Tools/NetFieldVerify/verify_net_fields.py [path-to-Dumper-7-SDK]

Exits non-zero if any table disagrees with the SDK.
"""
import os
import re
import sys

DEFAULT_SDK = r"C:/Dumper-7/4.23.0-9380822+++Fortnite+Release-10.40-FortniteGame/CppSDK/SDK"
CACHE = "AFortOnlineBeacon/Net/NativeClassNetCache.cs"

# A Dumper-7 property line: "  <type> <Name>;  // 0xOFF(0xSIZE)(Flags...)", optionally a C-array
# ("Name[0x6]") or a bitfield ("Name : 1").
PROP_RE = re.compile(
    r"\s(\w+)\s*(?:\[\s*0x[0-9A-Fa-f]+\s*\])?\s*(?::\s*1\s*)?;"
    r"\s*//\s*0x[0-9A-Fa-f]+\(0x[0-9A-Fa-f]+\)\((.*)\)\s*$")
NET_RE = re.compile(r"\bNet\b")

# (C# variable in NativeClassNetCache, UE module, UE class name, SDK file prefix)
TARGETS = [
    ("ActorOwnFields",                        "Engine",       "Actor",                        "Engine"),
    ("ControllerOwnFields",                   "Engine",       "Controller",                   "Engine"),
    ("PlayerControllerOwnFields",             "Engine",       "PlayerController",             "Engine"),
    ("PawnOwnFields",                         "Engine",       "Pawn",                         "Engine"),
    ("CharacterOwnFields",                    "Engine",       "Character",                    "Engine"),
    ("PlayerStateOwnFields",                  "Engine",       "PlayerState",                  "Engine"),
    ("FortPlayerControllerOwnFields",         "FortniteGame", "FortPlayerController",         "FortniteGame"),
    ("FortPlayerControllerGameplayOwnFields", "FortniteGame", "FortPlayerControllerGameplay", "FortniteGame"),
    ("FortPlayerControllerZoneOwnFields",     "FortniteGame", "FortPlayerControllerZone",     "FortniteGame"),
    ("FortPlayerControllerPvPOwnFields",      "FortniteGame", "FortPlayerControllerPvP",      "FortniteGame"),
    ("FortPlayerControllerAthenaOwnFields",   "FortniteGame", "FortPlayerControllerAthena",   "FortniteGame"),
    ("FortPlayerStateOwnFields",              "FortniteGame", "FortPlayerState",              "FortniteGame"),
    ("FortPlayerStateZoneOwnFields",          "FortniteGame", "FortPlayerStateZone",          "FortniteGame"),
    ("FortPlayerStateAthenaOwnFields",        "FortniteGame", "FortPlayerStateAthena",        "FortniteGame"),
    ("FortPawnOwnFields",                     "FortniteGame", "FortPawn",                     "FortniteGame"),
    ("FortPlayerPawnOwnFields",               "FortniteGame", "FortPlayerPawn",               "FortniteGame"),
    ("FortPlayerPawnAthenaOwnFields",         "FortniteGame", "FortPlayerPawnAthena",         "FortniteGame"),
    ("FortInventoryOwnFields",                "FortniteGame", "FortInventory",                "FortniteGame"),
]


def main():
    sdk = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SDK
    if not os.path.isdir(sdk):
        sys.exit("SDK directory not found: %s" % sdk)
    if not os.path.isfile(CACHE):
        sys.exit("Run this from the repo root - %s not found" % CACHE)

    files = {}

    def read(name):
        if name not in files:
            with open(os.path.join(sdk, name), encoding="utf-8", errors="replace") as fh:
                files[name] = fh.read()
        return files[name]

    def real_fields(module, cls, prefix):
        """The class's OWN net fields, exactly as SetUpRuntimeReplicationData would collect them."""
        funcs = []
        lines = read(prefix + "_functions.cpp").split("\n")
        pat = re.compile(r"// Function %s\.%s\.(\w+)$" % (re.escape(module), re.escape(cls)))
        for i, line in enumerate(lines):
            m = pat.match(line.strip())
            if m and i + 1 < len(lines):
                flags = lines[i + 1].strip()
                if flags.startswith("// (") and NET_RE.search(flags):
                    funcs.append(m.group(1))

        # Find the class by Dumper-7's own delimiter comment; the declaration itself may be
        # "class X : public Y" or "class X final : public Y".
        hpp = read(prefix + "_classes.hpp")
        marker = "// Class %s.%s\n" % (module, cls)
        start = hpp.find(marker)
        if start == -1:
            return None, None
        body = hpp[start:hpp.index("\n};", start)]

        props = []
        for line in body.split("\n"):
            m = PROP_RE.search(line)
            if not m:
                continue
            name, flags = m.group(1), m.group(2)
            if NET_RE.search(flags) and not name.startswith(("Pad_", "BitPad_")):
                props.append(name)
        return props, funcs

    with open(CACHE, encoding="utf-8-sig") as fh:
        src = fh.read()

    def ours(var):
        m = re.search(r"%s = OwnFieldsSorted\((.*?)\n    \);" % re.escape(var), src, re.S)
        if not m:
            return None
        return sorted(set(re.findall(r'"([^"]+)"', m.group(1))), key=str.lower)

    failures = 0
    for var, module, cls, prefix in TARGETS:
        mine = ours(var)
        if mine is None:
            print("?? %-42s not found in %s" % (var, CACHE))
            failures += 1
            continue

        props, funcs = real_fields(module, cls, prefix)
        if props is None:
            print("?? %-42s class %s.%s not found in the SDK" % (var, module, cls))
            failures += 1
            continue

        expected = sorted(set(props) | set(funcs), key=str.lower)
        missing = [x for x in expected if x not in mine]
        extra = [x for x in mine if x not in expected]

        if not missing and not extra:
            print("OK %-42s %3d fields (%dp + %df)" % (var, len(mine), len(props), len(funcs)))
            continue

        failures += 1
        print("!! %-42s ours=%d real=%d" % (var, len(mine), len(expected)))
        for name in missing:
            print("     MISSING  %s" % name)
        for name in extra:
            print("     EXTRA    %s" % name)

    print("\nall %d tables match the SDK" % len(TARGETS) if failures == 0
          else "\n%d table(s) disagree with the SDK" % failures)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
