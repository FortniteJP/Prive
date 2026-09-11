namespace AFortOnlineBeacon.Core.Objects;

/// <summary>
///     The stably-named stand-ins for actors that already exist in the client's map - chests, doors,
///     destructible scenery. <b>One registry per world</b>, unlike <see cref="UAssetRegistry"/>'s
///     plain assets, which are shared process-wide.
///     <para>
///         That split is the whole reason this class exists. A plain asset - an item definition, a
///         hero type, a gameplay effect - is immutable and only ever exported as a path, so a single
///         shared instance is correct and cheap. A stand-in is the opposite: ABuildingContainer's
///         <c>bAlreadySearched</c> IS the looted state of that chest, ABuildingWall carries a door's
///         swing, and an ABuildingActor's health is what is left of a tree. Those are match state.
///     </para>
///     <para>
///         Cached process-wide, as they were, the process boundary was the only thing resetting
///         them - fine while every match was its own game-server process, wrong the moment the
///         server is hosted in-process: a second match would start with the first match's chests
///         already open and its walls already gone, and two matches running at once would share
///         them outright.
///     </para>
/// </summary>
public sealed class UMapActorRegistry {
    private readonly Dictionary<string, UObject> StandIns = new();
    private readonly object Gate = new();

    /// <summary>
    ///     A stand-in for an object nested several levels deep, e.g. a net-startup actor placed in a
    ///     map:
    ///
    ///         /Game/Athena/Maps/Athena_Terrain.Athena_Terrain:PersistentLevel.DO_NOT_DELETE_FortWorldManager
    ///
    ///     UPackageMapClient.InternalWriteObject exports an object by (name, outer's NetGUID) and
    ///     recurses on the outer, so the whole chain has to exist as real UObjects here or the client
    ///     gets a package name it cannot find. That chain is
    ///     UPackage -> UWorld -> ULevel("PersistentLevel") -> AActor, and the separators in the path
    ///     ('.' and ':') are both just "next object down".
    /// </summary>
    public UObject GetOrCreate(string path) => GetOrCreate<UObject>(path);

    /// <summary>
    ///     Same as <see cref="GetOrCreate(string)"/>, but the LEAF of the chain is a real
    ///     <typeparamref name="T"/> instead of a plain UObject - needed for a net-startup ACTOR placed
    ///     in a map, where a bare UObject cannot be handed to UActorChannel.SetChannelActor. Everything
    ///     that makes the chain stably-named (RF_WasLoaded on every link, bContainsMap on the
    ///     PersistentLevel crossing) is identical; only the leaf's runtime type differs.
    /// </summary>
    public T GetOrCreate<T>(string path) where T : UObject, new() {
        lock (Gate) {
            if (StandIns.TryGetValue(path, out var existing)) return (T) existing;

            var lastSlash = path.LastIndexOf('/');
            var firstDot = path.IndexOf('.', lastSlash + 1);
            if (firstDot < 0) throw new ArgumentException($"UMapActorRegistry: '{path}' has no package/object separator", nameof(path));

            // The PACKAGE stays shared: it is immutable metadata, and bContainsMap below is only ever
            // set to true, so two worlds setting it agree. Everything under it is this world's own.
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

            StandIns[path] = leaf;
            return leaf;
        }
    }
}
