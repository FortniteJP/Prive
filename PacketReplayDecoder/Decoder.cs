using System.Net;
using System.Net.Sockets;
using AFortOnlineBeacon.Core;
using AFortOnlineBeacon.Core.Names;
using AFortOnlineBeacon.Net;
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

    private readonly Dictionary<int, int> _inReliable = new();
    private readonly Dictionary<int, ChannelState> _channels = new();
    private readonly Dictionary<uint, string> _guidPaths = new();

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

        var bunchReader = new FBitReader(payloadBytes, (int) bunchDataBits);
        bunchReader.SetEngineNetVer(UNetConnection.DefaultEngineNetworkProtocolVersion);
        bunchReader.SetGameNetVer(0);

        if (!_channels.TryGetValue(chIndex, out var state)) {
            state = new ChannelState { ChName = chName };
            _channels[chIndex] = state;
        }

        var chNameStr = chName.ToEName()?.ToString() ?? chName.ToString();
        if (_verbose) Log($"#{packetIndex}: bunch ChIndex={chIndex} ChName={chNameStr} bOpen={bOpen} bClose={bClose} bReliable={bReliable} bPartial={bPartial}/{bPartialInitial}/{bPartialFinal} bHasPackageMapExports={bHasPackageMapExports} bits={bunchDataBits}");

        if (bHasPackageMapExports) {
            DecodeExportBunch(packetIndex, bunchReader, chIndex);
        } else if (chName == EName.Actor) {
            DecodeActorChannelBunch(packetIndex, bunchReader, chIndex, state);
        } else if (chName == EName.Control) {
            DecodeControlChannelBunch(packetIndex, bunchReader, chIndex);
        } else if (bunchDataBits > 0 && _verbose) {
            Log($"#{packetIndex}:   ChIndex={chIndex} ({chNameStr}) content not decoded ({bunchDataBits} bits)");
        }

        if (bClose) Log($"#{packetIndex}: ChIndex={chIndex} ({chNameStr}) CLOSED, reason={closeReason}");

        return true;
    }

    // ---------------- NetGUID reading (two different wire shapes - see GroundTruth.cs comment) ----------------

    /// <summary>Inline object reference (SerializeNewActor's actor/archetype/level refs, RepLayout ObjectRef properties) - IsExportingNetGUIDBunch is false at these call sites, so no path is inlined (except the rare client-only "default GUID" case).</summary>
    private FNetworkGUID ReadGuidRef(FBitReader r) {
        var val = r.ReadUInt32Packed();
        var guid = new FNetworkGUID(val);
        if (!guid.IsValid()) return guid;
        if (guid.IsDefault()) {
            var hasPath = r.ReadByte();
            if (hasPath != 0) {
                ReadGuidRef(r); // outer, discarded (default-GUID path is a client->server-only case)
                r.ReadString();
            }
        }

        return guid;
    }

    /// <summary>Export-bunch entry (IsExportingNetGUIDBunch true for the whole bunch) - always carries a bHasPath byte, and recurses through the SAME export-style reader for its outer chain.</summary>
    private (FNetworkGUID guid, string? path) ReadGuidExport(FBitReader r) {
        var val = r.ReadUInt32Packed();
        var guid = new FNetworkGUID(val);
        if (!guid.IsValid()) return (guid, null);

        var hasPath = r.ReadByte();
        if (hasPath == 0) return (guid, null);

        var (_, outerPath) = ReadGuidExport(r);
        var name = r.ReadString();
        var full = string.IsNullOrEmpty(outerPath) ? name : $"{outerPath}.{name}";
        return (guid, full);
    }

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

            var actorGuid = ReadGuidRef(r);
            state.ActorGuid = actorGuid.Value;
            Log($"#{packetIndex}:   [ChIndex={chIndex}] ACTOR SPAWN: actorGuid={actorGuid.Value} (dynamic={actorGuid.IsDynamic()})");

            state.SpawnHeaderDecoded = true;

            if (!actorGuid.IsDynamic()) {
                Log($"#{packetIndex}:     actor guid is not dynamic - decoder only models the dynamic-actor spawn path, stopping content decode for this bunch");
                return;
            }

            var archetypeGuid = ReadGuidRef(r);
            var archetypePath = _guidPaths.GetValueOrDefault(archetypeGuid.Value, $"(unresolved guid {archetypeGuid.Value} - no preceding export bunch seen on this channel)");
            state.ActorClassPath = archetypePath;
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
                Log($"#{packetIndex}:   [ChIndex={chIndex}] sub-object content block (not decoded)");
                return;
            }

            var numPayloadBits = r.ReadUInt32Packed();
            var blockStart = r.Pos;
            var blockEnd = Math.Min(blockStart + numPayloadBits, r.GetNumBits());

            var label = state.ActorClassPath ?? "?";
            Log($"#{packetIndex}:   [ChIndex={chIndex}, class~{ShortClassName(label)}] content block bHasRepLayout={bHasRepLayout} numPayloadBits={numPayloadBits}");

            if (bHasRepLayout) DecodeRepLayoutBlob(packetIndex, r, chIndex, blockEnd, state);

            if (r.Pos < blockEnd) DecodeClassNetCacheFields(packetIndex, r, chIndex, blockEnd, state);

            r.Pos = blockEnd;
        }
    }

    private void DecodeRepLayoutBlob(int packetIndex, FBitReader r, int chIndex, long blockEnd, ChannelState state) {
        var handleMap = GetHandleMap(state.ActorClassPath);
        if (handleMap == null) {
            Log($"#{packetIndex}:     RepLayout blob present but no ground-truth handle table matched for class '{state.ActorClassPath}', skipping (raw hex below)");
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
        var cache = GuessClassNetCache(state.ActorClassPath);
        if (cache == null) {
            Log($"#{packetIndex}:     trailing field data present but no confident ClassNetCache match for class '{state.ActorClassPath}' - skipping field decode (raw hex below)");
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

            r.Pos = fieldEnd;
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
