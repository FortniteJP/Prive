using System.Net;

namespace AFortOnlineBeacon.Net;

public class FInPacketTraits {
    public bool ConnectionlessPacket { get; set; }
    public bool FromRecentlyDisconnected { get; set; }
}

public readonly struct FPacketDataView {
    private readonly byte[] _Data;
    private readonly int _Count;
    private readonly int _CountBits;

    public FPacketDataView(byte[] data, int length, ECountUnits unit) {
        _Data = data;

        if (unit == ECountUnits.Bits) {
            _Count = FMath.DivideAndRoundUp(length, 8);
            _CountBits = length;
        } else {
        /* if (unit == ECountUnits.Bytes) */
            _Count = length;
            _CountBits = length * 8;
        }
    }

    public byte[] GetData() => _Data;
    
    public int NumBits() => _CountBits;

    public int NumBytes() => _Count;
}

public class FReceivedPacketView
{
    public FReceivedPacketView(FPacketDataView dataView, IPEndPoint address, FInPacketTraits traits) {
        DataView = dataView;
        Address = address;
        Traits = traits;
    }

    public FPacketDataView DataView { get; set; }
    public IPEndPoint Address { get; set; }
    public FInPacketTraits Traits { get; set; }
}