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

    /// <summary>
    ///     FNetGUIDCache::ShouldAsyncLoad (PackageMapClient.cpp:3324). Real UE resolves this through
    ///     AsyncLoadMode / the net.AllowAsyncLoading CVar; the value that matters here is the
    ///     CLIENT's, because UActorChannel::ReceivedBunch drops every must-be-mapped GUID it reads
    ///     when its own ShouldAsyncLoad() is false. The 10.40 client's is on - it logs
    ///     "GetObjectFromNetGUID: Async loading package. Path: /Game/TimeOfDay/TODM/BR/TODM_BR",
    ///     a line that only exists inside a ShouldAsyncLoad() branch - so announcing these GUIDs
    ///     actually buys us the client-side bunch queueing. NET_ASYNC_LOAD=0 turns it back off.
    /// </summary>
    public bool ShouldAsyncLoad() => Environment.GetEnvironmentVariable("NET_ASYNC_LOAD") != "0";

    /// <summary>
    ///     FNetGUIDCache::CanClientLoadObject (PackageMapClient.cpp:610-639). Answers "could the
    ///     client pull this object in by itself, given only its path?" - which decides whether the
    ///     server may announce it as a must-be-mapped GUID and let the client stall the channel
    ///     until the load finishes.
    ///
    ///     No for dynamic GUIDs (runtime-spawned objects exist only because we told the client to
    ///     spawn them), and no for anything inside a map package (the client resolves those only by
    ///     travelling to the map). Everything else - Blueprint classes, item definitions, playlists -
    ///     is a normal asset it can async-load on demand.
    /// </summary>
    public bool CanClientLoadObject(UObject? obj, FNetworkGUID netGuid) {
        if (!netGuid.IsValid() || netGuid.IsDynamic()) return false;

        if (obj != null) return obj.GetOutermost() is not UPackage { bContainsMap: true };

        // Object already gone: fall back to whatever we decided when the GUID was registered.
        return !IsGUIDNoLoad(netGuid);
    }

    public bool IsGUIDNoLoad(FNetworkGUID netGuid) =>
        ObjectLookup.TryGetValue(netGuid, out var cacheObject) && cacheObject.bNoLoad;

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
            bNoLoad = !CanClientLoadObject(obj, netGuid)
        };

        ObjectLookup[netGuid] = cacheObject;
        NetGUIDLookup[obj] = netGuid;
    }
}
