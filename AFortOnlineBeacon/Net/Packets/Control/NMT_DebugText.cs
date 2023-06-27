namespace AFortOnlineBeacon.Net.Packets.Control;

public static class NMT_DebugText {
    public static void Send(UNetConnection conn, string a) {
        if (conn.Channels[0] != null && !conn.Channels[0].Closing) {
            using var bunch = new FControlChannelOutBunch(conn.Channels[0], false);
            bunch.WriteByte(17);
            bunch.WriteString(a);
            conn.Channels[0].SendBunch(bunch, true);
        }
    }

    public static bool Receive(FInBunch bunch, out string a) {
        a = bunch.ReadString();
        return !bunch.IsError();
    }

    public static void Discard(FInBunch bunch) => bunch.ReadString();
}