namespace AFortOnlineBeacon.Net.Packets.Control;

public static class NMT_NetGUIDAssign {
    public static void Send(UNetConnection conn, FNetworkGUID a, string b) {
        if (conn.Channels[0] != null && !conn.Channels[0].Closing) {
            using var bunch = new FControlChannelOutBunch(conn.Channels[0], false);
            bunch.WriteByte(18);
            throw new NotImplementedException("Unsupported type FNetworkGUID");
            bunch.WriteString(b);
            conn.Channels[0].SendBunch(bunch, true);
        }
    }

    public static bool Receive(FInBunch bunch, out FNetworkGUID a, out string b) {
        throw new NotImplementedException("Unsupported type FNetworkGUID");
        b = bunch.ReadString();
        return !bunch.IsError();
    }

    public static void Discard(FInBunch bunch) {
        throw new NotImplementedException("Unsupported type FNetworkGUID");
        bunch.ReadString();
    }
}