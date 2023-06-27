namespace AFortOnlineBeacon.Net;

public static class FChannelRecordImpl {
    // public static void ConsumeAllChannelRecords(FWrittenChannelsRecord)
}

public class FWrittenChannelsRecord {
    public class FChannelRecordEntry {
        public uint Value { get; set; } = 31;
        public uint IsSequence { get; set; } = 1;
    }

    public Queue<FChannelRecordEntry> CHannelRecord { get; set; } = new Queue<FChannelRecordEntry>();
    public int LastPacketId { get; set; } = -1;

    public FWrittenChannelsRecord(int initialSize = 1024) {
        CHannelRecord = new Queue<FChannelRecordEntry>(initialSize);
    }
}