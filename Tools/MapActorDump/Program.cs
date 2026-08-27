using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;

// Reads actor placements straight out of a cooked .umap so AFortOnlineBeacon can spawn players at a
// real PlayerStart instead of at the origin.
//
// AFortOnlineBeacon has no map data of its own and mostly does not need any: the client owns
// collision and the server simply accepts the ClientLoc reported in ServerMoveNoBase. The one thing
// it genuinely cannot invent is a spawn point. A server injected into the running game (like
// Project-Reboot-3.0) just calls GetAllActorsOfClass(FortPlayerStartWarmup); an external server has
// to read the map itself, which is what this does.
//
// Feed the result to the server as SPAWN_LOCATION="X,Y,Z".

if (args.Length < 2) {
    Console.WriteLine("Usage: MapActorDump <PaksDirectory> <AesKeyHex> [MapPath] [ClassFilter] [EGame]");
    Console.WriteLine();
    Console.WriteLine("  MapPath      default FortniteGame/Content/Athena/Maps/Athena_Terrain.umap");
    Console.WriteLine("               a directory prefix scans every .umap/.uasset under it in one mount;");
    Console.WriteLine("               \"find:<substring>\" just lists matching mounted file paths.");
    Console.WriteLine("  ClassFilter  case-insensitive substring of the actor's class name;");
    Console.WriteLine("               default \"PlayerStart\" (matches FortPlayerStartWarmup, PlayerStart, ...).");
    Console.WriteLine("               Pass \"*\" to list every actor class present with a count instead.");
    Console.WriteLine("  EGame        default GAME_UE4_23");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("""  MapActorDump "C:\Fortnite\FortniteGame\Content\Paks" 0x3ff2...""");
    return 1;
}

var paksDir = args[0];
var aesKey = args[1];
var mapPath = args.Length > 2 ? args[2] : "FortniteGame/Content/Athena/Maps/Athena_Terrain.umap";
var classFilter = args.Length > 3 ? args[3] : "PlayerStart";
var gameVersion = args.Length > 4 ? Enum.Parse<EGame>(args[4]) : EGame.GAME_UE4_23;

if (!Directory.Exists(paksDir)) {
    Console.WriteLine($"PAK directory not found: {paksDir}");
    return 1;
}

var provider = new DefaultFileProvider(paksDir, SearchOption.TopDirectoryOnly, new VersionContainer(gameVersion));
provider.Initialize();

foreach (var vfs in provider.UnloadedVfs.ToList()) {
    provider.SubmitKey(vfs.EncryptionKeyGuid, new FAesKey(aesKey));
}

Console.WriteLine($"Mounted files: {provider.Files.Count}");

// "find:<substring>" just lists mounted file paths matching a substring. Mounting is ~690k files
// and slow, so having the search here beats standing up a separate tool for it.
if (mapPath.StartsWith("find:", StringComparison.OrdinalIgnoreCase)) {
    var needle = mapPath["find:".Length..];
    var hits = provider.Files.Keys
        .Where(f => f.Contains(needle, StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToList();

    Console.WriteLine($"{hits.Count} file(s) matching '{needle}':");
    foreach (var f in hits) Console.WriteLine("  " + f);
    return 0;
}

// "grep:<needle>" scans the raw bytes of every .uasset under the ClassFilter prefix for a literal
// ASCII string. A cooked package's NAME TABLE is plain text, so this finds "which Blueprints
// reference X" - an import of /Script/FortniteUI.FortAsyncAction_SetUIState, an enum entry name,
// a function name - without needing to parse or decompile anything. Cheap enough to sweep a whole
// content subtree, and the only practical way to answer that question here: this build's .text is
// encrypted, so the native side cannot be disassembled (see Tools/BinXref).
if (mapPath.StartsWith("grep:", StringComparison.OrdinalIgnoreCase)) {
    var needle = System.Text.Encoding.ASCII.GetBytes(mapPath["grep:".Length..]);
    var prefix = classFilter == "PlayerStart" ? "FortniteGame/Content/" : classFilter;

    var candidates = provider.Files.Keys
        .Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                 && f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToList();

    Console.WriteLine($"Scanning {candidates.Count} .uasset under '{prefix}' for '{mapPath["grep:".Length..]}'...");

    var hits = 0;
    foreach (var f in candidates) {
        byte[] bytes;
        try { bytes = provider.SaveAsset(f); } catch { continue; }

        for (var i = 0; i + needle.Length <= bytes.Length; i++) {
            var ok = true;
            for (var j = 0; j < needle.Length; j++) {
                if (bytes[i + j] != needle[j]) { ok = false; break; }
            }
            if (!ok) continue;
            Console.WriteLine("  " + f);
            hits++;
            break;
        }
    }

    Console.WriteLine($"{hits} hit(s).");
    return 0;
}

// A path that is not an exact file is treated as a PREFIX and every .umap under it is scanned.
// Athena's PlayerStarts do not live in the persistent level - Athena_Terrain.umap is just HLODs plus
// 19 LevelStreamingAlwaysLoaded references - so scanning the whole Maps directory in one mount is
// far cheaper than re-mounting 690k files per sublevel guess.
var mapPaths = new List<string>();

if (provider.Files.ContainsKey(mapPath)) {
    mapPaths.Add(mapPath);
} else {
    var prefix = mapPath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)
        ? Path.GetDirectoryName(mapPath)!.Replace("\\", "/")
        : mapPath.TrimEnd('/');

    mapPaths.AddRange(provider.Files.Keys
        .Where(f => (f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)
                  || f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                 && f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

    if (mapPaths.Count == 0) {
        Console.WriteLine($"No .umap found at or under '{mapPath}'");
        return 1;
    }

    Console.WriteLine($"'{mapPath}' is not a file - scanning {mapPaths.Count} .umap under '{prefix}'");
}

// An actor's placement lives on its root SceneComponent, not on the actor itself, so resolve
// RootComponent and read RelativeLocation off it. Cooked maps sometimes inline the component as a
// separate export referenced by name, hence the lookup fallback.
FVector? LocationOf(UObject actor, List<UObject> siblings) {
    var rootComponent = actor.GetOrDefault<FPackageIndex?>("RootComponent", null);
    UObject? component = null;

    if (rootComponent is { IsNull: false }) component = rootComponent.Load();

    if (component == null) {
        var name = actor.GetOrDefault<FName?>("RootComponent", null)?.Text;
        if (name != null) component = siblings.FirstOrDefault(e => e.Name == name);
    }

    if (component != null && component.TryGetValue(out FVector relative, "RelativeLocation")) return relative;
    if (actor.TryGetValue(out FVector direct, "RelativeLocation")) return direct;

    return null;
}

var classCounts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var matchCount = 0;
var located = 0;

foreach (var path in mapPaths) {
    List<UObject> exports;
    try {
        exports = provider.LoadPackageObjects(path).ToList();
    } catch (Exception ex) {
        Console.WriteLine($"  !! {path}: {ex.GetType().Name}: {ex.Message}");
        continue;
    }

    // "props:<substring>" dumps every property of matching exports instead of hunting for a
    // location. Needed for things like Athena_GameMode_C's HUDClass, which is a CDO default rather
    // than a placed actor - and which nothing in a map file can tell you.
    if (classFilter.StartsWith("props:", StringComparison.OrdinalIgnoreCase)) {
        var want = classFilter["props:".Length..];

        foreach (var export in exports) {
            // A UDataTable keeps its contents in RowMap, not in Properties - so the ordinary
            // property walk below prints an empty object for one. Weapon stats (ClipSize,
            // damage, reload time) all live in these, reached from a WID's WeaponStatHandle.
            if (export is UDataTable dataTable) {
                foreach (var (rowName, row) in dataTable.RowMap) {
                    if (want.Length > 0 && !rowName.Text.Contains(want, StringComparison.OrdinalIgnoreCase)) continue;

                    Console.WriteLine();
                    Console.WriteLine($"--- {path}");
                    Console.WriteLine($"DataTable row  {rowName.Text}");
                    foreach (var rowProp in row.Properties) {
                        Console.WriteLine($"    {rowProp.Name,-46} {rowProp.Tag?.GenericValue}");
                    }
                    located++;
                }

                continue;
            }

            if (want.Length > 0
                && !export.ExportType.Contains(want, StringComparison.OrdinalIgnoreCase)
                && !export.Name.Contains(want, StringComparison.OrdinalIgnoreCase)) continue;

            Console.WriteLine();
            Console.WriteLine($"--- {path}");
            Console.WriteLine($"{export.ExportType}  {export.Name}");
            foreach (var prop in export.Properties) {
                // A struct property used to print as its C# type name and nothing else, which hid
                // exactly the things worth reading - a FDataTableRowHandle's table and row name, a
                // FScalableFloat's value. One level of recursion turns those into something usable.
                // CUE4Parse wraps a struct value in FScriptStruct, whose ToString() prints the
                // INNER type name - so the old output looked like it was already showing the struct
                // when it was showing nothing at all.
                var nested = (prop.Tag?.GenericValue as FScriptStruct)?.StructType as FStructFallback
                             ?? prop.Tag?.GenericValue as FStructFallback;

                if (nested != null) {
                    Console.WriteLine($"    {prop.Name,-46} {{");
                    foreach (var inner in nested.Properties) {
                        Console.WriteLine($"        {inner.Name,-42} {inner.Tag?.GenericValue}");
                    }
                    Console.WriteLine("    }");
                    continue;
                }

                Console.WriteLine($"    {prop.Name,-46} {prop.Tag?.GenericValue}");
            }
            located++;
        }
        continue;
    }

    if (classFilter == "*") {
        foreach (var export in exports) {
            classCounts.TryGetValue(export.ExportType, out var n);
            classCounts[export.ExportType] = n + 1;
        }
        continue;
    }

    var matches = exports
        .Where(e => e.ExportType.Contains(classFilter, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (matches.Count == 0) continue;

    matchCount += matches.Count;
    Console.WriteLine();
    Console.WriteLine($"--- {path}  ({matches.Count} match(es)) ---");

    foreach (var actor in matches) {
        // Streaming-level entries carry the world placement of a sublevel, not a RelativeLocation.
        // This matters for reading spawn points out of a POI sublevel: the actor coordinates in
        // there are LOCAL, and the real world position is that plus the parent's LevelTransform.
        if (actor.ExportType.Contains("LevelStreaming", StringComparison.Ordinal)) {
            // A cooked asset only serialises non-default properties, so an ABSENT LevelTransform
            // means identity - i.e. that sublevel's actor coordinates are already world coordinates.
            // UE 4.23's ULevelStreaming names the target level with WorldAsset (a TSoftObjectPtr);
            // PackageName is the older, deprecated field and is not what cooked maps carry.
            var packageName = actor.TryGetValue(out FSoftObjectPath worldAsset, "WorldAsset")
                ? worldAsset.AssetPathName.Text
                : actor.GetOrDefault<FName>("PackageNameToLoad").Text;
            var offset = actor.TryGetValue(out FStructFallback levelTransform, "LevelTransform")
                      && levelTransform.TryGetValue(out FVector t, "Translation")
                ? $"({t.X:F0},{t.Y:F0},{t.Z:F0})"
                : "identity";

            Console.WriteLine($"{actor.ExportType,-34} {packageName,-60} LevelTransform={offset}");
            located++;
            continue;
        }

        var location = LocationOf(actor, exports);
        if (location is { } l) {
            located++;
            Console.WriteLine($"{actor.ExportType,-34} {actor.Name,-40} SPAWN_LOCATION=\"{l.X:F0},{l.Y:F0},{l.Z:F0}\"");
        } else {
            Console.WriteLine($"{actor.ExportType,-34} {actor.Name,-40} (no RelativeLocation - at the origin)");
        }
    }
}

if (classFilter == "*") {
    foreach (var kv in classCounts.OrderByDescending(kv => kv.Value)) {
        Console.WriteLine($"{kv.Value,6}  {kv.Key}");
    }
    return 0;
}

Console.WriteLine();
Console.WriteLine($"{located} of {matchCount} matching actor(s) had an explicit location.");
return 0;
