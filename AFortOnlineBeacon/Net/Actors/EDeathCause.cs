namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     EDeathCause, straight out of the 10.40 SDK - what the client's elimination feed turns into
///     "eliminated by" wording and an icon. Only the causes this server can actually produce are
///     named; the real enum has 49 values, and that COUNT is what matters on the wire, since
///     UEnumProperty::NetSerializeItem writes CeilLogTwo(EDeathCause_MAX) = 6 bits (see
///     NativeRepLayouts' DeathInfo.DeathCause).
///
///     The numbers are the SDK's own and must not be renumbered.
/// </summary>
public enum EDeathCause : byte {
    /// <summary>The storm.</summary>
    OutsideSafeZone = 0,
    FallDamage = 1,
    Pistol = 2,
    Shotgun = 3,
    Rifle = 4,
    SMG = 5,
    Sniper = 6,
    Melee = 8,
    /// <summary>A thrown grenade - what FortProjectileSystem attributes an explosion to.</summary>
    Grenade = 10,
    C4 = 11,
    GrenadeLauncher = 12,
    RocketLauncher = 13,
    /// <summary>What a death this server cannot attribute goes out as.</summary>
    Unspecified = 48
}
