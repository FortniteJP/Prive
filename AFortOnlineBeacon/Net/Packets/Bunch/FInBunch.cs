namespace AFortOnlineBeacon.Net.Packets.Bunch;

public class FInBunch : FNetBitReader {
    public FInBunch(FInBunch inBunch, bool copyBuffer) : base(inBunch.PackageMap, null, 0) {
        PacketId = inBunch.PacketId;
        Next = inBunch.Next;
        Connection = inBunch.Connection;
        ChIndex = inBunch.ChIndex;
        ChType = inBunch.ChType;
        ChName = inBunch.ChName;
        ChSequence = inBunch.ChSequence;
        bOpen = inBunch.bOpen;
        bClose = inBunch.bClose;
        bDormant = inBunch.bDormant;
        bIsReplicationPaused = inBunch.bIsReplicationPaused;
        bReliable = inBunch.bReliable;
        bPartial = inBunch.bPartial;
        bPartialInitial = inBunch.bPartialInitial;
        bPartialFinal = inBunch.bPartialFinal;
        bHasPackageMapExports = inBunch.bHasPackageMapExports;
        bHasMustBeMappedGUIDs = inBunch.bHasMustBeMappedGUIDs;
        bIgnoreRPCs = inBunch.bIgnoreRPCs;
        CloseReason = inBunch.CloseReason;
        
        // Copy network version info
        SetEngineNetVer(inBunch.EngineNetVer());
        SetGameNetVer(inBunch.GameNetVer());

        if (copyBuffer) throw new NotImplementedException();
    }
    
    public FInBunch(UNetConnection inConnection, byte[]? src = null, int countBits = 0) : base(inConnection.PackageMap, src, countBits) {
        PacketId = 0;
        Next = null;
        Connection = inConnection;
        ChIndex = 0;
        ChType = EChannelType.CHTYPE_None;
        ChName = EName.None;
        ChSequence = 0;
        bOpen = false;
        bClose = false;
        bDormant = false;
        bReliable = false;
        bPartial = false;
        bPartialInitial = false;
        bPartialFinal = false;
        bHasPackageMapExports = false;
        bHasMustBeMappedGUIDs = false;
        bIgnoreRPCs = false;
        CloseReason = EChannelCloseReason.Destroyed;
        
        // TODO: SetByteSwapping(Connection->bNeedsByteSwapping);
        
        SetEngineNetVer(Connection.EngineNetworkProtocolVersion);
        SetGameNetVer(Connection.GameNetworkProtocolVersion);
    }

    public int PacketId { get; set; }
    public FInBunch? Next { get; set; }
    public UNetConnection Connection { get; }
    public int ChIndex { get; set; }
    public EChannelType ChType { get; set; }
    public FName ChName { get; set; }
    public int ChSequence { get; set; }
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
    public bool bIgnoreRPCs { get; set; }
    public EChannelCloseReason CloseReason { get; set; }
}