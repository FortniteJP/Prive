#!/usr/bin/env python
"""Bake every vehicle's SEAT ARRAY out of its Blueprint, so the server can replicate it.

WHY THE WHOLE ARRAY AND NOT JUST THE OCCUPANT. `UFortVehicleSeatComponent::PlayerSlots` is a
replicated TArray, and a replicated TArray is SERVER-AUTHORITATIVE: the client resizes to the server's
count and applies what arrives. Sending a one-element array with only `Player` set would blank the
sockets, camera offsets, exit sockets, sounds and display text the Blueprint had configured. So the
server has to know the seats - and it can, because they are in the Blueprint:

    ShoppingCartVehicleSK_C  2: Driver, Passenger        FerretVehicle_C  5
    GolfCartVehicleSK_C      4                           JackalVehicle_Athena_C  1

THE WIRE ORDER IS NOT THIS FILE'S BUSINESS, though an earlier version of this comment claimed it
was. It lives in NativeRepLayouts.VehicleSeatProps and is checked against the SDK by
`python Tools/RepHandles/verify_cs_handles.py`; only ONE member (`Player`) is ever replicated, and
the rest is carried here purely as the server's own copy of the Blueprint's configuration. Which is
why MEMBERS below can be - and is - shorter than the real struct: `CameraPitchConstraint` and
`CameraYawConstraint` are omitted because nothing on this side reads them. Adding a member here
changes what the server KNOWS, never what it sends, so it can never renumber a handle.

The one thing that IS load-bearing is the seat COUNT: a replicated TArray resizes the client's copy,
and a count that disagrees with the Blueprint's would throw away the very sockets and offsets this
bake exists to preserve.

INPUT - one `pakreader exports <blueprint>` JSON per vehicle, in a directory:

    python gen_vehicle_seats.py <exportsDir> <output.cs>
"""
import json
import os
import sys

# Which members to READ out of the Blueprint, with the SDK offset each one lives at. Not a wire
# order - see the module docstring. Deliberately not the full struct.
MEMBERS = [
    ('SeatSocket', 'name', 0x00),
    ('SeatChoiceSocket', 'name', 0x08),
    ('SeatIndicatorSocket', 'name', 0x10),
    ('SeatChoiceDisplayText', 'text', 0x18),
    ('SeatCollision', 'name', 0x30),
    ('ExitSockets', 'namearray', 0x38),
    ('ShootingCone.YawConstraint', 'float', 0x48),
    ('ShootingCone.PitchConstraint', 'float', 0x4C),
    ('SoundOnEnter', 'object', 0x50),
    ('SoundOnExit', 'object', 0x58),
    # One offset, so alphabetical among themselves.
    ('bCanEmote', 'bool', 0x60),
    ('bForceCrouch', 'bool', 0x60),
    ('bIsSelectable', 'bool', 0x60),
    ('bPlayEnterSoundForTransition', 'bool', 0x60),
    ('bPlayExitSoundForTransition', 'bool', 0x60),
    ('bUseGroundMotion', 'bool', 0x60),
    ('bUseVehicleIsOnGround', 'bool', 0x60),
    ('ActorSpaceCameraOffset', 'vector', 0x64),
    ('VehicleSpaceCameraOffset', 'vector', 0x70),
    ('SlopeCompensationCameraOffset', 'float', 0x7C),
    ('StandingFiringOffset', 'vector', 0x80),
    ('CrouchingFiringOffset', 'vector', 0x8C),
    ('EmoteOffset', 'vector', 0x98),
    ('Player', 'object', 0xA8),          # the one the server actually changes
    ('PlayerEntryTime', 'float', 0xB8),
    ('bConstrainPawnToSeatTransform', 'bool', 0xC0),
    ('bOffsetPlayerRelativeAttachLocation', 'bool', 0xC1),
    ('bUseExitTimer', 'bool', 0xC2),
    ('WeaponComponent', 'object', 0xC8),
]


def cs_string(value):
    return '"' + (value or '').replace('\\', '\\\\').replace('"', '\\"') + '"'


def cs_vector(value):
    # FVector here has no positional constructor - it is an object-initialiser type like the rest of
    # this project's maths types.
    v = value or {}
    return f'new() {{ X = {v.get("X", 0.0):g}f, Y = {v.get("Y", 0.0):g}f, Z = {v.get("Z", 0.0):g}f }}'


def slot_literal(slot):
    """One seat as a C# object initialiser, in wire order."""
    parts = []

    for name, kind, _ in MEMBERS:
        if '.' in name:
            outer, inner = name.split('.')
            value = (slot.get(outer) or {}).get(inner, 0.0)
        else:
            value = slot.get(name)

        field = name.replace('.', '')

        if kind == 'name':
            parts.append(f'{field} = {cs_string(value)}')
        elif kind == 'text':
            t = value or {}
            parts.append(f'{field}Namespace = {cs_string(t.get("Namespace"))}')
            parts.append(f'{field}Key = {cs_string(t.get("Key"))}')
            parts.append(f'{field}Source = {cs_string(t.get("SourceString"))}')
        elif kind == 'namearray':
            items = ', '.join(cs_string(v) for v in (value or []))
            parts.append(f'{field} = new[] {{ {items} }}')
        elif kind == 'float':
            parts.append(f'{field} = {float(value or 0.0):g}f')
        elif kind == 'bool':
            parts.append(f'{field} = {"true" if value else "false"}')
        elif kind == 'vector':
            parts.append(f'{field} = {cs_vector(value)}')
        elif kind == 'object':
            # Object references are baked as PATHS; the server resolves them through UAssetRegistry the
            # same way every other static asset reference here does. Player and WeaponComponent are
            # runtime values and are never baked.
            if name in ('Player', 'WeaponComponent'):
                continue
            path = (value or {}).get('ObjectPath', '') if isinstance(value, dict) else ''
            parts.append(f'{field} = {cs_string(path)}')

    return parts


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    vehicles = {}

    for entry in sorted(os.listdir(sys.argv[1])):
        if not entry.endswith('.json'):
            continue

        with open(os.path.join(sys.argv[1], entry), encoding='utf-8') as handle:
            exports = json.load(handle)

        for export in exports:
            props = export.get('Properties') or {}
            if 'PlayerSlots' in props:
                vehicles[entry[:-5]] = props['PlayerSlots']
                break

    with open(sys.argv[2], 'w', encoding='utf-8') as out:
        out.write('// <auto-generated> Tools/VehicleSeats/gen_vehicle_seats.py - do not edit by hand.\n')
        out.write('//\n')
        out.write("// Every vehicle's PlayerSlots, read out of its Blueprint. The FIELD ORDER IS THE WIRE ORDER:\n")
        out.write('// FRepLayout recurses into FAthenaCarPlayerSlot and sorts its members by offset, name as\n')
        out.write('// tie-break, skipping RepSkip ones (Controller, EnterSeatTime). Do not reorder.\n')
        out.write('\n')
        out.write('namespace AFortOnlineBeacon.Net.Actors;\n\n')
        out.write('internal static partial class FortVehicleSeats {\n')
        out.write(f'    /// <summary>{len(vehicles)} vehicles, by Blueprint class name.</summary>\n')
        out.write('    private static readonly Dictionary<string, FVehicleSeat[]> Seats =\n')
        out.write('        new(StringComparer.OrdinalIgnoreCase) {\n')

        for name in sorted(vehicles):
            out.write(f'            ["{name}"] = new FVehicleSeat[] {{\n')
            for slot in vehicles[name]:
                fields = ',\n                    '.join(slot_literal(slot))
                out.write(f'                new() {{\n                    {fields}\n                }},\n')
            out.write('            },\n')

        out.write('        };\n')
        out.write('}\n')

    total = sum(len(v) for v in vehicles.values())
    print(f'{len(vehicles)} vehicle(s), {total} seat(s) -> {sys.argv[2]}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
