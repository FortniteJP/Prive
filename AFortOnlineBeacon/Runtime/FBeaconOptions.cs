using System.Collections;
using System.Globalization;

namespace AFortOnlineBeacon.Runtime;

/// <summary>
///     Every tunable a world runs with, OWNED BY THE WORLD - the replacement for reading
///     `Environment.GetEnvironmentVariable` from wherever a knob happened to be needed.
///
///     WHY IT HAD TO EXIST. The beacon used to be configured entirely through process environment
///     variables - around 150 of them, set by PriveDev\run-beacon.ps1 - and a process has exactly
///     one environment. That was fine while every match was its own process and is wrong the moment
///     one process hosts several worlds: `Playlist_DefaultSolo` and a late-game playlist that wants
///     different safe-zone settings would be forced to share one configuration. Per-playlist
///     configuration is a requirement, not a nicety, so the knobs had to stop being process-wide.
///
///     SAME NAMES, SAME STRINGS, SAME PARSING. <see cref="Get" /> returns exactly what
///     GetEnvironmentVariable did for the same name, so converting a call site changes WHERE the
///     value comes from and nothing about how it is interpreted - every existing
///     `is "0"`, `float.TryParse(...)` and `?? default` idiom keeps its meaning. That is deliberate:
///     rewriting 150 knobs' parsing at the same time as moving them would make any behaviour change
///     impossible to attribute. The typed helpers below are for NEW code.
///
///     A SNAPSHOT, and that is not a behaviour change either: nothing in the beacon ever writes an
///     environment variable at run time, so a value read at world creation is the value every later
///     read would have seen.
///
///     run-beacon.ps1 keeps working unchanged: a world created without explicit options takes
///     <see cref="FromEnvironment" />, which is the old behaviour exactly. A host running several
///     worlds builds each one's options itself - typically <c>FromEnvironment().With(...)</c> for the
///     per-playlist differences.
/// </summary>
public sealed class FBeaconOptions {
    private readonly IReadOnlyDictionary<string, string> _values;

    private FBeaconOptions(IReadOnlyDictionary<string, string> values) => _values = values;

    /// <summary>No knobs set at all - every reader falls back to its own default.</summary>
    public static FBeaconOptions Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    ///     The process environment, copied once. Case-insensitive on Windows because Windows
    ///     environment names are: a script that set `Deployables=0` was honoured by a read of
    ///     `DEPLOYABLES` before, and still is.
    /// </summary>
    public static FBeaconOptions FromEnvironment() {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var values = new Dictionary<string, string>(comparer);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) {
            if (entry.Key is string key && entry.Value is string value) values[key] = value;
        }

        return new FBeaconOptions(values);
    }

    /// <summary>
    ///     A copy with some knobs changed - the per-playlist layer. A null value REMOVES the knob, so
    ///     an override can also restore a reader's built-in default.
    /// </summary>
    public FBeaconOptions With(IEnumerable<KeyValuePair<string, string?>> overrides) {
        var comparer = _values is Dictionary<string, string> d ? d.Comparer : StringComparer.Ordinal;
        var values = new Dictionary<string, string>(_values, comparer);

        foreach (var (key, value) in overrides) {
            if (value == null) values.Remove(key);
            else values[key] = value;
        }

        return new FBeaconOptions(values);
    }

    /// <summary>Single-knob convenience for <see cref="With(IEnumerable{KeyValuePair{string, string?}})" />.</summary>
    public FBeaconOptions With(string name, string? value) =>
        With(new[] { new KeyValuePair<string, string?>(name, value) });

    /// <summary>
    ///     The raw value, or null when unset - exactly GetEnvironmentVariable's contract, which is
    ///     what lets every existing call site convert without touching its parsing.
    /// </summary>
    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    /// <summary>`name` is set to exactly `value`.</summary>
    public bool Is(string name, string value) => Get(name) == value;

    /// <summary>
    ///     A float knob, invariant-culture - the culture question is the one thing the old idiom got
    ///     wrong on a machine whose decimal separator is a comma.
    /// </summary>
    public float Float(string name, float fallback) =>
        float.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public int Int(string name, int fallback) =>
        int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary>
    ///     An on/off knob in the beacon's own convention: "0" is off, "1" is on, anything else
    ///     (including unset) is <paramref name="defaultOn" />.
    /// </summary>
    public bool Flag(string name, bool defaultOn) => Get(name) switch {
        "0" => false,
        "1" => true,
        _ => defaultOn
    };

    /// <summary>Every knob that is set, for a startup log line or a diff between two worlds.</summary>
    public IEnumerable<KeyValuePair<string, string>> All => _values;
}
