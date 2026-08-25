namespace AFortOnlineBeacon.Net;

/// <summary>
///     Port of UE 4.23's FWrittenChannelsRecord plus the FChannelRecordImpl helpers that operate on it
///     (Engine/Private/NetConnection.cpp, "FChannelRecordImpl" section).
///
///     This is the record of which channels wrote data into each outgoing packet, and it is the ONLY
///     thing that lets a connection turn "packet N was delivered" back into "these channels' reliable
///     bunches can now be released". Without it, UChannel::OutRec never drains: FOutBunch.ReceivedAck
///     stays false forever, UChannel::ReceivedAcks always exits on its first loop test, NumOutRec
///     climbs monotonically, and the connection dies a few seconds in when it trips the
///     RELIABLE_BUFFER limit - which surfaces as an opaque send failure that never mentions acks.
///
///     The queue is a flat stream of entries rather than a map: one entry per transmitted packet
///     (IsSequence=1, Value=PacketId), each followed by zero or more channel entries (IsSequence=0,
///     Value=ChIndex). Packets are consumed strictly in order, which is safe because
///     FNetPacketNotify hands notifications back in order too.
/// </summary>
public sealed class FWrittenChannelsRecord {
    public readonly struct FChannelRecordEntry {
        public FChannelRecordEntry(uint value, bool isSequence) {
            Value = value;
            IsSequence = isSequence;
        }

        public uint Value { get; }
        public bool IsSequence { get; }
    }

    private readonly Queue<FChannelRecordEntry> _Record = new();

    public int LastPacketId { get; private set; } = UnrealConstants.IndexNone;

    public int Count => _Record.Count;

    /// <summary>
    ///     Called from InitSequence. Anything recorded before the packet sequence was initialised sits
    ///     outside FNetPacketNotify's accounting and would never be consumed, permanently offsetting
    ///     the queue against the notification stream.
    /// </summary>
    public void Reset() {
        _Record.Clear();
        LastPacketId = UnrealConstants.IndexNone;
    }

    /// <summary>
    ///     Called once per transmitted packet, even if no channel wrote into it, so that every packet
    ///     the notify layer will later report on has a matching entry to consume.
    /// </summary>
    public void PushPacketId(int packetId) {
        if (packetId == LastPacketId) return;

        _Record.Enqueue(new FChannelRecordEntry((uint) packetId, true));
        LastPacketId = packetId;
    }

    public void PushChannelRecord(int packetId, int channelIndex) {
        PushPacketId(packetId);
        _Record.Enqueue(new FChannelRecordEntry((uint) channelIndex, false));
    }

    /// <summary>
    ///     Consume the leading packet entry and every channel entry behind it, invoking
    ///     <paramref name="func"/> once per distinct channel. Real UE checks the leading entry really
    ///     is the expected packet id; here that mismatch is reported rather than fatal, since getting
    ///     it wrong desynchronises the whole queue and is worth seeing rather than crashing on.
    /// </summary>
    public void ConsumeChannelRecordsForPacket(int packetId, Action<int, uint> func) {
        if (_Record.Count == 0) {
            Console.WriteLine($"FWrittenChannelsRecord: no record to consume for PacketId={packetId} (queue empty)");
            return;
        }

        var packetEntry = _Record.Dequeue();

        if (!packetEntry.IsSequence || packetEntry.Value != (uint) packetId) {
            Console.WriteLine($"FWrittenChannelsRecord: record desync - expected packet entry for PacketId={packetId}, " +
                $"got IsSequence={packetEntry.IsSequence} Value={packetEntry.Value}");
            return;
        }

        var previousChannelIndex = uint.MaxValue;

        while (_Record.Count > 0 && !_Record.Peek().IsSequence) {
            var entry = _Record.Dequeue();
            var channelIndex = entry.Value;

            // Only process a channel once per packet, even if it wrote several bunches into it.
            if (channelIndex == previousChannelIndex) continue;

            func(packetId, channelIndex);
            previousChannelIndex = channelIndex;
        }
    }

    /// <summary>
    ///     Used by the internal-ack (replay) path, where every packet is delivered by definition.
    /// </summary>
    public void ConsumeAllChannelRecords(Action<uint> func) {
        var previousChannelIndex = uint.MaxValue;

        while (_Record.Count > 0) {
            var entry = _Record.Dequeue();
            var channelIndex = entry.Value;

            if (entry.IsSequence || channelIndex == previousChannelIndex) continue;

            func(channelIndex);
            previousChannelIndex = channelIndex;
        }
    }
}
