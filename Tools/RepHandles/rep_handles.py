#!/usr/bin/env python
"""Derive FRepLayout handle numbers for a class from the Dumper-7 10.40 SDK.

Reproduces, from the real UE 4.23 source:
  * UClass::SetUpRuntimeReplicationData - ClassReps = Super's ClassReps, then this class's OWN
    CPF_Net properties sorted by FCompareUFieldOffsets (offset ascending, name as tie-break),
    each contributing ArrayDim entries.
  * FRepLayout::InitFromClass + InitFromProperty_r (RepLayout.cpp:4586) - one handle per leaf cmd;
    a TArray is ONE handle (its inner cmds live in a nested handle space); a UStructProperty is
    ONE handle only if the struct is STRUCT_NetSerializeNative, otherwise it recurses into ALL of
    its non-RepSkip members (not just the Net ones), sorted by the same comparator.

Usage:
    python Tools/RepHandles/rep_handles.py <ClassName> [--sdk DIR] [--from N] [--to N]
"""
import os
import re
import sys
import argparse
from functools import lru_cache

DEFAULT_SDK = r"C:/Dumper-7/4.23.0-9380822+++Fortnite+Release-10.40-FortniteGame/CppSDK/SDK"

# Structs whose UScriptStruct carries STRUCT_NetSerializeNative (TStructOpsTypeTraits<T>::
# WithNetSerializer). These become a single handle instead of recursing. Engine entries were read
# out of the 4.23 source; Fortnite entries are the ones RepLayout demonstrably treats as atomic.
ATOMIC_STRUCTS = {
    "FVector_NetQuantize", "FVector_NetQuantize10", "FVector_NetQuantize100",
    "FVector_NetQuantizeNormal", "FRotator", "FVector", "FQuat", "FPlane",
    "FUniqueNetIdRepl", "FRepMovement", "FGameplayTag", "FGameplayTagContainer",
    "FGameplayAbilityTargetDataHandle", "FGameplayEffectContextHandle",
    "FPredictionKey", "FGameplayCueParameters", "FGameplayAbilitySpecHandle",
    "FGameplayEffectSpecHandle", "FMinimalGameplayCueReplicationProxy",
    "FFastArraySerializer", "FFloatRange", "FInt32Range",
    # --------------------------------------------------------------------------------------
    # Everything below was found by MACHINE-SCANNING the real UE 4.23 source for
    # `TStructOpsTypeTraits<X>` bodies containing `WithNetSerializer = true`, after the hand-written
    # list above turned out to be missing one:
    #
    #   FRootMotionSourceGroup (RootMotionSource.h:865). Recursing into its 5 members instead of
    #   treating it as one handle put every ACharacter/AFortPawn handle after it 4 too high, and a
    #   real client dropped the connection over it - "ReceiveProperties_r: Failed to receive
    #   property, BunchIsError - Property=LocalSpin, Parent=43, Cmd=65, ReadHandle=66", i.e.
    #   LocalSpin at 66 where this script said 70.
    #
    # One unlisted NetSerializer struct silently shifts every property after it, so this half of
    # the list is derived, not remembered. Note the scan is a LOWER bound: the core math types
    # (FVector/FRotator/FQuat/FPlane) get STRUCT_NetSerializeNative without a traits body the scan
    # can see, and are kept above on the strength of the live-probed AActor handles that depend on
    # them (ReplicatedMovement, AttachmentReplication.*).
    "FRootMotionSourceGroup", "FRootMotionSource", "FRootMotionSource_ConstantForce",
    "FRootMotionSource_JumpForce", "FRootMotionSource_MoveToDynamicForce",
    "FRootMotionSource_MoveToForce", "FRootMotionSource_RadialForce",
    "FHitResult",
    "FGameplayAbilityTargetData_ActorArray", "FGameplayAbilityTargetData_LocationInfo",
    "FGameplayAbilityTargetData_SingleTargetHit", "FGameplayAbilityTargetingLocationInfo",
    "FGameplayEffectContext", "FMinimalReplicationTagCountMap", "FNetQuantizeFaceCurve",
    "FMcpVariantChannelInfo", "FGameplayAbilityTargetData",
}

PROP_RE = re.compile(
    r"^\s+(?P<type>.+?)\s+(?P<name>\w+)"
    r"(?:\s*\[\s*(?P<dim>0x[0-9A-Fa-f]+|\d+)\s*\])?"
    r"(?P<bitfield>\s*:\s*1)?;"
    r"\s*//\s*0x(?P<off>[0-9A-Fa-f]+)\(0x(?P<size>[0-9A-Fa-f]+)\)\((?P<flags>.*)\)\s*$")

def has_flag(flags, name):
    """Dumper-7 glues the first flag of a bitfield onto "PropSize: 0x0001 (", so an exact token
    match silently drops e.g. AActor::bTearOff. Match on word boundaries instead."""
    return re.search(r"(?<![A-Za-z0-9_])" + name + r"(?![A-Za-z0-9_])", flags) is not None


# Dumper-7 emits both "struct alignas(0x08) F..." and "class SDK_ALIGN(0x08) A..."; missing the
# latter silently truncates a class chain (it cost AFortPlayerControllerGameplay once).
CLASS_RE = re.compile(r"^(?:class|struct)\s+(?:\w+\([^)]*\)\s+)?(\w+)(?:\s+final)?\s*(?::\s*public\s+(\w+))?\s*$")


class Sdk:
    def __init__(self, root):
        self.root = root
        self.types = {}          # name -> dict(super=..., props=[...])
        self._load()

    def _load(self):
        for fn in os.listdir(self.root):
            if not (fn.endswith("_classes.hpp") or fn.endswith("_structs.hpp")):
                continue
            with open(os.path.join(self.root, fn), "r", encoding="utf-8", errors="replace") as f:
                lines = f.read().splitlines()
            cur = None
            for line in lines:
                m = CLASS_RE.match(line)
                if m:
                    cur = {"name": m.group(1), "super": m.group(2), "props": [], "file": fn}
                    self.types[m.group(1)] = cur
                    continue
                if cur is None:
                    continue
                if line.startswith("};"):
                    cur = None
                    continue
                pm = PROP_RE.match(line)
                if pm:
                    name = pm.group("name")
                    if name.startswith("Pad_") or "Fixing Size" in pm.group("flags"):
                        continue
                    dim = pm.group("dim")
                    dim = int(dim, 16) if dim and dim.startswith("0x") else int(dim) if dim else 1
                    cur["props"].append({
                        "name": name,
                        "type": pm.group("type").strip(),
                        "offset": int(pm.group("off"), 16),
                        "size": int(pm.group("size"), 16),
                        "dim": dim,
                        "flags": pm.group("flags"),
                    })

    def all_props(self, name):
        """Every property of a UStruct INCLUDING its bases - TFieldIterator walks the super chain,
        so e.g. FPlaylistPropertyArray must contribute FFastArraySerializer's fields too (they are
        all RepSkip, but other bases are not)."""
        out, cur = [], name
        while cur:
            t = self.types.get(cur)
            if t is None:
                break
            out = t["props"] + out
            cur = t["super"]
        return out

    def chain(self, cls):
        out = []
        while cls:
            t = self.types.get(cls)
            if t is None:
                sys.stderr.write(f"; WARNING: unknown type {cls}, chain truncated\n")
                break
            out.append(t)
            cls = t["super"]
        return list(reversed(out))

    def struct_name(self, type_text):
        """Return the UStruct name if this property is a (non-array, non-pointer) struct."""
        t = type_text.strip()
        if t.startswith("TArray<") or t.endswith("*") or t.startswith("TWeakObjectPtr") \
           or t.startswith("TScriptInterface") or t.startswith("TSoftObject") \
           or t.startswith("TSoftClass") or t.startswith("TMap") or t.startswith("TSet"):
            return None
        t = t.replace("class ", "").replace("struct ", "").strip()
        if t in self.types and t.startswith("F"):
            return t
        return None


def sort_key(p):
    return (p["offset"], p["name"])


def expand(sdk, prop, depth, out, path):
    """Append (label, kind) for each handle this property contributes."""
    t = prop["type"].strip()
    name = path + prop["name"]
    if t.startswith("TArray<"):
        out.append((name, "DynamicArray " + t))
        return
    sname = sdk.struct_name(t)
    if sname and sname not in ATOMIC_STRUCTS:
        members = [m for m in sdk.all_props(sname) if not has_flag(m["flags"], "RepSkip")]
        members.sort(key=sort_key)
        if not members:
            out.append((name, f"struct {sname} (no members parsed - VERIFY)"))
            return
        for m in members:
            for j in range(m["dim"]):
                sub = f"{name}." if m["dim"] == 1 else f"{name}."
                idx = "" if m["dim"] == 1 else f"[{j}]"
                expand(sdk, dict(m, name=m["name"] + idx), depth + 1, out, sub)
        return
    if sname:
        out.append((name, f"atomic struct {sname}"))
        return
    out.append((name, t))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cls")
    ap.add_argument("--sdk", default=DEFAULT_SDK)
    ap.add_argument("--from", dest="lo", type=int, default=1)
    ap.add_argument("--to", dest="hi", type=int, default=10**9)
    ap.add_argument("--grep", default=None, help="only print handles whose label matches")
    a = ap.parse_args()

    sdk = Sdk(a.sdk)
    handles = []
    for t in sdk.chain(a.cls):
        own = [p for p in t["props"] if has_flag(p["flags"], "Net")]
        own.sort(key=sort_key)
        for p in own:
            for j in range(p["dim"]):
                idx = "" if p["dim"] == 1 else f"[{j}]"
                out = []
                expand(sdk, dict(p, name=p["name"] + idx), 0, out, "")
                for label, kind in out:
                    handles.append((t["name"], label, kind, p["offset"]))

    for i, (owner, label, kind, off) in enumerate(handles, start=1):
        if i < a.lo or i > a.hi:
            continue
        if a.grep and a.grep.lower() not in label.lower():
            continue
        print(f"{i:4}  0x{off:04X}  {owner:<28} {label:<52} {kind}")


main()
