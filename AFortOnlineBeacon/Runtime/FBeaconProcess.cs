namespace AFortOnlineBeacon.Runtime;

/// <summary>
///     The configuration of the few things that are SHARED by every world in the process - and that
///     are shared because splitting them would be wrong, not because nobody got round to it.
///
///     WHAT BELONGS HERE, AND WHY IT IS NOT PER WORLD:
///
///       * baked map data - collision hulls, the terrain height field, map props, building class
///         handles. Immutable, tens of megabytes, and identical for every world (10.40 has one Battle
///         Royale map). One copy is correct; one per world would only cost memory.
///       * learned map data - TerrainGroundTruth writes what players stand on to a FILE. Two worlds
///         writing one file each with their own copy would corrupt it.
///       * external connections - the Mongo client behind the locker lookup is designed to be one per
///         process.
///       * wire-format and diagnostic switches - every world talks to the same 10.40 client, so the
///         wire is the same for all of them, and a packet capture or debug log is one sink.
///
///     Everything ELSE - anything a playlist might want to differ on, and all match state - is on
///     the world: <see cref="UWorld.Options" /> and <see cref="FWorldSubsystem" />.
///
///     SET ONCE, BEFORE THE FIRST WORLD. The shared resources load lazily and keep what they loaded,
///     so configuring the process after one of them has read its settings would leave it on the old
///     ones - <see cref="Configure" /> refuses that rather than letting it half-apply. A host that
///     never calls it gets the process environment, which is exactly what run-beacon.ps1 relies on.
/// </summary>
public static class FBeaconProcess {
    private static readonly object Gate = new();
    private static FBeaconOptions? _options;
    private static bool _read;

    /// <summary>The process-level settings. The first read fixes them for the life of the process.</summary>
    public static FBeaconOptions Options {
        get {
            lock (Gate) {
                _read = true;
                return _options ??= FBeaconOptions.FromEnvironment();
            }
        }
    }

    /// <summary>
    ///     Sets the process-level configuration. Must run before anything has read it - i.e. before the
    ///     first world is created - and throws otherwise, because a shared resource that already loaded
    ///     would silently keep its old settings.
    /// </summary>
    public static void Configure(FBeaconOptions options) {
        lock (Gate) {
            if (_read)
                throw new InvalidOperationException(
                    "FBeaconProcess.Configure was called after the process configuration had already been read. " +
                    "Call it once at startup, before creating any UWorld.");
            _options = options;
        }
    }
}
