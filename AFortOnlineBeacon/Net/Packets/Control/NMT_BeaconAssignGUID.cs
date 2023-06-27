namespace AFortOnlineBeacon.Net.Packets.Control;

public static class NMT_BeaconAssignGUID {
    public static void Send(UNetConnection conn, FNetworkGUID a) {
        if (conn.Channels[0] != null && !conn.Channels[0].Closing) {
            using var bunch = new FControlChannelOutBunch(conn.Channels[0], false);
            bunch.WriteByte(27);
            throw new NotImplementedException("Unsupported type FNetworkGUID");
            conn.Channels[0].SendBunch(bunch, true);
        }
    }

    public static bool Receive(FInBunch bunch, out FNetworkGUID a) {
        throw new NotImplementedException("Unsupported type FNetworkGUID");
        return !bunch.IsError();
    }

    public static void Discard(FInBunch bunch) {
        throw new NotImplementedException("Unsupported type FNetworkGUID");
    }
}