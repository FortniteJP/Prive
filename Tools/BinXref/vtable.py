#!/usr/bin/env python
"""Find vtables in the decrypted image and read one slot out of each.

A UFunction's exec thunk gives away the VTABLE OFFSET of the virtual it forwards to - for example
`ACharacter::execCanJumpInternal` ends in `mov rax,[rcx]` / `call [rax+0x778]`, so
`CanJumpInternal_Implementation` is at +0x778 in every class that has one. That turns "where does
AFortPlayerPawn override it" into "read +0x778 out of the right vtable", which needs no symbols at
all.

A vtable is recognised structurally: a long run of 8-aligned qwords that all point into .text.
Filtering the results by an address range - the code region a class is known to live in, found with
memberxref.py - is what picks the class out.

Usage:

    python Tools/BinXref/vtable.py <dump.dmp> <hex-slot-offset> [lo-hex] [hi-hex] [min-run]

    lo/hi   report only vtables whose slot lands in [lo, hi) - static VAs
    min-run how many consecutive .text pointers before a run counts as a vtable (default 80)
"""
import struct
import sys

sys.path.insert(0, __file__.rsplit('\\', 1)[0].rsplit('/', 1)[0])
from dumpxref import Mem, PREFERRED_BASE, TEXT_LO, TEXT_HI  # noqa: E402

RDATA_LO = 0x144458000
RDATA_HI = 0x145CA9000


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1

    dump_path = sys.argv[1]
    slot = int(sys.argv[2], 16)
    lo = int(sys.argv[3], 16) if len(sys.argv) > 3 else TEXT_LO
    hi = int(sys.argv[4], 16) if len(sys.argv) > 4 else TEXT_HI
    min_run = int(sys.argv[5]) if len(sys.argv) > 5 else 80

    mem = Mem(dump_path)

    # Pull .rdata out of the dump in one go - it is where vtables live.
    size = RDATA_HI - RDATA_LO
    data = mem.read(mem.live(RDATA_LO), size)
    if not data:
        print('could not read .rdata out of the dump')
        return 1

    print('; .rdata %d bytes, looking for slot +0x%x landing in [0x%X, 0x%X)'
          % (len(data), slot, lo, hi))

    text_lo_live, text_hi_live = mem.live(TEXT_LO), mem.live(TEXT_HI)
    count = len(data) // 8
    values = struct.unpack_from('<%dQ' % count, data, 0)

    in_text = [text_lo_live <= v < text_hi_live for v in values]

    hits = 0
    i = 0
    while i < count:
        if not in_text[i]:
            i += 1
            continue

        j = i
        while j < count and in_text[j]:
            j += 1

        run = j - i
        if run >= min_run:
            vt_static = RDATA_LO + i * 8
            index = slot // 8
            if index < run:
                value = mem.static(values[i + index])
                if lo <= value < hi:
                    print('vtable 0x%X  (%d entries)   [+0x%x] = 0x%X'
                          % (vt_static, run, slot, value))
                    hits += 1

        i = j

    print('%d vtable(s) whose slot falls in range.' % hits)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
