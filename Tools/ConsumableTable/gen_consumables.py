#!/usr/bin/env python
"""Emit FortConsumables.Generated.cs - what a healing consumable actually does, read from the paks.

WHY THIS EXISTS. Using a Small Shield Potion is not one feature, it is three facts the server does
not have:

  1. WHICH ACTOR to spawn when the item is equipped (Athena_ShieldSmall ->
     B_ConsumableSmall_MiniShield_Athena_C). Without it the equip resolves to null and the pawn
     silently keeps whatever it was holding - the same failure the building tools had.
  2. WHICH ABILITY to grant (-> GA_Athena_ShieldSmall_C). The FGameplayAbilitySpecHandle the client
     sends in ServerTryActivateAbility is an INDEX into ActivatableAbilities, handed out by the
     server, so with no grant there is nothing for the client to activate and pressing fire is
     inert.
  3. WHAT IT HEALS. 25 shields, capped at 50.

Only (3) is a number anybody could have guessed, and guessing is exactly what this project does not
do: the amounts live in AthenaGameData curve rows named by the ability's own Row_ShieldAmount /
Row_ShieldCap handles, so they are READ. The cap is a FRACTION of the maximum (0.5), not an absolute.

The ability CDOs are consistent across all six healing consumables:

    HealsHealth / HealsShields          which pool it touches
    HasHealthCap / HasShieldCap         whether it stops part-way up that pool
    Row_HealthAmount / Row_ShieldAmount curve row for the amount
    Row_HealthCap    / Row_ShieldCap    curve row for the cap FRACTION
    HealthHealAmount                    a direct float that OVERRIDES the row (only the Med Kit)
    TriggerDuration                     how long the use animation takes

SLURP JUICE IS THE EXCEPTION and needs OVER_TIME below, because its ability carries no amounts at
all. GA_Athena_PurpleStuff_C applies GE_Athena_PurpleStuff_C, which grants GA_Athena_Slurp_Granted_C,
which ticks GE_Athena_PurpleStuff_Health (modifies FortHealthSet:Healing) and
GE_Athena_PurpleStuff_Shields (modifies FortHealthSet:CurrentShield) - and BOTH of those name the
same AthenaGameData row for their per-tick magnitude. So the ROUTING (which rows belong to Slurp
Juice) is written out by hand here, from reading that chain; the NUMBERS are still read from the pak
like every other one.

Do not trust GE_Athena_PurpleStuff's own Period (1.0) or Duration (24) - those are unoverridden
Blueprint defaults. The authoritative set is the three curve rows, and they agree with the real game:
75 effective health, 1 per tick, one tick every 0.5 s = 37.5 seconds.

NOT COVERED, deliberately: Chug Splash (GA_Athena_ChillBronco_C) applies a GameplayEffect asset
rather than carrying amounts of its own, so it needs the GE reader this does not have. It is left
out rather than approximated.

ALSO NOTE: every ability's AbilityCosts[0].ItemDefinition points at Athena_Shields, including the
Med Kit's. That is an unoverridden Blueprint parent default, not the cost - the item consumed is
whatever the player is holding. It is ignored here for that reason.

Producing the inputs:

    dotnet run --project Tools/MapActorDump -- <PaksDir> <AesKeyHex> \
        FortniteGame/Content/Athena/Items/Consumables/ props:Athena_ > consumable_defs.txt

    dotnet run --project Tools/PakReader -- rows \
        FortniteGame/Content/Athena/Balance/DataTables/AthenaGameData "" 500000 > athenagamedata.json

Usage:

    python Tools/ConsumableTable/gen_consumables.py consumable_defs.txt athenagamedata.json \
        > AFortOnlineBeacon/Net/Actors/FortConsumables.Generated.cs
"""
import json
import re
import sys


def parse_exports(path):
    """MapActorDump's props: output -> [(class_name, export_name, {prop: value})].

    The format is indentation-based; only top-level scalars and the RowName inside a one-level
    Row_* struct are needed, so nested blocks are tracked just deeply enough to find those.
    """
    exports = []
    current = None
    struct_stack = []
    package = None

    for raw in open(path, encoding="utf-8", errors="replace"):
        line = raw.rstrip("\n")
        if not line.strip():
            continue

        # "--- FortniteGame/Content/.../Athena_ShieldSmall.uasset" names the package the exports
        # below it came from. Needed because an item definition has to be REFERENCED by path over
        # the wire, not just named.
        if line.startswith("--- "):
            package = line[4:].strip()
            if package.endswith(".uasset"):
                package = package[: -len(".uasset")]
            if package.startswith("FortniteGame/Content/"):
                package = "/Game/" + package[len("FortniteGame/Content/"):]
            continue

        # "ClassName  ExportName" at column 0 starts a new export.
        if not line.startswith(" "):
            parts = re.split(r"\s{2,}", line.strip())
            if len(parts) == 2:
                current = (parts[0], parts[1], {"__package": package})
                exports.append(current)
                struct_stack = []
            continue

        if current is None:
            continue

        stripped = line.strip()
        indent = (len(line) - len(line.lstrip(" "))) // 4

        if stripped.endswith("{"):
            struct_stack = struct_stack[: indent - 1] + [stripped[:-1].strip()]
            continue
        if stripped == "}":
            struct_stack = struct_stack[: max(0, indent - 1)]
            continue

        parts = re.split(r"\s{2,}", stripped, maxsplit=1)
        if len(parts) != 2:
            continue
        key, value = parts[0], parts[1].strip()

        if indent == 1:
            current[2][key] = value
        elif struct_stack:
            # e.g. Row_ShieldAmount { Curve { RowName x } } -> "Row_ShieldAmount.RowName"
            current[2][f"{struct_stack[0]}.{key}"] = value

    return exports


def curve_values(rows_json):
    """Row name -> its value at time 0, which is what every consumable row is (a flat curve)."""
    text = open(rows_json, encoding="utf-8", errors="replace").read()
    data = json.loads(text[text.index("{"):])
    out = {}
    for name, row in data.get("Rows", {}).items():
        keys = row.get("Keys") or []
        if keys:
            out[name] = float(keys[0]["Value"])
    return out


# Item definition -> the AthenaGameData rows that describe its heal-over-time, for the consumables
# whose ability delegates its magnitude to a GameplayEffect chain instead of naming Row_* handles of
# its own. Routing read out of GE_Athena_PurpleStuff{,_Health,_Shields}; values still read from the
# curve table at generation time. See the module docstring.
OVER_TIME = {
    "Athena_PurpleStuff": (
        "Default.PurpleStuff.TotalEffectiveHealthGain",
        "Default.PurpleStuff.EffectiveHealthPerTick",
        "Default.PurpleStuff.TickRate",
    ),
}


def soft_path(value):
    """Strip CUE4Parse's decoration off an object reference, leaving the /Game/... path."""
    if not value:
        return None
    match = re.search(r"'([^']+)'", value)
    if match:
        # "BlueprintGeneratedClass'FortniteGame/Content/X/Y.Y_C'" -> "/Game/X/Y.Y_C"
        inner = match.group(1)
        if inner.startswith("FortniteGame/Content/"):
            return "/Game/" + inner[len("FortniteGame/Content/"):]
        return inner
    return value if value.startswith("/") else None


HEADER = '''// GENERATED by Tools/ConsumableTable/gen_consumables.py - do not edit by hand.
// Source: the 10.40 paks. Item definitions and ability CDOs from a CUE4Parse dump of
// FortniteGame/Content/Athena/Items/Consumables/, heal amounts from the AthenaGameData curve table
// rows those abilities name. See the script's docstring for the exact commands.

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     What a healing consumable does, and the two things needed to make it usable at all.
///
///     A consumable is a WEAPON in Fortnite's model - FortWeaponRangedItemDefinition, with a
///     WeaponActorClass to hold and a PrimaryFireAbility to fire - which is why equipping and using
///     one runs through exactly the same machinery as an assault rifle
///     (APawn.EquipInventoryItem grants the ability, the client activates it by spec handle). The
///     only thing this adds is what happens WHEN it activates.
///
///     Amounts are READ, not assumed. Each ability names AthenaGameData curve rows
///     (Default.ShieldSmallAmount = 25, Default.ShieldSmallCap = 0.5) and the cap is a FRACTION of
///     the maximum, not an absolute value - so a Small Shield Potion heals 25 shields but stops at
///     half of MaxShield, which is the rule the real game plays by.
/// </summary>
internal static class FortConsumables {
    /// <summary>
    ///     One healing consumable. A cap of NaN means "no cap" - fill to the pool's maximum.
    ///     TriggerDuration is how long the client's use animation takes; the server does not gate on
    ///     it yet, but a real one refuses a second activation until it has elapsed.
    /// </summary>
    internal readonly record struct FConsumableEffect(
        string DisplayName,
        bool HealsHealth, float HealthAmount, float HealthCapFraction,
        bool HealsShields, float ShieldAmount, float ShieldCapFraction,
        float TriggerDuration,
        float OverTimeTotal, float OverTimePerTick, float OverTimeTickRate);

'''


def emit(item_defs, abilities, curves):
    out = [HEADER]

    healing = {}
    for cls, name, props in abilities:
        if props.get("HealsHealth") != "True" and props.get("HealsShields") != "True":
            continue
        healing[cls] = props

    # item definition name -> (weapon actor class, ability class, ability props)
    usable = {}
    for cls, name, props in item_defs:
        if not cls.endswith("ItemDefinition"):
            continue
        ability = soft_path(props.get("PrimaryFireAbility"))
        actor = soft_path(props.get("WeaponActorClass"))
        if not ability or not actor:
            continue
        ability_class = ability.rsplit(".", 1)[-1]
        if ability_class not in healing:
            continue
        usable[name] = (actor, ability, healing[ability_class], props)

    def row(props, key):
        name = props.get(f"Row_{key}.RowName")
        return curves.get(name) if name else None

    def num(v):
        return "float.NaN" if v is None else f"{v:g}f"

    effects = []
    unmodelled = []

    for name in sorted(usable):
        _actor, _ability, ab, props = usable[name]

        heals_health = ab.get("HealsHealth") == "True"
        heals_shields = ab.get("HealsShields") == "True"

        # HealthHealAmount is a direct override of the curve row - only the Med Kit carries one.
        health_amount = float(ab["HealthHealAmount"]) if "HealthHealAmount" in ab else row(ab, "HealthAmount")
        shield_amount = row(ab, "ShieldAmount")
        health_cap = row(ab, "HealthCap") if ab.get("HasHealthCap") == "True" else None
        shield_cap = row(ab, "ShieldCap") if ab.get("HasShieldCap") == "True" else None

        # An ability whose magnitude lives in a GameplayEffect chain rather than in its own Row_*
        # handles - see OVER_TIME and the module docstring.
        total = per_tick = tick_rate = None
        if name in OVER_TIME:
            total_row, tick_row, rate_row = OVER_TIME[name]
            total, per_tick, tick_rate = curves.get(total_row), curves.get(tick_row), curves.get(rate_row)
            if None in (total, per_tick, tick_rate) or not per_tick or not tick_rate:
                unmodelled.append((name, props.get("DisplayName", name)))
                continue
        elif (heals_health and health_amount is None) or (heals_shields and shield_amount is None):
            # Claims to heal a pool and names no amount for it, and is not in OVER_TIME either.
            # Emitting a zero or a plausible-looking number would be inventing game balance.
            unmodelled.append((name, props.get("DisplayName", name)))
            continue

        display = props.get("DisplayName", name).replace('"', "'")
        effects.append(
            f'        ["{name}"] = new("{display}", '
            f'{str(heals_health).lower()}, {num(health_amount if heals_health else None)}, {num(health_cap)}, '
            f'{str(heals_shields).lower()}, {num(shield_amount if heals_shields else None)}, {num(shield_cap)}, '
            f'{float(ab.get("TriggerDuration", 0)):g}f, '
            f'{num(total)}, {num(per_tick)}, {num(tick_rate)}),\n')

    out.append("    /// <summary>Item definition name -> its healing effect. %d entries.</summary>\n"
               % len(effects))
    out.append("    private static readonly Dictionary<string, FConsumableEffect> Effects = "
               "new(StringComparer.OrdinalIgnoreCase) {\n")
    out.extend(effects)
    out.append("    };\n\n")

    if unmodelled:
        out.append("    // HELD AND EQUIPPABLE BUT NOT MODELLED - the ability says it heals, and names no\n"
                   "    // amount to heal by, because the magnitude lives in a GameplayEffect asset whose\n"
                   "    // modifiers are Blueprint rather than readable properties. Left out rather than\n"
                   "    // guessed; they still appear in the two tables below so they can be picked up,\n"
                   "    // equipped and held like any other item.\n")
        for name, display in unmodelled:
            out.append(f"    //   {name} ({display})\n")
        out.append("\n")

    out.append("    /// <summary>Item definition name -> the actor the pawn holds while using it.</summary>\n")
    out.append("    private static readonly Dictionary<string, string> ActorClasses = "
               "new(StringComparer.OrdinalIgnoreCase) {\n")
    for name in sorted(usable):
        out.append(f'        ["{name}"] = "{usable[name][0]}",\n')
    out.append("    };\n\n")

    out.append("    /// <summary>\n"
               "    ///     Item definition name -> its own asset path, in the \"package.object\" form an\n"
               "    ///     object reference needs on the wire. Loot tables carry full paths already; this is\n"
               "    ///     for naming a consumable by its short name (STARTING_CONSUMABLES, a console command).\n"
               "    /// </summary>\n")
    out.append("    private static readonly Dictionary<string, string> ItemPaths = "
               "new(StringComparer.OrdinalIgnoreCase) {\n")
    for name in sorted(usable):
        out.append(f'        ["{name}"] = "{usable[name][3]["__package"]}.{name}",\n')
    out.append("    };\n\n")

    out.append("    /// <summary>Item definition name -> the UGameplayAbility class granted on equip.</summary>\n")
    out.append("    private static readonly Dictionary<string, string> Abilities = "
               "new(StringComparer.OrdinalIgnoreCase) {\n")
    for name in sorted(usable):
        out.append(f'        ["{name}"] = "{usable[name][1]}",\n')
    out.append("    };\n\n")

    out.append('''    public static string? ActorClassFor(string itemDefinitionName) =>
        ActorClasses.GetValueOrDefault(itemDefinitionName);

    public static string? AbilityFor(string itemDefinitionName) =>
        Abilities.GetValueOrDefault(itemDefinitionName);

    public static string? ItemPathFor(string itemDefinitionName) =>
        ItemPaths.GetValueOrDefault(itemDefinitionName);

    public static FConsumableEffect? EffectFor(string itemDefinitionName) =>
        Effects.TryGetValue(itemDefinitionName, out var effect) ? effect : null;

    /// <summary>Every item definition name this table covers - used to answer "is this a consumable".</summary>
    public static IEnumerable<string> Names => Effects.Keys;
}
''')
    return "".join(out)


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)

    exports = parse_exports(sys.argv[1])
    curves = curve_values(sys.argv[2])
    sys.stdout.write(emit(exports, exports, curves))


if __name__ == "__main__":
    main()
