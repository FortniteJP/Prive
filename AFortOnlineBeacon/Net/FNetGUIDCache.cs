namespace AFortOnlineBeacon.Net;

/// <summary>
///     Server-side bookkeeping for a single tracked object: the info the client needs in order to
///     resolve a static GUID by path (mirrors the subset of FNetGuidCacheObject we actually use).
/// </summary>
public class FNetGuidCacheObject {
    public UObject? Object;
    public FNetworkGUID OuterGUID = new FNetworkGUID();
    public FName PathName;
    public bool bNoLoad;
}

/// <summary>
///     Server-only, simplified port of FNetGUIDCache. Only the write/assign path is implemented -
///     this codebase never runs as a network client, so the load/resolve path is intentionally absent.
/// </summary>
public class FNetGUIDCache {
    public FNetGUIDCache(UNetDriver driver) => Driver = driver;

    public UNetDriver Driver { get; }

    public Dictionary<FNetworkGUID, FNetGuidCacheObject> ObjectLookup { get; } = new();

    public Dictionary<UObject, FNetworkGUID> NetGUIDLookup { get; } = new();

    /// <summary>Set true only while UPackageMapClient is writing a GUID-export bunch.</summary>
    public bool IsExportingNetGUIDBunch { get; set; }

    private readonly uint[] _uniqueNetIds = new uint[2]; // index 0 = dynamic, index 1 = static

    public bool IsNetGUIDAuthority() => Driver.IsServer();

    public bool IsDynamicObject(UObject obj) => !obj.IsFullNameStableForNetworking();

    public bool SupportsObject(UObject? obj) {
        if (obj == null) return true;

        if (NetGUIDLookup.TryGetValue(obj, out var existing) && existing.IsValid()) return true;

        if (obj.IsFullNameStableForNetworking()) return true;

        // Not name-stable (e.g. a runtime-spawned actor) - still supported if the server is allowed
        // to just tell the client to spawn/assign an id for it dynamically. Without this, every actor
        // GUID assignment silently fails and GetOrAssignNetGUID hands back an invalid (0) GUID.
        return obj.IsSupportedForNetworking();
    }

    public FNetworkGUID GetOrAssignNetGUID(UObject? obj) {
        if (obj == null || !SupportsObject(obj)) return new FNetworkGUID();

        if (NetGUIDLookup.TryGetValue(obj, out var netGuid) && netGuid.IsValid()) return netGuid;

        if (!IsNetGUIDAuthority()) return FNetworkGUID.GetDefault();

        return AssignNewNetGUID_Server(obj);
    }

    public FNetworkGUID GetNetGUID(UObject? obj) {
        if (obj == null) return new FNetworkGUID();
        return NetGUIDLookup.GetValueOrDefault(obj, new FNetworkGUID());
    }

    public FNetworkGUID AssignNewNetGUID_Server(UObject obj) {
        var isStatic = IsDynamicObject(obj) ? 0u : 1u;

        _uniqueNetIds[isStatic]++;

        var newGuid = new FNetworkGUID((_uniqueNetIds[isStatic] << 1) | isStatic);

        RegisterNetGUID_Server(newGuid, obj);

        return newGuid;
    }

    public void RegisterNetGUID_Server(FNetworkGUID netGuid, UObject obj) {
        var cacheObject = new FNetGuidCacheObject {
            Object = obj,
            OuterGUID = GetOrAssignNetGUID(obj.GetOuter()),
            PathName = obj.GetFName(),
            bNoLoad = netGuid.IsDynamic()
        };

        ObjectLookup[netGuid] = cacheObject;
        NetGUIDLookup[obj] = netGuid;
    }
}
