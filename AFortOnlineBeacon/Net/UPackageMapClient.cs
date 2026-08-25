namespace AFortOnlineBeacon.Net;

/// <summary>
///     Simplified, server-only port of UPackageMapClient. Implements just the write/export path
///     (assigning NetGUIDs, exporting the path of newly-referenced static objects, and serializing a
///     newly-spawned actor's header) - there is no load/resolve path since this codebase never runs
///     as a network client.
/// </summary>
public class UPackageMapClient : UPackageMap {
    public UNetConnection? Connection { get; private set; }
    public FNetGUIDCache? GuidCache { get; private set; }

    private FOutBunch? _currentExportBunch;
    private int _exportNetGuidCount;
    private readonly List<FNetworkGUID> _currentExportNetGuids = new();
    private readonly List<FOutBunch> _exportBunches = new();
    private readonly HashSet<uint> _exportedGuids = new();

    public void Initialize(UNetConnection connection, FNetGUIDCache guidCache) {
        Connection = connection;
        GuidCache = guidCache;
    }

    public bool IsNetGUIDAuthority() => GuidCache?.IsNetGUIDAuthority() ?? true;

    /// <summary>Writes a reference to `obj` (assigning it a NetGUID if it doesn't have one yet), exporting its path the first time it's referenced.</summary>
    public FNetworkGUID SerializeObject(FArchive ar, UObject? obj) {
        var netGuid = GuidCache!.GetOrAssignNetGUID(obj);

        InternalWriteObject(ar, netGuid, obj, string.Empty, null);

        if (!netGuid.IsDefault() && obj != null && ShouldSendFullPath(obj, netGuid)) ExportNetGUID(netGuid, obj, string.Empty, null);

        return netGuid;
    }

    /// <summary>
    ///     Writes a newly-spawned actor's channel-open header: the actor's own (dynamic) NetGUID, its
    ///     Archetype (so the client knows what to spawn), and its initial transform. The actor's Level
    ///     is always sent as "unresolved" so the client spawns it into whatever level it already has
    ///     loaded, rather than us needing to correctly export our own level's identity.
    /// </summary>
    public unsafe void SerializeNewActor(FOutBunch bunch, UActorChannel channel, AActor actor) {
        var netGuid = SerializeObject(bunch, actor);

        channel.ActorNetGUID = netGuid;

        if (!netGuid.IsDynamic()) return;

        var archetype = actor.GetArchetype();
        SerializeObject(bunch, archetype);

        // ActorLevel: sending "no object" (GUID 0) tells the client to use its own current level.
        SerializeObject(bunch, null);

        // We don't have a transform/component system yet, so every actor spawns at the origin -
        // this matches real UE's own wire-format optimization for actors at the default transform.
        var bSerializeLocation = false;
        var bSerializeRotation = false;
        var bSerializeScale = false;
        var bSerializeVelocity = false;

        bunch.SerializeBits(&bSerializeLocation, 1);
        bunch.SerializeBits(&bSerializeRotation, 1);
        bunch.SerializeBits(&bSerializeScale, 1);
        bunch.SerializeBits(&bSerializeVelocity, 1);
    }

    private unsafe void InternalWriteObject(FArchive ar, FNetworkGUID netGuid, UObject? obj, string objectPathName, UObject? objectOuter) {
        netGuid.NetSerialize(ar);

        if (!netGuid.IsValid()) return;

        var bHasPath = false;

        if (netGuid.IsDefault()) {
            // Only clients send default GUIDs - we're always the server, so this path is unused here,
            // kept only for symmetry with the real implementation.
            bHasPath = true;
            ar.WriteByte(1);
        } else if (GuidCache!.IsExportingNetGUIDBunch) {
            bHasPath = obj != null ? ShouldSendFullPath(obj, netGuid) : !string.IsNullOrEmpty(objectPathName);
            ar.WriteByte((byte) (bHasPath ? 1 : 0));
        }

        if (!bHasPath) return;

        if (obj != null) {
            objectPathName = obj.GetFName().ToString();
            objectOuter = obj.GetOuter();
        }

        var outerGuid = GuidCache!.GetOrAssignNetGUID(objectOuter);
        InternalWriteObject(ar, outerGuid, objectOuter, string.Empty, null);

        ar.WriteString(objectPathName);

        if (GuidCache.ObjectLookup.TryGetValue(netGuid, out var cacheObject)) {
            cacheObject.PathName = new FName(objectPathName);
            cacheObject.OuterGUID = outerGuid;
        }

        if (GuidCache.IsExportingNetGUIDBunch) _currentExportNetGuids.Add(netGuid);

        _exportedGuids.Add(netGuid.Value);
    }

    private bool ShouldSendFullPath(UObject obj, FNetworkGUID netGuid) {
        if (Connection == null || !netGuid.IsValid()) return false;
        if (!obj.IsNameStableForNetworking()) return false;
        if (netGuid.IsDefault()) return true;

        // Simplification: once we've exported a GUID's path, never re-export it. Real UE tracks
        // per-connection ack state and re-exports on NAK; we don't retry lost exports yet.
        return !_exportedGuids.Contains(netGuid.Value);
    }

    private bool ExportNetGUID(FNetworkGUID netGuid, UObject? obj, string pathName, UObject? objOuter) {
        if (_currentExportBunch == null) {
            _currentExportBunch = new FOutBunch(this, Connection!.GetMaxSingleBunchSizeBits()) {
                bHasPackageMapExports = true
            };
            _currentExportBunch.WriteBit(false); // Not a rep-layout export.
            _exportNetGuidCount = 0;
            _currentExportBunch.WriteInt32(_exportNetGuidCount); // Placeholder, patched in ExportNetGUIDHeader.
        }

        GuidCache!.IsExportingNetGUIDBunch = true;
        InternalWriteObject(_currentExportBunch, netGuid, obj, pathName, objOuter);
        GuidCache.IsExportingNetGUIDBunch = false;

        Console.WriteLine($"ExportNetGUID: guid={netGuid} obj={obj?.GetFName()} pathName='{pathName}' objOuter={objOuter?.GetFName()} currentExportNetGuidsCount={_currentExportNetGuids.Count} currentExportBunchIsError={_currentExportBunch.IsError()} currentExportBunchNumBits={_currentExportBunch.GetNumBits()}");

        if (_currentExportNetGuids.Count == 0) return false;

        if (_currentExportBunch.IsError()) {
            // Real UE retries in a fresh bunch here; our exports are small enough that we don't expect
            // to hit this, so just drop it rather than port the overflow-retry machinery.
            _currentExportNetGuids.Clear();
            return false;
        }

        _currentExportBunch.ExportNetGUIDs.AddRange(_currentExportNetGuids);
        _currentExportNetGuids.Clear();
        _exportNetGuidCount++;

        return true;
    }

    private void ExportNetGUIDHeader() {
        if (_currentExportBunch == null) return;

        PatchExportBunchHeaderCount(_currentExportBunch, _exportNetGuidCount);

        if (_currentExportBunch.ExportNetGUIDs.Count != 0) _exportBunches.Add(_currentExportBunch);

        _currentExportBunch = null;
        _exportNetGuidCount = 0;
    }

    private static void PatchExportBunchHeaderCount(FOutBunch bunch, int newCount) {
        var restore = new FBitWriterMark(bunch);
        var rewind = new FBitWriterMark();
        rewind.PopWithoutClear(bunch);

        bunch.WriteBit(false);
        bunch.WriteInt32(newCount);

        restore.PopWithoutClear(bunch);
    }

    /// <summary>Called by UChannel.SendBunch to prepend any pending GUID-export bunches ahead of the content bunch.</summary>
    public void AppendExportBunches(List<FOutBunch> outgoingBunches) {
        if (_exportNetGuidCount > 0) ExportNetGUIDHeader();

        if (_exportBunches.Count == 0) return;

        outgoingBunches.AddRange(_exportBunches);
        _exportBunches.Clear();
    }
}
