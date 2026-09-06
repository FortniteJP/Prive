namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     One weapon's damage, as its stat row defines it - see Tools/WeaponStats for where the numbers
///     come from and how they are joined.
///
///     DAMAGE IS A CURVE, NOT A NUMBER, which is the whole reason this type exists. Four
///     (range, damage) breakpoints describe the falloff, and there are two independent sets of them:
///     one for players and one for structures. A shotgun is the case that makes the point - 9.5 per
///     pellet at 640 units and 1.0 by 3072 - and no single number can stand in for that.
/// </summary>
internal sealed class FWeaponStats {
    public float RngPB { get; init; }
    public float RngMid { get; init; }
    public float RngLong { get; init; }
    public float RngMax { get; init; }

    public float DmgPB { get; init; }
    public float DmgMid { get; init; }
    public float DmgLong { get; init; }
    public float DmgMax { get; init; }

    public float EnvPB { get; init; }
    public float EnvMid { get; init; }
    public float EnvLong { get; init; }
    public float EnvMax { get; init; }

    /// <summary>DamageZone_Critical - the headshot multiplier. 2.0 on most guns, 1.0 on the pickaxe.</summary>
    public float Critical { get; init; }

    /// <summary>
    ///     DamageZone_Vulnerability - the structural weak-spot multiplier. 10.0 on the pickaxe, 0 on
    ///     guns, which is not "no bonus" but "this weapon has no vulnerability zone at all" - see
    ///     <see cref="EnvironmentDamageAt" />.
    /// </summary>
    public float Vulnerability { get; init; }

    /// <summary>AmmoCostPerFire - rounds taken out of the magazine per shot. 1 for almost everything.</summary>
    public float AmmoPerFire { get; init; }

    /// <summary>
    ///     CartridgePerFire - how many PROJECTILES one trigger pull produces. 1 for a rifle, more for
    ///     a shotgun, and the reason a shotgun's DmgPB looks so small: it is per pellet, and the
    ///     client reports one hit per pellet.
    /// </summary>
    public float CartridgesPerFire { get; init; }

    /// <summary>ReloadTime, in seconds. Carried for completeness - reloading is the client's ability to time.</summary>
    public float ReloadTime { get; init; }

    /// <summary>ClipSize - the magazine. FortWeaponActorClasses has its own copy for loot spawning.</summary>
    public float ClipSize { get; init; }

    /// <summary>Damage to a PLAYER at this distance, before any zone multiplier.</summary>
    public float DamageAt(float distance) =>
        Interpolate(distance, RngPB, RngMid, RngLong, RngMax, DmgPB, DmgMid, DmgLong, DmgMax);

    /// <summary>Damage to a STRUCTURE at this distance, before any zone multiplier.</summary>
    public float EnvironmentDamageAt(float distance) =>
        Interpolate(distance, RngPB, RngMid, RngLong, RngMax, EnvPB, EnvMid, EnvLong, EnvMax);

    /// <summary>
    ///     Piecewise-linear falloff across the four breakpoints, flat before the first and after the
    ///     last.
    ///
    ///     ALL-ZERO RANGES MEAN FLAT, and that is a real case rather than a guard against bad data:
    ///     every melee row has RngPB..RngMax at 0 while still carrying four identical damage values.
    ///     Reading that literally would put every pickaxe swing past the last breakpoint - which
    ///     happens to give the right answer here, since the four damages are equal, but would be the
    ///     wrong reason. Treating it as "no falloff defined" is the honest reading.
    /// </summary>
    private static float Interpolate(float distance,
                                     float r0, float r1, float r2, float r3,
                                     float d0, float d1, float d2, float d3) {
        if (r3 <= 0f) return d0;
        if (distance <= r0) return d0;
        if (distance >= r3) return d3;

        if (distance <= r1) return Lerp(distance, r0, r1, d0, d1);
        if (distance <= r2) return Lerp(distance, r1, r2, d1, d2);

        return Lerp(distance, r2, r3, d2, d3);
    }

    private static float Lerp(float x, float x0, float x1, float y0, float y1) =>
        x1 <= x0 ? y1 : y0 + (y1 - y0) * ((x - x0) / (x1 - x0));
}

/// <summary>
///     Every weapon's damage numbers, baked from the paks by Tools/WeaponStats.
///
///     Replaces two flat placeholders that had been in place since damage was first implemented:
///     `FortDamageSystem.WeaponDamage` (20 for everything, pickaxe to sniper) and
///     `NativeRpcHandlers.BuildingDamagePerHit`. Both were honestly labelled as stand-ins for exactly
///     this table.
/// </summary>
internal static partial class FortWeaponStats {
    /// <summary>
    ///     This weapon's stats, or NULL when the table has never heard of it.
    ///
    ///     NULL RATHER THAN A DEFAULT, deliberately, and for the reason Tools/ProjectileTable's
    ///     equivalent gives: a plausible-looking default is indistinguishable from a real answer, so
    ///     an unknown weapon would silently do assault-rifle damage forever. The caller logs the
    ///     fallback instead, which is how a weapon missing from the bake becomes visible.
    /// </summary>
    public static FWeaponStats? For(string? weaponName) =>
        weaponName != null && Stats.TryGetValue(weaponName, out var stats) ? stats : null;
}
