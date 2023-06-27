namespace AFortOnlineBeacon.Net.Packets.Header;

public ref struct FNotificationHeader {
    public SequenceHistory History;
    public uint HistoryWordCount;
    public SequenceNumber Seq;
    public SequenceNumber AckedSeq;
}