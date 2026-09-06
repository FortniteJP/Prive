#!/usr/bin/env python
"""Turn each item definition's own MaxStackSize into a C# table the inventory can stack against.

WHY THIS EXISTS. The server was adding a new inventory ROW for every pickup, so two boxes of light
ammo sat in two slots instead of one stack of 60 - and the fix needs a per-item number, because
"stack them" is only right for some items. A weapon must never merge into another weapon, rockets cap
at 12 while bullets cap at 999, and those numbers are properties of the assets, not conventions:

    AthenaAmmoDataBulletsLight  999      Athena_Grenade      10      Athena_Bandage   15
    AthenaAmmoDataShells        999      Athena_StickyGrenade 6      AmmoDataRockets  12
    WoodItemData                999      Athena_KnockGrenade  9      MountedTurret    50

Same shape as Tools/HarvestTable and Tools/LootTable: pakreader dumps, this emits, the server compiles
the answer in. See PriveDev/docs/PORTING-TO-ANOTHER-VERSION.md.

ONE ROW OR MANY is the other half of the rule, and MaxStackSize does not carry it: ammo and resources
occupy exactly ONE inventory row however much you hold - harvesting past 999 wood drops the excess on
the ground rather than opening a second wood stack - while a throwable can legitimately sit in two
slots. The item's CLASS is what says which: FortAmmoItemDefinition and FortResourceItemDefinition are
the single-row ones, and grenades are FortWeaponRangedItemDefinition like every other weapon.

EVERY ITEM DEFINITION IS EMITTED, not only the stackable ones, and that is the whole difference
between this table answering "how many fit in a slot" and it also answering "does this take a slot at
all". The second question used to fall back to Tools/WeaponStats when this table had never heard of
an item - and that table holds only the 98 weapons with a stat row, so
`WID_Assault_Auto_Athena_C_Ore_T02` and `WID_Sniper_Auto_Suppressed_Scope_Athena_R` were invisible to
BOTH tables: not counted against the five carried slots, and never refused by a full inventory. A
live session hoarded eleven carried items. Emitting all 4472 closes it at the source, because the
CLASS is what answers the question and every item definition has one.

INPUT - one `path<TAB>MaxStackSize<TAB>Class` per line, from Tools/PakReader (which needs
FORTNITE_PAKS and FORTNITE_AES_KEY):

    pakreader find "Items/" 400000 > allitems.txt      # then keep WID_/TID_ and the Ammo,
                                                       # ResourcePickups, Consumables, Traps,
                                                       # BuildingTools and Gameplay folders,
                                                       # as /Game/Path/Name.Name object paths
    pakreader stacks <fileOfObjectPaths> > stacks.txt

Rows whose class is not an `*ItemDefinition` are dropped here - the path list is a net cast over
folders, and a texture or a blueprint in one of them has no stack rule to record.

    python gen_stacks.py <stacks.txt> <output.cs>
"""
import sys

# Item classes that occupy ONE row no matter how much is held. Everything else may open another.
SINGLE_ROW_CLASSES = {'FortAmmoItemDefinition', 'FortResourceItemDefinition'}

# Classes that live in the SECONDARY quickbar - the row with the materials - and therefore never take
# one of the five carried slots. Ammo and resources are the obvious two; TRAPS are the third, and the
# one that had to be reported before anyone looked: a trap sat in the weapon row and ate a slot.
#
# This is emitted rather than inferred at runtime because the runtime has no class information at
# all - an item definition there is a UAssetRegistry stand-in with a path and nothing else. The first
# attempt at a slot limit tried to infer it from the stack rules instead and made the game
# unplayable; see FortItemStacks.OccupiedSlots.
SECONDARY_ROW_CLASSES = SINGLE_ROW_CLASSES | {'FortTrapItemDefinition', 'FortContextTrapItemDefinition'}

# The build tools and the edit tool. They DO sit in the quickbar, and they are not among the five a
# player fills - every match starts with all of them held and all five slots free. Listed by class
# rather than by path for the same reason as everything else here: the class is what the asset says.
TOOL_CLASSES = {'FortBuildingItemDefinition', 'FortEditToolItemDefinition', 'FortSchematicItemDefinition'}

NOT_CARRIED_CLASSES = SECONDARY_ROW_CLASSES | TOOL_CLASSES

# Classes whose "the asset does not say" is emitted AS 0, so the runtime resolves it (see
# FortItemStacks.StackCap and TRAP_STACK_SIZE).
#
# The four FortContextTrapItemDefinition items (bouncer, campfire, poison dart, the generic context
# trap) override no MaxStackSize at all, and their real cap of 999 lives in a native CDO that is not
# in the paks. Everything ELSE that says nothing really does mean one per slot - a weapon above all,
# where resolving 0 to 999 would merge two guns into one - so those are emitted as an explicit 1.
UNSPECIFIED_MEANS_ASK_RUNTIME = {'FortTrapItemDefinition', 'FortContextTrapItemDefinition'}


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    rows = []
    for line in open(sys.argv[1], encoding='utf-8'):
        parts = line.rstrip('\n').split('\t')
        if len(parts) < 3:
            continue
        try:
            size = int(parts[1])
        except ValueError:
            continue

        klass = parts[2]
        if not klass.endswith('ItemDefinition'):
            continue

        if size < 2 and klass not in UNSPECIFIED_MEANS_ASK_RUNTIME:
            size = 1

        rows.append((parts[0], size, klass in SINGLE_ROW_CLASSES, klass not in NOT_CARRIED_CLASSES))

    rows.sort()

    with open(sys.argv[2], 'w', encoding='utf-8') as out:
        out.write('// <auto-generated> Tools/StackSizes/gen_stacks.py - do not edit by hand.\n')
        out.write('//\n')
        out.write("// EVERY item definition in the shipped 10.40 paks, with its own MaxStackSize and the two\n")
        out.write('// rules its CLASS decides. SingleRow marks the items that never open a second stack (ammo\n')
        out.write('// and resources); their overflow goes on the ground instead. Carried marks the items that\n')
        out.write('// occupy one of the five CARRIED quickbar slots - false for ammo, resources and TRAPS,\n')
        out.write('// which sit in the secondary row beside the materials, and false for the build tools and\n')
        out.write('// the edit tool, which are in the quickbar but are not among the five.\n')
        out.write('//\n')
        out.write('// Unstackable items are listed with a 1 rather than left out. They used to be omitted -\n')
        out.write('// the cap was all this table was for - which left every weapon without a stat row invisible\n')
        out.write('// to the slot limit in BOTH directions: never counted, never refused. See gen_stacks.py.\n')
        out.write('\n')
        out.write('namespace AFortOnlineBeacon.Net.Actors;\n\n')
        out.write('internal static partial class FortItemStacks {\n')
        out.write(f'    /// <summary>{len(rows)} item definitions, by object path.</summary>\n')
        out.write('    private static readonly Dictionary<string, (int Max, bool SingleRow, bool Carried)> Sizes =\n')
        out.write('        new(StringComparer.OrdinalIgnoreCase) {\n')
        for path, size, single, carried in rows:
            out.write(f'            ["{path}"] = ({size}, {"true" if single else "false"}, '
                      f'{"true" if carried else "false"}),\n')
        out.write('        };\n')
        out.write('}\n')

    print(f'{len(rows)} item definition(s) -> {sys.argv[2]}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
