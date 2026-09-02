using AFortOnlineBeacon.Core.Math;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     The spawn island's player starts - the runtime half of FortWarmupStarts.Generated.cs.
///
///     A real server calls GetAllActorsOfClass(FortPlayerStartWarmup) and hands each joining player
///     one of them; this server has no map loaded, so the 121 positions are generated offline from
///     the paks instead (see the generated file for why they are WORLD coordinates and what went
///     wrong when they were not).
///
///     Handed out ROUND-ROBIN rather than always giving out the first one, for the same reason the
///     real game spreads players around the island: two players spawning on the same spot push each
///     other apart, and with client-authoritative movement this server has no say in how that
///     resolves.
/// </summary>
internal static partial class FortWarmupStarts {
    private static int _next;

    /// <summary>How many starts there are - one per X,Y,Z triple.</summary>
    public static int Count => Starts.Length / 3;

    /// <summary>
    ///     The next start to use, cycling. Deliberately not random: a fixed order makes a test run
    ///     reproducible, and there is nothing to be gained from surprising the person debugging it.
    /// </summary>
    public static FVector Next() {
        if (Count == 0) return new FVector();

        var i = _next++ % Count;
        return new FVector {
            X = Starts[i * 3],
            Y = Starts[i * 3 + 1],
            Z = Starts[i * 3 + 2]
        };
    }
}
