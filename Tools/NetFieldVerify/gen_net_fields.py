#!/usr/bin/env python
"""Emit a NativeClassNetCache OwnFieldsSorted(...) block for a class, from the Dumper-7 10.40 SDK.

Same extraction rule as verify_net_fields.py (which checks the hand-written tables): a class's OWN
net fields are its CPF_Net properties plus its FUNC_Net functions. FClassNetCacheMgr::
GetClassNetCache assigns FieldNetIndex by walking those name-sorted (case-insensitive), base class
first - so one missing entry shifts every later index and silently misdecodes the wire.

Usage:  python Tools/NetFieldVerify/gen_net_fields.py <Module> <Class> [FilePrefix]
        python Tools/NetFieldVerify/gen_net_fields.py --chain <Module> <Class> [FilePrefix]
"""
import os
import re
import sys

SDK = r"C:/Dumper-7/4.23.0-9380822+++Fortnite+Release-10.40-FortniteGame/CppSDK/SDK"

PROP_RE = re.compile(
    r"\s(\w+)\s*(?:\[\s*0x[0-9A-Fa-f]+\s*\])?\s*(?::\s*1\s*)?;"
    r"\s*//\s*0x[0-9A-Fa-f]+\(0x[0-9A-Fa-f]+\)\((.*)\)\s*$")
NET_RE = re.compile(r"(?<![A-Za-z0-9_])Net(?![A-Za-z0-9_])")

_cache = {}


def read(name):
    if name not in _cache:
        path = os.path.join(SDK, name)
        if not os.path.isfile(path):
            _cache[name] = ""
        else:
            with open(path, encoding="utf-8", errors="replace") as fh:
                _cache[name] = fh.read()
    return _cache[name]


def own_fields(module, cls, prefix):
    funcs = []
    lines = read(prefix + "_functions.cpp").split("\n")
    pat = re.compile(r"// Function %s\.%s\.(\w+)$" % (re.escape(module), re.escape(cls)))
    for i, line in enumerate(lines):
        m = pat.match(line.strip())
        if m and i + 1 < len(lines):
            flags = lines[i + 1].strip()
            if flags.startswith("// (") and NET_RE.search(flags):
                funcs.append(m.group(1))

    hpp = read(prefix + "_classes.hpp")
    marker = "// Class %s.%s\n" % (module, cls)
    start = hpp.find(marker)
    if start == -1:
        return None, None, None
    body = hpp[start:hpp.index("\n};", start)]
    super_m = re.search(r"^(?:class|struct)\s+(?:\w+\([^)]*\)\s+)?\w+(?:\s+final)?\s*:\s*public\s+(\w+)",
                        body, re.M)

    props = []
    for line in body.split("\n"):
        m = PROP_RE.search(line)
        if not m:
            continue
        name, flags = m.group(1), m.group(2)
        if NET_RE.search(flags) and not name.startswith(("Pad_", "BitPad_")):
            props.append(name)
    return props, funcs, (super_m.group(1) if super_m else None)


def emit(module, cls, prefix):
    props, funcs, _ = own_fields(module, cls, prefix)
    if props is None:
        print("// !! %s.%s not found in the SDK" % (module, cls))
        return
    names = sorted(set(props) | set(funcs), key=str.lower)
    var = cls + "OwnFields"
    print("    // /Script/%s.%s - %d own net fields (%d properties + %d functions)."
          % (module, cls, len(names), len(props), len(funcs)))
    print("    private static readonly string[] %s = OwnFieldsSorted(" % var)
    line = "       "
    out = []
    for n in names:
        piece = ' "%s",' % n
        if len(line) + len(piece) > 108:
            out.append(line)
            line = "       "
        line += piece
    out.append(line)
    body = "\n".join(out).rstrip(",")
    print(body)
    print("    );")
    print()


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return
    module, cls = args[0], args[1]
    prefix = args[2] if len(args) > 2 else module
    emit(module, cls, prefix)


main()
