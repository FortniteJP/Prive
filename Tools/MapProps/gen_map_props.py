"""Bake every DESTRUCTIBLE map actor's world position and its client-facing PATH.

    pakreader mapprops > PriveDev/mapprops.txt
    python gen_map_props.py [mapprops.txt] [out.bin]

WHY A BAKE AND NOT A LOOKUP. AFortOnlineBeacon can already damage and destroy a map actor - see
NativeRpcHandlers.DamageLevelActor - but only ever REACTIVELY, because the actor's path is something
the CLIENT supplies when it reports a hit. These actors live in streaming sublevels the server never
loads, so nothing at runtime can tell it that a tree exists at a given place, let alone what to call
it. A server that wants to destroy scenery on its own initiative (a shockwave grenade throwing
somebody through a wall) has to have been told in advance.

WHAT IS KEPT. The same gate the runtime damage path uses: an actor is destructible exactly when
FortHarvestResources resolves its name stem, so the stems are read straight out of that generated
file rather than re-derived. Four families are then excluded because they are not scenery even
though they resolve:

  * Tiered_*      chests, ammo boxes and floor-loot spawners. DamageLevelActor already refuses a
                  registered ABuildingContainer; baking them in would make a shockwave destroy the
                  chest it was standing next to, which is not what the game does.
  * LODActor,     engine proxies for distant geometry - one LODActor stands in for dozens of real
    FortHLODSMActor  actors and destroying it would take a whole hillside with it.
  * LevelBounds,  not geometry at all.
    Emitter

EACH PROP CARRIES ITS SIZE, and version 1 did not, which is what "the floor above me did not
break" was. With only a position the server has to ask "is the pivot within some fixed radius of the
swept line", and a 512-unit floor tile's pivot is up to 362 units away (half its diagonal) while the
tile is directly overhead. Measured misses of 361/382/394 against a 320 radius are exactly that, and
no single radius fixes it without also destroying barrels four metres away.

FORMAT (little-endian, same shape as the other TerrainHeightMap bakes):

    char[4]  magic "THMP"
    int32    Version (3)
    int32    PackageCount
      per package: int32 byte length, then UTF-8
    int32    PropCount
      per prop:  int32 PackageId, float X, float Y, float Z,
                 float RadiusXY, float CenterZ, float HalfZ,
                 int32 name length, UTF-8 name

CenterZ IS NOT REDUNDANT and version 2 not having it is what "sometimes it goes through and
sometimes it does not" was. A mesh's pivot is not its centre - a wall or a tree is authored with its
pivot on the FLOOR - so a box of pivot +/- half-height puts half of itself underground and stops
halfway up the real thing. Which half the sweep crossed decided whether the prop broke.

The path is reassembled at runtime as `<package>.<leaf>:PersistentLevel.<name>` - splitting it that
way is what keeps the file small, since ~450 packages carry ~65,000 actors between them.
"""
import io
import os
import re
import struct
import sys

SRC = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\user\Documents\PriveDev\mapprops.txt"
OUT = sys.argv[2] if len(sys.argv) > 2 else r"C:\Users\user\Documents\PriveDev\TerrainHeightMap.props.bin"
HARVEST = (r"c:\Users\user\Documents\Prive\AFortOnlineBeacon\Net\Actors"
           r"\FortHarvestResources.Generated.cs")

# Not scenery, however well their stems resolve - see the module docstring.
EXCLUDED_CLASSES = {"LODActor", "FortHLODSMActor", "LevelBounds", "Emitter"}
EXCLUDED_NAME_PREFIXES = ("Tiered_",)

# The largest radius a prop may claim.
#
# MESH RENDER BOUNDS ARE THE WRONG SHAPE FOR FOLIAGE, and only for foliage. A tree's bounds enclose
# its CANOPY - a hero tree measures 4,486 units, so being thrown within 45 metres of the trunk would
# fell it - while the thing a swept capsule actually hits is the trunk, which is why you can walk
# under one. Nothing in the bounds themselves distinguishes a tree from a building piece, so the
# only lever here is a ceiling.
#
# 800 IS MEASURED, NOT CHOSEN TO LOOK TIDY, and an earlier 512 was wrong for a reason worth keeping:
# it was picked from a bake that ignored FBoxSphereBounds.Origin, when the largest building piece
# looked like 362. With the origin included, a floor tile whose pivot sits at the middle of an edge
# measures 572, and 3,175 SM_FORT_Floors_Generic_BasicTile plus 405 Boardwalk_Floor land between 512
# and 800 - all of them real floors, the very thing a shockwave is supposed to punch through. A cap
# that clips a floor causes the under-reporting this whole bake exists to fix, so the ceiling has to
# sit above every building piece and merely bound the canopies.
#
# The honest fix is each prop's COLLISION rather than its render bounds - `world.hulls.bin` already
# holds the game's own convex shapes, and 61% of these props share an exact translation with one of
# its instances, so the join is there to be made. That is another piece of work; this is the bound
# to remove when it exists.
MAX_RADIUS = 800.0


def stem(name):
    """Identical to FortHarvestResources.Stem - the keys stop matching the moment it drifts."""
    return re.sub(r"_?\d+$", "", name).rstrip("_")


def harvest_stems():
    text = io.open(HARVEST, encoding="utf-8-sig").read()
    start = text.index("StemYields")
    end = text.index("};", start)
    return set(m.group(1) for m in re.finditer(r'\["([^"]+)"\]', text[start:end]))


def main():
    stems = harvest_stems()
    print("harvest stems: %d" % len(stems))

    packages = {}
    props = []
    skipped_class = skipped_stem = sized_default = capped = 0

    for line in io.open(SRC, encoding="utf-8", errors="replace"):
        parts = line.rstrip("\n").split("\t")
        if len(parts) != 4:
            continue

        location, class_name, path, extent = parts

        if class_name in EXCLUDED_CLASSES:
            skipped_class += 1
            continue

        # "/Game/A/B.B:PersistentLevel.Name" -> package "/Game/A/B", actor "Name". The world object
        # between them is always the package's own leaf, so it is not stored.
        package, _, actor = path.partition(":PersistentLevel.")
        package = package.rsplit(".", 1)[0]
        if not actor:
            continue

        if actor.startswith(EXCLUDED_NAME_PREFIXES):
            skipped_class += 1
            continue

        if stem(actor) not in stems:
            skipped_stem += 1
            continue

        x, y, z = (float(v) for v in location.split(","))
        radius_xy, center_z, half_z = (float(v) for v in extent.split(","))

        # A prop whose meshes would not load has no size to offer. Kept anyway, with a nominal
        # tile-ish extent, because dropping it would silently make that actor indestructible - and
        # "a bit too eager" is the recoverable direction here.
        if radius_xy <= 0.0:
            radius_xy, center_z, half_z = 128.0, 128.0, 128.0
            sized_default += 1

        if radius_xy > MAX_RADIUS:
            radius_xy = MAX_RADIUS
            capped += 1

        if package not in packages:
            packages[package] = len(packages)

        props.append((packages[package], x, y, z, radius_xy, center_z, half_z, actor))

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with io.open(OUT, "wb") as f:
        f.write(b"THMP")
        f.write(struct.pack("<i", 3))
        f.write(struct.pack("<i", len(packages)))
        for package, _ in sorted(packages.items(), key=lambda kv: kv[1]):
            raw = package.encode("utf-8")
            f.write(struct.pack("<i", len(raw)))
            f.write(raw)

        f.write(struct.pack("<i", len(props)))
        for package_id, x, y, z, radius_xy, center_z, half_z, actor in props:
            raw = actor.encode("utf-8")
            f.write(struct.pack("<iffffffi", package_id, x, y, z, radius_xy, center_z, half_z, len(raw)))
            f.write(raw)

    print("wrote %d prop(s) in %d package(s) to %s (%.1f MB)"
          % (len(props), len(packages), OUT, os.path.getsize(OUT) / 1048576.0))
    print("  skipped %d not-scenery, %d whose stem no harvest row covers" % (skipped_class, skipped_stem))
    print("  %d had no loadable mesh and took the nominal 128 extent" % sized_default)
    print("  %d were capped at %.0f (canopy bounds - see MAX_RADIUS)" % (capped, MAX_RADIUS))

    sizes = sorted(prop[4] for prop in props)
    if sizes:
        print("  radius: min %.0f  median %.0f  p90 %.0f  max %.0f"
              % (sizes[0], sizes[len(sizes) // 2], sizes[int(len(sizes) * 0.9)], sizes[-1]))


main()
