namespace AFortOnlineBeacon.Net.Channels.Actor;

public class UActorChannel : UChannel {
    public AActor? Actor { get; private set; }
    public FNetworkGUID ActorNetGUID { get; set; } = new FNetworkGUID();

    public UActorChannel() {
        ChType = EChannelType.CHTYPE_Actor;
        ChName = EName.Actor;
        // bClearRecentActorRefs = true;
        // bHoldQueuedExportBunchesAndGUIDs = false;
        // QueuedCloseReason = EChannelCloseReason::Destroyed;
    }

    /// <summary>Binds this channel to the actor it will replicate. Simplified port of UActorChannel::SetChannelActor.</summary>
    public void SetChannelActor(AActor inActor) {
        Actor = inActor;

        var packageMap = (UPackageMapClient) Connection!.PackageMap!;
        ActorNetGUID = packageMap.GuidCache!.GetOrAssignNetGUID(inActor);
    }

    /// <summary>
    ///     Properties marked dirty for the one-shot initial replication below. Both Role and
    ///     RemoteRole were confirmed working on a real client (2026-08-24) once
    ///     FRepLayout.WriteChangedProperties started writing the leading bDoChecksum bit that real
    ///     Fortnite 10.40's compiled FRepLayout::SendProperties always emits - see that file's
    ///     comment for the full story (that missing bit, not the handle numbering, was the actual
    ///     cause of every earlier disconnect).
    /// </summary>
    private static readonly HashSet<string> InitialReplicatedProperties = new() { "RemoteRole", "Role" };

    /// <summary>
    ///     One-shot version of UActorChannel::ReplicateActor - writes and sends the actor's spawn
    ///     header, followed by a property push via NativeRepLayouts/FRepLayout (this project's
    ///     minimal stand-in for FObjectReplicator::ReplicateProperties/FRepLayout::SendProperties).
    ///     Re-enabled 2026-08-24 after ground-truth packet capture from a real Project-Reboot-3.0
    ///     session (see AFortOnlineBeacon repo history / conversation notes) confirmed the handle
    ///     numbering NativeRepLayouts computes is correct: a captured real bunch's handle 10
    ///     decoded exactly as AttachmentReplication.RotationOffset (a plausible FRotator value) and
    ///     the very next handle was exactly 16, matching this project's independently-computed
    ///     PlayerState handle. That rules out the handle NUMBER as the cause of every previous
    ///     live-test failure (Role=9, Role=14, RemoteRole alone) - the bug has to be somewhere in
    ///     how the payload gets written/framed, not in NativeRepLayouts' arithmetic.
    /// </summary>
    public unsafe void ReplicateActor() {
        if (Actor == null || Connection == null) return;

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        var packageMap = (UPackageMapClient) Connection.PackageMap!;
        packageMap.SerializeNewActor(bunch, this, Actor);
        Actor.OnSerializeNewActor(bunch);

        var payload = new FNetBitWriter(Connection.PackageMap, 64);
        NativeRepLayouts.Get(Actor).WriteChangedProperties(payload, Actor, InitialReplicatedProperties);

        bunch.WriteBit(true); // bHasRepLayout
        bunch.WriteBit(true); // bIsActor

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        Console.WriteLine($"ReplicateActor: RemoteRole={Actor.RemoteRole} Role={Actor.Role} numPayloadBits={numPayloadBits} payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        SendBunch(bunch, false);
    }

    public override void Tick() {
        base.Tick();
        // TODO: ProcessQueuedBunches
    }

    public override bool CanStopTicking() {
        return base.CanStopTicking() /* PendingGuidResolves / QueuedBunches */;
    }

    /// <summary>
    ///     Simplified port of UActorChannel::ReceivedBunch. A bunch can contain several back-to-back
    ///     "content blocks" (one per replicated object). Each one starts with a 2-bit header
    ///     (bHasRepLayout, bIsActor) followed by a packed bit count and that many payload bits.
    ///     A content block's payload can itself contain several fields (property updates / RPC
    ///     calls) back-to-back - see ReadContentBlockFields.
    /// </summary>
    protected override unsafe void ReceivedBunch(FInBunch bunch) {
        while (!bunch.AtEnd() && !bunch.IsError()) {
            var bHasRepLayout = bunch.ReadBit();
            if (bunch.IsError()) break;

            var bIsActor = bunch.ReadBit();
            if (bunch.IsError()) break;

            if (!bIsActor) {
                // Sub-object content blocks need SerializeObject's load path (NetGUID resolution),
                // which we haven't implemented (see UPackageMapClient - save/write side only so far).
                // Bail out rather than misinterpret the rest of the bunch.
                Console.WriteLine($"UActorChannel.ReceivedBunch: sub-object content block not supported yet, dropping rest of bunch. ChIndex={ChIndex} Actor={Actor?.GetFName()}");
                return;
            }

            uint numPayloadBits = 0;
            bunch.SerializeIntPacked(&numPayloadBits);
            if (bunch.IsError()) break;

            var payloadStart = bunch.Pos;
            var payloadEnd = payloadStart + Math.Min((long) numPayloadBits, bunch.GetBitsLeft());

            var rawHex = HexDumpBits(bunch, payloadStart, Math.Min(payloadEnd, payloadStart + 512));
            var truncated = payloadEnd - payloadStart > 512 ? "..." : "";
            Console.WriteLine($"UActorChannel.ReceivedBunch: content block ChIndex={ChIndex} Actor={Actor?.GetFName()} bHasRepLayout={bHasRepLayout} numPayloadBits={numPayloadBits} raw={rawHex}{truncated}");

            if (Actor != null) ReadContentBlockFields(bunch, NativeClassNetCache.Get(Actor), payloadEnd);

            // The outer NumPayloadBits is ground truth regardless of how the inner field decode
            // above went (it relies on a best-effort, unverified ClassNetCache guess - see
            // NativeClassNetCache) - always resync here so a bad guess can never desync the bunch.
            bunch.Pos = payloadEnd;
        }
    }

    /// <summary>
    ///     Walks the individual fields (property updates / RPC calls) packed into one actor content
    ///     block's payload, per UActorChannel::ReadFieldHeaderAndPayload: each field is a bounded-int
    ///     RepIndex (range = ClassNetCache->GetMaxIndex()+1, real UE's FBitReader::ReadInt encoding,
    ///     NOT SerializeIntPacked), a packed bit count, then that many payload bits.
    /// </summary>
    private unsafe void ReadContentBlockFields(FInBunch bunch, FClassNetCache classCache, long payloadEnd) {
        var maxIndex = classCache.GetMaxIndex();

        while (bunch.Pos < payloadEnd && !bunch.IsError()) {
            var repIndex = (int) bunch.ReadInt((uint) (maxIndex + 1));
            if (bunch.IsError()) break;

            uint fieldNumBits = 0;
            bunch.SerializeIntPacked(&fieldNumBits);
            if (bunch.IsError()) break;

            var fieldName = classCache.GetFromIndex(repIndex)?.Name ?? "?";
            Console.WriteLine($"UActorChannel.ReceivedBunch:   field[{repIndex}]={fieldName} on ChIndex={ChIndex} Actor={Actor?.GetFName()} ({fieldNumBits} payload bits)");

            var bitsToSkip = Math.Min((long) fieldNumBits, payloadEnd - bunch.Pos);
            bunch.Pos += bitsToSkip;
        }
    }

    /// <summary>Side-effect-free hex dump of [startBit, endBit) - does not touch bunch.Pos.</summary>
    private static string HexDumpBits(FInBunch bunch, long startBit, long endBit) {
        var numBits = endBit - startBit;
        var bytes = new byte[(numBits + 7) / 8];
        for (long i = 0; i < numBits; i++) {
            if (bunch.BufferBits[(int) (startBit + i)]) bytes[i >> 3] |= (byte) (1 << (int) (i & 7));
        }

        return Convert.ToHexString(bytes);
    }
}