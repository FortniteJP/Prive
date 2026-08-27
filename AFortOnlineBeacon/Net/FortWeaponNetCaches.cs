using AFortOnlineBeacon.Net.Actors;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     Per-weapon-class FClassNetCache. Every other actor in this project has one fixed class, so
///     NativeClassNetCache can hand-transcribe its chain once; a weapon cannot, because the class to
///     spawn comes from the item definition (see FortWeaponActorClasses). An assault rifle is a
///     B_Assault_Auto_Athena_C - AFortWeapon -> AFortWeaponRanged -> B_Ranged_Generic_C ->
///     B_Assault_Generic_C -> itself, 38 net fields - and the pickaxe is a
///     B_Athena_Pickaxe_Generic_C - AFortWeapon -> AFortWeaponPickaxeAthena -> itself, 32. Since
///     FieldNetIndex is written as a bounded int over GetMaxIndex(), those two do not even agree on
///     how many BITS a field index takes, so one shared cache cannot serve both.
///
///     This table could not exist until 2026-08-27. A Blueprint class that is not loaded is not in
///     the Dumper-7 SDK at all, and every dump until then was taken from the lobby, where no weapon
///     Blueprint is resident - B_Athena_Pickaxe_Generic_C was simply absent. Dumping again while
///     actually in a match (which is also what confirmed
///     PlayerPawn_Athena_Generic_C/_Parent_C really do add no net fields, an assumption
///     NativeClassNetCache had been carrying on faith) put all 107 AFortWeapon subclasses in reach.
///
///     A weapon class NOT in the table falls back to the bare AFortWeapon chain, which is correct
///     only for a class that adds nothing of its own. That is logged rather than silent: a wrong
///     field width misdecodes without complaining, and the log line is the cue to re-dump with that
///     weapon in hand.
/// </summary>
internal static partial class FortWeaponNetCaches {
    private static readonly Dictionary<string, FClassNetCache> Built = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    /// <summary>
    ///     The cache for this weapon actor's real class. <paramref name="actorCache"/> is
    ///     NativeClassNetCache's AActor cache, which every chain here sits on top of.
    /// </summary>
    public static FClassNetCache For(AFortWeapon weapon, FClassNetCache actorCache) {
        var className = ClassNameOf(weapon);

        if (className == null || !Chains.TryGetValue(className, out var chain)) {
            if (className != null && Warned.Add(className)) {
                Console.WriteLine($"FortWeaponNetCaches: no chain known for '{className}' - falling back to the bare " +
                                  "AFortWeapon chain. Any client RPC addressed to this weapon may decode with the " +
                                  "wrong field width. Re-run Tools/NetFieldVerify/gen_weapon_net_fields.py against a " +
                                  "Dumper-7 dump taken while holding this weapon.");
            }

            return For(new[] { "FortWeapon" }, actorCache);
        }

        return For(chain, actorCache);
    }

    private static FClassNetCache For(string[] chain, FClassNetCache actorCache) {
        var key = string.Join('/', chain);
        if (Built.TryGetValue(key, out var existing)) return existing;

        // Built base-first, exactly as FClassNetCacheMgr::GetClassNetCache walks a class chain -
        // each link's FieldsBase is the running total of everything above it.
        var cache = actorCache;
        foreach (var link in chain) {
            cache = new FClassNetCache(cache, OwnFields.GetValueOrDefault(link, Array.Empty<string>()));
        }

        Built[key] = cache;
        return cache;
    }

    /// <summary>
    ///     "/Game/Weapons/.../B_Assault_Auto_Athena.B_Assault_Auto_Athena_C" -> "B_Assault_Auto_Athena_C".
    ///     The UClass's NativePackagePath is the only place the real class name lives: the C# type is
    ///     AFortWeapon for every weapon (see GUClassArray.StaticClassForPath).
    /// </summary>
    private static string? ClassNameOf(AFortWeapon weapon) {
        var path = weapon.GetClass().NativePackagePath;
        if (path == null) return null;

        var dot = path.LastIndexOf('.');
        return dot < 0 ? path : path[(dot + 1)..];
    }
}
