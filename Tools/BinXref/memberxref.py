#!/usr/bin/env python
"""Find code that touches a class member, by its OFFSET, in the decrypted .text from a dump.

Without symbols, the usual anchors do not work on this binary: the game's own log strings are few,
and UE registers native functions through .rdata tables rather than through instructions that name
them. But the Dumper-7 SDK gives an exact offset for every member of every class - and an offset is
something the CODE has to spell out literally.

`AFortPlayerPawn::JumpLastActivatedTime` is at 0x11B0, so any instruction of the form
`movss [rbx+11B0h], xmm0` is jump code, and there will not be many of them. That turns "find the jump
gate in 67 MB of unnamed functions" into a search for one 4-byte displacement.

Usage:

    python Tools/BinXref/memberxref.py <dump.dmp> <hex-offset> [context-instructions]

Prints every instruction whose memory operand uses that displacement, with the enclosing function's
start where it can be found. Offsets below 0x100 are refused - small displacements appear everywhere
and the output is meaningless.
"""
import struct
import sys

import capstone

sys.path.insert(0, __file__.rsplit('\\', 1)[0].rsplit('/', 1)[0])
from dumpxref import Mem  # noqa: E402  - same directory, shares the minidump reader


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        return 1

    dump_path = sys.argv[1]
    offset = int(sys.argv[2], 16)
    context = int(sys.argv[3]) if len(sys.argv) > 3 else 6

    if offset < 0x100:
        print('offset 0x%x is too small to be distinctive - the output would be noise' % offset)
        return 1

    mem = Mem(dump_path)
    needle = struct.pack('<i', offset)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True

    hits = 0
    for base, data in mem.text_blocks():
        start = 0
        while True:
            i = data.find(needle, start)
            if i < 0:
                break
            start = i + 1

            # The displacement is the tail of the instruction, so back up a little and decode
            # forwards until an instruction actually lands on it - that filters out the constant
            # 0x11B0 appearing as immediate data or inside an unrelated encoding.
            for back in range(3, 12):
                if i - back < 0:
                    continue

                chunk = data[i - back:i - back + 16]
                try:
                    ins = next(md.disasm(chunk, base + i - back, 1))
                except StopIteration:
                    continue

                if ins.size <= back or ins.size > back + 8:
                    continue

                uses = any(op.type == capstone.x86.X86_OP_MEM and op.mem.disp == offset
                           for op in ins.operands)
                if not uses:
                    continue

                print('0x%X  %-24s %s %s' % (ins.address, ins.bytes.hex(), ins.mnemonic, ins.op_str))

                # A few instructions either side make it obvious whether this is a read, a write, or
                # a compare - which is usually enough to tell a gate from a bookkeeping update.
                for follow in md.disasm(data[i - back:i - back + 16 * context], ins.address, context):
                    if follow.address == ins.address:
                        continue
                    print('        0x%X  %s %s' % (follow.address, follow.mnemonic, follow.op_str))

                print()
                hits += 1
                break

    print('%d instruction(s) using displacement 0x%x.' % (hits, offset))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
