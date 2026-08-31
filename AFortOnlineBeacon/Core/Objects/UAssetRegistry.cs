namespace AFortOnlineBeacon.Core.Objects;

/// <summary>
///     Models a plain, already-loaded on-disk asset (a UObject inside a /Game package) so it can be
///     referenced over the network by path - e.g. AFortPlayerState::HeroType, which points at a
///     UFortHeroType asset rather than at anything this server spawns.
///
///     Every object reference this project sent before this was either a dynamically-spawned actor
///     (fresh NetGUID, no path) or a class default object (UClass.CreateDefaultObject). An asset is
///     the third case: it needs a NetGUID exported WITH its path, which
///     UPackageMapClient.ExportNetGUID / InternalWriteObject's bHasPath branch already does - the
///     only thing missing was an object to hand it. One instance per path, because FNetGUIDCache's
///     lookup is keyed by object identity.
///
///     The RF_WasLoaded flag is what makes these name-stable: real UE's
///     UObject::IsNameStableForNetworking (Obj.cpp:4515) is
///     HasAnyFlags(RF_WasLoaded | RF_DefaultSubObject) || IsNative() || IsDefaultSubobject(),
///     and a loaded asset is precisely the RF_WasLoaded case.
///
///     Paths are the usual "package.object" form, e.g.
///     "/Game/Athena/Heroes/HID_001_Athena_Commando_F.HID_001_Athena_Commando_F". Note the client
///     must ALREADY have the asset loaded - this exports a reference, it does not stream content -
///     and an unresolvable path simply leaves the property unmapped client-side rather than
///     erroring, so a wrong path shows up as a silently missing value, not a disconnect.
/// </summary>
internal static class UAssetRegistry {
    private static readonly Dictionary<string, UObject> Assets = new();

    /// <summary>
    ///     The other direction, for a reference that came BACK from a client as a bare NetGUID.
    ///
    ///     A client can only export a path for an object it has no id for; once the server has
    ///     introduced one (an emote asset, say), the client's own NetGUIDLookup has it and every
    ///     later reference is just the packed id - a real capture shows the same ServerPlayEmoteItem
    ///     costing 94.4 bytes the first time and 5.4 bytes the second. The guid resolves back to the
    ///     very object this registry handed out, so the path is still knowable; it just has to be
    ///     looked up rather than read off the wire. See FRpcReader's AssetPath kind.
    /// </summary>
    private static readonly Dictionary<UObject, string> AssetPaths = new();

    public static string? PathOf(UObject asset) => AssetPaths.GetValueOrDefault(asset);

    public static UObject GetOrCreate(string path) {
        if (Assets.TryGetValue(path, out var existing)) return existing;

        var dot = path.LastIndexOf('.');
        if (dot < 0) throw new ArgumentException($"UAssetRegistry: '{path}' is not a package.object path", nameof(path));

        var asset = new UObject();
        asset.InitializeObjectProperties(UPackageRegistry.GetOrCreate(path[..dot]), new FName(path[(dot + 1)..]));
        asset.SetFlags(EObjectFlags.RF_Public | EObjectFlags.RF_WasLoaded);

        Assets[path] = asset;
        AssetPaths[asset] = path;
        return asset;
    }

    /// <summary>
    ///     Same idea as <see cref="GetOrCreate"/> but for an object nested several levels deep, e.g. a
    ///     net-startup actor placed in a map:
    ///
    ///         /Game/Athena/Maps/Athena_Terrain.Athena_Terrain:PersistentLevel.DO_NOT_DELETE_FortWorldManager
    ///
    ///     UPackageMapClient.InternalWriteObject exports an object by (name, outer's NetGUID) and
    ///     recurses on the outer, so the whole chain has to exist as real UObjects here or the client
    ///     gets a package name it cannot find. That chain is
    ///     UPackage -> UWorld -> ULevel("PersistentLevel") -> AActor, and the separators in the path
    ///     ('.' and ':') are both just "next object down".
    /// </summary>
    public static UObject GetOrCreateSubObject(string path) => GetOrCreateSubObject<UObject>(path);

    /// <summary>
    ///     Same as <see cref="GetOrCreateSubObject(string)"/>, but the LEAF of the chain is a real
    ///     <typeparamref name="T"/> instead of a plain UObject - needed for a net-startup ACTOR placed
    ///     in a map, where a bare UObject cannot be handed to UActorChannel.SetChannelActor. Everything
    ///     that makes the chain stably-named (RF_WasLoaded on every link, bContainsMap on the
    ///     PersistentLevel crossing) is identical; only the leaf's runtime type differs.
    /// </summary>
    public static T GetOrCreateSubObject<T>(string path) where T : UObject, new() {
        if (Assets.TryGetValue(path, out var existing)) return (T) existing;

        var lastSlash = path.LastIndexOf('/');
        var firstDot = path.IndexOf('.', lastSlash + 1);
        if (firstDot < 0) throw new ArgumentException($"UAssetRegistry: '{path}' has no package/object separator", nameof(path));

        var package = UPackageRegistry.GetOrCreate(path[..firstDot]);
        UObject outer = package;
        var components = path[(firstDot + 1)..].Split('.', ':');
        T leaf = null!;

        for (var i = 0; i < components.Length; i++) {
            var component = components[i];

            // "PersistentLevel" only ever exists as a subobject of a UWorld, so a path that walks
            // through one is by definition a path into a map package. That matters on the wire: see
            // UPackage.bContainsMap and FNetGUIDCache.CanClientLoadObject - the client must never be
            // asked to wait on a GUID it can only resolve by loading a map.
            if (component == "PersistentLevel") package.bContainsMap = true;

            var isLeaf = i == components.Length - 1;
            UObject child = isLeaf ? leaf = new T() : new UObject();
            child.InitializeObjectProperties(outer, new FName(component));
            child.SetFlags(EObjectFlags.RF_Public | EObjectFlags.RF_WasLoaded);
            outer = child;
        }

        Assets[path] = leaf;
        return leaf;
    }
}
