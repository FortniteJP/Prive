namespace AFortOnlineBeacon.Net.Packets.Header;

public delegate void HandlePacketNotification(PacketNotifyUpdateContext context, SequenceNumber ackedSequence, bool delivered);

public readonly ref struct PacketNotifyUpdateContext {
    public readonly HandlePacketNotification Func;
    public readonly List<FChannelCloseInfo> ChannelToClose;

    public PacketNotifyUpdateContext(HandlePacketNotification func, List<FChannelCloseInfo> channelToClose) {
        Func = func;
        ChannelToClose = channelToClose;
    }
}

public class FNetPacketNotify {
    public const int SequenceNumberBits = 14;
    public const int MaxSequenceHistoryLength = 256;

    private readonly Queue<FSentAckData> _AckRecord = new Queue<FSentAckData>();
    private uint _WrittenHistoryWordCount;
    private SequenceNumber _WrittenInAckSeq;
    
    private readonly SequenceHistory _InSeqHistory = new SequenceHistory();
    private SequenceNumber _InSeq;
    private SequenceNumber _InAckSeq;
    private SequenceNumber _InAckSeqAck;

    private SequenceNumber _OutSeq;
    private SequenceNumber _OutAckSeq;

    public void Init(SequenceNumber initialInSeq, SequenceNumber initialOutSeq) {
        _InSeqHistory.Reset();
        _InSeq = initialInSeq;
        _InAckSeq = initialInSeq;
        _InAckSeqAck = initialInSeq;
        _OutSeq = initialOutSeq;
        _OutAckSeq = new SequenceNumber((ushort)(initialOutSeq.Value - 1));
    }

    public SequenceNumber GetInSeq() {
        return _InSeq;
    }

    public SequenceNumber GetInAckSeq() {
        return _InAckSeq;
    }

    public SequenceNumber GetOutSeq() {
        return _OutSeq;
    }

    public SequenceNumber GetOutAckSeq() {
        return _OutAckSeq;
    }

    public bool WriteHeader(FBitWriter writer, bool bRefresh) {
        var currentHistoryWorkCount = Math.Clamp((GetCurrentSequenceHistoryLength() + SequenceHistory.BitsPerWord - 1) / SequenceHistory.BitsPerWord, 1, SequenceHistory.WordCount);
        
        if (bRefresh && (currentHistoryWorkCount > _WrittenHistoryWordCount)) return false;
        
        _WrittenHistoryWordCount = bRefresh ? _WrittenHistoryWordCount : currentHistoryWorkCount;
        _WrittenInAckSeq = _InAckSeq;

        var seq = _OutSeq;
        var ackedSeq = _InAckSeq;
        
        var packedHeader = FPackedHeader.Pack(seq, ackedSeq, _WrittenHistoryWordCount - 1);
        writer.WriteUInt32(packedHeader);
        _InSeqHistory.Write(writer, _WrittenHistoryWordCount);

        return true;
    }

    private uint GetCurrentSequenceHistoryLength() {
        if (_InAckSeq.GreaterEq(_InAckSeqAck)) return (uint) SequenceNumber.Diff(_InAckSeq, _InAckSeqAck);

        return SequenceHistory.Size;
    }

    public bool ReadHeader(ref FNotificationHeader data, FBitReader reader) {
        var packedHeader = reader.ReadUInt32();

        data.Seq = FPackedHeader.GetSeq(packedHeader);
        data.AckedSeq = FPackedHeader.GetAckedSeq(packedHeader);
        data.HistoryWordCount = FPackedHeader.GetHistoryWordCount(packedHeader) + 1;
        data.History = new SequenceHistory();
        data.History.Read(reader, data.HistoryWordCount);
        
        return !reader.IsError();
    }

    public int GetSequenceDelta(FNotificationHeader notificationData) {
        if (!notificationData.Seq.Greater(_InSeq)) return 0;
        if (!notificationData.AckedSeq.GreaterEq(_OutAckSeq)) return 0;
        if (!_OutSeq.Greater(notificationData.AckedSeq)) return 0;
        
        return SequenceNumber.Diff(notificationData.Seq, _InSeq);
    }

    public int Update(FNotificationHeader notificationData, PacketNotifyUpdateContext context) {
        var inSeqDelta = GetSequenceDelta(notificationData);
        if (inSeqDelta > 0) {
            ProcessReceivedAcks(notificationData, context);
            _InSeq = notificationData.Seq;
            return inSeqDelta;
        }

        return 0;
    }

    private void ProcessReceivedAcks(FNotificationHeader notificationData, PacketNotifyUpdateContext context) {
        if (notificationData.AckedSeq.Greater(_OutAckSeq)) {
            var ackCount = SequenceNumber.Diff(notificationData.AckedSeq, _OutAckSeq);
            _InAckSeqAck = UpdateInAckSeqAck(ackCount, notificationData.AckedSeq);
            var currentAck = new SequenceNumber(_OutAckSeq.Value).IncrementAndGet();
            if (ackCount > SequenceHistory.Size) {}
            
            while (ackCount > SequenceHistory.Size) {
                --ackCount;
                context.Func(context, currentAck, false);
                currentAck = currentAck.IncrementAndGet();
            }
            
            while (ackCount > 0) {
                --ackCount;
                context.Func(context, currentAck, notificationData.History.IsDelivered(ackCount));
                currentAck = currentAck.IncrementAndGet();
            }

            _OutAckSeq = notificationData.AckedSeq;
        }
    }

    private SequenceNumber UpdateInAckSeqAck(int ackCount, SequenceNumber ackedSeq) {
        if (ackCount <= _AckRecord.Count) {
            if (ackCount > 1) for (var i = 0; i < ackCount - 1; i++) _AckRecord.Dequeue();

            var ackData = _AckRecord.Dequeue();
            if (ackData.OutSeq.Equals(ackedSeq)) return ackData.InAckSeq;
        }

        return new SequenceNumber((ushort)(ackedSeq.Value - MaxSequenceHistoryLength));
    }

    public void AckSeq(SequenceNumber seq) => AckSeq(seq, true);

    public void NakSeq(SequenceNumber seq) => AckSeq(seq, false);

    private void AckSeq(SequenceNumber ackedSeq, bool isAck) {
        while (ackedSeq.Greater(_InAckSeq)) {
            _InAckSeq = _InAckSeq.IncrementAndGet();
            var bReportAcked = _InAckSeq.Equals(ackedSeq) ? isAck : false;
            _InSeqHistory.AddDeliveryStatus(bReportAcked);
        }
    }
    
    public SequenceNumber CommitAndIncrementOutSeq() {
        _AckRecord.Enqueue(new FSentAckData(_OutSeq, _WrittenInAckSeq));
        _WrittenHistoryWordCount = 0;
        _OutSeq = _OutSeq.IncrementAndGet();

        return _OutSeq;
    }
}