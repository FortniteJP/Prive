using System.Net;
using System.Net.Sockets;
using AFortOnlineBeacon.Core;
using AFortOnlineBeacon.Core.Names;
using AFortOnlineBeacon.Net;
using AFortOnlineBeacon.Net.Abilities;
using AFortOnlineBeacon.Net.Channels;
using AFortOnlineBeacon.Net.Packets;
using AFortOnlineBeacon.Net.Packets.Header;
using AFortOnlineBeacon.Net.Packets.Control;
using AFortOnlineBeacon.Serialization;

namespace PacketReplayDecoder;

/// <summary>Minimal, no-op FNetDriver used only to host a real PacketHandler (StatelessConnect/AES/Oodle) - never actually sends/receives real UDP.</summary>
internal sealed class ReplayNetDriver : UNetDriver {
    public override bool IsNetResourceValid() => true;
    public override void LowLevelSend(IPEndPoint address, byte[] data, int countBits, FOutPacketTraits traits) {
        // no-op: this decoder never sends anything back, it only replays a captured stream.
    }
}

/// <summary>
///     A real client responding to a handshake challenge actually SENDS a response packet
///     (StatelessConnectHandlerComponent.SendChallengeResponse -&gt; UIpConnection.LowLevelSend -&gt;
///     Socket.Send). This decoder only replays a capture and never has a connected socket to send
///     through, so LowLevelSend is silenced here - the component's internal handshake STATE still
///     advances correctly either way (that's tracked independently of whether the send succeeds).
/// </summary>
internal sealed class SilentIpConnection : UIpConnection {
    public override void LowLevelSend(byte[] data, int countBits, FOutPacketTraits traits) {}
}

internal sealed class ReplayNotify : FNetworkNotify {
    public EAcceptConnection NotifyAcceptingConnection() => EAcceptConnection.Accept;
    public void NotifyAcceptedConnection(UNetConnection connection) {}
    public bool NotifyAcceptingChannel(UChannel channel) => true;
    public void NotifyControlMessage(UNetConnection connection, NMT messageType, AFortOnlineBeacon.Net.Packets.Bunch.FInBunch bunch) {}
}

internal sealed class ChannelState {
    public FName ChName;
    public bool SpawnHeaderDecoded;
    public string? ActorClassPath;
    public uint ActorGuid;
}

/// <summary>
///     Accumulates the fragments of one partial (multi-packet) bunch on one channel. Real UE splits
///     a bunch too large for one packet into several raw sends, each with its own bunch header
///     (bPartial/bPartialInitial/bPartialFinal) but only ONE of them - the FIRST - carries the
///     semantic flags that describe the logical whole (bHasPackageMapExports,
///     bHasMustBeMappedGUIDs, ChName): later fragments are lightweight continuations whose own copies
///     of those bits read back false. Buffering here and dispatching only once, on the final
///     fragment, is what makes a payload like AFortWeap_BuildingTool's DefaultMetadata (split across
///     two packets in the PR3.0 capture at #2210, 977 + 379 bits) readable instead of two bunches of
///     noise - see the README's "Not implemented" section, now implemented.
/// </summary>
internal sealed class PartialBunchAccumulator {
    public required FBitWriter Writer;
    public required bool HasPackageMapExports;
    public required bool HasMustBeMappedGUIDs;
    public required FName ChName;
    public required bool BOpen;
}

/// <summary>
///     Structural + best-effort semantic decoder for one direction of a captured Fortnite 10.40
///     session, replaying it against AFortOnlineBeacon's real PacketHandler (StatelessConnect/AES/
///     Oodle) for the handshake/crypto/compression layer, then walking packet/bunch/content-block
///     framing with a from-scratch parser informed by (but not calling into) UNetConnection.
///     ReceivedPacket/UChannel/UActorChannel - those assume a server that never legitimately
///     receives actor-channel traffic, so they don't implement the actor-spawn-header or GUID-export
///     read paths this decoder actually needs.
/// </summary>
internal sealed class RoleDecoder {
    private readonly bool _isServerRole; // true = decoding C->S (we play the server); false = decoding S->C (we play the client)
    private readonly bool _verbose;

    private readonly ReplayNetDriver _driver = new();
    private UIpConnection? _conn;
    private UdpClient? _dummySocket;
    private readonly IPEndPoint _fakeAddr = new(IPAddress.Loopback, 40000);

    private readonly FNetPacketNotify _notify = new();
    private int _inPacketId;
    private bool _sequenceInitialized;
    private static bool _warnedAboutChain;

    private readonly Dictionary<int, int> _inReliable = new();
    private readonly Dictionary<int, ChannelState> _channels = new();
    private readonly Dictionary<int, PartialBunchAccumulator> _partialBunches = new();
    /// <summary>
    ///     Exported NetGUID -&gt; path, SHARED between the two role decoders rather than one each.
    ///
    ///     A NetGUID namespace belongs to the CONNECTION, not to a direction: only the server assigns
    ///     ids, it exports them on the S-&gt;C side, and the client then names things by those same ids
    ///     on the way back. With a table per direction the C-&gt;S decoder can never resolve anything -
    ///     which is exactly how eleven ability batches ended up reported as
    ///     "struct guid 9039, no path", while packet #1971 in the other direction had already said
    ///     plainly that 9039 is FortGameplayAbilityTargetData_SingleTargetHit.
    ///
    ///     Static is honest here: this tool decodes exactly one captured session per run.
    /// </summary>
    private static readonly Dictionary<uint, string> _guidPaths = new();

    /// <summary>
    ///     ChIndex -&gt; archetype class path, SHARED between the two RoleDecoder instances for the
    ///     SAME reason `_guidPaths` is static: a channel index belongs to the CONNECTION, not to a
    ///     direction. Only the server ever spawns an actor (an ACTOR SPAWN header only ever appears
    ///     in S-&gt;C traffic), so the C-&gt;S decoder's own per-instance `ChannelState.ActorClassPath`
    ///     can never be set from anything it reads itself - every RPC a client sends on that channel
    ///     was doomed to `class~?`, unable to name the field even when the ClassNetCache table
    ///     already knows it (confirmed: capture packet #2211, a 814-bit C-&gt;S field on the channel
    ///     used moments before a real building placement succeeded, right where
    ///     `ServerCreateBuildingActor` would be - unnamed only because of this gap, not because the
    ///     field is actually unresolvable). Cleared on close so a reused ChIndex can't inherit a
    ///     stale class the way `ChannelState` itself already guards against per-direction.
    /// </summary>
    private static readonly Dictionary<int, string> _sharedChannelClasses = new();

    // Per-class-name (best-effort matched from the archetype path string) handle table for RepLayout decode.
    private readonly Dictionary<string, Dictionary<uint, GroundTruth.RepHandleDef>> _handleMaps;

    public RoleDecoder(bool isServerRole, bool verbose) {
        _isServerRole = isServerRole;
        _verbose = verbose;

        _handleMaps = new Dictionary<string, Dictionary<uint, GroundTruth.RepHandleDef>> {
            ["GameState"] = GroundTruth.BuildHandleMap(GroundTruth.GameStateProps),
            ["PlayerController"] = GroundTruth.BuildHandleMap(GroundTruth.PlayerControllerProps),
            ["PlayerState"] = GroundTruth.BuildHandleMap(GroundTruth.PlayerStateProps),
            ["Pawn"] = GroundTruth.BuildHandleMap(GroundTruth.PawnProps),
            ["Controller"] = GroundTruth.BuildHandleMap(GroundTruth.ControllerProps),
            ["Weapon"] = GroundTruth.BuildHandleMap(GroundTruth.WeaponProps),
            ["Actor"] = GroundTruth.BuildHandleMap(GroundTruth.ActorProps)
        };

        _driver.Init(new ReplayNotify());

        if (!_isServerRole) {
            var conn = new SilentIpConnection();
            _dummySocket = new UdpClient(0);
            _driver.ServerConnection = conn; // makes Driver.IsServer() false -> Handler.Mode = Client
            conn.InitLocalConnection(_driver, _dummySocket, new FUrl(), EConnectionState.USOCK_Pending);
            _conn = conn;
        }
        // Server role's `_conn` is created lazily by ForceInitFromCookie once the handshake cookie's
        // sequence numbers are known (see that method's comment for why the real connectionless
        // handshake dance can't be replayed directly here).
    }

    public (int serverSeq, int clientSeq)? RecoveredHandshakeSeqs { get; private set; }

    /// <summary>
    ///     StatelessConnectHandlerComponent's SERVER-side connectionless validation is
    ///     deliberately unspoofable without the real server's own randomly-rolled HMAC secret (which
    ///     this decoder obviously doesn't have) - replaying the captured C-&gt;S handshake packets
    ///     against a freshly-generated local secret can never validate. But the two sequence numbers
    ///     the handshake exists to exchange are embedded directly in the SAME cookie bytes the CLIENT
    ///     role decoder already recovers (harmlessly, since client-mode does no HMAC check at all) -
    ///     so the server role just borrows those numbers directly instead of re-deriving them itself.
    /// </summary>
    public void ForceInitFromCookie(int serverSeq, int clientSeq) {
        if (_conn != null) return;

        RecoveredHandshakeSeqs = (serverSeq, clientSeq);

        var conn = new SilentIpConnection();
        _dummySocket = new UdpClient(0);
        conn.InitRemoteConnection(_driver, _dummySocket, new FUrl(), _fakeAddr, EConnectionState.USOCK_Open);
        _conn = conn;

        // Mirror UNetConnection.InitSequence(incoming=clientSeq, outgoing=serverSeq).
        _inPacketId = clientSeq - 1;
        _notify.Init(new((ushort) _inPacketId), new((ushort) serverSeq));
        _sequenceInitialized = true;
    }

    public void Feed(int packetIndex, byte[] rawIn) {
        try {
            // AESHandlerComponent/OodleHandlerComponent unconditionally peek a "is this
            // encrypted/compressed" bit even on handshake-shaped packets where StatelessConnect has
            // already consumed every real bit (this project's PacketHandler chain has apparently
            // never been exercised in HandlerMode.Client before, since this codebase only ever runs
            // as a server - that peek can land exactly on the packet's own trailing byte boundary and
            // throw). FPacketDataView's reported length is independent of the byte array's actual
            // size, so padding the array (while keeping the reported length the same) gives that peek
            // safe zero bits to land on without changing what FIncoming_Internal's own sentinel-byte
            // trim examines (still the true last byte of `rawIn`).
            var raw = new byte[rawIn.Length + 8];
            Array.Copy(rawIn, raw, rawIn.Length);

            if (_isServerRole) FeedServerRole(packetIndex, raw, rawIn.Length);
            else FeedClientRole(packetIndex, raw, rawIn.Length);
        } catch (Exception ex) {
            Log($"#{packetIndex}: EXCEPTION during decode: {ex}");
        }
    }

    // ---------------- Server role (C->S) ----------------

    private void FeedServerRole(int packetIndex, byte[] raw, int trueLength) {
        if (_conn == null) {
            Log($"#{packetIndex}: [server role] waiting for handshake sequence numbers (recovered from the S->C side), skipping");
            return;
        }

        // A stray handshake-shaped retry after we've already forced the connection open is a no-op
        // on the per-connection (not connectionless) StatelessConnectHandlerComponent - safe to feed
        // through the normal path either way.
        FeedEstablished(packetIndex, raw, trueLength, senderIsServer: false);
    }

    // ---------------- Client role (S->C) ----------------

    private void FeedClientRole(int packetIndex, byte[] raw, int trueLength) {
        var wasInitialized = _conn!.Handler!.IsFullyInitialized();
        FeedEstablished(packetIndex, raw, trueLength, senderIsServer: true, preHandshakeCheck: true);

        if (!wasInitialized && _conn.Handler.IsFullyInitialized()) {
            _conn.StatelessConnectComponent!.GetChallengeSequences(out var serverSeq, out var clientSeq);
            RecoveredHandshakeSeqs = (serverSeq, clientSeq);

            // Mirror UNetConnection.InitSequence(incoming=serverSeq, outgoing=clientSeq).
            _inPacketId = serverSeq - 1;
            _notify.Init(new((ushort) _inPacketId), new((ushort) clientSeq));
            _sequenceInitialized = true;

            Log($"#{packetIndex}: [client role] handshake complete (serverSeq={serverSeq}, clientSeq={clientSeq})");
        }
    }

    // ---------------- Shared: unwrap via real Handler, then our own packet/bunch parse ----------------

    private void FeedEstablished(int packetIndex, byte[] raw, int trueLength, bool senderIsServer, bool preHandshakeCheck = false) {
        var traits = new FInPacketTraits();
        var view = new FReceivedPacketView(new FPacketDataView(raw, trueLength, ECountUnits.Bytes), _fakeAddr, traits);

        var ok = _conn!.Handler!.Incoming(view);
        if (!ok) {
            Log($"#{packetIndex}: Handler.Incoming failed (AES/Oodle/StatelessConnect rejected packet, len={trueLength})");
            return;
        }

        if (view.DataView.NumBytes() == 0) {
            if (!preHandshakeCheck) Log($"#{packetIndex}: (handshake-only or empty packet after unwrap)");
            return;
        }

        if (!_sequenceInitialized) {
            Log($"#{packetIndex}: packet carries data but sequence isn't initialized yet - skipping (handshake not complete?)");
            return;
        }

        var data = view.DataView.GetData();
        var count = view.DataView.NumBytes();
        if (_verbose) Log($"#{packetIndex}: post-Handler.Incoming clean bytes ({count}): {Convert.ToHexString(data, 0, count)}");
        var lastByte = data[count - 1];
        if (lastByte == 0) {
            Log($"#{packetIndex}: malformed packet (trailing termination byte is 0)");
            return;
        }

        var bitSize = count * 8 - 1;
        while ((lastByte & 0x80) == 0) {
            lastByte *= 2;
            bitSize--;
        }

        var reader = new FBitReader(data, bitSize);
        reader.SetEngineNetVer(UNetConnection.DefaultEngineNetworkProtocolVersion);
        reader.SetGameNetVer(0);

        if (reader.GetBitsLeft() <= 0) return;

        ParsePacketBody(packetIndex, reader, senderIsServer);
    }

    private void ParsePacketBody(int packetIndex, FBitReader reader, bool senderIsServer) {
        if (reader.IsError()) {
            Log($"#{packetIndex}: reader error before header");
            return;
        }

        // NOT using FNetPacketNotify.ReadHeader/_notify here (see below) - this capture's own
        // HistoryWordCount field (4 bits, so a raw range of 0-15 -> 1-16 words) regularly exceeds
        // AFortOnlineBeacon's own SequenceHistory storage, which is hard-capped at 8 words (256
        // bits / 32) - FNetPacketNotify.MaxSequenceHistoryLength=256 there, vs the 4-bit field's own
        // implied range of up to 512 bits. AFortOnlineBeacon's WRITE side self-consistently never
        // emits more than 8 (so this never surfaced against a real client, which only ever receives
        // AFortOnlineBeacon's own capped packets) - but Project-Reboot-3.0's packets use the fuller
        // range, and SequenceHistory.Read()'s internal Math.Min(numWords, WordCount=8) would silently
        // under-consume relative to what was actually written, desyncing every bit after it. Reading
        // the packed header fields directly and consuming the UNCLAMPED word count ourselves avoids
        // that - we don't need the ack-history bits' actual content for a read-only decode anyway.
        var packedHeaderRaw = reader.ReadUInt32();
        var seq = FPackedHeader.GetSeq(packedHeaderRaw);
        var ackedSeq = FPackedHeader.GetAckedSeq(packedHeaderRaw);
        var historyWordCount = FPackedHeader.GetHistoryWordCount(packedHeaderRaw) + 1;

        for (var i = 0; i < historyWordCount; i++) reader.ReadUInt32();

        if (reader.IsError()) {
            Log($"#{packetIndex}: reader error while consuming SequenceHistory ({historyWordCount} words)");
            return;
        }

        if (_verbose) Log($"#{packetIndex}: header Seq={seq.Value} AckedSeq={ackedSeq.Value} HistoryWordCount={historyWordCount}");

        // FNetPacketNotify caps the history at MaxSequenceHistoryLength/32 = 8 words, so anything
        // above that is not a real header - it is a misaligned one, and by far the likeliest cause is
        // a PacketHandler chain that does not match the client the capture was taken from. Every
        // component costs bits whether or not it does anything (AES and Oodle each read one leading
        // flag bit), so one surplus component shifts the whole body by one bit and the packet still
        // "decodes" - into plausible garbage. This warning exists because that cost a full session:
        // the decoder was written off as broken when it was being run with AES in the chain against a
        // capture whose client had `!Components=ClearArray` and only Oodle.
        if (historyWordCount > 8 && !_warnedAboutChain) {
            _warnedAboutChain = true;
            Log($"#{packetIndex}: HistoryWordCount={historyWordCount} exceeds the 8-word maximum, so this header " +
                "is misaligned rather than unusual. The PacketHandler chain almost certainly does not match the " +
                "client this capture came from - set NET_HANDLER_COMPONENTS (e.g. \"oodle,stateless\" for a " +
                "capture with AES removed from [PacketHandlerComponents]) and run again.");
        }

        // ReadPacketInfo equivalent (see UNetConnection.WritePacketInfo/ReadPacketInfo): 1 bit +
        // optional byte (only ever written by a SERVER sender) + 1 mandatory byte, always in that order.
        var bHasServerFrameTime = reader.ReadBit();
        if (bHasServerFrameTime && senderIsServer) reader.ReadByte();
        reader.ReadByte();
        if (reader.IsError()) {
            Log($"#{packetIndex}: reader error in packet info");
            return;
        }

        while (!reader.AtEnd()) {
            if (!ParseOneBunch(packetIndex, reader)) break;
        }
    }

    private unsafe bool ParseOneBunch(int packetIndex, FBitReader reader) {
        var bControl = reader.ReadBit();
        var bOpen = bControl && reader.ReadBit();
        var bClose = bControl && reader.ReadBit();

        var closeReason = bClose ? (EChannelCloseReason) reader.ReadInt((uint) EChannelCloseReason.MAX) : EChannelCloseReason.Destroyed;

        var bIsReplicationPaused = reader.ReadBit();
        var bReliable = reader.ReadBit();
        var chIndex = (int) reader.ReadUInt32Packed();
        if (chIndex is < 0 or > 32767) { // DefaultMaxChannelSize
            Log($"#{packetIndex}: implausible ChIndex={chIndex} - bunch header looks misaligned, abandoning rest of packet");
            return false;
        }

        var bHasPackageMapExports = reader.ReadBit();
        var bHasMustBeMappedGUIDs = reader.ReadBit();
        var bPartial = reader.ReadBit();

        int chSequence;
        if (bReliable) {
            var raw = (int) reader.ReadInt(1024); // MaxChSequence
            var reference = _inReliable.GetValueOrDefault(chIndex, 0);
            chSequence = MakeRelative(raw, reference, 1024);
            _inReliable[chIndex] = chSequence;
        } else if (bPartial) chSequence = _inPacketId;
        else chSequence = 0;

        var bPartialInitial = bPartial && reader.ReadBit();
        var bPartialFinal = bPartial && reader.ReadBit();

        var chName = new FName(EName.None);
        if (bReliable || bOpen) {
            FName? name = null;
            if (!UPackageMap.StaticSerializeName(reader, ref name) || reader.IsError()) {
                Log($"#{packetIndex}: channel-name deserialization failed at ChIndex={chIndex}");
                return false;
            }

            chName = name!.Value;
        }

        var bunchDataBits = reader.ReadInt((uint) (1024 * 8));
        if (reader.IsError()) {
            Log($"#{packetIndex}: bunch header overflow at ChIndex={chIndex}");
            return false;
        }

        var payloadBytes = new byte[(bunchDataBits + 7) / 8];
        fixed (byte* p = payloadBytes) reader.SerializeBits(p, bunchDataBits);

        if (reader.IsError()) {
            Log($"#{packetIndex}: bunch payload overflow at ChIndex={chIndex}");
            return false;
        }

        var chNameStr = chName.ToEName()?.ToString() ?? chName.ToString();
        if (_verbose) Log($"#{packetIndex}: bunch ChIndex={chIndex} ChName={chNameStr} bOpen={bOpen} bClose={bClose} bReliable={bReliable} bPartial={bPartial}/{bPartialInitial}/{bPartialFinal} bHasPackageMapExports={bHasPackageMapExports} bHasMustBeMappedGUIDs={bHasMustBeMappedGUIDs} bits={bunchDataBits}");

        // A bunch too large for one packet arrives as several fragments, each with its own header -
        // only the FIRST (bPartialInitial) carries the real bHasPackageMapExports/
        // bHasMustBeMappedGUIDs/ChName; later fragments are raw continuations whose own copies of
        // those bits read back false/None. Dispatching each fragment on its own (the previous
        // behaviour) fed a random continuation of bits into "parse a fresh actor spawn header",
        // which is exactly the "SUB-OBJECT block claims N bits with only M left" garbage this
        // produced on the PR3.0 capture's building-tool spawn (#2210: 977 + 379 bits, two
        // fragments). Buffer instead, and only dispatch once bPartialFinal completes the set.
        if (bPartial) {
            if (bPartialInitial) {
                _partialBunches[chIndex] = new PartialBunchAccumulator {
                    Writer = new FBitWriter(bunchDataBits, inAllowResize: true, usePool: false),
                    HasPackageMapExports = false,
                    HasMustBeMappedGUIDs = false,
                    ChName = chName,
                    BOpen = bOpen
                };
            }

            if (!_partialBunches.TryGetValue(chIndex, out var partial)) {
                Log($"#{packetIndex}: partial continuation fragment on ChIndex={chIndex} with no initial fragment buffered - dropping it");
                return true;
            }

            // OR across every fragment, not "only the initial fragment's copy counts" (an earlier
            // version of this fix assumed that, based on bHasPackageMapExports only ever appearing
            // true on the initial fragment in the one example available - but bHasMustBeMappedGUIDs
            // on that SAME bunch appeared true on the FINAL fragment instead, false on the initial.
            // Whichever fragment's send actually needed the flag carries it; the reassembled whole
            // needs whichever fragment(s) set it.
            partial.HasPackageMapExports |= bHasPackageMapExports;
            partial.HasMustBeMappedGUIDs |= bHasMustBeMappedGUIDs;

            fixed (byte* p = payloadBytes) partial.Writer.SerializeBits(p, bunchDataBits);

            if (!bPartialFinal) {
                if (_verbose) Log($"#{packetIndex}:   buffered partial fragment, {partial.Writer.GetNumBits()} bit(s) accumulated so far");
                return true;
            }

            _partialBunches.Remove(chIndex);

            if (_verbose) Log($"#{packetIndex}:   partial bunch complete: {partial.Writer.GetNumBits()} bit(s) reassembled");

            bHasPackageMapExports = partial.HasPackageMapExports;
            bHasMustBeMappedGUIDs = partial.HasMustBeMappedGUIDs;
            chName = partial.ChName;
            bOpen = partial.BOpen;
            chNameStr = chName.ToEName()?.ToString() ?? chName.ToString();
            bunchDataBits = (uint) partial.Writer.GetNumBits();
            payloadBytes = partial.Writer.GetData();
        }

        var bunchReader = new FBitReader(payloadBytes, (int) bunchDataBits);
        bunchReader.SetEngineNetVer(UNetConnection.DefaultEngineNetworkProtocolVersion);
        bunchReader.SetGameNetVer(0);

        // bOpen means a channel index is starting a NEW lifetime - channel indices are reused
        // constantly (a weapon closes, the next one equipped reopens the same ChIndex), and without
        // this reset the new actor's spawn header was read with the PREVIOUS occupant's
        // SpawnHeaderDecoded=true/ActorClassPath still attached, so the fresh header bytes were
        // parsed as if they were content - the "class~?" / garbage "SUB-OBJECT" noise this produced
        // right after a channel reopened is what exposed it.
        if (bOpen || !_channels.TryGetValue(chIndex, out var state)) {
            state = new ChannelState { ChName = chName };
            _channels[chIndex] = state;
        }

        // ORDER MATTERS: UChannel::ReceivedRawBunch calls PackageMap->ReceiveNetGUIDBunch(Bunch)
        // UNCONDITIONALLY FIRST - before the bunch is ever handed to UActorChannel::ReceivedBunch,
        // which is the one that reads the must-be-mapped GUID list. Reading must-be-mapped-guids
        // before the export batch (an earlier version of this code did) is backwards; when both
        // bits are set on the same bunch, that ordering bug alone is enough to misalign every read
        // that follows, including the actor's own spawn-header GUID.
        //
        // bHasPackageMapExports names WHAT'S AT THE FRONT of this payload, not "the whole payload
        // IS an export list" - after ReceiveNetGUIDBunch, ReceivedRawBunch STILL dispatches the
        // remainder to the channel as normal. Treating the two as mutually exclusive (an early
        // version of this code did) meant every bunch that opened an actor WITH an inline export -
        // exactly the shape of a building tool's spawn, which exports its own class the moment its
        // channel opens - decoded the export and then silently never looked at the actor content
        // that followed it in the same payload.
        if (bHasPackageMapExports) DecodeExportBunch(packetIndex, bunchReader, chIndex);

        // UActorChannel::ReceivedBunch reads the must-be-mapped GUID list off the FRONT of the
        // payload before anything else IT looks at - a uint16 count, then that many packed
        // NetGUIDs - but that is still AFTER PackageMap exports, which ReceivedRawBunch already
        // consumed above. Originally this bit was read and the list itself never consumed at all,
        // so every bunch carrying one began decoding 16+ bits early: actor GUIDs came out as 0 or
        // 1, no channel ever resolved an actor class, and all 324 RepLayout blobs were skipped.
        if (chName == EName.Actor && bHasMustBeMappedGUIDs) {
            var numMustBeMapped = bunchReader.ReadUInt16();
            for (var i = 0; i < numMustBeMapped && !bunchReader.IsError(); i++) {
                var mapped = new FNetworkGUID();
                mapped.NetSerialize(bunchReader);
            }

            if (_verbose) Log($"#{packetIndex}:   consumed {numMustBeMapped} must-be-mapped GUID(s)");
        }

        if (chName == EName.Actor) {
            DecodeActorChannelBunch(packetIndex, bunchReader, chIndex, state);
        } else if (chName == EName.Control) {
            DecodeControlChannelBunch(packetIndex, bunchReader, chIndex);
        } else if (!bHasPackageMapExports && bunchDataBits > 0 && _verbose) {
            Log($"#{packetIndex}:   ChIndex={chIndex} ({chNameStr}) content not decoded ({bunchDataBits} bits)");
        }

        if (bClose) {
            Log($"#{packetIndex}: ChIndex={chIndex} ({chNameStr}) CLOSED, reason={closeReason}");
            _sharedChannelClasses.Remove(chIndex);
        }

        return true;
    }

    /// <summary>
    ///     `state.ActorClassPath` falls back to `_sharedChannelClasses` for the direction that never
    ///     saw this channel's own ACTOR SPAWN header (see that field's declaration for why).
    /// </summary>
    private static string? EffectiveClassPath(ChannelState state, int chIndex) =>
        state.ActorClassPath ?? _sharedChannelClasses.GetValueOrDefault(chIndex);

    // ---------------- NetGUID reading (two different wire shapes - see GroundTruth.cs comment) ----------------

    /// <summary>Inline object reference (SerializeNewActor's actor/archetype/level refs, RepLayout ObjectRef properties) - IsExportingNetGUIDBunch is false at these call sites, so no path is inlined (except the rare client-only "default GUID" case).</summary>
    /// <summary>
    ///     Delegates to the SERVER's own object-reference parser rather than keeping a second copy.
    ///     That is the point of this decoder: running the production reader against a recording of a
    ///     known-good server is the only check on it that does not need a live client. A private
    ///     lookalike here would agree with itself and prove nothing - and did, right up until it was
    ///     found to be skipping the export checksum that the real one also skipped.
    /// </summary>
    private FNetworkGUID ReadGuidRef(FBitReader r) {
        UPackageMapClient.ReadObjectReference(r, out var guid, out _);
        return guid;
    }

    private FNetworkGUID ReadGuidRef(FBitReader r, out string path) {
        UPackageMapClient.ReadObjectReference(r, out var guid, out path);
        return guid;
    }

    /// <summary>
    ///     Export-bunch entry. While IsExportingNetGUIDBunch is true, InternalWriteObject writes the
    ///     export flags for EVERY guid, not just the default one - so this cannot share
    ///     ReadObjectReference, which (correctly, for its own callers) only expects flags after a
    ///     default guid.
    ///
    ///     The flags are a bitfield and bit 2 is bHasNetworkChecksum, which puts a uint32 AFTER the
    ///     path. Missing it did not fail loudly: it swallowed the next entry's first four bytes, so
    ///     exports came out as real-looking paths with their object names sheared off
    ///     ("/Game/Athena/Athena_PlayerController." with nothing after the dot) and every guid after
    ///     the first in a bunch was garbage.
    /// </summary>
    private (FNetworkGUID guid, string? path) ReadGuidExport(FBitReader r) {
        var val = r.ReadUInt32Packed();
        var guid = new FNetworkGUID(val);
        if (!guid.IsValid()) return (guid, null);

        var exportFlags = r.ReadByte();
        var hasPath = (exportFlags & 1) != 0;
        var hasNetworkChecksum = (exportFlags & 4) != 0;

        if (!hasPath) return (guid, null);

        var (_, outerPath) = ReadGuidExport(r);
        var name = r.ReadString();

        if (hasNetworkChecksum) r.ReadUInt32();

        var full = string.IsNullOrEmpty(outerPath) ? name : $"{outerPath}.{name}";
        return (guid, full);
    }

    /// <summary>
    ///     UFortAbilitySystemComponentAthena's net-field index space, generated from the 10.40 SDK
    ///     the same way NativeClassNetCache's tables are: each class's own CPF_Net properties plus
    ///     FUNC_Net functions, name-sorted, assigned base-first.
    /// </summary>
    /// <summary>
    ///     Runs the SERVER's own ability-RPC readers over a captured field and reports whether they
    ///     land exactly on its end.
    ///
    ///     This is the only validation available for a layout derived on paper. An
    ///     FGameplayAbilityTargetDataHandle has no internal framing: a decode that is a few bits off
    ///     still yields a coordinate and a name that both look entirely reasonable. What it cannot do
    ///     is finish in the right place - so "consumed N of N bits" against a recording of a
    ///     known-good server is the actual proof, and it costs no client, no test round and no match.
    /// </summary>
    private void DecodeAbilityRpcField(int packetIndex, FBitReader r, string fieldName, long fieldStart, uint fieldBits) {
        var fieldEnd = fieldStart + fieldBits;

        try {
            switch (fieldName) {
                case "ServerAbilityRPCBatch": {
                    // One struct parameter, so one leading "send" bit for the whole thing.
                    if (!r.ReadBit()) {
                        Log($"#{packetIndex}:       ServerAbilityRPCBatch send bit clear - no batch present");
                        return;
                    }

                    var batch = FServerAbilityRPCBatch.NetSerializeRead(r, ResolveExportedGuid);
                    Log($"#{packetIndex}:       {batch}");
                    ReportConsumed(packetIndex, r, fieldStart, fieldEnd, exact: true);
                    return;
                }

                case "ServerSetReplicatedTargetData": {
                    if (!r.ReadBit()) return;
                    var handle = r.ReadInt32();

                    if (!r.ReadBit()) {
                        Log($"#{packetIndex}:       ServerSetReplicatedTargetData Handle={handle} (no prediction key)");
                    } else {
                        var key = FPredictionKey.NetSerializeRead(r);
                        Log($"#{packetIndex}:       ServerSetReplicatedTargetData Handle={handle} PredictionKey=[{key}]");
                    }

                    if (!r.ReadBit()) return;

                    var targetData = FGameplayAbilityTargetDataHandle.NetSerializeRead(r, ResolveExportedGuid);
                    Log($"#{packetIndex}:       TargetData=[{targetData}]");

                    // An FGameplayTag and a second FPredictionKey follow, so this one is NOT expected
                    // to land on the field end - see NativeRpcHandlers for why the tag is undecodable.
                    ReportConsumed(packetIndex, r, fieldStart, fieldEnd, exact: false);
                    return;
                }
            }
        } catch (Exception ex) {
            Log($"#{packetIndex}:       {fieldName} decode threw at bit {r.Pos - fieldStart} of {fieldBits}: {ex.Message}");
        }
    }

    /// <summary>
    ///     A client names a UScriptStruct by id whenever the server exported that id earlier in the
    ///     session, sending no path at all. The decoder has been recording every export bunch it saw
    ///     all along - this is what makes that record answer the question.
    /// </summary>
    private string? ResolveExportedGuid(uint guid) => _guidPaths.GetValueOrDefault(guid);

    private void ReportConsumed(int packetIndex, FBitReader r, long fieldStart, long fieldEnd, bool exact) {
        var consumed = r.Pos - fieldStart;
        var leftover = fieldEnd - r.Pos;

        if (exact && leftover == 0) {
            Log($"#{packetIndex}:       decode consumed {consumed} of {fieldEnd - fieldStart} bits - EXACT");
        } else {
            Log($"#{packetIndex}:       decode consumed {consumed} of {fieldEnd - fieldStart} bits, {leftover} left over" +
                (exact ? " - MISMATCH, the declared layout does not fit the wire" : " (expected: undecoded tail)"));
        }
    }

    private const int AscMaxIndex = 53;

    private static readonly string[] AscFields = {
        // UActorComponent (0-1)
        "bIsActive", "bReplicates",
        // UGameplayTasksComponent (2)
        "SimulatedTasks",
        // UAbilitySystemComponent (3-49)
        "ActivatableAbilities", "ActiveGameplayCues", "ActiveGameplayEffects", "AvatarActor",
        "BlockedAbilityBindings", "ClientActivateAbilityFailed", "ClientActivateAbilitySucceed",
        "ClientActivateAbilitySucceedWithEventData", "ClientCancelAbility", "ClientDebugStrings",
        "ClientEndAbility", "ClientPrintDebug_Response", "ClientSetReplicatedEvent",
        "ClientTryActivateAbility", "MinimalReplicationGameplayCues", "MinimalReplicationTags",
        "NetMulticast_InvokeGameplayCueAdded", "NetMulticast_InvokeGameplayCueAdded_WithParams",
        "NetMulticast_InvokeGameplayCueAddedAndWhileActive_FromSpec",
        "NetMulticast_InvokeGameplayCueAddedAndWhileActive_WithParams",
        "NetMulticast_InvokeGameplayCueExecuted", "NetMulticast_InvokeGameplayCueExecuted_FromSpec",
        "NetMulticast_InvokeGameplayCueExecuted_WithParams",
        "NetMulticast_InvokeGameplayCuesAddedAndWhileActive_WithParams",
        "NetMulticast_InvokeGameplayCuesExecuted", "NetMulticast_InvokeGameplayCuesExecuted_WithParams",
        "OwnerActor", "RepAnimMontageInfo", "ReplicatedPredictionKeyMap", "ServerAbilityRPCBatch",
        "ServerCancelAbility", "ServerCurrentMontageJumpToSectionName",
        "ServerCurrentMontageSetNextSectionName", "ServerCurrentMontageSetPlayRate",
        "ServerDebugStrings", "ServerEndAbility", "ServerPrintDebug_Request",
        "ServerPrintDebug_RequestWithStrings", "ServerSetInputPressed", "ServerSetInputReleased",
        "ServerSetReplicatedEvent", "ServerSetReplicatedEventWithPayload",
        "ServerSetReplicatedTargetData", "ServerSetReplicatedTargetDataCancelled",
        "ServerTryActivateAbility", "ServerTryActivateAbilityWithEventData", "SpawnedAttributes",
        // UFortAbilitySystemComponent (50-52)
        "LandingMontagePair", "NetMulticast_RefreshActiveGameplayEffectCueEvents", "RepSharedAnimInfo"
    };

    private static string AscFieldName(uint index) =>
        index < AscFields.Length ? AscFields[index] : $"<out of range, max {AscFields.Length - 1}>";

    private void DecodeExportBunch(int packetIndex, FBitReader r, int chIndex) {
        var notRepLayoutExport = r.ReadBit();
        var count = r.ReadInt32();
        Log($"#{packetIndex}:   [ChIndex={chIndex}] GUID-export bunch, notRepLayoutExport={notRepLayoutExport}, count={count}");

        if (count is < 0 or > 64) {
            Log($"#{packetIndex}:     export count {count} looks implausible, aborting export decode");
            return;
        }

        for (var i = 0; i < count && !r.IsError(); i++) {
            var (guid, path) = ReadGuidExport(r);
            if (guid.IsValid() && path != null) {
                _guidPaths[guid.Value] = path;
                Log($"#{packetIndex}:     export[{i}] guid={guid.Value} path={path}");
            } else {
                Log($"#{packetIndex}:     export[{i}] guid={guid.Value} (no path)");
            }
        }

        if (Environment.GetEnvironmentVariable("DECODER_BIT_TRACE") == "1") {
            var pos = r.GetPosBits();
            var left = r.GetBitsLeft();
            var peek = Math.Min(left, 128);
            var bits = new char[peek];
            for (var i = 0; i < peek; i++) bits[i] = r.BufferBits[pos + i] ? '1' : '0';
            Log($"#{packetIndex}:     [bit-trace] after export batch: pos={pos} bitsLeft={left} next{peek}bits={new string(bits)}");
        }
    }

    private void DecodeControlChannelBunch(int packetIndex, FBitReader r, int chIndex) {
        if (r.GetBitsLeft() < 8) return;

        var msgByte = r.ReadByte();
        var nmt = Enum.IsDefined(typeof(NMT), (int) msgByte) ? ((NMT) msgByte).ToString() : $"unknown({msgByte})";
        Log($"#{packetIndex}:   [ChIndex={chIndex}] Control channel message (first in bunch): {nmt} ({r.GetBitsLeft()} bits remain in bunch, not further parsed)");
    }

    private void DecodeActorChannelBunch(int packetIndex, FBitReader r, int chIndex, ChannelState state) {
        if (!state.SpawnHeaderDecoded) {
            if (r.GetBitsLeft() < 8) {
                Log($"#{packetIndex}:   [ChIndex={chIndex}] actor channel bunch too short for a spawn header, skipping");
                return;
            }

            var actorGuidPos = r.GetPosBits();
            var actorGuid = ReadGuidRef(r, out var actorPath);
            state.ActorGuid = actorGuid.Value;
            Log($"#{packetIndex}:   [ChIndex={chIndex}] ACTOR SPAWN: actorGuid={actorGuid.Value} (dynamic={actorGuid.IsDynamic()}) path='{actorPath}' consumedBits={r.GetPosBits() - actorGuidPos}");

            state.SpawnHeaderDecoded = true;

            if (!actorGuid.IsDynamic()) {
                Log($"#{packetIndex}:     actor guid is not dynamic - decoder only models the dynamic-actor spawn path, stopping content decode for this bunch");
                return;
            }

            var archetypeGuid = ReadGuidRef(r);
            var archetypePath = _guidPaths.GetValueOrDefault(archetypeGuid.Value, $"(unresolved guid {archetypeGuid.Value} - no preceding export bunch seen on this channel)");
            state.ActorClassPath = archetypePath;
            _sharedChannelClasses[chIndex] = archetypePath;
            Log($"#{packetIndex}:     archetype guid={archetypeGuid.Value} path={archetypePath}");

            var levelGuid = ReadGuidRef(r);
            Log($"#{packetIndex}:     levelGuid={levelGuid.Value}");

            var bLoc = r.ReadBit();
            var bRot = r.ReadBit();
            var bScale = r.ReadBit();
            var bVel = r.ReadBit();
            Log($"#{packetIndex}:     bSerializeLocation={bLoc} bSerializeRotation={bRot} bSerializeScale={bScale} bSerializeVelocity={bVel}");

            if (bLoc || bRot || bScale || bVel) {
                Log($"#{packetIndex}:     spawn includes transform data - wire format for quantized Location/Rotation/Scale/Velocity is not modeled by this decoder, stopping content decode for this bunch");
                return;
            }
        }

        DecodeContentBlocks(packetIndex, r, chIndex, state);
    }

    private void DecodeContentBlocks(int packetIndex, FBitReader r, int chIndex, ChannelState state) {
        while (!r.AtEnd() && !r.IsError()) {
            var bHasRepLayout = r.ReadBit();
            var bIsActor = r.ReadBit();
            if (!bIsActor) {
                // UActorChannel::ReadContentBlockHeader's sub-object branch: the block names the
                // replicated COMPONENT it belongs to, then carries an ordinary field payload.
                // Every GameplayAbilities RPC arrives this way.
                var subGuid = ReadGuidRef(r);
                var subPath = _guidPaths.GetValueOrDefault(subGuid.Value);
                var subBits = r.ReadUInt32Packed();
                var subStart = r.Pos;

                // A block claiming more bits than the packet has left means the bunch is already
                // desynced - almost always because this sub-object is NOT an AbilitySystemComponent
                // and its own header framing differs. Decoding on regardless produces field indices
                // and payload sizes that look real and are pure noise; that noise was briefly
                // mistaken for genuine ActivatableAbilities traffic. Stop instead.
                if (subBits > (uint) (r.GetNumBits() - subStart)) {
                    Log($"#{packetIndex}:   [ChIndex={chIndex}] SUB-OBJECT block guid={subGuid.Value} claims " +
                        $"{subBits} bits with only {r.GetNumBits() - subStart} left - not an ASC block, or the " +
                        "bunch is desynced. Abandoning this bunch.");
                    return;
                }

                var subEnd = subStart + subBits;

                Log($"#{packetIndex}:   [ChIndex={chIndex}] SUB-OBJECT block guid={subGuid.Value} " +
                    $"path={subPath ?? "(unknown)"} bHasRepLayout={bHasRepLayout} numPayloadBits={subBits}");

                // The AbilitySystemComponent's own ClassNetCache, derived from the 10.40 SDK:
                // UObject(0) + UActorComponent(2) + UGameplayTasksComponent(1) +
                // UAbilitySystemComponent(47) + UFortAbilitySystemComponent(3) = GetMaxIndex 53,
                // so a field index is SerializeInt(.., 54) = 6 bits.
                if (!bHasRepLayout && subBits > 0) {
                    while (r.Pos < subEnd && !r.IsError()) {
                        var fieldIndex = r.ReadInt((uint) (AscMaxIndex + 1));
                        var fieldBits = r.ReadUInt32Packed();
                        if (r.IsError() || r.Pos + fieldBits > subEnd) {
                            Log($"#{packetIndex}:     field decode stopped (index={fieldIndex} bits={fieldBits} would overrun the block)");
                            break;
                        }

                        var fieldStart = r.Pos;
                        var hex = new System.Text.StringBuilder();
                        for (var bit = 0; bit < fieldBits && bit < 256; bit++) {
                            if (bit % 8 == 0 && bit > 0) hex.Append(' ');
                            hex.Append(r.BufferBits[(int) (fieldStart + bit)] ? '1' : '0');
                        }

                        Log($"#{packetIndex}:     FIELD index={fieldIndex} ({AscFieldName(fieldIndex)}) numPayloadBits={fieldBits} bits={hex}");

                        DecodeAbilityRpcField(packetIndex, r, AscFieldName(fieldIndex), fieldStart, fieldBits);

                        r.Pos = (int) (fieldStart + fieldBits);
                    }
                }

                r.Pos = (int) subEnd;
                continue;
            }

            var numPayloadBits = r.ReadUInt32Packed();
            var blockStart = r.Pos;
            var blockEnd = Math.Min(blockStart + numPayloadBits, r.GetNumBits());

            var label = EffectiveClassPath(state, chIndex) ?? "?";
            Log($"#{packetIndex}:   [ChIndex={chIndex}, class~{ShortClassName(label)}] content block bHasRepLayout={bHasRepLayout} numPayloadBits={numPayloadBits}");

            if (bHasRepLayout) DecodeRepLayoutBlob(packetIndex, r, chIndex, blockEnd, state);

            if (r.Pos < blockEnd) DecodeClassNetCacheFields(packetIndex, r, chIndex, blockEnd, state);

            r.Pos = blockEnd;
        }
    }

    private void DecodeRepLayoutBlob(int packetIndex, FBitReader r, int chIndex, long blockEnd, ChannelState state) {
        var classPath = EffectiveClassPath(state, chIndex);
        var handleMap = GetHandleMap(classPath);
        if (handleMap == null) {
            Log($"#{packetIndex}:     RepLayout blob present but no ground-truth handle table matched for class '{classPath}', skipping (raw hex below)");
            LogHexRemainder(packetIndex, r, r.Pos, blockEnd);
            return;
        }

        var checksumBit = r.ReadBit();
        if (checksumBit) Log($"#{packetIndex}:     unexpected leading checksum bit=true (expected false) - decode may already be desynced");

        while (true) {
            if (r.Pos >= blockEnd || r.IsError()) {
                Log($"#{packetIndex}:     ran out of block space before a handle-0 terminator");
                return;
            }

            var handle = r.ReadUInt32Packed();
            if (handle == 0) {
                Log($"#{packetIndex}:     [terminator]");
                break;
            }

            if (!handleMap.TryGetValue(handle, out var def)) {
                Log($"#{packetIndex}:     handle {handle}: UNKNOWN (beyond this project's ground-truth table) - stopping RepLayout decode here");
                LogHexRemainder(packetIndex, r, r.Pos, blockEnd);
                return;
            }

            if (def.IsObjectRef) {
                var guid = ReadGuidRef(r);
                var path = _guidPaths.GetValueOrDefault(guid.Value);
                Log($"#{packetIndex}:     handle {handle} = {def.Name} (ObjectRef) guid={guid.Value}{(path != null ? $" path={path}" : "")}");
                continue;
            }

            if (def.FixedBits == null) {
                Log($"#{packetIndex}:     handle {handle} = {def.Name} (name known, width NOT modeled) - stopping RepLayout decode here");
                LogHexRemainder(packetIndex, r, r.Pos, blockEnd);
                return;
            }

            if (r.Pos + def.FixedBits.Value > blockEnd) {
                Log($"#{packetIndex}:     handle {handle} = {def.Name}: declared width ({def.FixedBits} bits) overruns the block - stopping");
                return;
            }

            r.Pos += def.FixedBits.Value;
            Log($"#{packetIndex}:     handle {handle} = {def.Name} ({def.FixedBits} bits, value skipped)");
        }
    }

    private void DecodeClassNetCacheFields(int packetIndex, FBitReader r, int chIndex, long blockEnd, ChannelState state) {
        var classPath = EffectiveClassPath(state, chIndex);
        var cache = GuessClassNetCache(classPath);
        if (cache == null) {
            Log($"#{packetIndex}:     trailing field data present but no confident ClassNetCache match for class '{classPath}' - skipping field decode (raw hex below)");
            LogHexRemainder(packetIndex, r, r.Pos, blockEnd);
            return;
        }

        var maxIndex = cache.GetMaxIndex();

        while (r.Pos < blockEnd && !r.IsError()) {
            var repIndex = (int) r.ReadInt((uint) (maxIndex + 1));
            if (r.IsError()) break;

            var fieldBits = r.ReadUInt32Packed();
            var fieldStart = r.Pos;
            var fieldEnd = Math.Min(fieldStart + fieldBits, blockEnd);
            var fieldName = cache.GetFromIndex(repIndex)?.Name ?? "?";

            Log($"#{packetIndex}:     field[{repIndex}] = {fieldName} ({fieldBits} bits)");

            if (fieldName == "ServerCreateBuildingActor") {
                DecodeCreateBuildingActorField(packetIndex, r, fieldStart, fieldBits);
            }

            r.Pos = fieldEnd;
        }
    }

    /// <summary>
    ///     AFortPlayerController::ServerCreateBuildingActor(FCreateBuildingActorData) - SOLVED.
    ///
    ///     The struct is STRUCT_NetSerializeNative, so RepLayout emits one generic cmd for the whole
    ///     thing and a hand-written native NetSerialize decides the bytes. The layout, how it was
    ///     derived (from a client memory dump, plus the one AddPropertyCmd log line that proves the
    ///     flag), and the three things about it that no SDK dump can tell you - BuildLoc is three RAW
    ///     floats despite being declared FVector_NetQuantize10, only BuildRot.Yaw is sent and only as
    ///     one of four codes, and BuildingClassData.BuildingClass never reaches the wire at all - are
    ///     all documented on AFortOnlineBeacon's FCreateBuildingActorData.
    ///
    ///     Two earlier hypotheses failed and are worth remembering, because both were reasonable:
    ///
    ///     1. A flat per-member sequential dump in declaration order. Wrong on three counts at once -
    ///     it missed SendPropertiesForRPC's leading per-parameter "send" bit, it read BuildLoc as
    ///     SerializePackedVector&lt;10,24&gt;, and it expected a full FRotator. It always stalled at a
    ///     constant 127 of 178 bits with BuildLoc identical across every sample.
    ///
    ///     2. A RepLayout handle sequence (packed handle, value, ..., terminator). Wrong because
    ///     SendPropertiesForRPC only uses that shape on an InternalAck (replay/demo) connection; a
    ///     live connection writes one presence bit per non-bool parameter and then the cmds back to
    ///     back with no handles at all.
    /// </summary>
    private void DecodeCreateBuildingActorField(int packetIndex, FBitReader r, long fieldStart, uint fieldBits) {
        var fieldEnd = fieldStart + fieldBits;

        // One leading presence bit for the single (non-bool) parameter - FRepLayout::ReceivePropertiesForRPC.
        if (!r.ReadBit()) {
            Log($"#{packetIndex}:       ServerCreateBuildingActor CreateBuildingData: not sent (presence bit 0)");
            return;
        }

        var handle = r.ReadInt(0x1FF);
        var mirrored = r.ReadUInt32() != 0;   // bool serialized as a legacy 32-bit UBOOL
        var x = r.ReadFloat();
        var y = r.ReadFloat();
        var z = r.ReadFloat();
        var upgradeLevel = r.ReadByte();
        var syncKey = ((int) r.ReadInt(0x1000000) - 0x800000) / 13f;
        var yawCode = r.ReadByte();
        var yaw = yawCode switch { 0 => 180f, 1 => 90f, 2 => 0f, _ => -90f };

        Log($"#{packetIndex}:       ServerCreateBuildingActor BuildLoc=({x:F1}, {y:F1}, {z:F1}) Yaw={yaw} " +
            $"Mirrored={mirrored} BuildingClassHandle={handle} UpgradeLevel={upgradeLevel} SyncKey={syncKey:F2}");

        // A struct with no internal framing gives no other sign of a misread, so the field's own
        // declared width is the only check there is - and it is a strict one here.
        var leftover = fieldEnd - r.Pos;
        if (leftover != 0) {
            Log($"#{packetIndex}:       ...decoded {r.Pos - fieldStart} of {fieldBits} bits - {leftover} left over, values above are suspect");
            LogHexRemainder(packetIndex, r, fieldStart, fieldEnd);
        }
    }

    private Dictionary<uint, GroundTruth.RepHandleDef>? GetHandleMap(string? classPath) {
        var key = ClassifyClassPath(classPath);
        return key != null ? _handleMaps[key] : null;
    }

    private FClassNetCache? GuessClassNetCache(string? classPath) {
        return ClassifyClassPath(classPath) switch {
            "PlayerController" => GroundTruth.PlayerControllerCache,
            "PlayerState" => GroundTruth.PlayerStateCache,
            "Pawn" => GroundTruth.PawnCache,
            // GameState (NativeClassNetCache has no real entry for it either - falls back to the
            // generic ActorCache there too, which is too small to trust for a bounded-int read) and
            // bare Controller (no dedicated cache duplicated here) are deliberately left unresolved -
            // guessing a maxIndex would silently misdecode rather than fail safely.
            _ => null
        };
    }

    /// <summary>Best-effort bucketing of a real archetype path string into one of our ground-truth tables, by substring match against well-known Fortnite/UE class name fragments.</summary>
    private static string? ClassifyClassPath(string? classPath) {
        if (string.IsNullOrEmpty(classPath)) return null;
        if (classPath.Contains("PlayerController", StringComparison.OrdinalIgnoreCase)) return "PlayerController";
        if (classPath.Contains("PlayerState", StringComparison.OrdinalIgnoreCase)) return "PlayerState";
        if (classPath.Contains("GameState", StringComparison.OrdinalIgnoreCase)) return "GameState";
        if (classPath.Contains("Pawn", StringComparison.OrdinalIgnoreCase) || classPath.Contains("Character", StringComparison.OrdinalIgnoreCase)) return "Pawn";
        if (classPath.Contains("Controller", StringComparison.OrdinalIgnoreCase)) return "Controller";
        // Every weapon asset this project knows about (rifles, pickaxes, building tools) lives under
        // a "/Weapons/" content path - the same signal FortWeaponActorClasses.cs's generated table
        // was built from.
        if (classPath.Contains("/Weapons/", StringComparison.OrdinalIgnoreCase)) return "Weapon";
        return "Actor";
    }

    private static string ShortClassName(string path) {
        var lastDot = path.LastIndexOf('.');
        return lastDot >= 0 ? path[(lastDot + 1)..] : path;
    }

    private void LogHexRemainder(int packetIndex, FBitReader r, long startBit, long endBit) {
        var numBits = endBit - startBit;
        if (numBits <= 0) return;

        var bytes = new byte[(numBits + 7) / 8];
        for (long i = 0; i < numBits; i++) {
            if (r.BufferBits[(int) (startBit + i)]) bytes[i >> 3] |= (byte) (1 << (int) (i & 7));
        }

        Log($"#{packetIndex}:       remaining {numBits} bits raw hex: {Convert.ToHexString(bytes)}");
    }

    private static int BestSignedDifference(int value, int reference, int max) => ((value - reference + max / 2) & (max - 1)) - max / 2;
    private static int MakeRelative(int value, int reference, int max) => reference + BestSignedDifference(value, reference, max);

    private static void Log(string s) => Console.WriteLine(s);
}
