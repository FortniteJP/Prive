namespace AFortOnlineBeacon.Net.Packets.Control;

public static class NMT_Skip {
    public static void Send(UNetConnection conn, FGuid a) {
        if (conn.Channels[0] != null && !conn.Channels[0].Closing) {
            using var bunch = new FControlChannelOutBunch(conn.Channels[0], false);
            bunch.WriteByte(12);
            throw new NotImplementedException("Unsupported type FGuid");
            conn.Channels[0].SendBunch(bunch, true);
        }
    }

    public static bool Receive(FInBunch bunch, out FGuid a) {
        throw new NotImplementedException("Unsupported type FGuid");
        return !bunch.IsError();
    }

    public static void Discard(FInBunch bunch) {
        throw new NotImplementedException("Unsupported type FGuid");
    }
}