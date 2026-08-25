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
    ///     Properties marked dirty for the one-shot initial replication below. Handle numbering
    ///     (NativeRepLayouts) was ground-truthed via live wire probes on 2026-08-24 - see that
    ///     file's doc comment for the full story (AttachmentReplication consumes SIX handles, not
    ///     one, which is what made every downstream handle wrong earlier this session). AGameState
    ///     needs bReplicatedHasBegunPlay, APlayerState needs bHasStartedPlaying, and
    ///     APlayerController needs both bHasServerFinishedLoading and the PlayerState object
    ///     reference itself (so the client can resolve PlayerController->PlayerState) - together,
    ///     per Project-Reboot-3.0's own disabled workaround comment, these are what gate a real
    ///     client's loading-screen dismissal.
    /// </summary>
    private static HashSet<string> GetInitialReplicatedProperties(AActor actor) => actor switch {
        AGameState => new HashSet<string> { "RemoteRole", "Role", "bReplicatedHasBegunPlay", "MatchState" },
        APlayerState => new HashSet<string> { "RemoteRole", "Role", "PlayerNamePrivate", "bHasStartedPlaying", "HeroType" },
        // AFortInventory's own InventoryType (handle 16). Its other Net property, Inventory
        // (FFortItemList), is a FastArraySerializer / Custom Delta property and cannot go through
        // FRepLayout at all - see NativeRepLayouts.InventoryProps.
        AFortInventory => new HashSet<string> { "RemoteRole", "Role", "Owner", "InventoryType" },
        // WorldInventory maps to handle 34 as a plain ObjectRef - CONFIRMED live by the truncated
        // name probe, which reported "Property=WorldInventory, Parent=25, Cmd=53, ReadHandle=34"
        // with ReadLen=8, i.e. a packed NetGUID byte, matching PlayerState's own ObjectRef read at
        // handle 16 (and unlike the 1-bit bools that really occupy 49/50/51, where every earlier
        // attempt had wrongly placed this). See NativeRepLayouts.PlayerControllerProps.
        APlayerController => new HashSet<string> { "RemoteRole", "Role", "bHasServerFinishedLoading", "PlayerState", "WorldInventory" },
        _ => new HashSet<string> { "RemoteRole", "Role" }
    };

    /// <summary>
    ///     Default subobjects (CreateDefaultSubobject in the native constructor) to replicate via a
    ///     sub-object content block (see ReplicateSubobject) alongside this actor's own RepLayout
    ///     property push, mirroring real UE's AActor::ReplicateSubobjects override per class.
    ///     Currently unused - AFortPlayerController::WorldInventory was originally modeled this way,
    ///     but a live client rejected it ("Sub-object cannot be actor class"), proving
    ///     /Script/FortniteGame.FortInventory is really an actor class, not a UObject default
    ///     subobject - see AFortInventory/NativeRepLayouts.PlayerControllerProps for where it lives
    ///     now (a plain ObjectRef Cmd, like AController.PlayerState). Left in place - and
    ///     WriteContentBlockHeader/WriteContentBlockPayload/ReplicateSubobject below still real,
    ///     ported directly from UE 4.23's own DataChannel.cpp - for whatever future property turns
    ///     out to actually be a genuine UObject default subobject.
    /// </summary>
    private static IEnumerable<UObject> GetInitialReplicatedSubobjects(AActor actor) {
        yield break;
    }

    /// <summary>
    ///     One-shot version of UActorChannel::ReplicateActor - writes and sends the actor's spawn
    ///     header, followed by a property push via NativeRepLayouts/FRepLayout (this project's
    ///     minimal stand-in for FObjectReplicator::ReplicateProperties/FRepLayout::SendProperties).
    ///
    ///     TEMPORARY diagnostic, replacing the normal layout entirely: if REPLAYOUT_PROBE_HANDLE=N
    ///     is set, sends HandleProbe's truncated name probe for handle N on the actor named by
    ///     REPLAYOUT_PROBE_ACTOR (substring match on the runtime type name, default APlayerController).
    ///     It makes the client's read of that property overflow, so ReceiveProperties_r logs
    ///     "BunchIsError - Property=<real name>, Parent=<idx>, Cmd=<idx>" - the only condition under
    ///     which the client will name a handle at all. See HandleProbe.cs for how to read the result.
    /// </summary>
    public unsafe void ReplicateActor() {
        if (Actor == null || Connection == null) return;

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        var packageMap = (UPackageMapClient) Connection.PackageMap!;
        packageMap.SerializeNewActor(bunch, this, Actor);
        Actor.OnSerializeNewActor(bunch);

        var payload = new FNetBitWriter(Connection.PackageMap, 64);
        // REPLAYOUT_PROBE_HANDLE - the truncated name probe (see HandleProbe.WriteTruncatedNameProbe).
        // Any handle is valid, including the already-confirmed 1-22, which are useful as calibration
        // targets; there is deliberately no guard that can throw here, since this runs deep inside
        // packet dispatch where an exception kills the server outright.
        var probeHandleEnv = Environment.GetEnvironmentVariable("REPLAYOUT_PROBE_HANDLE");
        // REPLAYOUT_PROBE_ACTOR picks which actor's channel carries the probe, matched against the
        // runtime type name (case-insensitive substring, e.g. "AFortInventory"). Defaults to
        // APlayerController, which is what every probe so far has targeted. Only one actor should
        // ever match: the probe deliberately corrupts that channel, and the client closes the
        // connection as soon as it fails.
        var probeActor = Environment.GetEnvironmentVariable("REPLAYOUT_PROBE_ACTOR") ?? nameof(APlayerController);
        if (Actor.GetType().Name.Contains(probeActor, StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(probeHandleEnv, out var probeHandle) && probeHandle > 0) {
            Console.WriteLine($"ReplicateActor: REPLAYOUT_PROBE_HANDLE={probeHandle} on {Actor.GetType().Name} - sending TRUNCATED name probe (RemoteRole anchor + bare handle, no value bits, no terminator) instead of the normal layout");
            HandleProbe.WriteTruncatedNameProbe(payload, Actor, probeHandle);
        } else {
            NativeRepLayouts.Get(Actor).WriteChangedProperties(payload, Actor, GetInitialReplicatedProperties(Actor));
            WriteCustomDeltaProperties(payload);
        }

        bunch.WriteBit(true); // bHasRepLayout
        bunch.WriteBit(true); // bIsActor

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        Console.WriteLine($"ReplicateActor: RemoteRole={Actor.RemoteRole} Role={Actor.Role} numPayloadBits={numPayloadBits} payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        foreach (var subobject in GetInitialReplicatedSubobjects(Actor)) ReplicateSubobject(subobject, bunch);

        SendBunch(bunch, false);
    }

    /// <summary>
    ///     Port of UActorChannel::WriteContentBlockHeader (DataChannel.cpp). Every replicated object
    ///     in a bunch - the actor itself, or one of its subobjects - is preceded by this small header:
    ///     a bHasRepLayout bit, a bIsActor bit (1 lets the reader skip straight to the payload, since
    ///     it already knows which actor this channel is for), and, for anything else, the object's own
    ///     NetGUID reference (the exact same SerializeObject/InternalWriteObject/ExportNetGUID pipeline
    ///     already used for SerializeNewActor's Archetype reference and AController.PlayerState) plus
    ///     a "stably named" bit. Real UE gates the stably-named-vs-class-fallback choice on
    ///     Connection->Driver->IsServer(); that's always true in this codebase (server-only), so it's
    ///     not checked here. NET_CHECKSUM(Bunch) right after the object reference in real UE is a
    ///     complete no-op outside non-shipping/non-test builds (see CoreNet.h's
    ///     `#if !(UE_BUILD_SHIPPING || UE_BUILD_TEST)` gate on the NET_CHECKSUM macro) - this project's
    ///     already-proven-working SerializeNewActor/InternalWriteObject paths likewise never emit
    ///     anything for it, so it's omitted here too for consistency with a real (shipping/test) client.
    /// </summary>
    public void WriteContentBlockHeader(UObject obj, FOutBunch bunch, bool hasRepLayout) {
        bunch.WriteBit(hasRepLayout);

        var isActor = obj == Actor;
        bunch.WriteBit(isActor);
        if (isActor) return;

        var packageMap = (UPackageMapClient) Connection!.PackageMap!;
        packageMap.SerializeObject(bunch, obj);

        if (obj.IsNameStableForNetworking()) {
            bunch.WriteBit(true);
        } else {
            bunch.WriteBit(false);

            // UClass.CreateDefaultObject() is what actually gives a UClass instance its own
            // resolvable (package, name) identity (see its own comment) - it's normally only called
            // lazily via GetDefaultObject()/GetArchetype() when spawning an actor of that class.
            // A class referenced ONLY here (as a subobject's class fallback, never as some actor's
            // archetype) would otherwise still have a blank FName/null Outer at export time -
            // confirmed live 2026-08-24 ("FullNetGUIDPath: [15]EMPTY"). Calling GetDefaultObject()
            // first (result unused) forces that lazy initialization before we export the class itself.
            var classObj = obj.GetClass();
            classObj.GetDefaultObject();
            packageMap.SerializeObject(bunch, classObj);
        }
    }

    /// <summary>
    ///     Port of UActorChannel::WriteContentBlockPayload (DataChannel.cpp): a content block header
    ///     followed by a packed bit count and that many payload bits.
    /// </summary>
    public unsafe void WriteContentBlockPayload(UObject obj, FOutBunch bunch, bool hasRepLayout, FNetBitWriter payload) {
        WriteContentBlockHeader(obj, bunch, hasRepLayout);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());
    }

    /// <summary>
    ///     Simplified port of UActorChannel::ReplicateSubobject (DataChannel.cpp) - writes a
    ///     sub-object content block for a non-actor UObject owned by this channel's Actor (e.g.
    ///     AFortPlayerController::WorldInventory, a default subobject created via
    ///     CreateDefaultSubobject in the native constructor). Real UE first tries
    ///     FObjectReplicator::ReplicateProperties (the subobject's own FRepLayout push) and only
    ///     falls back to an empty-payload content block if that had nothing to send; this project
    ///     doesn't implement per-subobject FRepLayout property replication, so it always takes that
    ///     fallback (`WriteContentBlockPayload(Obj, Bunch, false, EmptyPayload)`), which is also
    ///     exactly what real UE does the very first time a subobject with no changed properties of
    ///     its own is replicated - the sole purpose is letting the client resolve/create the
    ///     subobject's NetGUID.
    ///
    ///     Real UE's version also has a defensive pre-step
    ///     (`if (!GuidCache->SupportsObject(Obj)) GuidCache->AssignNewNetGUID_Server(Obj)`) before
    ///     calling SerializeObject, needed because FNetGUIDCache::SupportsObject would otherwise
    ///     reject a default subobject whose Outer (the dynamically-spawned actor) isn't itself
    ///     name-stable. That's not needed here: UFortWorldItem already overrides
    ///     IsSupportedForNetworking() to return true (see its own doc comment), so
    ///     FNetGUIDCache.SupportsObject/GetOrAssignNetGUID (called inside SerializeObject, inside
    ///     WriteContentBlockHeader) already assigns a NetGUID correctly on the first reference without
    ///     a separate bootstrap step.
    /// </summary>
    public void ReplicateSubobject(UObject obj, FOutBunch bunch) {
        if (Connection == null) return;

        var emptyPayload = new FNetBitWriter(Connection.PackageMap!, 8);
        Console.WriteLine($"ReplicateSubobject: obj={obj.GetFName()} class={obj.GetClass().GetFName()} owner={Actor?.GetFName()}");
        WriteContentBlockPayload(obj, bunch, false, emptyPayload);
    }

    /// <summary>
    ///     Sends AController::ClientRestart(APawn* NewPawn) on this (already-open) PlayerController
    ///     channel - the server->client RPC real UE fires from Possess()/RestartPlayer() to tell the
    ///     client-side PlayerController it now controls a pawn. A real PR3.0 client capture
    ///     (2026-08-24) showed this is what the client is actually waiting on: without it, the
    ///     client kept calling ServerSetSpectatorLocation forever (still thinks it's a spectator);
    ///     with it, the client immediately replies with ServerAcknowledgePossession and switches to
    ///     ServerMoveNoBase. Only a single object-reference parameter, so this is hand-written rather
    ///     than routed through a generic RPC-writer abstraction (see FRpcReader for the read-side
    ///     equivalent, which this project only needed for client->server calls until now).
    /// </summary>
    /// <summary>
    ///     Stand-in for FObjectReplicator::ReplicateCustomDeltaProperties (DataReplication.cpp:1421),
    ///     called straight after the RepLayout property push so both land in the same content-block
    ///     payload - the same single-Writer arrangement real UE uses in ReplicateProperties.
    ///
    ///     Only AFortInventory::Inventory is sent, and only ever as an empty delta. That property is
    ///     an FFortItemList, which derives from FFastArraySerializer and is therefore a Custom Delta
    ///     property: it is excluded from FRepLayout's handle stream entirely and has to travel as
    ///     its own RepIndex-addressed field. See FFastArraySerializerWriter for the wire format and
    ///     why an empty delta is still a real message rather than a no-op.
    ///
    ///     Why send an empty inventory at all: the client's ClientRestart_Implementation currently
    ///     stops with "Quickbars are invalid, waiting to finish restarting". Quickbars are not
    ///     replicated on this build - AFortQuickBars derives from AFortClientOnlyActor and
    ///     AFortPlayerController::ClientQuickBars carries no Net flag, so the client builds them
    ///     itself - and the inventory is the most plausible thing it is waiting on, since quickbars
    ///     are a view onto it and AFortInventory has both an OnRep on Inventory and its own
    ///     HandleInventoryLocalUpdate. Unconfirmed; if this does not move the client on, the field
    ///     framing here is still the prerequisite for any real item replication later.
    /// </summary>
    private unsafe void WriteCustomDeltaProperties(FNetBitWriter payload) {
        if (Actor is not AFortInventory || Connection == null) return;

        var classCache = NativeClassNetCache.Get(Actor);
        var field = classCache.GetFromName("Inventory");
        if (field == null) {
            Console.WriteLine("WriteCustomDeltaProperties: 'Inventory' not found in AFortInventory's ClassNetCache, not sending");
            return;
        }

        var inventory = (AFortInventory) Actor;
        var fieldPayload = new FNetBitWriter(Connection.PackageMap, 256);
        FFastArraySerializerWriter.WriteItemListDelta(fieldPayload, inventory.Inventory);

        // Identical framing to SendClientRestart's RPC field below, which is the point: real UE
        // routes custom delta properties and RPCs through the very same
        // UActorChannel::WriteFieldHeaderAndPayload.
        var fieldIndex = (uint) field.FieldNetIndex;
        payload.SerializeInt(&fieldIndex, (uint) (classCache.GetMaxIndex() + 1));
        var fieldBits = (uint) fieldPayload.GetNumBits();
        payload.SerializeIntPacked(&fieldBits);
        var fieldData = fieldPayload.GetData();
        fixed (byte* p = fieldData) payload.SerializeBits(p, fieldPayload.GetNumBits());

        Console.WriteLine($"WriteCustomDeltaProperties: Inventory fieldIndex={fieldIndex} maxIndex={classCache.GetMaxIndex()} items={inventory.Inventory.Count} fieldBits={fieldBits}");
    }

    public void SendClientRestart(APawn pawn) => SendPawnRpc("ClientRestart", pawn);

    /// <summary>
    ///     APlayerController::ClientRetryClientRestart - the server's half of the possession retry
    ///     handshake, and the answer to a question the client had been asking us over and over with
    ///     no reply.
    ///
    ///     Real UE (PlayerController.cpp:2684-2701, 672-706): when the client's
    ///     ClientRestart_Implementation cannot finish - on this build it bails with
    ///     "ClientRestart_Implementation failed because Quickbars are invalid, waiting to finish
    ///     restarting" - it never acknowledges the pawn, and instead calls
    ///     ServerCheckClientPossessionReliable. The server answers:
    ///
    ///         void APlayerController::ServerCheckClientPossession_Implementation() {
    ///             if (AcknowledgedPawn != GetPawn()) {
    ///                 LastRetryPlayerTime = ForceRetryClientRestartTime;   // bypass the throttle
    ///                 SafeRetryClientRestart();                            // ClientRetryClientRestart(GetPawn())
    ///             }
    ///         }
    ///
    ///     and ClientRetryClientRestart_Implementation does strictly MORE than re-issue the call -
    ///     it relinks pawn and controller client-side first:
    ///
    ///         SetPawn(NewPawn); NewPawn->Controller = this; NewPawn->OnRep_Controller();
    ///         ClientRestart(GetPawn());
    ///
    ///     So this is not merely a retry: OnRep_Controller is client-side state we had no other way
    ///     to trigger. Until now this project sent ClientRestart exactly once and ignored every
    ///     ServerCheckClientPossession(Reliable) that came back, so the loop the engine designed to
    ///     converge here could never run.
    ///
    ///     Same single-APawn-parameter shape as ClientRestart, so it shares SendPawnRpc.
    /// </summary>
    public void SendClientRetryClientRestart(APawn pawn) => SendPawnRpc("ClientRetryClientRestart", pawn);

    /// <summary>
    ///     Answers ServerCheckClientPossession / ...Reliable, mirroring
    ///     APlayerController::SafeRetryClientRestart. No throttle is applied on purpose: real UE
    ///     stamps LastRetryPlayerTime with ForceRetryClientRestartTime immediately before calling,
    ///     precisely so this path is NOT throttled - its own comment says "Client already throttles
    ///     their call to this function, so respond immediately". The RetryClientRestartThrottleTime
    ///     check only guards the other, timer-driven callers of SafeRetryClientRestart.
    /// </summary>
    public void SafeRetryClientRestart() {
        if (Actor is not APlayerController pc) return;
        if (pc.Pawn == null) {
            Console.WriteLine("SafeRetryClientRestart: PlayerController has no Pawn, not retrying");
            return;
        }

        SendClientRetryClientRestart(pc.Pawn);
    }

    private unsafe void SendPawnRpc(string fieldName, APawn pawn) {
        if (Actor == null || Connection == null) return;

        var classCache = NativeClassNetCache.Get(Actor);
        var field = classCache.GetFromName(fieldName);
        if (field == null) {
            Console.WriteLine($"SendPawnRpc: '{fieldName}' not found in {Actor.GetFName()}'s ClassNetCache, not sending");
            return;
        }

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        var payload = new FNetBitWriter(Connection.PackageMap, 64);
        payload.WriteBit(false); // bDoChecksum
        uint terminator = 0;
        payload.SerializeIntPacked(&terminator); // empty RepLayout property section

        var fieldPayload = new FNetBitWriter(Connection.PackageMap, 64);
        fieldPayload.WriteBit(true); // NewPawn is present (non-bool RPC param protocol - see FRpcReader)
        ((UPackageMapClient) fieldPayload.PackageMap!).SerializeObject(fieldPayload, pawn);

        var fieldIndex = (uint) field.FieldNetIndex;
        payload.SerializeInt(&fieldIndex, (uint) (classCache.GetMaxIndex() + 1));
        var fieldBits = (uint) fieldPayload.GetNumBits();
        payload.SerializeIntPacked(&fieldBits);
        var fieldData = fieldPayload.GetData();
        fixed (byte* p = fieldData) payload.SerializeBits(p, fieldPayload.GetNumBits());

        bunch.WriteBit(true); // bHasRepLayout
        bunch.WriteBit(true); // bIsActor

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);
        var payloadData = payload.GetData();
        Console.WriteLine($"SendPawnRpc: {fieldName} fieldIndex={fieldIndex} numPayloadBits={numPayloadBits} payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");
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

            if (Actor != null) {
                try {
                    ReadContentBlockFields(bunch, NativeClassNetCache.Get(Actor), payloadEnd);
                } catch (Exception ex) {
                    // A ClassNetCache gap (an incompletely ground-truthed class - see
                    // NativeClassNetCache's own comments for which ones still are) desyncs the
                    // bounded-int field-index read and can throw (e.g. FBitReader::SerializeInt
                    // overflowing). The outer NumPayloadBits resync right below is ground truth
                    // regardless, so this is recoverable - better to lose this one content block
                    // than crash the whole process.
                    Console.WriteLine($"UActorChannel.ReceivedBunch: ReadContentBlockFields threw on ChIndex={ChIndex} Actor={Actor.GetFName()}: {ex}");
                }
            }

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
    ///     NOT SerializeIntPacked), a packed bit count, then that many payload bits. A field that
    ///     names a registered RPC (see NativeRpcHandlers) gets its parameters decoded and its
    ///     handler invoked; anything else is just logged and skipped, same as before - the field's
    ///     own declared bit count always resyncs bunch.Pos at the end of the loop body regardless of
    ///     what a handler actually consumed, so a handler bug can't desync the rest of the bunch.
    /// </summary>
    private unsafe void ReadContentBlockFields(FInBunch bunch, FClassNetCache classCache, long payloadEnd) {
        var maxIndex = classCache.GetMaxIndex();
        var rpcTable = Actor != null ? NativeRpcHandlers.Get(Actor) : null;

        while (bunch.Pos < payloadEnd && !bunch.IsError()) {
            var repIndex = (int) bunch.ReadInt((uint) (maxIndex + 1));
            if (bunch.IsError()) break;

            uint fieldNumBits = 0;
            bunch.SerializeIntPacked(&fieldNumBits);
            if (bunch.IsError()) break;

            var fieldStart = bunch.Pos;
            var fieldEnd = Math.Min(fieldStart + (long) fieldNumBits, payloadEnd);
            var fieldName = classCache.GetFromIndex(repIndex)?.Name ?? "?";

            if (rpcTable != null && rpcTable.TryGetValue(fieldName, out var rpcDef)) {
                try {
                    var values = FRpcReader.ReadParams(bunch, rpcDef.Params);
                    rpcDef.Invoke(Actor!, values);

                    // Real UE routes both of these into
                    // APlayerController::ServerCheckClientPossession_Implementation, whose whole job
                    // is to re-send the possession RPC - see SafeRetryClientRestart. Handled here
                    // rather than in NativeRpcHandlers because the reply needs the channel, and the
                    // handler table is deliberately channel-agnostic.
                    if (fieldName is "ServerCheckClientPossession" or "ServerCheckClientPossessionReliable") {
                        SafeRetryClientRestart();
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"UActorChannel.ReceivedBunch: RPC {fieldName} failed to decode on ChIndex={ChIndex} Actor={Actor?.GetFName()}: {ex}");
                }
            } else {
                Console.WriteLine($"UActorChannel.ReceivedBunch:   field[{repIndex}]={fieldName} on ChIndex={ChIndex} Actor={Actor?.GetFName()} ({fieldNumBits} payload bits)");
            }

            bunch.Pos = fieldEnd;
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