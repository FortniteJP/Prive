using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

namespace PakReader;

/// <summary>
///     Reads the shipped 10.40 game data out of the pak files, so this project can stop guessing the
///     magnitudes it has been filling in with placeholders (building HP, weapon damage, storm phase
///     timings, loot tables).
///
///     A GENERATOR, NOT A SERVER DEPENDENCY. Deliberately a Tools/ command like Tools/HarvestTable and
///     Tools/RepHandles: it runs offline, its output gets committed, and AFortOnlineBeacon itself never
///     grows a 60 GB dependency or a multi-second startup. What the server needs from the paks is
///     per-class CONSTANTS, and those can be enumerated ahead of time.
///
///     Why this is even possible now, and cheap: 10.40 is UE 4.23, which still cooks TAGGED property
///     serialization - property names are in the assets - so no .usmap mappings file is needed. That
///     is the part that makes modern Fortnite versions expensive to parse, and it simply does not
///     apply here. Oodle decompression runs at GB/s; the only real cost is mounting and indexing the
///     56 paks once.
///
///     Configuration comes from the environment so no key ever lands in the repo:
///         FORTNITE_PAKS     directory holding pakchunk*.pak  (default: the local 10.40 install)
///         FORTNITE_AES_KEY  0x-prefixed 32-byte key
///
///     Commands:
///         pakreader find &lt;substring&gt; [max]     search the mounted file index by path
///         pakreader exports &lt;objectPath&gt;       full JSON of every export in a package
///         pakreader props &lt;objectPath&gt; [names] one line per matching property (comma-separated filter)
/// </summary>
public static class Program {
    private const string DefaultPaks = @"C:\Users\user\Documents\10.40\FortniteGame\Content\Paks";

    public static int Main(string[] args) {
        if (args.Length == 0) {
            Console.Error.WriteLine("usage: pakreader <find|exports|props> ...");
            return 2;
        }

        var paks = Environment.GetEnvironmentVariable("FORTNITE_PAKS") is { Length: > 0 } p ? p : DefaultPaks;
        var key = Environment.GetEnvironmentVariable("FORTNITE_AES_KEY");
        if (string.IsNullOrWhiteSpace(key)) {
            Console.Error.WriteLine("FORTNITE_AES_KEY is not set - nothing can be decrypted without it.");
            return 2;
        }

        // GAME_UE4_23 is what 10.40 is. Getting this wrong does not fail loudly - it fails as
        // structs that deserialise to nonsense - so it is worth being explicit about.
        var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE4_23));
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(key));
        provider.PostMount();

        Console.Error.WriteLine($"pakreader: mounted {provider.Files.Count} files from {paks}");

        try {
            switch (args[0]) {
                case "find": return Find(provider, args);
                case "exports": return Exports(provider, args);
                case "props": return Props(provider, args);
                case "rows": return Rows(provider, args);
                case "actors": return Actors(provider, args);
                case "tileinfo": return TileInfo(provider, args);
                case "foundations": return Foundations(provider, args);
                case "spawnpoints": return SpawnPoints(provider, args);
                case "placements": return SpawnPoints(provider, args, verbose: true);
                case "raw": return Raw(provider, args);
                case "buildinghealth": return BuildingHealth(provider, args);
                case "walls": return Walls(provider, args);
                case "connectivity": return Connectivity(provider, args);
                default:
                    Console.Error.WriteLine($"unknown command '{args[0]}'");
                    return 2;
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"pakreader: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Search the mounted index. The single most useful command: asset paths are rarely what you guess.</summary>
    private static int Find(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader find <substring> [max]"); return 2; }
        var needle = args[1];
        var max = args.Length > 2 && int.TryParse(args[2], out var m) ? m : 100;

        var shown = 0;
        foreach (var path in provider.Files.Keys) {
            if (!path.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine(path);
            if (++shown >= max) break;
        }

        Console.Error.WriteLine($"pakreader: {shown} match(es) shown");
        return 0;
    }

    private static int Exports(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader exports <objectPath>"); return 2; }
        var exports = provider.LoadPackage(args[1]).GetExports();
        Console.WriteLine(JsonConvert.SerializeObject(exports, Formatting.Indented));
        return 0;
    }

    /// <summary>
    ///     `Class<TAB>MaxHealth<TAB>BuildTime` for every player-buildable piece.
    ///
    ///     Resolves the chain the game uses at runtime, in one mount. A building CDO does not carry its
    ///     own health: it carries `AttributeInitKeys` - a CATEGORY and a SUBCATEGORY, e.g.
    ///     `AthenaPlayerBuildingWood1` + `Size9` - which index a GAS attribute-defaults curve table.
    ///     WHICH table is named only in `FortniteGame/Config/DefaultGame.ini` under
    ///     `[/Script/GameplayAbilities.AbilitySystemGlobals]`, which is why no amount of asset browsing
    ///     finds it; for Athena it is
    ///     `Athena/Balance/DataTables/AthenaAttributesBuildingSection`, whose rows are
    ///     `&lt;Category&gt;.&lt;Size&gt;.FortHealthSet.MaxHealth` with the player's building SKILL as the
    ///     curve's X (flat across levels in Battle Royale, so index 0 is the answer).
    ///
    ///     Two entries exist in AttributeInitKeys - the Save-the-World one first, the Athena one
    ///     second. Battle Royale wants the SECOND.
    /// </summary>
    /// <summary>
    ///     Every building class -> its edit-mode pattern, and every pattern's ConnectivityCube.
    ///
    ///     This is the data the real UBuildingStructuralSupportSystem::AreNeighborsConnected decides
    ///     with (see the afortonlinebeacon-status memory, Round 71): the disassembly bottoms out in
    ///     EditModePatternData, a UBuildingEditModeMetadata UDataAsset, and connectivity is authored
    ///     per pattern rather than computed from the piece's geometry. The SDK shows FConnectivityCube
    ///     as an opaque 0xC0 blob, but 4.23 cooks TAGGED properties so the real member names are here.
    ///
    ///     Six faces - Front, Left, Back, Right, Upper, Lower - each a 25-entry bool array (5x5).
    /// </summary>
    private static int Connectivity(DefaultFileProvider provider, string[] args) {
        var faces = new[] { "Front", "Left", "Back", "Right", "Upper", "Lower" };

        // Pattern path -> the classes that use it. Printed so an asymmetric class can be picked out
        // when working out the mask's row/column convention.
        var users = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in provider.Files.Keys) {
            if (!path.Contains("/Building/ActorBlueprints/Player/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            if (!provider.TryLoadPackage(path, out var package)) continue;

            foreach (var export in package.GetExports()) {
                if (!export.ExportType.StartsWith("PBWA_", StringComparison.OrdinalIgnoreCase)) continue;

                var json = Newtonsoft.Json.Linq.JObject.Parse(JsonConvert.SerializeObject(export));
                var pattern = (string?) json["Properties"]?["EditModePatternData"]?["ObjectPath"];
                if (pattern == null) continue;

                var name = pattern[(pattern.LastIndexOf('/') + 1)..].Split('.')[0];
                if (!users.TryGetValue(name, out var list)) users[name] = list = new List<string>();
                list.Add(export.Name);
            }
        }

        foreach (var path in provider.Files.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) {
            if (!path.Contains("/Building/EditModePatterns/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            if (!provider.TryLoadPackage(path, out var package)) continue;

            foreach (var export in package.GetExports()) {
                var json = Newtonsoft.Json.Linq.JObject.Parse(JsonConvert.SerializeObject(export));
                if (json["Properties"]?["ConnectivityCubeData"] is not Newtonsoft.Json.Linq.JObject cube) continue;

                var name = export.Name;
                users.TryGetValue(name, out var list);
                Console.WriteLine($"# {name}  [{export.ExportType}]  used by: " +
                                  (list is { Count: > 0 } ? string.Join(",", list.OrderBy(x => x)) : "(none)"));

                foreach (var face in faces) {
                    var bits = cube[face] as Newtonsoft.Json.Linq.JArray;
                    var flat = bits == null
                        ? new string('?', 25)
                        : string.Concat(bits.Select(b => (bool) b ? '1' : '0'));
                    Console.WriteLine($"{name}	{face}	{flat}");
                }
            }
        }

        return 0;
    }

    private static int BuildingHealth(DefaultFileProvider provider, string[] args) {
        const string table = "FortniteGame/Content/Athena/Balance/DataTables/AthenaAttributesBuildingSection";

        var curves = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var export in provider.LoadPackage(table).GetExports()) {
            var json = Newtonsoft.Json.Linq.JObject.Parse(JsonConvert.SerializeObject(export));
            if (json["Rows"] is not Newtonsoft.Json.Linq.JObject rows) continue;
            foreach (var row in rows.Properties()) {
                var first = row.Value["Keys"]?.FirstOrDefault()?["Value"];
                if (first != null) curves[row.Name] = (float) first;
            }
        }

        Console.Error.WriteLine($"pakreader: {curves.Count} attribute rows");

        foreach (var path in provider.Files.Keys) {
            if (!path.Contains("/Building/ActorBlueprints/Player/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;

            if (!provider.TryLoadPackage(path, out var package)) continue;

            foreach (var export in package.GetExports()) {
                if (!export.ExportType.StartsWith("PBWA_", StringComparison.OrdinalIgnoreCase)) continue;

                var json = Newtonsoft.Json.Linq.JObject.Parse(JsonConvert.SerializeObject(export));
                var keys = json["Properties"]?["AttributeInitKeys[1]"] ?? json["Properties"]?["AttributeInitKeys"];
                var category = (string?) keys?["AttributeInitCategory"];
                var size = (string?) keys?["AttributeInitSubCategory"];
                if (category == null || size == null) continue;

                curves.TryGetValue($"{category}.{size}.FortHealthSet.MaxHealth", out var health);
                curves.TryGetValue($"{category}.FortBuildingActorSet.BuildTime", out var buildTime);
                if (health <= 0f) continue;

                Console.WriteLine($"{export.ExportType}	{health:F0}	{buildTime:F0}");
            }
        }

        return 0;
    }

    /// <summary>
    ///     A non-asset file out of the paks, as text - the config inis above all.
    ///
    ///     Worth having because several things are configured in ini rather than in an asset, and are
    ///     otherwise invisible: GAS's attribute-initialisation curve tables are named in
    ///     `FortniteGame/Config/DefaultGame.ini` under
    ///     `[/Script/GameplayAbilities.AbilitySystemGlobals]`, which is the only pointer to where a
    ///     building's real MaxHealth comes from.
    /// </summary>
    private static int Raw(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader raw <path> [grepSubstring]"); return 2; }

        var bytes = provider.SaveAsset(args[1]);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        if (args.Length > 2) {
            foreach (var line in text.Split('\n')) {
                if (line.Contains(args[2], StringComparison.OrdinalIgnoreCase)) Console.WriteLine(line.TrimEnd());
            }
        } else {
            Console.WriteLine(text);
        }

        return 0;
    }

    /// <summary>
    ///     Every placed actor of a class across the whole Athena map, in WORLD coordinates - the
    ///     command a map-driven feature actually wants.
    ///
    ///     Does the join <see cref="Foundations"/> exists for, in one mount: read the level foundations
    ///     (which name a sublevel and carry its placement), then for each sublevel enumerate the actors
    ///     and offset them by their foundation's transform. Yaw is applied because a foundation may be
    ///     rotated - most are not, but a POI that is would otherwise come out mirrored across the map.
    ///
    ///     Prints `X,Y,Z` per line, ready to be turned into a generated table.
    /// </summary>
    /// <summary>
    ///     Every placed actor across the whole Athena map whose CLASS is a building wall, as
    ///     `ActorName&lt;TAB&gt;ClassName`.
    ///
    ///     Exists because an actor's NAME DOES NOT PREDICT ITS CLASS, which this project has now been
    ///     bitten by three times. The one that produced this command: interacting with
    ///     `Prop_Athena_Doorbell_Interactable_2` matched a `Contains("Door")` test, so the server built
    ///     an ABuildingWall stand-in for it and pushed wall handles at an actor whose real class is
    ///     `BuildingPropSimpleInteract` - and the client dropped the connection. Meanwhile the actual
    ///     door beside it is named `DestroyedHouse_DoorC` and its class is `ShutterHouse_DoorC_C`, so
    ///     neither the name nor a substring of it identifies a door.
    ///
    ///     What DOES identify one: the class asset lives under Building/ActorBlueprints/Wall and its
    ///     chain reaches Parent_BuildingWall. That is checkable offline and nowhere else, which is
    ///     what makes this a generated table rather than a runtime guess.
    /// </summary>
    private static int Walls(DefaultFileProvider provider, string[] args) {
        // EVERY .umap under Athena, not the foundation walk the other commands use. That walk only
        // reaches sublevels named directly by a top-level LF_ actor - 102 of them - and building
        // interiors like Athena_SUB_3x3_House_g2 are nested deeper, so the very door that prompted
        // this command was missing from the first sweep. Only NAMES are needed here, not positions,
        // so the exhaustive walk is both simpler and complete.
        var verdict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var seen = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var maps = 0;

        bool IsBuildingWall(string className) {
            if (verdict.TryGetValue(className, out var known)) return known;

            var result = false;
            var bare = className.EndsWith("_C", StringComparison.Ordinal) ? className[..^2] : className;

            // The class ASSET's location is the gate: only walls live under Building/ActorBlueprints/Wall.
            foreach (var file in provider.Files.Keys) {
                if (!file.EndsWith($"/{bare}.uasset", StringComparison.OrdinalIgnoreCase)) continue;
                result = file.Contains("/Building/ActorBlueprints/", StringComparison.OrdinalIgnoreCase)
                      && file.Contains("/Wall/", StringComparison.OrdinalIgnoreCase);
                break;
            }

            verdict[className] = result;
            return result;
        }

        foreach (var map in provider.Files.Keys.ToList()) {
            if (!map.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) continue;
            if (!map.Contains("/Athena/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!provider.TryLoadPackage(map, out var package)) continue;

            maps++;
            foreach (var actor in package.GetExports()) {
                // NO name filter. An earlier version also required the class name to contain "Door",
                // which is the exact mistake this table exists to remove - it excluded
                // `Rural_House_Wall_9_C`, a `Parent_BuildingWall_C` subclass WITH a door in it, and
                // so the door in that house could never be opened.
                if (!IsBuildingWall(actor.ExportType)) continue;

                seen[actor.Name] = actor.ExportType;
            }
        }

        foreach (var (name, cls) in seen) Console.WriteLine($"{name}	{cls}");
        Console.Error.WriteLine($"pakreader: {seen.Count} distinct building-wall actor name(s) across {maps} map(s)");
        return 0;
    }

    private static int SpawnPoints(DefaultFileProvider provider, string[] args, bool verbose = false) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader spawnpoints|placements <classSubstring>"); return 2; }
        var needle = args[1];

        string[] foundationMaps = {
            "FortniteGame/Content/Athena/Maps/Athena_Streaming_Grid.umap",
            "FortniteGame/Content/Athena/Maps/Athena_POI_Foundations.umap"
        };

        var found = 0;
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in foundationMaps) {
            found += WalkLevel(provider, map, new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0), 0f,
                               needle, onStack, 0, verbose);
        }

        Console.Error.WriteLine($"pakreader: {found} '{needle}' spawn point(s) in world space");
        return 0;
    }

    /// <summary>How deep foundations may nest before this gives up. POI -> building kit is two.</summary>
    private const int MaxFoundationDepth = 8;

    /// <summary>
    ///     Emit every actor matching <paramref name="needle"/> in this level and in every level its
    ///     foundations place, in WORLD space.
    ///
    ///     THE RECURSION IS THE WHOLE POINT, and leaving it out is why floor loot looked like it was
    ///     barely on the map: the first version of this walked the two foundation maps, followed each
    ///     foundation's AdditionalWorlds ONE level down, and scanned only that. But a POI sublevel
    ///     places its own building kits through its own foundations -
    ///     `Athena_POI_Castle_002` -> `LF_Athena_POI_5x10` -> `Athena_ICE_5x9_CastleBuildings_a_02` -
    ///     and the building kits are where nearly all the floor loot, chests and ammo boxes actually
    ///     live. Stopping at depth 1 found 932 spawners and missed the interiors entirely; the
    ///     symptom was a player standing in a house with the nearest generated spawn point 150m away.
    ///
    ///     Transforms compose the obvious way: a child level's origin is the parent's origin plus the
    ///     foundation's own RelativeLocation rotated by the parent's accumulated yaw, and yaws add.
    ///     Yaw only, and Z unrotated - foundations are placed flat, and that is what the single-level
    ///     version already assumed.
    ///
    ///     Foundations are recognised by HAVING `AdditionalWorlds` rather than by an `LF_` class-name
    ///     prefix. The name is a convention; the property is the actual capability, and nested
    ///     foundations are not guaranteed to follow the naming.
    /// </summary>
    private static int WalkLevel(DefaultFileProvider provider, string packagePath,
                                 CUE4Parse.UE4.Objects.Core.Math.FVector origin, float yaw,
                                 string needle, HashSet<string> onStack, int depth,
                                 bool verbose = false) {
        if (depth > MaxFoundationDepth) return 0;

        // A level that places itself, directly or through a chain, would otherwise recurse forever.
        // Keyed on the STACK rather than on everything seen: the same building kit is legitimately
        // placed dozens of times at different transforms and every one of those is wanted.
        if (!onStack.Add(packagePath)) return 0;

        try {
            if (!provider.TryLoadPackage(packagePath, out var package)) return 0;

            var radians = yaw * MathF.PI / 180f;
            var cos = MathF.Cos(radians);
            var sin = MathF.Sin(radians);
            var found = 0;

            foreach (var export in package.GetExports()) {
                var root = export.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("RootComponent", null);
                var component = root?.Load();

                if (export.ExportType.Contains(needle, StringComparison.OrdinalIgnoreCase) && component != null) {
                    var local = component.GetOrDefault("RelativeLocation", new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0));
                    var worldX = origin.X + (local.X * cos - local.Y * sin);
                    var worldY = origin.Y + (local.X * sin + local.Y * cos);
                    var worldZ = origin.Z + local.Z;

                    if (verbose) {
                        // The actor's OWN yaw composes with the accumulated foundation yaw the same
                        // way its location does. Anything that has to face the way it was placed -
                        // a vehicle, a door - needs this and `spawnpoints` does not carry it.
                        var localYaw = component.GetOrDefault("RelativeRotation",
                            new CUE4Parse.UE4.Objects.Core.Math.FRotator(0, 0, 0)).Yaw;

                        Console.WriteLine($"{export.ExportType}	{worldX:F0},{worldY:F0},{worldZ:F0}	{yaw + localYaw:F1}");
                    } else {
                        Console.WriteLine($"{worldX:F0},{worldY:F0},{worldZ:F0}");
                    }

                    found++;
                }

                var worlds = export.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FSoftObjectPath[]>("AdditionalWorlds", null);
                if (worlds is not { Length: > 0 }) continue;

                var childLocal = component?.GetOrDefault("RelativeLocation", new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0))
                                 ?? new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0);
                var childYaw = component?.GetOrDefault("RelativeRotation", new CUE4Parse.UE4.Objects.Core.Math.FRotator(0, 0, 0)).Yaw ?? 0f;

                var childOrigin = new CUE4Parse.UE4.Objects.Core.Math.FVector(
                    origin.X + (childLocal.X * cos - childLocal.Y * sin),
                    origin.Y + (childLocal.X * sin + childLocal.Y * cos),
                    origin.Z + childLocal.Z);

                foreach (var world in worlds) {
                    // "/Game/..." is the mount point; the provider indexes it as "FortniteGame/Content/...".
                    var sub = world.AssetPathName.Text.Split('.')[0]
                        .Replace("/Game/", "FortniteGame/Content/") + ".umap";
                    found += WalkLevel(provider, sub, childOrigin, yaw + childYaw, needle, onStack, depth + 1, verbose);
                }
            }

            return found;
        } finally {
            onStack.Remove(packagePath);
        }
    }

    /// <summary>
    ///     LEVEL FOUNDATIONS - `Name<TAB>X,Y,Z<TAB>Yaw<TAB>StreamedWorld`, one per foundation actor.
    ///
    ///     THIS IS THE MISSING LINK for anything read out of an Athena sublevel. Athena's POI sublevels
    ///     are NOT world-composition tiles (their packages have no WorldTileInfo) and are NOT in the
    ///     persistent map's LevelStreaming list. They are streamed by `ABuildingFoundation` actors -
    ///     `LF_Athena_POI_*`, in `Maps/Athena_Streaming_Grid.umap` and `Maps/Athena_POI_Foundations.umap` -
    ///     whose `AdditionalWorlds` names the sublevel and whose own transform PLACES it. The proof it
    ///     is the placement: a foundation's `StreamingBoundingBox` is centred on the origin
    ///     (Min -10240 / Max +10240), which only makes sense for content stored in local space.
    ///
    ///     So: world position of a sublevel actor = foundation transform applied to the actor's own
    ///     local RelativeLocation.
    /// </summary>
    private static int Foundations(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader foundations <umapPath>"); return 2; }
        if (!provider.TryLoadPackage(args[1], out var package)) return 0;

        foreach (var export in package.GetExports()) {
            // Every level foundation Blueprint is named LF_*; the class name is the reliable marker
            // since the actor names vary per POI.
            if (!export.ExportType.StartsWith("LF_", StringComparison.OrdinalIgnoreCase)) continue;

            var worlds = export.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FSoftObjectPath[]>("AdditionalWorlds", null);
            var streamed = worlds is { Length: > 0 } ? worlds[0].AssetPathName.Text : "";

            var root = export.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("RootComponent", null);
            var component = root?.Load();
            var location = component?.GetOrDefault("RelativeLocation", new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0))
                           ?? new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0);
            var rotation = component?.GetOrDefault("RelativeRotation", new CUE4Parse.UE4.Objects.Core.Math.FRotator(0, 0, 0))
                           ?? new CUE4Parse.UE4.Objects.Core.Math.FRotator(0, 0, 0);

            Console.WriteLine($"{export.Name}\t{location.X:F1},{location.Y:F1},{location.Z:F1}\t{rotation.Yaw:F1}\t{streamed}");
        }

        return 0;
    }

    /// <summary>
    ///     A World Composition tile's world POSITION - the offset that turns a sublevel's local actor
    ///     coordinates into world ones.
    ///
    ///     Athena has 453 umaps but its persistent map lists only 21 `LevelStreaming*` objects, which
    ///     is the giveaway: the `Sublevel_XnYn` grid is streamed by **World Composition**, and a
    ///     composition tile is not positioned by anything in the persistent map at all. Each tile
    ///     carries its own placement, serialised into ITS OWN package's file summary at
    ///     `WorldTileInfoDataOffset` - outside the export table, which is why no amount of dumping
    ///     properties finds it.
    ///
    ///     Prints `X,Y,Z`, or nothing for a map that is not a composition tile (offset 0).
    /// </summary>
    private static int TileInfo(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader tileinfo <umapPath>"); return 2; }

        if (provider.LoadPackage(args[1]) is not CUE4Parse.UE4.Assets.Package package) {
            Console.Error.WriteLine("not a legacy .umap package");
            return 1;
        }

        var offset = package.Summary.WorldTileInfoDataOffset;
        if (offset <= 0) return 0; // not a composition tile

        var path = args[1].EndsWith(".umap", StringComparison.OrdinalIgnoreCase) ? args[1] : args[1] + ".umap";
        var bytes = provider.SaveAsset(path);

        var ar = new CUE4Parse.UE4.Assets.Readers.FAssetArchive(
            new CUE4Parse.UE4.Readers.FByteArchive(path, bytes, provider.Versions), package);
        ar.Position = offset;

        var info = new CUE4Parse.UE4.Assets.Exports.Engine.FWorldTileInfo(ar);
        Console.WriteLine($"{info.Position.X},{info.Position.Y},{info.Position.Z}");
        return 0;
    }

    /// <summary>
    ///     Placed actors of a class in a .umap, with their world locations - `Name<TAB>X,Y,Z`.
    ///
    ///     This is how a map-driven feature gets its data: floor-loot spawners, chests, player starts
    ///     are all just actors placed in a streaming sublevel. A cooked umap keeps the transform on the
    ///     actor's ROOT COMPONENT export rather than the actor, so that is what gets read.
    ///
    ///     Prints nothing and returns 0 for a map that has none, so this can be run in a loop over
    ///     hundreds of sublevels.
    /// </summary>
    private static int Actors(DefaultFileProvider provider, string[] args) {
        if (args.Length < 3) { Console.Error.WriteLine("usage: pakreader actors <umapPath> <classSubstring>"); return 2; }

        var needle = args[2];
        if (!provider.TryLoadPackage(args[1], out var package)) return 0;

        foreach (var export in package.GetExports()) {
            if (!export.ExportType.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;

            // RootComponent is an object reference into this same package; its RelativeLocation is
            // the placed transform.
            var root = export.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("RootComponent", null);
            var component = root?.Load();
            var location = component?.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("RelativeLocation",
                new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0));
            if (location == null) continue;

            Console.WriteLine($"{export.Name}\t{location.Value.X:F1},{location.Value.Y:F1},{location.Value.Z:F1}");
        }

        return 0;
    }

    /// <summary>
    ///     Row names and values of a UDataTable or UCurveTable, optionally filtered by row name.
    ///
    ///     This is where Fortnite keeps almost every number this project has been guessing: the storm
    ///     phase plan, the loot pools, the per-material building health. A CURVE table's rows are
    ///     keyed curves rather than scalars, so what gets printed is the key/value pairs - for the
    ///     tables that matter here there is usually one key at time 0, which is the constant.
    /// </summary>
    private static int Rows(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader rows <objectPath> [rowNameSubstring] [max]"); return 2; }

        var needle = args.Length > 2 ? args[2] : null;
        var max = args.Length > 3 && int.TryParse(args[3], out var m) ? m : 2000;

        var shown = 0;
        foreach (var export in provider.LoadPackage(args[1]).GetExports()) {
            // Both UDataTable and UCurveTable expose their rows as a name->object map; go through the
            // generic property view so this does not need a typed reference to either.
            var json = JsonConvert.SerializeObject(export, Formatting.Indented);
            foreach (var line in json.Split('\n')) {
                if (needle != null && !line.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine(line.TrimEnd());
                if (++shown >= max) return 0;
            }
        }

        Console.Error.WriteLine($"pakreader: {shown} line(s) shown");
        return 0;
    }

    /// <summary>
    ///     The compact form, and the one a generator actually wants: property name = value, one per
    ///     line, optionally filtered. A full export dump of a Fortnite asset is enormous and mostly
    ///     irrelevant; this keeps the answer to a question the size of the question.
    /// </summary>
    private static int Props(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader props <objectPath> [name,name,...]"); return 2; }

        var filter = args.Length > 2
            ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        foreach (var export in provider.LoadPackage(args[1]).GetExports()) {
            Console.WriteLine($"# {export.Name} ({export.ExportType})");
            foreach (var prop in export.Properties) {
                var name = prop.Name.Text;
                if (filter.Length > 0 && !filter.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
                Console.WriteLine($"{name} = {prop.Tag}");
            }
        }

        return 0;
    }
}
