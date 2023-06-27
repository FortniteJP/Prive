namespace AFortOnlineBeacon.Net.Channels;

[Flags]
public enum EChannelCreateFlags {
    None = (1 << 0),
    OpenedLocally = (1 << 1)
}