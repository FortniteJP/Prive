namespace AFortOnlineBeacon.Net.Packets.Control;

public static class NMT_Welcome {
    public static void Send(UNetConnection conn, string a, string b, string c) {
        if (conn.Channels[0] != null && !conn.Channels[0].Closing) {
            using var bunch = new FControlChannelOutBunch(conn.Channels[0], false);
            bunch.WriteByte(1);
            bunch.WriteString(a);
            bunch.WriteString(b);
            bunch.WriteString(c);
            conn.Channels[0].SendBunch(bunch, true);
        }
    }

    public static bool Receive(FInBunch bunch, out string a, out string b, out string c) {
        a = bunch.ReadString();
        b = bunch.ReadString();
        c = bunch.ReadString();
        return !bunch.IsError();
    }

    public static void Discard(FInBunch bunch) {
        bunch.ReadString();
        bunch.ReadString();
        bunch.ReadString();
    }
}