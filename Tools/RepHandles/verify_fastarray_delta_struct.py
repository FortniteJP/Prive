#!/usr/bin/env python
"""Cross-check the DELTA-STRUCT fast array item writers against the 10.40 SDK.

FFastArraySerializerWriter's Write*DeltaStruct methods put a hand-written handle in front of every
member of a fast array item. Those handles are FRepLayout's, for the array's INNER struct, and
`rep_handles.py <Struct> --struct` is the tool of record for them - this script just makes sure the
C# still agrees with it.

WHY IT MATTERS MORE HERE THAN ANYWHERE ELSE. An item's handle stream has no resync point and the
client checks only ONE thing about it: that it ends on handle 0. A skipped or duplicated handle
shifts every value after it, the terminator lands in the middle of a value, and the client logs
"ReceiveFastArrayItem: Invalid property terminator handle - Handle=<garbage>" and abandons the
array - which on the AbilitySystemComponent means the player has no abilities at all, for the rest
of the match. That exact line is what started this whole piece of work; see [[fastarray-delta-latch]].

What it checks:
  * the top-level handles a writer emits are exactly 1, 2, ... N with nothing missing or repeated;
  * N equals the number of handles the SDK says that struct has;
  * where an item contains an array OF STRUCTS, the per-element divisor the C# multiplies by is the
    SDK's handle count for the element struct (get this wrong and every element after the first is
    renumbered - it does not lose a member, it corrupts the lot).

Usage: python Tools/RepHandles/verify_fastarray_delta_struct.py
Exits non-zero on a mismatch.
"""
import io
import os
import re
import subprocess
import sys

CS = "AFortOnlineBeacon/Net/Replication/FFastArraySerializerWriter.cs"
REP_HANDLES = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rep_handles.py")

# (C# method, the UE struct it writes one element of)
WRITERS = [
    ("WriteAbilitySpecDeltaStruct", "FGameplayAbilitySpec"),
    ("WriteActiveGameplayEffectDeltaStruct", "FActiveGameplayEffect"),
    ("WriteItemEntryDeltaStruct", "FFortItemEntry"),
    ("WriteActiveGameplayCueDeltaStruct", "FActiveGameplayCue"),
]

# (C# method, element struct, the `* N` the method must use for it). Only arrays whose element is a
# multi-handle struct need one: a one-handle element is written as `(uint) index + 1` with no
# multiplier, and 1 is the only divisor that can be spelled that way.
ELEMENT_DIVISORS = [
    ("WriteActiveGameplayEffectDeltaStruct", "FGameplayEffectModifiedAttribute"),
]


def sdk_handles(struct):
    """The handle list rep_handles.py derives for one struct's element layout."""
    out = subprocess.run(
        [sys.executable, REP_HANDLES, struct, "--struct"],
        capture_output=True, text=True, check=True).stdout
    return [line for line in out.splitlines() if line.strip()]


def method_body(source, name):
    """The text of one C# method, from its signature to the closing brace at its own indent."""
    start = source.index(f" {name}(")
    start = source.rindex("\n", 0, start) + 1
    end = source.index("\n    }\n", start)
    return source[start:end]


def main():
    source = io.open(CS, encoding="utf-8-sig").read()
    failures = 0

    for method, struct in WRITERS:
        body = method_body(source, method)

        # Only LITERAL handles are top level. A handle computed from a loop variable is inside an
        # array element, which has its own handle space and is checked by the divisor rule below.
        written = [int(n) for n in re.findall(r"(?:WriteHandle|BeginArray)\(payload,\s*(\d+)[,)]", body)]
        expected = sdk_handles(struct)

        if written != list(range(1, len(written) + 1)):
            print(f"FAIL {method}: handles are not 1..N in order - {written}")
            failures += 1
        elif len(written) != len(expected):
            print(f"FAIL {method}: writes {len(written)} handles, {struct} has {len(expected)}")
            for line in expected[len(written):]:
                print(f"       missing: {line}")
            failures += 1
        else:
            print(f"ok   {method}: {len(written)} handles == {struct}")

    for method, element in ELEMENT_DIVISORS:
        body = method_body(source, method)
        count = len(sdk_handles(element))

        if f"* {count};" in body or f"* {count}\n" in body:
            print(f"ok   {method}: element divisor {count} == {element}")
        else:
            print(f"FAIL {method}: {element} has {count} handles per element, "
                  f"but the method never multiplies by {count}")
            failures += 1

    print("\nAll delta-struct item writers agree with the SDK." if failures == 0
          else f"\n{failures} mismatch(es).")
    return 1 if failures else 0


sys.exit(main())
