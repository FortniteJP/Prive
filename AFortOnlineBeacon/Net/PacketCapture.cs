using System.Net;

namespace AFortOnlineBeacon.Net;

public enum EPacketDirection {
    Incoming,
    Outgoing
}

/// <summary>
///     Fire-and-forget hook so callers (e.g. AFortOnlineBeacon.Test) can capture raw UDP traffic
///     to a file without the network layer itself depending on file I/O.
/// </summary>
public static class PacketCapture {
    public static event Action<EPacketDirection, IPEndPoint, byte[]>? OnPacket;

    public static void Raise(EPacketDirection direction, IPEndPoint address, byte[] data) => OnPacket?.Invoke(direction, address, data);
}
