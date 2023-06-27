namespace AFortOnlineBeacon.Net.Packets.Control;

public static class NMT_Upgrade {
    public static void Send(UNetConnection conn, uint a) {
        if (conn.Channels[0] != null && !conn.Channels[0].Closing) {
            using var bunch = new FControlChannelOutBunch(conn.Channels[0], false);
            bunch.WriteByte(2);
            bunch.WriteUInt32(a);
            conn.Channels[0].SendBunch(bunch, true);
        }
    }

    public static bool Receive(FInBunch bunch, out uint a) {
        a = bunch.ReadUInt32();
        return !bunch.IsError();
    }

    public static void Discard(FInBunch bunch) => bunch.ReadUInt32();
}