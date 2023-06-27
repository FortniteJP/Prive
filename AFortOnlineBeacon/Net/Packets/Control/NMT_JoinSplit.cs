namespace AFortOnlineBeacon.Net.Packets.Control;

public static class NMT_JoinSplit {
    public static void Send(UNetConnection conn, string a, FUniqueNetIdRepl b) {
        if (conn.Channels[0] != null && !conn.Channels[0].Closing) {
            using var bunch = new FControlChannelOutBunch(conn.Channels[0], false);
            bunch.WriteByte(10);
            bunch.WriteString(a);
            FUniqueNetIdRepl.Write(bunch, b);
            conn.Channels[0].SendBunch(bunch, true);
        }
    }

    public static bool Receive(FInBunch bunch, out string a, out FUniqueNetIdRepl b) {
        a = bunch.ReadString();
        b = FUniqueNetIdRepl.Read(bunch);
        return !bunch.IsError();
    }

    public static void Discard(FInBunch bunch) {
        bunch.ReadString();
        FUniqueNetIdRepl.Read(bunch);
    }
}