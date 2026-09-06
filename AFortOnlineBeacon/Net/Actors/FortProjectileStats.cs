namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     What a thrown item does when it goes off, as the item itself says rather than as the frag
///     grenade says.
///
///     THE BUG THIS EXISTS FOR: FortProjectileSystem had ONE set of constants, read out of the frag
///     grenade's ability, and applied them to every projectile it spawned. A shockwave grenade, whose
///     real damage is 5, killed a full-health player; so did a stink bomb (5), and so did a bush and
///     every Playset grenade in the game (0). The comment in FortProjectileSystem even said what the
///     honest fix was - "another generated column beside ProjectileClasses" - which is this.
///
///     Tools/ProjectileTable joins each item's WeaponStatHandle to its row in
///     Balance/DataTables/UtilityItemDamage. Several items legitimately share a row.
/// </summary>
internal static partial class FortProjectileStats {
    /// <summary>
    ///     Damage for a thrown item, or null when the item is not in the table.
    ///
    ///     NULL RATHER THAN A DEFAULT, because the caller's fallback is the frag grenade's numbers and
    ///     that choice belongs where it can be logged. Silently handing back 100/375 here is how every
    ///     throwable came to be a frag grenade in the first place.
    /// </summary>
    public static (float Player, float Environment)? For(string? itemName) =>
        itemName is { Length: > 0 } && Damage.TryGetValue(itemName, out var stats) ? stats : null;
}
