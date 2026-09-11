using AFortOnlineBeacon.Runtime;
using AFortOnlineBeacon.Net.Actors;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     A vehicle Blueprint's ClassNetCache - the native chain plus whatever the Blueprint adds.
///
///     WHY THE BLUEPRINT'S OWN FIELDS MATTER, and it is not about naming them correctly: an RPC's
///     field index travels as a bounded int whose WIDTH comes from the cache's MaxIndex. A Blueprint
///     that adds five net fields makes that number wider, so a server decoding with only the native
///     fields reads too few bits and every bit after the index is garbage. The whole bunch is lost,
///     not just its label.
///
///     Same idea and same shape as FortWeaponNetCaches, which exists because weapons hit this first.
/// </summary>
internal static partial class FortVehicleNetCaches {
    private static readonly Dictionary<string, FClassNetCache> Built = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The cache for this vehicle, built once per Blueprint class on top of the native
    ///     AFortAthenaVehicle chain.
    ///
    ///     A CLASS THE TABLE DOES NOT KNOW gets the native chain and says so once. That is the right
    ///     fallback rather than a guess: a Blueprint that adds nothing needs exactly the native chain,
    ///     and one that adds fields cannot be served correctly by any invention here - it needs
    ///     Tools/VehicleNetCaches re-run.
    /// </summary>
    /// <summary>
    ///     Whether to use the DERIVED vehicle chain at all. Off by default, and that is an honest
    ///     statement about the data rather than caution: the native own-field lists were read out of
    ///     the SDK headers, and those headers carry no FUNC_Net flag for functions - which ones are
    ///     RPCs was inferred from their NAMES. A single missing or invented field changes MaxIndex and
    ///     therefore the width of every field index, and the result is not a mislabelled field, it is
    ///     a bunch decoded as noise.
    ///
    ///     Nothing needs vehicle RPCs yet - driving works through the pawn and the controller - so
    ///     until the field list can be verified against a runtime dump (Tools/NetFieldVerify does this
    ///     for weapons), the honest behaviour is to leave those blocks alone.
    ///     VEHICLE_RPC_DECODE=1 turns the attempt back on.
    /// </summary>
    public static bool DecodeEnabled => FBeaconProcess.Options.Get("VEHICLE_RPC_DECODE") is "1";

    public static FClassNetCache For(AFortAthenaVehicle vehicle, FClassNetCache nativeChain) {
        // Shared by every world, so get-or-build is locked: a Dictionary written from two threads
        // at once is corrupted, not merely raced.
        lock (Built) return ForLocked(vehicle, nativeChain);
    }

    private static FClassNetCache ForLocked(AFortAthenaVehicle vehicle, FClassNetCache nativeChain) {
        var className = vehicle.GetClass()?.GetFName().ToString();
        if (className == null) return nativeChain;

        if (Built.TryGetValue(className, out var cached)) return cached;

        if (!OwnFields.TryGetValue(className, out var fields)) {
            if (Warned.TryAdd(className, 0))
                Console.WriteLine($"FortVehicleNetCaches: '{className}' is not in the generated table - using the " +
                                  "native AFortAthenaVehicle chain. If this Blueprint adds replicated fields, every " +
                                  "RPC it sends will decode at the wrong field width. Re-run " +
                                  "Tools/VehicleNetCaches/gen_vehicle_caches.py.");

            Built[className] = nativeChain;
            return nativeChain;
        }

        // Sorted the way UClass::SetUpRuntimeReplicationData sorts NetFields - properties and
        // functions in ONE list, by name, case-insensitively (FString::operator< compares with
        // ESearchCase::IgnoreCase).
        var sorted = (string[]) fields.Clone();
        Array.Sort(sorted, StringComparer.OrdinalIgnoreCase);

        var built = new FClassNetCache(nativeChain, sorted);
        Built[className] = built;

        Console.WriteLine($"FortVehicleNetCaches: '{className}' adds {sorted.Length} net field(s) - " +
                          $"maxIndex {built.GetMaxIndex()}.");

        return built;
    }
}
