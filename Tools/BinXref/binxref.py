#!/usr/bin/env python
"""Static xref/disassembly helper for the shipping Fortnite client.

Usage:
  binxref.py str  <substring>            list UTF-16LE (and ASCII) strings containing <substring>
  binxref.py xref <hex-va>               find `lea reg,[rip+disp]` sites that point at <va>
  binxref.py dis  <hex-va> [count]       disassemble <count> instructions from <va>
  binxref.py fn   <hex-va> [maxbytes]    find the enclosing function start and disassemble it
"""
import sys, re, struct
import pefile, capstone

EXE = r"C:\Users\user\Documents\10.40\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"

_pe = None
def pe():
    global _pe
    if _pe is None:
        _pe = pefile.PE(EXE, fast_load=True)
    return _pe

def image_base():
    return pe().OPTIONAL_HEADER.ImageBase

def sections():
    out = []
    for s in pe().sections:
        name = s.Name.rstrip(b"\x00").decode("latin1")
        out.append((name, image_base() + s.VirtualAddress, s.get_data()))
    return out

def sec_for(va):
    for name, base, data in sections():
        if base <= va < base + len(data):
            return name, base, data
    return None

def read(va, n):
    r = sec_for(va)
    if not r: return None
    _, base, data = r
    return data[va - base: va - base + n]

def cmd_str(sub):
    needle16 = sub.encode("utf-16-le")
    needle8 = sub.encode("latin1")
    for name, base, data in sections():
        if name not in (".rdata", ".data", ".text"): continue
        for needle, kind in ((needle16, "utf16"), (needle8, "ascii")):
            start = 0
            while True:
                i = data.find(needle, start)
                if i < 0: break
                start = i + 1
                # walk back to string start
                if kind == "utf16":
                    j = i
                    while j >= 2 and data[j-2:j] != b"\x00\x00" and 0x20 <= data[j-2] < 0x7f and data[j-1] == 0:
                        j -= 2
                    k = i
                    while k + 2 <= len(data) and data[k:k+2] != b"\x00\x00":
                        k += 2
                    txt = data[j:k].decode("utf-16-le", "replace")
                else:
                    j = i
                    while j > 0 and 0x20 <= data[j-1] < 0x7f: j -= 1
                    k = i
                    while k < len(data) and 0x20 <= data[k] < 0x7f: k += 1
                    txt = data[j:k].decode("latin1")
                    if len(txt) < 5: continue
                print(f"{kind:5} {name:8} 0x{base + j:X}  {txt[:160]!r}")

LEA_RE = re.compile(rb"(?:\x48|\x4C|\x49|\x4D)\x8D[\x05\x0D\x15\x1D\x25\x2D\x35\x3D]", re.S)

def cmd_xref(va):
    for name, base, data in sections():
        if name != ".text": continue
        for m in LEA_RE.finditer(data):
            off = m.start()
            if off + 7 > len(data): continue
            disp = struct.unpack_from("<i", data, off + 3)[0]
            nxt = base + off + 7
            if nxt + disp == va:
                print(f"lea @ 0x{base + off:X}")

def disasm(va, count):
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = False
    buf = read(va, count * 16 + 64)
    n = 0
    for ins in md.disasm(buf, va):
        print(f"0x{ins.address:X}  {ins.mnemonic:<8} {ins.op_str}")
        n += 1
        if n >= count: break

def find_fn_start(va, back=0x2000):
    """Scan back for int3 padding followed by a plausible prologue."""
    r = sec_for(va)
    if not r: return None
    _, base, data = r
    off = va - base
    lo = max(0, off - back)
    best = None
    for i in range(off, lo, -1):
        # aligned target right after CC padding
        if data[i-1] == 0xCC and data[i-2] == 0xCC:
            best = base + i
            break
    return best

def main():
    if len(sys.argv) < 3: print(__doc__); return
    cmd = sys.argv[1]
    if cmd == "str": cmd_str(sys.argv[2])
    elif cmd == "xref": cmd_xref(int(sys.argv[2], 16))
    elif cmd == "dis": disasm(int(sys.argv[2], 16), int(sys.argv[3]) if len(sys.argv) > 3 else 60)
    elif cmd == "fn":
        s = find_fn_start(int(sys.argv[2], 16))
        print(f"; function start guess: 0x{s:X}")
        disasm(s, int(sys.argv[3]) if len(sys.argv) > 3 else 200)
    else: print(__doc__)

main()
