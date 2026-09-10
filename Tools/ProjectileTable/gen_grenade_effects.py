"""Bake each thrown item's NON-DAMAGE behaviour out of AthenaGameData into FortGrenadeEffects.Generated.cs.

    python gen_grenade_effects.py [AthenaGameData.json] [out.cs]

WHY A TABLE. Every thrown item already flies and explodes - the projectile class, the throw ability
and the per-item damage row are all baked (FortConsumables, FortProjectileStats). What none of that
carries is what a grenade does BESIDES damage, and for most of them that IS the item.

WHERE THE NUMBERS COME FROM. Not from the projectile Blueprints' properties - those hold the particle
systems and the sounds - but from AthenaGameData, the curve table the Blueprints themselves read at
runtime. `B_Prj_Athena_KnockGrenade`'s bytecode calls EvaluateCurveTableRow six times, on
`Default.KnockGrenade.LaunchVelocity` and `Default.KnockGrenade.AddToZBeforeLaunch` among others, and
then calls LaunchCharacter with the result. Every row is a curve keyed by an input Battle Royale
never varies, so the value at time 0 is the constant.

THE TWO NAMES ARE THE OTHER WAY ROUND FROM THE OBVIOUS READING, and it cost a round:
`Athena_KnockGrenade` is the **IMPULSE** grenade (its ability tags say
`Athena.Quests.Ability.Thrown.ImpulseGrenade`) and `Athena_ShockGrenade` is the **SHOCKWAVE**
(its assets live in `Consumables/ShockwaveGrenade/`, and its curve family is `ShockwaveGrenade`).

DAMAGES IS NOT READ FROM THE STAT ROW, because the stat row does not say. `Athena_DanceGrenade`'s
own WeaponStatHandle names the row `Athena_Grenade` - the frag's, 100/375 - and a boogie bomb that
explodes for 100 kills whoever it was supposed to make dance, which is exactly what it did. Nothing
in the projectile Blueprints or the throw abilities mentions damage at all (checked: zero references
in either), so the gate is native and unreadable from the paks. It is spelled out per family here
instead, from what each item demonstrably does in game.
"""
import io
import re
import sys

src = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\user\Documents\PriveDev\dumpwork\AthenaGameData.json"
out = (sys.argv[2] if len(sys.argv) > 2 else
       r"c:\Users\user\Documents\Prive\AFortOnlineBeacon\Net\Actors\FortGrenadeEffects.Generated.cs")

text = io.open(src, encoding="utf-8", errors="replace").read()


def row(name, default=0.0):
    """The curve row's value at time 0, or `default` when the table has no such row."""
    m = re.search(r'"Default\.%s":\s*\{.*?"Keys":\s*\[\s*\{\s*"Time":\s*0\.0,\s*"Value":\s*([-\d.eE+]+)'
                  % re.escape(name), text, re.S)
    return float(m.group(1)) if m else default


# item, effect kind, curve family, does it damage, note
FAMILIES = [
    ("Athena_KnockGrenade", "Knockback", "KnockGrenade", False,
     "Impulse Grenade - lands, waits half a second, throws everyone. No damage at all."),
    ("Athena_ShockGrenade", "Knockback", "ShockwaveGrenade", False,
     "Shockwave Grenade - the same but harder (3800), and nobody takes fall damage afterwards."),
    ("Athena_DanceGrenade", "Dance", "DanceGrenade", False,
     "Boogie Bomb - no damage; everyone in range dances for five seconds."),
    ("Athena_IceGrenade", "Chill", "IceGrenade", False,
     "Chiller - a small launch plus slippery feet."),
    ("Athena_GasGrenade", "Gas", "GasGrenade", False,
     "Stink Bomb - the blast does nothing; the CLOUD is what damages, every half second."),
    ("Athena_SmokeGrenade", "Smoke", "SmokeGrenade", False,
     "Smoke Grenade - cover, and nothing else. Lifetime is its own row rather than a Duration."),
    ("Athena_StickyGrenade", "Sticky", "StickyGrenade", True,
     "Clinger - sticks where it lands and goes off 2.5s later, for real damage."),
]

rows = []
for item, kind, family, damages, note in FAMILIES:
    duration = row("%s.Duration" % family,
                   row("%s.GasDuration" % family,
                       row("%sLifetime" % family)))          # SmokeGrenadeLifetime has no dot

    # HOW FAR THE THROWN PLAYER SMASHES THROUGH BUILDINGS, and only the shockwave does it: it is the
    # only projectile Blueprint carrying a ShouldDestroy/DestroyDistance pair at all
    # (B_Prj_Athena_KnockGrenade has neither), which is the difference between an impulse grenade
    # throwing you INTO a wall and a shockwave throwing you THROUGH it. Folded into one number here -
    # a distance of 0 means "no" - because ShouldDestroyStructure is a bool wearing a float's clothes
    # and nothing ever reads it on its own.
    should_destroy = row("%s.ShouldDestroyStructure" % family)

    rows.append((item, kind, note, damages, {
        "DestroyDistance": row("%s.DestructionDistance" % family) if should_destroy > 0 else 0.0,
        "Radius": row("%s.Radius" % family, row("%s.FXRadius" % family, 0.0)),
        "LaunchVelocity": row("%s.LaunchVelocity" % family),
        # THE SHOCKWAVE HAS NO SUCH ROW and does not need one: its own projectile Blueprint adds a
        # LITERAL 50 in the graph (`Add_FloatFloat(v.Z, 50)`, read out of the bytecode of
        # B_Prj_Athena_ShockGrenade), where the impulse grenade next door reads
        # `Default.KnockGrenade.AddToZBeforeLaunch` from this table - which is also 50. Taking the
        # missing row as 0 made the shockwave the ONE launcher without it.
        "AddToZBeforeLaunch": row("%s.AddToZBeforeLaunch" % family,
                                  50.0 if family == "ShockwaveGrenade" else 0.0),
        "Duration": duration,
        "Period": row("%s.DamagePeriod" % family),
        "HitDelay": row("%s.OnHitExplodeDelay" % family),
        "FriendlyFire": row("%s.FriendlyFire" % family, 1.0),
        "FallDamage": row("%s.AllPlayersTakeFallDamage" % family, 1.0),
    }))

with io.open(out, "w", encoding="utf-8-sig", newline="\r\n") as f:
    w = f.write
    w("// <auto-generated> Tools/ProjectileTable/gen_grenade_effects.py - do not edit by hand. </auto-generated>\n")
    w("//\n")
    w("// What a thrown item does BESIDES damage, read from the AthenaGameData curve table - the same\n")
    w("// table the projectile Blueprints themselves call EvaluateCurveTableRow against, so these are the\n")
    w("// values the game plays by rather than an impression of them.\n")
    w("//\n")
    w("// NOTE THE NAMES: Athena_KnockGrenade is the IMPULSE grenade and Athena_ShockGrenade is the\n")
    w("// SHOCKWAVE. Their tags and their asset folders say so, and the obvious reading is backwards.\n")
    w("//\n")
    w("// Damages is spelled out rather than read: an item's stat row does not say whether its ability\n")
    w("// applies it, and the boogie bomb's row IS the frag's (100/375) - see the generator's header.\n")
    w("//\n")
    w("// A zero means the family has no such row, not that the effect is off; see the Kind.\n")
    w("\n")
    w("namespace AFortOnlineBeacon.Net.Actors;\n")
    w("\n")
    w("/// <summary>What a thrown item does when it goes off, beyond its damage.</summary>\n")
    w("internal enum EGrenadeEffect { None, Knockback, Dance, Chill, Gas, Smoke, Sticky }\n")
    w("\n")
    w("internal static partial class FortGrenadeEffects {\n")
    w("    private static readonly (string Item, EGrenadeEffect Kind, float Radius, float LaunchVelocity,\n")
    w("                             float AddToZ, float Duration, float Period, float HitDelay,\n")
    w("                             float DestroyDistance,\n")
    w("                             bool FriendlyFire, bool Damages, bool FallDamage)[] Rows = {\n")

    for item, kind, note, damages, v in rows:
        w("        // %s\n" % note)
        w('        ("%s", EGrenadeEffect.%s, %gf, %gf, %gf, %gf, %gf, %gf, %gf, %s, %s, %s),\n'
          % (item, kind, v["Radius"], v["LaunchVelocity"], v["AddToZBeforeLaunch"],
             v["Duration"], v["Period"], v["HitDelay"], v["DestroyDistance"],
             "true" if v["FriendlyFire"] >= 0.5 else "false",
             "true" if damages else "false",
             "true" if v["FallDamage"] >= 0.5 else "false"))

    w("    };\n")
    w("}\n")

print("wrote %d grenade effect(s) to %s" % (len(rows), out))
for item, kind, note, damages, v in rows:
    print("  %-22s %-9s radius=%-6g launch=%-6g duration=%-5g period=%-4g hitDelay=%-4g destroy=%-6g damages=%s"
          % (item, kind, v["Radius"], v["LaunchVelocity"], v["Duration"], v["Period"], v["HitDelay"], v["DestroyDistance"], damages))
