#!/usr/bin/env python
"""Read the DECRYPTED Fortnite code out of a process memory dump, and xref/disassemble it.

Why this exists: FortniteClient-Win64-Shipping.exe ships with its main .text section encrypted
(entropy 8.00 on disk - see Tools/BinXref/binxref.py and the fortnite-exe-text-encrypted memory
note), so nothing in it can be disassembled statically. In a RUNNING process it is decrypted, so a
full user-mode minidump of the live client contains the real code at its normal virtual addresses.
This parses the dump's Memory64List directly - no debugger, no attaching, entirely offline.

Making the dump (client sitting at the stuck loading screen):
    Task Manager -> Details -> FortniteClient-Win64-Shipping.exe -> right click -> Create dump file
  or, if that is blocked:
    procdump64 -ma FortniteClient-Win64-Shipping.exe out.dmp

Usage:
    dumpxref.py <dump.dmp> ranges                     list the memory ranges covering the exe
    dumpxref.py <dump.dmp> xref <hexVA>               find lea reg,[rip+disp] sites pointing at VA
    dumpxref.py <dump.dmp> ptr  <hexVA> [radius]      find 8-byte POINTERS to VA (UE registers
                                                      native functions through .rdata tables, not
                                                      through lea, so this is what finds them)
    dumpxref.py <dump.dmp> dis  <hexVA> [count]       disassemble from VA
    dumpxref.py <dump.dmp> fn   <hexVA> [count]       find the enclosing function start, disassemble
"""
import struct
import sys

PREFERRED_BASE = 0x140000000
TEXT_LO = 0x140001000
TEXT_HI = 0x144458000   # end of the encrypted-on-disk .text

# All addresses on the command line are STATIC (preferred-base) VAs, the ones Tools/BinXref/binxref.py
# prints from the file on disk. The live image is relocated by ASLR - in the first dump taken it sat
# at 0x7FF756610000 - so everything is translated through the slide found in ModuleListStream.


def read_streams(path):
    """{stream_type: [(size, rva)]} from the minidump stream directory."""
    with open(path, "rb") as f:
        head = f.read(32)
        if head[:4] != b"MDMP":
            sys.exit("not a minidump (missing MDMP signature)")
        num_streams, stream_dir_rva = struct.unpack_from("<II", head, 8)
        f.seek(stream_dir_rva)
        dirs = f.read(num_streams * 12)
    out = {}
    for i in range(num_streams):
        stype, dsize, drva = struct.unpack_from("<III", dirs, i * 12)
        out.setdefault(stype, []).append((dsize, drva))
    return out


def find_image_base(path, needle="FortniteClient-Win64-Shipping.exe"):
    """The relocated load address of the main module, from ModuleListStream (type 4)."""
    streams = read_streams(path)
    with open(path, "rb") as f:
        for _, rva in streams.get(4, []):
            f.seek(rva)
            count = struct.unpack("<I", f.read(4))[0]
            data = f.read(count * 108)
            for i in range(count):
                base, size_of_image, _, _, name_rva = struct.unpack_from("<QIIII", data, i * 108)
                f.seek(name_rva)
                n = struct.unpack("<I", f.read(4))[0]
                name = f.read(n).decode("utf-16-le", "replace")
                if needle.lower() in name.lower():
                    return base
    sys.exit("could not find %s in the dump's module list" % needle)


def read_dump(path):
    """Return a sorted list of (va, size, file_offset) from the Memory64ListStream."""
    with open(path, "rb") as f:
        head = f.read(32)
        if head[:4] != b"MDMP":
            sys.exit("not a minidump (missing MDMP signature)")
        num_streams, stream_dir_rva = struct.unpack_from("<II", head, 8)

        f.seek(stream_dir_rva)
        dirs = f.read(num_streams * 12)

        ranges = []
        for i in range(num_streams):
            stype, dsize, drva = struct.unpack_from("<III", dirs, i * 12)
            if stype != 9:  # Memory64ListStream
                continue
            f.seek(drva)
            count, base_rva = struct.unpack("<QQ", f.read(16))
            descs = f.read(count * 16)
            offset = base_rva
            for j in range(count):
                va, size = struct.unpack_from("<QQ", descs, j * 16)
                ranges.append((va, size, offset))
                offset += size
        if not ranges:
            sys.exit("no Memory64ListStream - this looks like a MINI dump, not a FULL one "
                     "(Task Manager's 'Create dump file' makes a full one)")
        ranges.sort()
        return ranges


class Mem:
    def __init__(self, path):
        self.path = path
        self.ranges = read_dump(path)
        self.f = open(path, "rb")
        self.image_base = find_image_base(path)
        self.slide = self.image_base - PREFERRED_BASE
        sys.stderr.write("; image base 0x%X, slide 0x%X" % (self.image_base, self.slide) + chr(10))


    def live(self, static_va):
        return static_va + self.slide

    def static(self, live_va):
        return live_va - self.slide

    def read(self, va, n):
        for base, size, off in self.ranges:
            if base <= va < base + size:
                avail = min(n, base + size - va)
                self.f.seek(off + (va - base))
                return self.f.read(avail)
        return None

    def text_blocks(self):
        """(static_base, bytes) for every dumped range overlapping .text."""
        lo_live, hi_live = self.live(TEXT_LO), self.live(TEXT_HI)
        for base, size, off in self.ranges:
            if base + size <= lo_live or base >= hi_live:
                continue
            lo = max(base, lo_live)
            hi = min(base + size, hi_live)
            self.f.seek(off + (lo - base))
            yield self.static(lo), self.f.read(hi - lo)


def cmd_ptr(mem, target, radius):
    """Find every 8-byte little-endian POINTER to `target` anywhere in the dumped image.

    Code xrefs are not enough for UE. A native UFunction is not registered by instructions that
    load its name - it is registered from a STATIC TABLE of {const char* name, FNativeFuncPtr}
    pairs that the compiler emits into .rdata:

        static const FNameNativePtrPair Funcs[] = {
            { "CanJumpInternal", &ACharacter::execCanJumpInternal },
            ...

    So the way in is a DATA reference: find the qword holding the name string's address, and the
    function pointer is the qword right after it. `radius` qwords either side are printed too,
    because the neighbours are the rest of the same table - which is often the fastest way to
    confirm a hit is a real table rather than a coincidence.
    """
    needle = struct.pack("<Q", mem.live(target))
    hits = 0

    for base, size, off in mem.ranges:
        mem.f.seek(off)
        data = mem.f.read(size)
        start = 0
        while True:
            i = data.find(needle, start)
            if i < 0:
                break
            start = i + 1
            if i % 8:
                continue  # a table entry is aligned; anything else is a coincidence

            live = base + i
            print("ptr @ 0x%X (static 0x%X)" % (live, mem.static(live)))

            for k in range(-radius, radius + 1):
                o = i + k * 8
                if o < 0 or o + 8 > len(data):
                    continue
                (value,) = struct.unpack_from("<Q", data, o)
                mark = "  <== name" if k == 0 else ("  <== likely exec fn" if k == 1 else "")
                print("    [%+3d] 0x%016X   static 0x%X%s"
                      % (k, value, mem.static(value) if value > mem.slide else 0, mark))
            hits += 1

    print("%d pointer hit(s)." % hits)

def cmd_ranges(mem):
    total = 0
    lo_live, hi_live = mem.live(TEXT_LO), mem.live(TEXT_HI)
    for base, size, off in mem.ranges:
        if base + size > lo_live and base < hi_live:
            print("VA 0x%X  size 0x%X  fileoff 0x%X" % (base, size, off))
            total += size
    print("covering .text: 0x%X bytes across the ranges above" % total)


def cmd_xref(mem, target):
    import re
    LEA = re.compile(rb"[\x48\x4C\x49\x4D]\x8D[\x05\x0D\x15\x1D\x25\x2D\x35\x3D]", re.S)
    hits = 0
    for base, data in mem.text_blocks():
        for m in LEA.finditer(data):
            o = m.start()
            if o + 7 > len(data):
                continue
            disp = struct.unpack_from("<i", data, o + 3)[0]
            if base + o + 7 + disp == target:
                print("lea @ 0x%X" % (base + o))
                hits += 1
    print("%d xref(s)." % hits)


def disasm(mem, va, count):
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    buf = mem.read(mem.live(va), count * 16 + 64)
    if not buf:
        sys.exit("VA 0x%X is not in the dump" % va)
    n = 0
    for ins in md.disasm(buf, va):
        print("0x%X  %-10s %s" % (ins.address, ins.mnemonic, ins.op_str))
        n += 1
        if n >= count:
            break


def cmd_fn(mem, va, count):
    """Walk back to the int3 padding that precedes the function."""
    buf = mem.read(mem.live(va) - 0x3000, 0x3000)
    if not buf:
        sys.exit("VA 0x%X is not in the dump" % va)
    start = None
    for i in range(len(buf) - 1, 1, -1):
        if buf[i - 1] == 0xCC and buf[i - 2] == 0xCC:
            start = va - 0x3000 + i
            break
    if start is None:
        sys.exit("no int3 padding found within 0x3000 bytes before 0x%X" % va)
    print("; function start guess: 0x%X" % start)
    disasm(mem, start, count)


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return
    mem = Mem(sys.argv[1])
    cmd = sys.argv[2]
    if cmd == "ranges":
        cmd_ranges(mem)
    elif cmd == "ptr":
        cmd_ptr(mem, int(sys.argv[3], 16), int(sys.argv[4]) if len(sys.argv) > 4 else 2)
    elif cmd == "xref":
        cmd_xref(mem, int(sys.argv[3], 16))
    elif cmd == "dis":
        disasm(mem, int(sys.argv[3], 16), int(sys.argv[4]) if len(sys.argv) > 4 else 80)
    elif cmd == "fn":
        cmd_fn(mem, int(sys.argv[3], 16), int(sys.argv[4]) if len(sys.argv) > 4 else 250)
    else:
        print(__doc__)


main()
