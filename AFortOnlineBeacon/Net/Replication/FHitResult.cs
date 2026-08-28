namespace AFortOnlineBeacon.Net.Replication;

/// <summary>
///     Engine.FHitResult, receive side only - what a trace hit, as the CLIENT saw it.
///
///     This is the payload every shot and every pickaxe swing carries, wrapped in an
///     FGameplayAbilityTargetDataHandle (see FGameplayAbilityTargetDataHandle). Until it could be
///     read, this server knew a round had left the barrel and nothing whatsoever about where it
///     went; with it, <see cref="ActorPath"/> names the thing that was hit.
///
///     FHitResult::NetSerialize (Collision.cpp:42) is HAND-WRITTEN, not a property walk, so the
///     member order on the wire is not the struct's declaration order and five of the fields are
///     conditional on a leading flag byte. Two of those flags are pure compression - ImpactPoint and
///     ImpactNormal are usually identical to Location and Normal and are then simply not sent - and
///     three say "this index/depth is at its default", which is why a hit on ordinary static
///     geometry is much smaller than the struct suggests.
///
///     Note the two DIFFERENT vector encodings. Location/ImpactPoint/TraceStart/TraceEnd are
///     FVector_NetQuantize (packed, 1/20); Normal/ImpactNormal are FVector_NetQuantizeNormal
///     (fixed-width, 1/16). Reading a normal with the packed reader consumes a bit-count header that
///     is not there and shifts everything after it - and everything after it is the object
///     references, i.e. exactly what we came for.
/// </summary>
public class FHitResult {
    public bool bBlockingHit;
    public bool bStartPenetrating;

    /// <summary>Where along the trace the hit happened, 0..1.</summary>
    public float Time;

    public FVector Location = new();
    public FVector Normal = new();
    public FVector ImpactPoint = new();
    public FVector ImpactNormal = new();
    public FVector TraceStart = new();
    public FVector TraceEnd = new();

    public float PenetrationDepth;

    /// <summary>Which element of the hit primitive was struck, or INDEX_NONE.</summary>
    public int Item = -1;

    public int FaceIndex = -1;

    /// <summary>
    ///     The actor that was hit - null whenever the client named something this server never gave
    ///     a NetGUID to, which is the usual case for map geometry. <see cref="ActorPath"/> still
    ///     carries the name in that case; see UPackageMapClient.ReadObjectRef.
    /// </summary>
    public UObject? Actor;

    public string ActorPath = string.Empty;

    public UObject? Component;
    public string ComponentPath = string.Empty;

    public UObject? PhysMaterial;
    public string PhysMaterialPath = string.Empty;

    /// <summary>Which bone of a skeletal mesh was hit - how Fortnite tells a headshot from a body shot.</summary>
    public FName? BoneName;

    /// <summary>The best name we have for what was hit: the resolved object if there is one, else the exported path.</summary>
    public string HitName => Actor?.GetFName().ToString() ?? (ActorPath.Length > 0 ? ActorPath : "(nothing)");

    /// <summary>
    ///     <paramref name="resolvePath"/> names an object that arrived as a bare NetGUID. A live
    ///     server resolves those through its own package map and does not need it; the offline
    ///     capture decoder has only the export bunches it recorded, and without this every hit after
    ///     the first on the same tree reads as "(nothing)".
    /// </summary>
    public static FHitResult NetSerializeRead(FArchive ar, Func<uint, string?>? resolvePath = null) {
        var hit = new FHitResult();

        // Ar.SerializeBits(&Flags, 7) - seven single bits, LSB first, NOT a byte. The last five are
        // "the value that follows is absent" flags, so they have to be read before anything else can
        // be sized.
        hit.bBlockingHit = ar.ReadBit();
        hit.bStartPenetrating = ar.ReadBit();
        var bImpactPointEqualsLocation = ar.ReadBit();
        var bImpactNormalEqualsNormal = ar.ReadBit();
        var bInvalidItem = ar.ReadBit();
        var bInvalidFaceIndex = ar.ReadBit();
        var bNoPenetrationDepth = ar.ReadBit();

        hit.Time = ar.ReadFloat();

        hit.Location = FVector.NetSerializeReadQuantized(ar, 1, 20);
        hit.Normal = FVector.NetSerializeReadFixed(ar, 1, 16);

        hit.ImpactPoint = bImpactPointEqualsLocation
            ? hit.Location
            : FVector.NetSerializeReadQuantized(ar, 1, 20);

        hit.ImpactNormal = bImpactNormalEqualsNormal
            ? hit.Normal
            : FVector.NetSerializeReadFixed(ar, 1, 16);

        hit.TraceStart = FVector.NetSerializeReadQuantized(ar, 1, 20);
        hit.TraceEnd = FVector.NetSerializeReadQuantized(ar, 1, 20);

        if (!bNoPenetrationDepth) hit.PenetrationDepth = ar.ReadFloat();
        if (!bInvalidItem) hit.Item = ar.ReadInt32();

        // Three object references back to back. Each is a TWeakObjectPtr, which in a net archive is
        // serialized as its bare UObject* (FArchiveUObject::SerializeWeakObjectPtr) - so they cost
        // exactly what any other object reference costs, and carry a full exported path each time
        // for anything the server has not itself introduced to this client.
        hit.PhysMaterial = ReadRef(ar, resolvePath, out hit.PhysMaterialPath);
        hit.Actor = ReadRef(ar, resolvePath, out hit.ActorPath);
        hit.Component = ReadRef(ar, resolvePath, out hit.ComponentPath);

        FName? boneName = null;
        UPackageMap.StaticSerializeName(ar, ref boneName);
        hit.BoneName = boneName;

        if (!bInvalidFaceIndex) hit.FaceIndex = ar.ReadInt32();

        return hit;
    }

    /// <summary>
    ///     One reference, named however it can be: by the path the client exported, by the object
    ///     this server resolved, or by whatever the caller's own guid table knows.
    /// </summary>
    private static UObject? ReadRef(FArchive ar, Func<uint, string?>? resolvePath, out string pathName) {
        var obj = UPackageMapClient.ReadObjectRef(ar, out var netGuid, out pathName);

        if (pathName.Length == 0) {
            pathName = obj?.GetFName().ToString() ?? resolvePath?.Invoke(netGuid.Value) ?? string.Empty;
        }

        return obj;
    }

    public override string ToString() {
        var bone = BoneName is { } name && !name.ToString().Equals("None", StringComparison.Ordinal)
            ? $" Bone={name}"
            : string.Empty;

        return $"{HitName} at {ImpactPoint}{bone} Blocking={bBlockingHit} Time={Time:F3}";
    }
}
