#!/usr/bin/env python
"""Emit FortTraps.Generated.cs - what a trap item IS, read from the paks.

WHY THIS EXISTS. A trap in 10.40 is held the way a building piece is: the item's WeaponActorClass is
a TOOL (TrapTool_C, or TrapTool_ContextTrap_Athena_C for the "one item, three surfaces" traps), and
the tool is what draws the ghost and sends the placement RPC. None of the weapon-class tables cover
them - those are built from Athena/Items/Weapons and Athena/Items/Consumables - so the equip resolved
to nothing and a trap could not even be held.

Placing one needs four more facts per item, none of them guessable:

    BlueprintClass                       the trap ACTOR to spawn (a BuildingTrap subclass)
    bAutoCreateAttachmentBuilding        placing on bare ground builds a piece under it first...
    AutoCreateAttachmentBuildingShapes   ...of these shapes (a floor for a launch pad, a floor or a
                                         wall for the damage trap)
    FloorTrap / WallTrap / CeilingTrap   a CONTEXT trap is not itself placed: it names the real
                                         per-surface item, and that item's BlueprintClass is spawned

GridPlacementOffset is carried for completeness; the client has already applied it to the location
it sends, so the server does not re-apply it.

Producing the input (the AES key is read from the environment, never written down):

    dotnet run -c Release --project Tools/MapActorDump -- <PaksDir> <AesKeyHex> \
        FortniteGame/Content/Athena/Items/Traps/ props:TID_ > tid_defs.txt

Usage:

    python Tools/TrapTable/gen_traps.py tid_defs.txt > AFortOnlineBeacon/Net/Actors/FortTraps.Generated.cs
"""
import re
import sys


def to_game_path(raw):
    """Any of the dump's reference spellings -> "/Game/Dir/Name.Name" (or None).

    "FortTrapItemDefinition'FortniteGame/Content/A/B.B'" and "/Game/A/B.B" both appear.
    """
    if raw is None:
        return None
    value = raw.strip()
    m = re.match(r"^[A-Za-z0-9_]+'(.*)'$", value)
    if m:
        value = m.group(1)
    if value.startswith("FortniteGame/Content/"):
        value = "/Game/" + value[len("FortniteGame/Content/"):]
    if not value.startswith("/Game/") or value in ("None", ""):
        return None
    return value


def parse(path):
    """-> [(class_name, export_name, package_path, {key: scalar | [items] | {sub: scalar}})]."""
    exports = []
    current = None
    package = None
    open_array = None     # (dict, key) while reading "[i] value" lines
    open_struct = None    # (dict, key) while reading a "Key {" block at indent 1

    for raw in open(path, encoding="utf-8", errors="replace"):
        line = raw.rstrip("\n")
        if not line.strip():
            continue

        if line.startswith("--- "):
            package = line[4:].strip()
            if package.endswith(".uasset"):
                package = package[: -len(".uasset")]
            if package.startswith("FortniteGame/Content/"):
                package = "/Game/" + package[len("FortniteGame/Content/"):]
            continue

        if not line.startswith(" "):
            parts = re.split(r"\s{2,}", line.strip())
            if len(parts) == 2 and package is not None:
                current = (parts[0], parts[1], package, {})
                exports.append(current)
            else:
                current = None
            open_array = open_struct = None
            continue

        if current is None:
            continue

        indent = (len(line) - len(line.lstrip(" "))) // 4
        stripped = line.strip()
        props = current[3]

        if indent == 1:
            open_array = open_struct = None
            m = re.match(r"^(\w+) \[(\d+)\]$", stripped)
            if m:
                props[m.group(1)] = []
                open_array = (props, m.group(1))
                continue
            if stripped.endswith("{"):
                key = stripped[:-1].strip()
                props[key] = {}
                open_struct = (props, key)
                continue
            parts = re.split(r"\s{2,}", stripped, maxsplit=1)
            if len(parts) == 2:
                props[parts[0]] = parts[1].strip()
            continue

        if indent == 2:
            if open_array is not None:
                m = re.match(r"^\[\d+\]\s+(.*)$", stripped)
                if m:
                    open_array[0][open_array[1]].append(m.group(1).strip())
            elif open_struct is not None:
                parts = re.split(r"\s{2,}", stripped, maxsplit=1)
                if len(parts) == 2:
                    open_struct[0][open_struct[1]][parts[0]] = parts[1].strip()

    return exports


SHAPES = {
    "/Game/Building/EditModePatterns/Floor/EMP_Floor_Floor.EMP_Floor_Floor": "Floor",
    "/Game/Building/EditModePatterns/Wall/EMP_Wall_Solid.EMP_Wall_Solid": "Wall",
}


def shape_list(values):
    out = []
    for v in values or []:
        p = to_game_path(v)
        if p in SHAPES:
            out.append(SHAPES[p])
        elif p:
            out.append("Unknown")
    return out


def cs(value):
    return "null" if value is None else '"' + value + '"'


def main():
    exports = [e for e in parse(sys.argv[1])
               if e[0] in ("FortTrapItemDefinition", "FortContextTrapItemDefinition")]

    rows = []
    for class_name, name, package, props in exports:
        item_path = f"{package}.{name}"
        is_context = class_name == "FortContextTrapItemDefinition"
        offset = props.get("GridPlacementOffset")
        rows.append({
            "name": name,
            "item": item_path,
            "tool": to_game_path(props.get("WeaponActorClass")),
            "actor": to_game_path(props.get("BlueprintClass")),
            "context": is_context,
            "offset": float(offset) if offset else 0.0,
            "auto": props.get("bAutoCreateAttachmentBuilding") == "True",
            "auto_shapes": shape_list(props.get("AutoCreateAttachmentBuildingShapes")),
            "floor": to_game_path(props.get("FloorTrap")),
            "wall": to_game_path(props.get("WallTrap")),
            "ceiling": to_game_path(props.get("CeilingTrap")),
            "stat_row": (props.get("WeaponStatHandle") or {}).get("RowName"),
        })

    rows.sort(key=lambda r: r["name"].lower())
    holdable = sum(1 for r in rows if r["tool"])

    out = []
    out.append("// GENERATED by Tools/TrapTable/gen_traps.py from a MapActorDump \"props:TID_\" dump of\n")
    out.append("// FortniteGame/Content/Athena/Items/Traps/. Do not edit by hand.\n\n")
    out.append("namespace AFortOnlineBeacon.Net.Actors;\n\n")
    out.append("internal static partial class FortTraps {\n")
    out.append(f"    /// <summary>Every trap item definition in 10.40's Athena/Items/Traps - {len(rows)} rows, "
               f"{holdable} with a tool to hold.</summary>\n")
    out.append("    private static readonly Dictionary<string, FTrapDef> Table = new(StringComparer.OrdinalIgnoreCase) {\n")
    for r in rows:
        shapes = ", ".join(f"EAutoCreateShape.{s}" for s in r["auto_shapes"] if s != "Unknown")
        shapes_cs = f"new[] {{ {shapes} }}" if shapes else "Array.Empty<EAutoCreateShape>()"
        out.append(f'        ["{r["name"]}"] = new(\n')
        out.append(f'            ItemPath: {cs(r["item"])},\n')
        out.append(f'            ToolClass: {cs(r["tool"])},\n')
        out.append(f'            ActorClass: {cs(r["actor"])},\n')
        out.append(f'            IsContext: {"true" if r["context"] else "false"},\n')
        out.append(f'            GridPlacementOffset: {r["offset"]:g}f,\n')
        out.append(f'            AutoCreateAttachmentBuilding: {"true" if r["auto"] else "false"},\n')
        out.append(f'            AutoCreateShapes: {shapes_cs},\n')
        out.append(f'            FloorTrap: {cs(r["floor"])},\n')
        out.append(f'            WallTrap: {cs(r["wall"])},\n')
        out.append(f'            CeilingTrap: {cs(r["ceiling"])},\n')
        out.append(f'            StatRow: {cs(r["stat_row"])}),\n')
    out.append("    };\n")
    out.append("}\n")

    sys.stdout.write("".join(out))
    print(f"gen_traps: {len(rows)} trap items, {holdable} holdable", file=sys.stderr)


if __name__ == "__main__":
    main()
