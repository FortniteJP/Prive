namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Checks the damage falloff against the numbers in the baked table, at the breakpoints and
///     between them.
///
///     WHY IT NEEDS TO EXIST. Every other part of this feature is data: if a stat row is wrong, the
///     row is wrong and the generated file says so in a comment. The INTERPOLATION is the one piece
///     of logic, and a bug in it is invisible - a shotgun doing 5.7 where it should do 9.5 is a
///     plausible number that nobody can spot in play, and it would silently make every fight wrong.
///
///     Uses real rows rather than synthetic ones on purpose: the three cases here are the three
///     shapes the table actually contains - a melee row with all four ranges at zero, a rifle whose
///     damage flattens after RngLong, and a shotgun that falls all the way to 1.0.
/// </summary>
public static class FortWeaponStatsSelfTest {
    public static bool RunSelfTest() {
        var failures = 0;

        // A MELEE ROW: all four ranges are 0 and all four damages equal. The "no falloff defined"
        // reading has to hold at every distance, including absurd ones.
        failures += CheckWeapon("WID_Harvest_Pickaxe_Athena_C_T01", stats => {
            var bad = 0;
            bad += Check("pickaxe at 0", stats.DamageAt(0f), 20f);
            bad += Check("pickaxe at 250", stats.DamageAt(250f), 20f);
            bad += Check("pickaxe at 100000", stats.DamageAt(100000f), 20f);
            bad += Check("pickaxe structure damage", stats.EnvironmentDamageAt(250f), 50f);

            // The two multipliers this weapon is the reason for: no crit bonus at all, and the
            // weak-spot bonus the server used to guess at 3.
            bad += Check("pickaxe crit multiplier", stats.Critical, 1f);
            bad += Check("pickaxe vulnerability multiplier", stats.Vulnerability, 10f);
            return bad;
        });

        // A RIFLE: 33 out to 5000, then down to 22.275 by 10000, then FLAT to 27500.
        failures += CheckWeapon("WID_Assault_Auto_Athena_R_Ore_T03", stats => {
            var bad = 0;
            bad += Check("rifle point blank", stats.DamageAt(0f), 33f);
            bad += Check("rifle at RngPB", stats.DamageAt(5000f), 33f);
            bad += Check("rifle at RngMid", stats.DamageAt(7500f), 26.4f);
            bad += Check("rifle at RngLong", stats.DamageAt(10000f), 22.275f);
            bad += Check("rifle at RngMax", stats.DamageAt(27500f), 22.275f);
            bad += Check("rifle beyond RngMax", stats.DamageAt(50000f), 22.275f);

            // Halfway between RngPB and RngMid, so halfway between 33 and 26.4.
            bad += Check("rifle midway PB->Mid", stats.DamageAt(6250f), 29.7f);

            // A structure takes the same 33 at every range - the EnvDmg set is flat on this weapon,
            // which is why it is checked separately from the player curve rather than assumed to
            // follow it.
            bad += Check("rifle structure damage near", stats.EnvironmentDamageAt(100f), 33f);
            bad += Check("rifle structure damage far", stats.EnvironmentDamageAt(20000f), 33f);

            bad += Check("rifle crit multiplier", stats.Critical, 2f);
            return bad;
        });

        // A SHOTGUN: the case that makes a single damage number impossible - 9.5 PER PELLET up
        // close, 1.0 at range.
        failures += CheckWeapon("WID_Shotgun_Standard_Athena_C_Ore_T03", stats => {
            var bad = 0;
            bad += Check("shotgun at RngPB", stats.DamageAt(640f), 9.5f);
            bad += Check("shotgun at RngMid", stats.DamageAt(1280f), 5.7f);
            bad += Check("shotgun at RngLong", stats.DamageAt(1792f), 3.325f);
            bad += Check("shotgun at RngMax", stats.DamageAt(3072f), 1f);
            bad += Check("shotgun midway PB->Mid", stats.DamageAt(960f), 7.6f);
            return bad;
        });

        // An item that is not a weapon at all must come back null, not a default - see
        // FortWeaponStats.For for why that distinction is load-bearing.
        failures += Check("an unknown weapon has no stats", FortWeaponStats.For("WID_NotAWeapon") == null, true);
        failures += Check("a null weapon name has no stats", FortWeaponStats.For(null) == null, true);

        Console.WriteLine(failures == 0
            ? "FortWeaponStatsSelfTest: falloff, crit and weak-spot multipliers all match the baked rows. OK."
            : $"FortWeaponStatsSelfTest: {failures} FAILURE(S).");

        return failures == 0;
    }

    private static int CheckWeapon(string name, Func<FWeaponStats, int> check) {
        if (FortWeaponStats.For(name) is not { } stats) {
            Console.WriteLine($"FortWeaponStatsSelfTest: '{name}' is not in the baked table - re-run " +
                              "Tools/WeaponStats/gen_weapon_stats.py.");
            return 1;
        }

        return check(stats);
    }

    /// <summary>Float comparison with a tolerance well below anything the game displays.</summary>
    private static int Check(string what, float got, float expected) {
        if (MathF.Abs(got - expected) <= 0.001f) return 0;

        Console.WriteLine($"FortWeaponStatsSelfTest: {what} - got {got}, expected {expected}");
        return 1;
    }

    private static int Check(string what, bool got, bool expected) {
        if (got == expected) return 0;

        Console.WriteLine($"FortWeaponStatsSelfTest: {what} - got {got}, expected {expected}");
        return 1;
    }
}
