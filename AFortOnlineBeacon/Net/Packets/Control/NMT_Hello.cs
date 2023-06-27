namespace AFortOnlineBeacon.Net.Packets.Control;

public static class NMT_Hello {
    public static void Send(UNetConnection conn, byte a, uint b, string c) {
        if (conn.Channels[0] != null && !conn.Channels[0].Closing) {
            using var bunch = new FControlChannelOutBunch(conn.Channels[0], false);
            bunch.WriteByte(0);
            bunch.WriteByte(a);
            bunch.WriteUInt32(b);
            bunch.WriteString(c);
            conn.Channels[0].SendBunch(bunch, true);
        }
    }

    public static bool Receive(FInBunch bunch, out byte a, out uint b, out string c) {
        a = bunch.ReadByte();
        b = bunch.ReadUInt32();
        c = bunch.ReadString();
        return !bunch.IsError();
    }

    public static void Discard(FInBunch bunch) {
        bunch.ReadByte();
        bunch.ReadUInt32();
        bunch.ReadString();
    }
}