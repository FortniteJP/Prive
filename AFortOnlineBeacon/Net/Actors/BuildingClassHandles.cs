namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Maps FCreateBuildingActorData.BuildingClassHandle onto the building ACTOR class it names.
///
///     This is the only per-placement signal of WHICH piece the player selected, which makes it the
///     only way to get piece switching right. Proven from a live session's logs: cycling
///     Wall/Floor/Stair/Roof inside build mode sends no RPC of its own at all - not
///     ServerSetPlayerBuildableClass (0 calls, even with the info actor's Owner resolving correctly
///     client-side) and not ServerExecuteInventoryItem (that fires only when toggling between the
///     pickaxe and build mode). One session produced 9 distinct handles while only TWO building items
///     were ever equipped, so CurrentWeapon.WeaponData is structurally incapable of telling the
///     pieces apart - it always names whichever piece build mode was entered with.
///
///     The handle is an index into AFortGameStateAthena::AllPlayerBuildableClasses (array at 0xd50,
///     count at 0xd58 - exactly the bound ServerCreateBuildingActor_Validate checks, read off the
///     client dump at vtable +0x1118). That array is NOT replicated: both sides build it locally, so
///     this server cannot resolve an index into it without reproducing the client's list. Until
///     someone does, the mapping is measured rather than derived - which is fine, because it is
///     stable for a given build and playlist.
///
///     CALIBRATING: run the server, place each piece and material once, and read the handle off the
///     "BuildingClassHandle N is not in the handle table" line each placement logs. Put the answers
///     in BuildingClassHandles.txt next to the server (or set BUILDING_CLASS_HANDLES to a path).
///     The file is re-read on every placement, so calibration needs no rebuild or restart:
///
///         # handle = piece            (alias: Wood/Stone/Metal + Wall/Floor/Stair/Roof, tier L1-L3)
///         62  = Wood:Stair
///         129 = Stone:Wall
///         85  = Metal:Roof:L2
///         # ...or spell the class path out in full when an alias does not cover it
///         110 = /Game/Building/ActorBlueprints/Player/Wood/L1/PBWA_W1_Floor.PBWA_W1_Floor_C
/// </summary>
public static class BuildingClassHandles {
    /// <summary>
    ///     Where the table lives. BUILDING_CLASS_HANDLES overrides it outright; otherwise the file is
    ///     looked for next to the SERVER BINARY first and only then in the working directory.
    ///
    ///     That order is not arbitrary: the server runs with a working directory that is not its own
    ///     output folder (AFortOnlineBeacon.Test writes its Captures to AppContext.BaseDirectory for
    ///     the same reason), so resolving a bare filename against the CWD - which this did at first -
    ///     silently found nothing and reported every handle as uncalibrated no matter what the file
    ///     actually said.
    /// </summary>
    private static readonly string[] CandidatePaths =
        Environment.GetEnvironmentVariable("BUILDING_CLASS_HANDLES") is { Length: > 0 } configured
            ? new[] { configured }
            : new[] {
                Path.Combine(AppContext.BaseDirectory, "BuildingClassHandles.txt"),
                "BuildingClassHandles.txt"
            };

    private static string TablePath => CandidatePaths.FirstOrDefault(File.Exists) ?? CandidatePaths[0];

    private static Dictionary<uint, string> _table = new();
    private static DateTime _loadedStamp = DateTime.MinValue;
    private static long _loadedLength = -1;
    private static bool _reportedMissing;

    /// <summary>
    ///     Friendly names for the four base pieces. Any OTHER word is taken as the asset suffix
    ///     verbatim, so every edit variant is reachable without extending this table -
    ///     "Wood:DoorC", "Stone:RoofO", "Metal:HalfWallS", "Wood:StairSpiral" all resolve.
    ///
    ///     The paths are completely systematic:
    ///     .../Player/{Material}/L{Tier}/PBWA_{Code}{Tier}_{Suffix}.PBWA_{Code}{Tier}_{Suffix}_C.
    ///     Confirmed against ~120 distinct real paths extracted from a Project-Reboot-3.0 capture -
    ///     all three materials (Wood/Stone/Metal -> W/S/M) and the four base suffixes
    ///     (Solid/Floor/StairW/RoofC) among them, including the Wood roof this table used to only
    ///     infer. Only L1 appears in that capture: BR uses tier 1 for all three materials, and L2/L3
    ///     are a Save The World thing, so the default tier is the only one likely to resolve.
    /// </summary>
    private static readonly Dictionary<string, string> PieceSuffix = new(StringComparer.OrdinalIgnoreCase) {
        ["Wall"] = "Solid",
        ["Floor"] = "Floor",
        ["Stair"] = "StairW",
        ["Roof"] = "RoofC"
    };

    private static readonly Dictionary<string, string> MaterialCode = new(StringComparer.OrdinalIgnoreCase) {
        ["Wood"] = "W",
        ["Stone"] = "S",
        ["Metal"] = "M"
    };

    /// <summary>
    ///     The class this handle names, or null when the table has no entry for it yet. Resolved as
    ///     ABuildingActor (not a plain AActor) so a spawned instance carries HP/material/piece-kind -
    ///     see ABuildingActor's doc comment, and FortWeaponActorClasses.BuildingActorClassFor's for
    ///     why every building-path lookup in this project must agree on that same C# type.
    /// </summary>
    public static UClass? ClassFor(uint handle) {
        var path = PathFor(handle);
        return path == null ? null : GUClassArray.StaticClassForPath<ABuildingActor>(path);
    }

    public static string? PathFor(uint handle) {
        ReloadIfChanged();
        return _table.GetValueOrDefault(handle);
    }

    /// <summary>
    ///     Re-reads the table whenever the file's timestamp or length changes, so a calibration pass
    ///     is "place a piece, read the handle, add a line, place again" with the server left running.
    /// </summary>
    private static void ReloadIfChanged() {
        try {
            var path = TablePath;

            if (!File.Exists(path)) {
                if (_loadedLength != -1) { _table = new Dictionary<uint, string>(); _loadedLength = -1; }

                if (!_reportedMissing) {
                    _reportedMissing = true;
                    Console.WriteLine($"BuildingClassHandles: no table found - looked in " +
                                      $"[{string.Join(", ", CandidatePaths)}]. Every handle reads as " +
                                      $"uncalibrated until one of those exists.");
                }

                return;
            }

            _reportedMissing = false;
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc == _loadedStamp && info.Length == _loadedLength) return;

            _loadedStamp = info.LastWriteTimeUtc;
            _loadedLength = info.Length;
            _table = Parse(File.ReadAllLines(path));

            Console.WriteLine($"BuildingClassHandles: loaded {_table.Count} handle(s) from {path}");
        } catch (IOException) {
            // Being written to right now - keep whatever was already parsed and try again next time.
        }
    }

    private static Dictionary<uint, string> Parse(IEnumerable<string> lines) {
        var table = new Dictionary<uint, string>();

        foreach (var raw in lines) {
            var line = raw;
            var comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0) continue;

            var split = line.IndexOf('=');
            if (split < 0) {
                Console.WriteLine($"BuildingClassHandles: ignoring malformed line '{raw.Trim()}' (expected 'handle = piece')");
                continue;
            }

            if (!uint.TryParse(line[..split].Trim(), out var handle)) {
                Console.WriteLine($"BuildingClassHandles: ignoring line '{raw.Trim()}' - '{line[..split].Trim()}' is not a handle number");
                continue;
            }

            var path = ResolvePath(line[(split + 1)..].Trim());
            if (path == null) {
                Console.WriteLine($"BuildingClassHandles: ignoring line '{raw.Trim()}' - cannot resolve the piece it names");
                continue;
            }

            table[handle] = path;
        }

        return table;
    }

    /// <summary>A full class path passes through unchanged; anything else is read as a Material:Piece[:Tier] alias.</summary>
    private static string? ResolvePath(string value) {
        if (value.StartsWith('/')) return value;

        string material = "Wood", piece = value, tier = "L1";

        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (parts.Length) {
            case 1: piece = parts[0]; break;
            case 2: material = parts[0]; piece = parts[1]; break;
            case 3: material = parts[0]; piece = parts[1]; tier = parts[2]; break;
            default: return null;
        }

        if (!MaterialCode.TryGetValue(material, out var code)) return null;

        // A friendly name if we know one, otherwise the caller spelled the asset suffix out - which
        // is how the ~40 edit variants (DoorC, WindowC, RoofO, HalfWallS, StairSpiral, ...) are
        // reached. Rejecting anything with a path separator or a dot keeps a typo'd full path from
        // being silently mangled into a nonsense asset name instead of failing the '/' check above.
        var suffix = PieceSuffix.GetValueOrDefault(piece, piece);
        if (suffix.Length == 0 || suffix.Contains('/') || suffix.Contains('.')) return null;

        // "L2" and a bare "2" both mean tier 2.
        var tierDigit = tier.TrimStart('L', 'l');
        if (tierDigit.Length != 1 || !char.IsAsciiDigit(tierDigit[0])) return null;

        var asset = $"PBWA_{code}{tierDigit}_{suffix}";
        return $"/Game/Building/ActorBlueprints/Player/{material}/L{tierDigit}/{asset}.{asset}_C";
    }
}
