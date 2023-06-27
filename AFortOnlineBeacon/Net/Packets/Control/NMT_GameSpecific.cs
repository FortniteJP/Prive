namespace AFortOnlineBeacon.Net.Packets.Control;

public static class NMT_GameSpecific {
    public static void Send(UNetConnection conn, byte a, string b) {
        if (conn.Channels[0] != null && !conn.Channels[0].Closing) {
            using var bunch = new FControlChannelOutBunch(conn.Channels[0], false);
            bunch.WriteByte(20);
            bunch.WriteByte(a);
            bunch.WriteString(b);
            conn.Channels[0].SendBunch(bunch, true);
        }
    }

    public static bool Receive(FInBunch bunch, out byte a, out string b) {
        a = bunch.ReadByte();
        b = bunch.ReadString();
        return !bunch.IsError();
    }

    public static void Discard(FInBunch bunch) {
        bunch.ReadByte();
        bunch.ReadString();
    }
}