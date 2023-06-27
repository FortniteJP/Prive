namespace AFortOnlineBeacon.Net;

public readonly struct FPacketIdRange {
    public FPacketIdRange() {
        First = UnrealConstants.IndexNone;
        Last = UnrealConstants.IndexNone;
    }

    public FPacketIdRange(int packetId) {
        First = packetId;
        Last = packetId;
    }

    public FPacketIdRange(int first, int last) {
        First = first;
        Last = last;
    }
    
    public int First { get; }
    public int Last { get; }

    public bool InRange(int packetId) => First <= packetId && packetId <= Last;
}