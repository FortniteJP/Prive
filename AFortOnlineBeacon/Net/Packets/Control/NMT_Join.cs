namespace AFortOnlineBeacon.Net.Packets.Control;

public static class NMT_Join {
    public static void Send(UNetConnection conn) {
        if (conn.Channels[0] != null && !conn.Channels[0].Closing) {
            using var bunch = new FControlChannelOutBunch(conn.Channels[0], false);
            bunch.WriteByte(9);
            conn.Channels[0].SendBunch(bunch, true);
        }
    }

    public static bool Receive(FInBunch bunch) => !bunch.IsError();

    public static void Discard(FInBunch bunch) {}
}