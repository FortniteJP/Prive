using System.Diagnostics;

namespace AFortOnlineBeacon.Runtime;

/// <summary>
///     Names the phase of the world tick that took too long.
///
///     WHY: a client times out when the server sends it nothing, and the server sends nothing while
///     its tick is blocked - keepalives ride UNetConnection.Tick, which is inside the very tick that
///     stalled. So a frozen tick looks, from both logs, like the server simply going quiet: the
///     server's console has an unexplained gap and the client's has a timeout. There is nothing in
///     either to say WHAT was slow, and "it froze once" is not something that can be chased by
///     reading code, because every phase is a candidate.
///
///     This makes the next occurrence self-explaining. It costs one Stopwatch read per phase - tens
///     of nanoseconds - and prints nothing at all unless a tick crosses the threshold, so it is not
///     a debug switch to be turned on after the fact. That matters: the stall that prompted it was
///     a single event nobody could reproduce on demand.
///
///     TICK_WATCHDOG_MS sets the threshold (default 1000). TICK_WATCHDOG=0 turns it off.
/// </summary>
public sealed class FTickWatchdog {
    private static readonly bool Enabled = FBeaconProcess.Options.Get("TICK_WATCHDOG") is not "0";

    private static readonly float ThresholdMs =
        float.TryParse(FBeaconProcess.Options.Get("TICK_WATCHDOG_MS"), out var ms) && ms > 0f
            ? ms
            : 1000f;

    private readonly Stopwatch _stopwatch = new();

    /// <summary>Phase name and the millisecond mark it ENDED at, in order. Reused, never reallocated.</summary>
    private readonly List<(string Name, double EndedAtMs)> _phases = new();

    /// <summary>Ticks seen and how many were slow, so a recurring stall can be told from a one-off.</summary>
    private long _ticks;
    private long _slowTicks;

    public void Begin() {
        if (!Enabled) return;

        _phases.Clear();
        _stopwatch.Restart();
    }

    /// <summary>Records that <paramref name="name" /> has just finished.</summary>
    public void Mark(string name) {
        if (!Enabled) return;

        _phases.Add((name, _stopwatch.Elapsed.TotalMilliseconds));
    }

    /// <summary>
    ///     Prints the breakdown if this tick was slow. Silent otherwise, which is nearly always.
    ///
    ///     The whole breakdown is printed rather than just the worst phase: a stall is as likely to
    ///     be several phases each a bit slow (a growing list walked three times) as one phase
    ///     blocking, and those two need different fixes.
    /// </summary>
    public void End() {
        if (!Enabled) return;

        _ticks++;

        var totalMs = _stopwatch.Elapsed.TotalMilliseconds;
        if (totalMs < ThresholdMs) return;

        _slowTicks++;

        var breakdown = new List<string>();
        var previous = 0.0;

        foreach (var (name, endedAt) in _phases) {
            var took = endedAt - previous;
            previous = endedAt;

            // Only the phases that actually cost something - a list of thirty "0 ms" entries buries
            // the one that matters.
            if (took >= 1.0) breakdown.Add($"{name} {took:F0}ms");
        }

        Console.WriteLine($"FTickWatchdog: the world tick took {totalMs:F0}ms " +
                          $"(threshold {ThresholdMs:F0}ms; {_slowTicks} slow of {_ticks} ticks). " +
                          "THE CLIENT RECEIVES NOTHING WHILE THIS RUNS - keepalives are sent from " +
                          "inside it. " +
                          (breakdown.Count > 0
                              ? $"Phases: {string.Join(", ", breakdown)}."
                              : "No single phase stands out, so the time went somewhere unmeasured - " +
                                "add a Mark() around the suspect."));
    }
}
