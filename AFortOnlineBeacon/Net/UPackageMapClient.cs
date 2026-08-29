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
    private readonly List<FNetworkGUID> _mustBeMappedGuidsInLastBunch = new();

    public void Initialize(UNetConnection connection, FNetGUIDCache guidCache) {
        Connection = connection;
        GuidCache = guidCache;
    }

    public bool IsNetGUIDAuthority() => GuidCache?.IsNetGUIDAuthority() ?? true;

    /// <summary>
    ///     UPackageMapClient::GetMustBeMappedGuidsInLastBunch. GUIDs referenced since the last bunch
    ///     was flushed that name an asset the client may still have to load. UChannel.SendBunch
    ///     drains this into the front of the outgoing bunch - see UChannel.AppendMustBeMappedGuids.
    /// </summary>
    public List<FNetworkGUID> GetMustBeMappedGuidsInLastBunch() => _mustBeMappedGuidsInLastBunch;

    /// <summary>Writes a reference to `obj` (assigning it a NetGUID if it doesn't have one yet), exporting its path the first time it's referenced.</summary>
    public FNetworkGUID SerializeObject(FArchive ar, UObject? obj) {
        var netGuid = GuidCache!.GetOrAssignNetGUID(obj);

        InternalWriteObject(ar, netGuid, obj, string.Empty, null);

        if (!netGuid.IsDefault() && obj != null && ShouldSendFullPath(obj, netGuid)) ExportNetGUID(netGuid, obj, string.Empty, null);

        return netGuid;
    }

    /// <summary>
    ///     The read half of <see cref="SerializeObject"/> - UPackageMapClient::InternalLoadObject
    ///     (PackageMapClient.cpp), reduced to what a SERVER can legitimately receive.
    ///
    ///     Two shapes arrive. The common one is a bare NetGUID naming something this server itself
    ///     exported, which is just a cache lookup. The other is a DEFAULT guid (value 1) followed by
    ///     export flags, the outer's reference, and a path string: that is a client naming an object
    ///     the server never gave an id to - a default subobject it created locally, like an
    ///     AbilitySystemComponent. A server cannot spawn objects on a client's say-so
    ///     (DataChannel.cpp:3376, "Client attempted to create sub-object"), so an unresolvable path
    ///     returns null; <paramref name="pathName"/> still comes back so the caller can say WHAT it
    ///     could not resolve, which is the whole diagnostic value of reading this at all.
    /// </summary>
    public UObject? SerializeObjectRead(FArchive ar, out FNetworkGUID netGuid, out string pathName) {
        var bHasPath = ReadObjectReference(ar, out netGuid, out pathName);

        // A path means the client named something this server never assigned an id to, so there is
        // nothing in the cache to look up - and inventing an object for a client-supplied name is
        // exactly what real UE refuses to do on the server (DataChannel.cpp:3376, "Client attempted
        // to create sub-object"). The caller still has the path, which is the whole diagnostic value.
        if (ar.IsError() || bHasPath) return null;

        return GuidCache?.GetObjectFromNetGUID(netGuid);
    }

    /// <summary>Matches real UE's INTERNAL_LOAD_OBJECT_RECURSION_LIMIT - a malformed outer chain must not recurse forever.</summary>
    private const int InternalLoadObjectRecursionLimit = 16;

    /// <summary>
    ///     The PARSE half of UPackageMapClient::InternalLoadObject, with no resolution and no
    ///     dependence on a package map: it consumes exactly the bits an object reference occupies and
    ///     reports what it saw. Returns true when the reference carried an exported path (and so has
    ///     no server-assigned id behind it).
    ///
    ///     Separate from resolution on purpose. Consuming the right number of bits is what keeps the
    ///     rest of a bunch readable, and it is decided entirely by the wire - whereas resolution
    ///     needs a live GUID cache. Splitting them lets the offline capture decoder run this exact
    ///     code against a real Project-Reboot-3.0 recording, which is the only way any of this gets
    ///     checked without a client in the loop.
    /// </summary>
    public static bool ReadObjectReference(FArchive ar, out FNetworkGUID netGuid, out string pathName,
                                           int recursionCount = 0) {
        netGuid = new FNetworkGUID();
        pathName = string.Empty;

        if (recursionCount > InternalLoadObjectRecursionLimit) {
            Console.WriteLine("ReadObjectReference: recursion limit reached, refusing to follow the outer chain further");
            ar.SetError();
            return false;
        }

        netGuid.NetSerialize(ar);
        if (ar.IsError() || !netGuid.IsValid()) return false;

        // Export flags only follow a DEFAULT guid here. Real UE also reads them while processing a
        // NetGUID export bunch, but those arrive on their own path and never through an actor
        // channel's content block, which is the only caller of this.
        //
        // FExportFlags (PackageMapClient.h) is a BITFIELD, not a single flag:
        //   bit 0 bHasPath, bit 1 bNoLoad, bit 2 bHasNetworkChecksum.
        var bHasPath = false;
        var bHasNetworkChecksum = false;

        if (netGuid.IsDefault()) {
            var exportFlags = ar.ReadByte();
            if (ar.IsError()) return false;

            bHasPath = (exportFlags & 1) != 0;
            bHasNetworkChecksum = (exportFlags & 4) != 0;
        }

        if (!bHasPath) return false;

        ReadObjectReference(ar, out _, out var outerPath, recursionCount + 1);
        if (ar.IsError()) return true;

        var name = ar.ReadString();
        if (ar.IsError()) return true;

        // A uint32 that FOLLOWS the path (InternalWriteObject, PackageMapClient.cpp:740-746). The
        // client's FNetGUIDCache::NetworkChecksumMode defaults to SaveAndUse, so every path a client
        // exports carries one, and skipping it leaves four bytes of someone else's data in the
        // stream. That went unnoticed for as long as the only thing read after a content block
        // header was NumPayloadBits, which resynchronises regardless - but an object reference read
        // MID-payload (an FHitResult's Actor, say) has nothing to resync against, and the very next
        // field decodes as garbage.
        if (bHasNetworkChecksum) {
            ar.ReadUInt32();
            if (ar.IsError()) return true;
        }

        pathName = string.IsNullOrEmpty(outerPath) ? name : $"{outerPath}.{name}";
        return true;
    }

    /// <summary>
    ///     Reads one object reference and resolves it if this archive can. Everything a client sends
    ///     that names an object goes through here - an RPC parameter, an FHitResult's
    ///     Actor/Component/PhysMaterial, an FGameplayAbilityTargetDataHandle's UScriptStruct - so
    ///     they all get identical treatment for exports and for unresolvable paths.
    ///
    ///     A CLIENT can never assign a NetGUID (FNetGUIDCache::GetOrAssignNetGUID returns the default
    ///     guid when !IsNetGUIDAuthority), so anything the server never introduced arrives as the
    ///     default guid plus its full path, EVERY time - which is why an unresolved reference still
    ///     yields a usable name.
    /// </summary>
    public static UObject? ReadObjectRef(FArchive ar, out string pathName) =>
        ReadObjectRef(ar, out _, out pathName);

    /// <summary>
    ///     As above, but also hands back the NetGUID. Worth having whenever an unresolved reference
    ///     is still worth naming: a reference with no path is one the SERVER introduced, so its id
    ///     is the only handle on it a caller has left.
    /// </summary>
    public static UObject? ReadObjectRef(FArchive ar, out FNetworkGUID netGuid, out string pathName) {
        var bHasPath = ReadObjectReference(ar, out netGuid, out pathName);
        if (ar.IsError() || bHasPath) return null;

        return ar is FNetBitReader { PackageMap: UPackageMapClient packageMap }
            ? packageMap.GuidCache?.GetObjectFromNetGUID(netGuid)
            : null;
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

        // SerializeCompressedInitial (PackageMapClient.cpp:444-503). Each flag is IMMEDIATELY followed
        // by its value when set - they are not four flags up front. Writing all four as false, as this
        // used to, produces a byte-identical stream to the real thing for an actor at the default
        // transform, which is why it went unnoticed; it just could not express anything else.
        //
        // Location matters because the client spawns the pawn wherever we say and then runs its own
        // physics: with nothing sent, a real client put the pawn at the origin and it fell to
        // Z=-5042, below the landscape (which sits at Z=-1692). Scale/Velocity are still left at
        // their defaults - there is no component system here to source them from.
        var location = actor.GetActorLocation();
        var bSerializeLocation = !location.IsNearlyZero();
        bunch.SerializeBits(&bSerializeLocation, 1);
        if (bSerializeLocation) location.NetSerializeWriteQuantized(bunch, 10, 24); // FVector_NetQuantize10

        // Rotation matters for exactly the same reason location does, and for building pieces it is
        // the whole game: a wall's yaw is what decides which side of the tile it sits on. This used
        // to be hardcoded false, so every spawned actor arrived at ZeroRotator and every placed
        // building faced the same way regardless of what the client asked for.
        var rotation = actor.GetActorRotation();
        var bSerializeRotation = !rotation.IsNearlyZero();
        bunch.SerializeBits(&bSerializeRotation, 1);
        if (bSerializeRotation) rotation.NetSerializeWrite(bunch); // FRotator::NetSerialize -> SerializeCompressedShort

        var bSerializeScale = false;
        bunch.SerializeBits(&bSerializeScale, 1);

        var bSerializeVelocity = false;
        bunch.SerializeBits(&bSerializeVelocity, 1);
    }

    private unsafe void InternalWriteObject(FArchive ar, FNetworkGUID netGuid, UObject? obj, string objectPathName, UObject? objectOuter) {
        // PackageMapClient.cpp:645-655. Any static GUID the client is capable of loading by path has
        // to be announced ahead of the bunch that uses it, so the client can hold that channel's
        // bunches until the async load finishes instead of processing a reference it cannot resolve
        // yet. Skipping this is what produced "Unresolved Archetype GUID. Path: Default__TODM_BR_C"
        // on the first attempt at spawning a Blueprint actor the client had not already loaded: a
        // path export NAMES an asset, it does not stream one, and without the announcement the
        // client had no reason to wait for it.
        //
        // Deliberately not done while writing the export bunch itself (those GUIDs are the
        // announcement) nor for anything CanClientLoadObject rejects.
        var bNoLoad = !GuidCache!.CanClientLoadObject(obj, netGuid);

        if (GuidCache.ShouldAsyncLoad() && IsNetGUIDAuthority() && !GuidCache.IsExportingNetGUIDBunch
            && !bNoLoad && !_mustBeMappedGuidsInLastBunch.Contains(netGuid)) {
            _mustBeMappedGuidsInLastBunch.Add(netGuid);
        }

        netGuid.NetSerialize(ar);

        if (!netGuid.IsValid()) return;

        var bHasPath = false;

        if (netGuid.IsDefault()) {
            // Only clients send default GUIDs - we're always the server, so this path is unused here,
            // kept only for symmetry with the real implementation.
            bHasPath = true;
            ar.WriteByte(1);
        } else if (GuidCache.IsExportingNetGUIDBunch) {
            bHasPath = obj != null ? ShouldSendFullPath(obj, netGuid) : !string.IsNullOrEmpty(objectPathName);

            // FExportFlags (PackageMapClient.h): bit 0 bHasPath, bit 1 bNoLoad, bit 2 bHasNetworkChecksum.
            // bNoLoad says "resolve this by name only, never try to load a package for it" - true for
            // anything CanClientLoadObject rejects, which includes a component whose outer is a
            // runtime-spawned actor. Every asset export this project already relies on is loadable,
            // so for those the byte is unchanged.
            ar.WriteByte((byte) ((bHasPath ? 1 : 0) | (bNoLoad ? 2 : 0)));
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
