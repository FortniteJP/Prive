namespace AFortOnlineBeacon.Net.Packets.Control;

public static class NMT_PCSwap {
    public static void Send(UNetConnection conn, int a) {
        if (conn.Channels[0] != null && !conn.Channels[0].Closing) {
            using var bunch = new FControlChannelOutBunch(conn.Channels[0], false);
            bunch.WriteByte(15);
            bunch.WriteInt32(a);
            conn.Channels[0].SendBunch(bunch, true);
        }
    }

    public static bool Receive(FInBunch bunch, out int a) {
        a = bunch.ReadInt32();
        return !bunch.IsError();
    }

    public static void Discard(FInBunch bunch) => bunch.ReadInt32();
}