namespace AFortOnlineBeacon.Net;

/// <summary>
///     Defers the per-packet diagnostic Console.WriteLine calls sprinkled through
///     UNetConnection.ReceivedPacket/FNetPacketNotify.ReadHeader so a packet that turns out to be a
///     pure ack/keepalive (no bunches - the vast majority of traffic once a connection is idle) never
///     prints anything at all, instead of drowning out the handful of lines that actually matter
///     (bunch/RPC/property traffic) in noise. Begin() buffers; Flush() prints everything buffered
///     since the last Begin(); Discard() (or simply never calling Flush()) throws the buffer away.
///     [ThreadStatic] so packets from different connections processed on different threads don't
///     clobber each other's buffers.
/// </summary>
internal static class NetDebugLog {
    /// <summary>
    ///     NET_VERBOSE=1 turns the per-packet firehose back on. Off by default: once a connection is
    ///     established the ack/bunch traffic scrolls a console faster than it can be read, which
    ///     hides exactly the lines worth watching (possession handshake, RPC dispatch, errors).
    ///     Errors and one-shot milestones are logged with Console.WriteLine directly and are never
    ///     suppressed by this.
    /// </summary>
    public static readonly bool VerboseEnabled = Environment.GetEnvironmentVariable("NET_VERBOSE") == "1";

    [ThreadStatic] private static List<string>? _Buffer;

    public static void Begin() => _Buffer = new List<string>();

    public static void Write(string line) {
        if (!VerboseEnabled) return;
        if (_Buffer != null) _Buffer.Add(line);
        else Console.WriteLine(line);
    }

    public static void Flush() {
        var buffer = _Buffer;
        _Buffer = null;
        if (buffer == null) return;
        foreach (var line in buffer) Console.WriteLine(line);
    }

    public static void Discard() => _Buffer = null;
}
