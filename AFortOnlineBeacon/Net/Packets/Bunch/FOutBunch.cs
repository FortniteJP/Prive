namespace AFortOnlineBeacon.Net.Packets.Bunch;

public class FOutBunch : FNetBitWriter {
    public FOutBunch() : base(0) => ChName = EName.None;

    public FOutBunch(UChannel inChannel, bool bInClose) : base(inChannel.Connection!.PackageMap!, inChannel.Connection.GetMaxSingleBunchSizeBits()) {
        Next = null;
        Channel = inChannel;
        Time = 0;
        ChIndex = inChannel.ChIndex;
        ChType = inChannel.ChType;
        ChName = inChannel.ChName;
        ChSequence = 0;
        PacketId = 0;
        ReceivedAck = false;
        bOpen = false;
        bClose = bInClose;
        bDormant = false;
        bIsReplicationPaused = false;
        bReliable = false;
        bPartial = false;
        bPartialInitial = false;
        bPartialFinal = false;
        bHasPackageMapExports = false;
        bHasMustBeMappedGUIDs = false;
        CloseReason = EChannelCloseReason.Destroyed;
        
        // Match the byte swapping settings of the connection
        // TODO: SetByteSwapping(Channel->Connection->bNeedsByteSwapping);
        
        // Reserve channel and set bunch info.
        if (Channel.NumOutRec >= UNetConnection.ReliableBuffer - 1 + (bClose ? 1 : 0)) SetOverflowed(-1);
    }
    
    public FOutBunch(UPackageMap inPackageMap, long maxBits) : base(inPackageMap, maxBits) {
        Next = null;
        Channel = null;
        Time = 0;
        ChIndex = 0;
        ChType = EChannelType.CHTYPE_None;
        ChName = EName.None;
        ChSequence = 0;
        PacketId = 0;
        ReceivedAck = false;
        bOpen = false;
        bClose = false;
        bDormant = false;
        bIsReplicationPaused = false;
        bReliable = false;
        bPartial = false;
        bPartialInitial = false;
        bPartialFinal = false;
        bHasPackageMapExports = false;
        bHasMustBeMappedGUIDs = false;
        CloseReason = EChannelCloseReason.Destroyed;
    }

    public FOutBunch(FOutBunch bunch) : base(bunch) {
        Next = bunch.Next;
        Channel = bunch.Channel;
        Time = bunch.Time;
        ChIndex = bunch.ChIndex;
        ChType = bunch.ChType;
        ChName = bunch.ChName;
        ChSequence = bunch.ChSequence;
        PacketId = bunch.PacketId;
        ReceivedAck = bunch.ReceivedAck;
        bOpen = bunch.bOpen;
        bClose = bunch.bClose;
        bDormant = bunch.bDormant;
        bIsReplicationPaused = bunch.bIsReplicationPaused;
        bReliable = bunch.bReliable;
        bPartial = bunch.bPartial;
        bPartialInitial = bunch.bPartialInitial;
        bPartialFinal = bunch.bPartialFinal;
        bHasPackageMapExports = bunch.bHasPackageMapExports;
        bHasMustBeMappedGUIDs = bunch.bHasMustBeMappedGUIDs;
        CloseReason = bunch.CloseReason;
    }

    public FOutBunch? Next { get; set; }
    public UChannel? Channel { get; set; }
    public double Time { get; set; }
    public int ChIndex { get; set; }
    public EChannelType ChType { get; set; }
    public FName ChName { get; set; }
    public int ChSequence { get; set; }
    public int PacketId { get; set; }
    public bool ReceivedAck { get; set; }
    public bool bOpen { get; set; }
    public bool bClose { get; set; }
    public bool bDormant { get; set; }
    public bool bIsReplicationPaused { get; set; }
    public bool bReliable { get; set; }
    public bool bPartial { get; set; }
    public bool bPartialInitial { get; set; }
    public bool bPartialFinal { get; set; }
    public bool bHasPackageMapExports { get; set; }
    public bool bHasMustBeMappedGUIDs { get; set; }
    public EChannelCloseReason CloseReason { get; set; }
    public List<FNetworkGUID> ExportNetGUIDs { get; } = new List<FNetworkGUID>();
    public List<ulong> NetFieldExports { get; } = new List<ulong>();
}