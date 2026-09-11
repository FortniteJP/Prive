namespace AFortOnlineBeacon.Core.Objects;

/// <summary>
///     Caches one UPackage instance per native package path (e.g. "/Script/Engine"), so repeated
///     lookups for the same package always resolve to the same object identity - required for
///     FNetGUIDCache's NetGUIDLookup, which is keyed by object reference.
/// </summary>
internal static class UPackageRegistry {
    private static readonly Dictionary<string, UPackage> Packages = new();
    private static readonly object Gate = new();

    public static UPackage GetOrCreate(string path) {
        lock (Gate) {
            if (Packages.TryGetValue(path, out var existing)) return existing;

            var package = new UPackage();
            package.InitializeObjectProperties(null, new FName(path));

            Packages[path] = package;
            return package;
        }
    }
}
