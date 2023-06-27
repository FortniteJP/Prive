namespace AFortOnlineBeacon.Net.Packets.Control;

public static class NMT_Login {
    public static void Send(UNetConnection conn, string a, string b, FUniqueNetIdRepl c, string d) {
        if (conn.Channels[0] != null && !conn.Channels[0].Closing) {
            using var bunch = new FControlChannelOutBunch(conn.Channels[0], false);
            bunch.WriteByte(5);
            bunch.WriteString(a);
            bunch.WriteString(b);
            FUniqueNetIdRepl.Write(bunch, c);
            bunch.WriteString(d);
            conn.Channels[0].SendBunch(bunch, true);
        }
    }

    public static bool Receive(FInBunch bunch, out string a, out string b, out FUniqueNetIdRepl c, out string d) {
        a = bunch.ReadString();
        b = bunch.ReadString();
        c = FUniqueNetIdRepl.Read(bunch);
        d = bunch.ReadString();
        return !bunch.IsError();
    }

    public static void Discard(FInBunch bunch) {
        bunch.ReadString();
        bunch.ReadString();
        FUniqueNetIdRepl.Read(bunch);
        bunch.ReadString();
    }
}