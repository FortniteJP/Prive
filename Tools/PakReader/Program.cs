using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
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
///         pakreader supers &lt;pathPrefix&gt;       class -&gt; super for every Blueprint class under a path
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
                case "supers": return Supers(provider, args);
                case "rows": return Rows(provider, args);
                case "actors": return Actors(provider, args);
                case "tileinfo": return TileInfo(provider, args);
                case "meshes": return Meshes(provider, args);
                case "meshcover": return MeshCover(provider, args);
                case "whereis": return WhereIs(provider, args);
                case "collision": return Collision(provider, args);
                case "stacks": return Stacks(provider, args);
                case "weaponstats": return WeaponStats(provider, args);
                case "netfields": return NetFields(provider, args);
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


    /// <summary>A world placement: translation, rotation, per-axis scale. No shear.</summary>
    private readonly record struct Placement(
        CUE4Parse.UE4.Objects.Core.Math.FVector Translation,
        CUE4Parse.UE4.Objects.Core.Math.FQuat Rotation,
        CUE4Parse.UE4.Objects.Core.Math.FVector Scale) {

        public static readonly Placement Identity = new(
            new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0),
            CUE4Parse.UE4.Objects.Core.Math.FQuat.Identity,
            new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1));

        public CUE4Parse.UE4.Objects.Core.Math.FVector TransformPosition(
            CUE4Parse.UE4.Objects.Core.Math.FVector local) =>
            Rotation.RotateVector(local * Scale) + Translation;

        public static Placement Compose(Placement parent, Placement child) => new(
            parent.TransformPosition(child.Translation),
            parent.Rotation * child.Rotation,
            parent.Scale * child.Scale);
    }

    /// <summary>A component's own relative transform.</summary>
    private static Placement ComponentPlacement(CUE4Parse.UE4.Assets.Exports.UObject component) {
        var location = component.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FVector loc, "RelativeLocation")
            ? loc : new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0);
        var rotation = component.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FRotator rot, "RelativeRotation")
            ? rot.Quaternion() : CUE4Parse.UE4.Objects.Core.Math.FQuat.Identity;
        var scale = component.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FVector scl, "RelativeScale3D")
            ? scl : new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1);
        return new Placement(location, rotation, scale);
    }

    /// <summary>The actor's root component object, or null.</summary>
    private static CUE4Parse.UE4.Assets.Exports.UObject? RootComponentOf(
        CUE4Parse.UE4.Assets.Exports.UObject actor,
        IReadOnlyList<CUE4Parse.UE4.Assets.Exports.UObject> siblings) {
        var index = actor.GetOrDefault<FPackageIndex?>("RootComponent", null);
        if (index is { IsNull: false } && index.Load() is { } loaded) return loaded;

        var name = actor.GetOrDefault<FName?>("RootComponent", null)?.Text;
        return name != null ? siblings.FirstOrDefault(e => e.Name == name) : null;
    }

    /// <summary>
    ///     Where a component sits in its package's own space: the owning actor's placement composed
    ///     with the component's own - EXCEPT when the component is the actor's root, where those are
    ///     the same transform and composing them applies it twice.
    /// </summary>
    private static Placement ComponentInLevel(
        CUE4Parse.UE4.Assets.Exports.UObject component,
        IReadOnlyList<CUE4Parse.UE4.Assets.Exports.UObject> exports) {
        var ownerName = component.Outer?.Name.Text;
        var owner = ownerName != null ? exports.FirstOrDefault(e => e.Name == ownerName) : null;

        if (owner == null || ReferenceEquals(RootComponentOf(owner, exports), component))
            return ComponentPlacement(component);

        var root = RootComponentOf(owner, exports);
        var actorPlacement = root != null ? ComponentPlacement(root) : Placement.Identity;
        return Placement.Compose(actorPlacement, ComponentPlacement(component));
    }

    /// <summary>
    ///     Every static mesh component in ONE .umap, with the mesh it draws and where its bounds land -
    ///     `Component&lt;TAB&gt;Mesh&lt;TAB&gt;X0..X1,Y0..Y1,Z0..Z1&lt;TAB&gt;triangles`.
    ///
    ///     THE POINT IS THE ONE-PACKAGE SCOPE. TerrainHeightMapBaker answers the same question, but
    ///     only while scanning all 453 Athena umaps - six minutes per question. When the question is
    ///     "what is actually under the warmup spawn point", it is about a single sublevel, and this
    ///     answers it in seconds so a hypothesis can be tested more than twice an hour.
    ///
    ///     The optional offset is the sublevel's own placement (a level foundation's transform, from
    ///     `pakreader foundations`), since a POI sublevel's contents are authored in LOCAL space -
    ///     without it every box printed here is thousands of units from where the thing stands.
    ///
    ///     A component whose mesh is unset or unloadable prints `&lt;none&gt;` rather than being skipped:
    ///     the interesting failure is usually the mesh that ISN'T there.
    /// </summary>
    private static int Meshes(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) {
            Console.Error.WriteLine("usage: pakreader meshes <umapPath> [offsetX offsetY offsetZ]");
            return 2;
        }

        var offset = args.Length >= 5
            ? new CUE4Parse.UE4.Objects.Core.Math.FVector(
                float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture))
            : new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0);

        if (!provider.TryLoadPackage(args[1], out var package)) { Console.Error.WriteLine("no such package"); return 1; }

        var exports = package.GetExports().ToList();

        foreach (var export in exports) {
            if (export is not CUE4Parse.UE4.Assets.Exports.Component.StaticMesh.UStaticMeshComponent smc) continue;

            CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh? mesh = null;
            try { mesh = smc.GetLoadedStaticMesh(); } catch { /* printed as <none> below */ }

            // The component's place within the sublevel - the owning ACTOR's transform included,
            // without applying it twice when the component is that actor's root.
            var place = Placement.Compose(
                new Placement(offset, CUE4Parse.UE4.Objects.Core.Math.FQuat.Identity,
                              new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1)),
                ComponentInLevel(smc, exports));

            if (mesh?.RenderData?.Bounds is not { } bounds) {
                Console.WriteLine($"{smc.Name}\t<none>\t{place.Translation.X:F0},{place.Translation.Y:F0},{place.Translation.Z:F0}\t0");
                continue;
            }

            // The eight corners transformed, then an axis-aligned box around them - so a rotated
            // placement reports the space it really occupies.
            float bMinX = float.MaxValue, bMinY = float.MaxValue, bMinZ = float.MaxValue;
            float bMaxX = float.MinValue, bMaxY = float.MinValue, bMaxZ = float.MinValue;

            for (var corner = 0; corner < 8; corner++) {
                var local = new CUE4Parse.UE4.Objects.Core.Math.FVector(
                    bounds.Origin.X + ((corner & 1) == 0 ? -bounds.BoxExtent.X : bounds.BoxExtent.X),
                    bounds.Origin.Y + ((corner & 2) == 0 ? -bounds.BoxExtent.Y : bounds.BoxExtent.Y),
                    bounds.Origin.Z + ((corner & 4) == 0 ? -bounds.BoxExtent.Z : bounds.BoxExtent.Z));

                var w = place.TransformPosition(local);
                bMinX = MathF.Min(bMinX, w.X); bMaxX = MathF.Max(bMaxX, w.X);
                bMinY = MathF.Min(bMinY, w.Y); bMaxY = MathF.Max(bMaxY, w.Y);
                bMinZ = MathF.Min(bMinZ, w.Z); bMaxZ = MathF.Max(bMaxZ, w.Z);
            }

            var triangles = (mesh.RenderData?.LODs?.FirstOrDefault()?.IndexBuffer?.Length ?? 0) / 3;

            Console.WriteLine($"{smc.Name}\t{mesh.Name}\t" +
                              $"{bMinX:F0}..{bMaxX:F0},{bMinY:F0}..{bMaxY:F0},{bMinZ:F0}..{bMaxZ:F0}\t" +
                              $"{triangles}");
        }

        return 0;
    }

    /// <summary>
    ///     Does one mesh's TRIANGLES actually cover a world XY - and if not, how far away is the
    ///     nearest one? `meshcover &lt;umapPath&gt; &lt;meshNameSubstring&gt; &lt;X&gt; &lt;Y&gt; [offX offY offZ]`.
    ///
    ///     THIS IS THE QUESTION A BOUNDING BOX CANNOT ANSWER, and the one blocking the map bake: the
    ///     warmup island's ground mesh has a box that contains the spawn point and 4,076 triangles
    ///     that (as far as the bake can tell) do not. "Contained by the box" and "covered by the
    ///     surface" are different claims, and only the second one is ground anyone can stand on.
    ///
    ///     The DISTANCE is the part that decides what to do next. A few hundred units means the bake
    ///     is rasterising a real surface with a gap in it - the mesh is right and the sampling is
    ///     wrong. Several thousand means the surface genuinely is not there and the ground under that
    ///     point belongs to some other asset the scan has not reached.
    /// </summary>
    private static int MeshCover(DefaultFileProvider provider, string[] args) {
        if (args.Length < 5) {
            Console.Error.WriteLine("usage: pakreader meshcover <umapPath> <meshNameSubstring> <X> <Y> [offX offY offZ]");
            return 2;
        }

        var needle = args[2];
        var px = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        var py = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);

        // --points <file>: one "X Y" per line, answered in a single pass over the package. The
        // per-point verdict is what makes this an acceptance test rather than an anecdote - "68 of
        // 121 spawn points have nothing under them" is a fact about the map; one probe is not.
        var pointsFile = Array.IndexOf(args, "--points");
        var points = new List<(float X, float Y)>();
        if (pointsFile >= 0 && pointsFile + 1 < args.Length)
            foreach (var line in File.ReadAllLines(args[pointsFile + 1])) {
                var parts = line.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                points.Add((float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
            }
        if (points.Count == 0) points.Add((px, py));

        // Best (highest) covering Z per point, and which mesh provided it.
        var bestPerPoint = new float?[points.Count];
        var bestMesh = new string[points.Count];
        var offX = args.Length >= 8 ? float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 0f;
        var offY = args.Length >= 8 ? float.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture) : 0f;
        var offZ = args.Length >= 8 ? float.Parse(args[7], System.Globalization.CultureInfo.InvariantCulture) : 0f;

        if (!provider.TryLoadPackage(args[1], out var package)) { Console.Error.WriteLine("no such package"); return 1; }

        var exports = package.GetExports().ToList();

        foreach (var export in exports) {
            if (export is not CUE4Parse.UE4.Assets.Exports.Component.StaticMesh.UStaticMeshComponent smc) continue;

            CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh? mesh = null;
            try { mesh = smc.GetLoadedStaticMesh(); } catch { continue; }
            var all = needle == "*";
            if (mesh == null || (!all && !mesh.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))) continue;

            // The sublevel's own offset (a foundation transform, passed in), then the component's
            // place within the sublevel - actor transform included, which is the whole point.
            var levelPlacement = new Placement(
                new CUE4Parse.UE4.Objects.Core.Math.FVector(offX, offY, offZ),
                CUE4Parse.UE4.Objects.Core.Math.FQuat.Identity,
                new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1));
            var componentPlacement = Placement.Compose(levelPlacement, ComponentInLevel(smc, exports));

            var placements = new List<Placement>();
            if (smc is CUE4Parse.UE4.Assets.Exports.Component.StaticMesh.UInstancedStaticMeshComponent ismc &&
                ismc.PerInstanceSMData is { Length: > 0 } instances)
                foreach (var inst in instances)
                    placements.Add(Placement.Compose(componentPlacement,
                        new Placement(inst.TransformData.Translation, inst.TransformData.Rotation,
                                      inst.TransformData.Scale3D)));
            else
                placements.Add(componentPlacement);

            var lod = mesh.RenderData?.LODs?.FirstOrDefault();
            var verts = lod?.PositionVertexBuffer?.Verts;
            var indices = lod?.IndexBuffer;
            if (verts == null || indices == null) { Console.WriteLine($"{mesh.Name}: no LOD0 geometry"); continue; }

            // WHICH LOD IS THIS, REALLY. A cooked mesh can have its top LODs stripped for the target
            // platform, so "LODs[0]" is not necessarily the mesh the artist made - and a decimated
            // LOD is exactly what a patchy, holed ground surface would look like. Printed for the
            // named form only; in "*" mode it would be 1,869 lines of noise.
            if (!all && mesh.RenderData?.LODs is { } allLods)
                Console.WriteLine($"  {mesh.Name}: {allLods.Length} LOD(s), triangles " +
                                  string.Join("/", allLods.Select(l => (l.IndexBuffer?.Length ?? 0) / 3)) +
                                  $", {verts.Length} vert(s) in LOD0");

            var placement = Placement.Identity;
            float Wx(int i) => placement.TransformPosition(verts[i]).X;
            float Wy(int i) => placement.TransformPosition(verts[i]).Y;
            float Wz(int i) => placement.TransformPosition(verts[i]).Z;

            var hits = 0;
            var bestZ = float.NaN;
            var nearest = float.MaxValue;

            foreach (var thisPlacement in placements) {
            placement = thisPlacement;

            for (var t = 0; t + 2 < indices.Length; t += 3) {
                int ia = (int) indices[t], ib = (int) indices[t + 1], ic = (int) indices[t + 2];
                float ax = Wx(ia), ay = Wy(ia), bx = Wx(ib), by = Wy(ib), cx = Wx(ic), cy = Wy(ic);

                var den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (MathF.Abs(den) < 0.0001f) continue;

                for (var pi = 0; pi < points.Count; pi++) {
                    var qx = points[pi].X;
                    var qy = points[pi].Y;

                    var w0 = ((by - cy) * (qx - cx) + (cx - bx) * (qy - cy)) / den;
                    var w1 = ((cy - ay) * (qx - cx) + (ax - cx) * (qy - cy)) / den;
                    var w2 = 1f - w0 - w1;

                    if (w0 >= 0f && w1 >= 0f && w2 >= 0f) {
                        var z = w0 * Wz(ia) + w1 * Wz(ib) + w2 * Wz(ic);
                        if (bestPerPoint[pi] is not { } had || z > had) {
                            bestPerPoint[pi] = z;
                            bestMesh[pi] = mesh.Name;
                        }

                        if (pi == 0) { hits++; if (float.IsNaN(bestZ) || z > bestZ) bestZ = z; nearest = 0f; }
                        continue;
                    }

                    if (pi != 0) continue;

                    // Centroid distance is a cheap stand-in for real point-to-triangle distance; at
                    // the scale that matters here (200 units away or 5,000?) it is enough.
                    var dx = (ax + bx + cx) / 3f - qx;
                    var dy = (ay + by + cy) / 3f - qy;
                    var d = MathF.Sqrt(dx * dx + dy * dy);
                    if (d < nearest) nearest = d;
                }
            }
            }

            // In "*" mode only the answer that matters gets printed; naming 1,869 misses is noise.
            if (all && hits == 0) continue;

            Console.WriteLine($"{mesh.Name} ({indices.Length / 3} tri): " +
                              (hits > 0
                                  ? $"COVERS ({px:F0},{py:F0}) in {hits} triangle(s), top Z {bestZ:F0}"
                                  : $"does NOT cover ({px:F0},{py:F0}); nearest triangle centroid {nearest:F0} units away"));

            // A PICTURE, because "39% of its bounding box" describes both a solid central blob and a
            // full island with a hole in it, and those call for opposite next steps. 64x32 characters
            // over the mesh's own extent: '#' covered, '.' not, '@' the queried point.
            if (!args.Contains("--map", StringComparer.OrdinalIgnoreCase)) continue;

            float mnx = float.MaxValue, mxx = float.MinValue, mny = float.MaxValue, mxy = float.MinValue;
            for (var i = 0; i < verts.Length; i++) {
                mnx = MathF.Min(mnx, Wx(i)); mxx = MathF.Max(mxx, Wx(i));
                mny = MathF.Min(mny, Wy(i)); mxy = MathF.Max(mxy, Wy(i));
            }

            const int cols = 64, rows = 32;
            var grid = new char[rows][];
            for (var r = 0; r < rows; r++) { grid[r] = new char[cols]; Array.Fill(grid[r], '.'); }

            for (var t = 0; t + 2 < indices.Length; t += 3) {
                int ia = (int) indices[t], ib = (int) indices[t + 1], ic = (int) indices[t + 2];
                float ax = Wx(ia), ay = Wy(ia), bx = Wx(ib), by = Wy(ib), cx2 = Wx(ic), cy2 = Wy(ic);

                var den = (by - cy2) * (ax - cx2) + (cx2 - bx) * (ay - cy2);
                if (MathF.Abs(den) < 0.0001f) continue;

                for (var r = 0; r < rows; r++)
                for (var c = 0; c < cols; c++) {
                    if (grid[r][c] == '#') continue;

                    var sx = mnx + (mxx - mnx) * (c + 0.5f) / cols;
                    var sy = mny + (mxy - mny) * (r + 0.5f) / rows;

                    var u0 = ((by - cy2) * (sx - cx2) + (cx2 - bx) * (sy - cy2)) / den;
                    var u1 = ((cy2 - ay) * (sx - cx2) + (ax - cx2) * (sy - cy2)) / den;
                    if (u0 >= 0f && u1 >= 0f && 1f - u0 - u1 >= 0f) grid[r][c] = '#';
                }
            }

            var qc = (int) ((px - mnx) / (mxx - mnx) * cols);
            var qr = (int) ((py - mny) / (mxy - mny) * rows);
            if (qc >= 0 && qc < cols && qr >= 0 && qr < rows) grid[qr][qc] = '@';

            Console.WriteLine($"  X {mnx:F0}..{mxx:F0}  Y {mny:F0}..{mxy:F0}  ('@' = the queried point)");
            for (var r = 0; r < rows; r++) Console.WriteLine("  " + new string(grid[r]));
        }

        if (points.Count > 1) {
            var covered = 0;
            for (var pi = 0; pi < points.Count; pi++) {
                if (bestPerPoint[pi] is { } z) {
                    covered++;
                    Console.WriteLine($"{points[pi].X:F0} {points[pi].Y:F0}\t{z:F0}\t{bestMesh[pi]}");
                } else {
                    Console.WriteLine($"{points[pi].X:F0} {points[pi].Y:F0}\tNONE\t-");
                }
            }

            Console.Error.WriteLine($"meshcover: {covered} of {points.Count} point(s) have geometry under them.");
        }

        return 0;
    }

    /// <summary>
    ///     Which package holds geometry over a point, IN EACH PACKAGE'S OWN LOCAL SPACE -
    ///     `whereis &lt;X&gt; &lt;Y&gt; &lt;pathPrefix&gt;`.
    ///
    ///     THE POINT IS TO ASK WITHOUT ASSUMING THE PLACEMENT IS RIGHT. TerrainHeightMapBaker asks the
    ///     same question in WORLD space, so a sublevel it places wrongly is invisible to it - the
    ///     content is somewhere, just not where it was looked for, and the answer comes back "nothing
    ///     covers this point" either way. Athena's POI sublevels are authored around their own origin
    ///     and offset by a level foundation, so a point expressed in one POI's local space will land
    ///     inside whichever OTHER sublevel holds the same piece of ground, if one does.
    ///
    ///     Prints `package&lt;TAB&gt;mesh&lt;TAB&gt;Z0..Z1` per covering component. Bounding boxes, not
    ///     triangles: this is a "which asset should I be looking at" search, and `meshcover` answers
    ///     the exact question once there is a candidate.
    /// </summary>
    private static int WhereIs(DefaultFileProvider provider, string[] args) {
        if (args.Length < 4) {
            Console.Error.WriteLine("usage: pakreader whereis <X> <Y> <pathPrefix>");
            return 2;
        }

        var px = float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        var py = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        var prefix = args[3];

        var scanned = 0;
        var found = 0;

        foreach (var file in provider.Files.Keys
                     .Where(f => f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) &&
                                 f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            scanned++;

            CUE4Parse.UE4.Assets.Exports.UObject[] exports;
            try {
                if (!provider.TryLoadPackage(file, out var package)) continue;
                exports = package.GetExports().ToArray();
            } catch { continue; }

            foreach (var export in exports) {
                if (export is not CUE4Parse.UE4.Assets.Exports.Component.StaticMesh.UStaticMeshComponent smc) continue;

                CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh? mesh = null;
                try { mesh = smc.GetLoadedStaticMesh(); } catch { continue; }
                if (mesh?.RenderData?.Bounds is not { } bounds) continue;

                var rel = smc.GetOrDefault("RelativeLocation", new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0));
                var scale = smc.GetOrDefault("RelativeScale3D", new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1));

                // Per-instance transforms matter here for the same reason they matter in the baker:
                // a POI's floors are frequently ONE instanced component holding hundreds of them.
                var origins = new List<CUE4Parse.UE4.Objects.Core.Math.FVector>();
                if (smc is CUE4Parse.UE4.Assets.Exports.Component.StaticMesh.UInstancedStaticMeshComponent ism &&
                    ism.PerInstanceSMData is { Length: > 0 } instances) {
                    foreach (var inst in instances)
                        origins.Add(new CUE4Parse.UE4.Objects.Core.Math.FVector(
                            rel.X + inst.TransformData.Translation.X,
                            rel.Y + inst.TransformData.Translation.Y,
                            rel.Z + inst.TransformData.Translation.Z));
                } else {
                    origins.Add(rel);
                }

                foreach (var origin in origins) {
                    var cx = origin.X + bounds.Origin.X * scale.X;
                    var cy = origin.Y + bounds.Origin.Y * scale.Y;
                    var cz = origin.Z + bounds.Origin.Z * scale.Z;
                    var ex = bounds.BoxExtent.X * MathF.Abs(scale.X);
                    var ey = bounds.BoxExtent.Y * MathF.Abs(scale.Y);
                    var ez = bounds.BoxExtent.Z * MathF.Abs(scale.Z);

                    if (px < cx - ex || px > cx + ex || py < cy - ey || py > cy + ey) continue;

                    Console.WriteLine($"{file}\t{mesh.Name}\t{cz - ez:F0}..{cz + ez:F0}");
                    found++;
                    break;
                }
            }
        }

        Console.Error.WriteLine($"whereis: {scanned} .umap scanned, {found} covering component(s).");
        return 0;
    }

    /// <summary>
    ///     What SIMPLE COLLISION every mesh in a .umap actually has -
    ///     `mesh&lt;TAB&gt;traceFlag&lt;TAB&gt;convex/box/sphere/capsule&lt;TAB&gt;triangles`, one line per distinct mesh.
    ///
    ///     THIS DECIDES AN ARCHITECTURE, which is why it is worth a command. The map bake voxelises
    ///     RENDER triangles because an early note recorded that Fortnite's meshes are
    ///     CTF_UseComplexAsSimple and carry no simple shapes - so the render mesh was assumed to BE
    ///     the collision. If that is wrong, the server is approximating with 36 million triangles what
    ///     the game itself describes with a handful of convex hulls, and worse, it is approximating
    ///     something the CLIENT does not use: with the default trace flag a projectile sweep hits
    ///     SIMPLE collision, so a server tracing render triangles disagrees with the client that sent
    ///     it the shot.
    ///
    ///     The hulls are plain properties (`BodySetup.AggGeom.ConvexElems[].VertexData`) - the
    ///     PhysX-cooked blob beside them (`CookedFormatData: PhysXPC`) never has to be parsed.
    /// </summary>
    private static int Collision(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader collision <umapPath>"); return 2; }
        if (!provider.TryLoadPackage(args[1], out var package)) { Console.Error.WriteLine("no such package"); return 1; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var export in package.GetExports()) {
            if (export is not CUE4Parse.UE4.Assets.Exports.Component.StaticMesh.UStaticMeshComponent smc) continue;

            CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh? mesh = null;
            try { mesh = smc.GetLoadedStaticMesh(); } catch { continue; }
            if (mesh == null || !seen.Add(mesh.Name)) continue;

            // BodySetup is a STRONGLY-TYPED field on UStaticMesh, not a tagged property - it is read
            // positionally during Deserialize (UStaticMesh.cs:43), so a property lookup by that name
            // finds nothing and every mesh reports "no collision". That false negative is exactly the
            // note this command exists to check.
            var bodySetup = mesh.BodySetup?.Load();

            var flag = bodySetup?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("CollisionTraceFlag").Text
                       ?? "<default>";
            if (string.IsNullOrEmpty(flag)) flag = "<default>";

            var convex = 0; var boxes = 0; var spheres = 0; var capsules = 0;

            if (bodySetup?.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback?>("AggGeom", null) is { } agg) {
                convex = agg.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("ConvexElems", []).Length;
                boxes = agg.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("BoxElems", []).Length;
                spheres = agg.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("SphereElems", []).Length;
                capsules = agg.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("SphylElems", []).Length;
            }

            var triangles = (mesh.RenderData?.LODs?.FirstOrDefault()?.IndexBuffer?.Length ?? 0) / 3;

            Console.WriteLine($"{mesh.Name}\t{flag}\t{convex}c {boxes}b {spheres}s {capsules}k\t{triangles}");
        }

        return 0;
    }

    /// <summary>
    ///     `MaxStackSize` for a list of item definitions - `path&lt;TAB&gt;MaxStackSize`, one per line.
    ///
    ///     BATCHED because that is the whole difficulty: the number is one plain property on each
    ///     item definition, but there are a couple of hundred of them and mounting the paks costs
    ///     seconds, so asking one process per item takes twenty minutes to answer a question worth a
    ///     single table.
    ///
    ///     A path that does not resolve, or an item definition that does not set the property, is
    ///     printed with a size of 0 - the caller decides what to do with "unknown", and the honest
    ///     default there is "does not stack" (a weapon must never merge into another weapon).
    ///
    ///     usage: pakreader stacks &lt;fileOfObjectPaths&gt;
    /// </summary>
    private static int Stacks(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader stacks <fileOfObjectPaths>"); return 2; }

        var resolved = 0;
        var missing = 0;

        foreach (var raw in File.ReadAllLines(args[1])) {
            var path = raw.Trim();
            if (path.Length == 0 || path.StartsWith('#')) continue;

            CUE4Parse.UE4.Assets.Exports.UObject? item = null;
            try { item = provider.LoadPackageObject(path); } catch { /* reported as 0 below */ }

            if (item == null) {
                missing++;
                Console.WriteLine($"{path}	0");
                continue;
            }

            resolved++;
            // THE CLASS COMES TOO, because a cap is only half the rule. Ammo and resources live in
            // ONE row each however much you carry - a second wood stack is not a thing - while a
            // throwable can legitimately occupy two slots, and nothing in MaxStackSize
            // distinguishes them. The class does: FortAmmoItemDefinition / FortResourceItemDefinition.
            Console.WriteLine($"{path}	{item.GetOrDefault("MaxStackSize", 0)}	{item.ExportType}");
        }

        Console.Error.WriteLine($"stacks: {resolved} item definition(s) read, {missing} unresolved.");
        return 0;
    }

    /// <summary>
    ///     Which BALANCE ROW each item's damage comes from, plus the projectile and ability it uses -
    ///     `path&lt;TAB&gt;StatRow&lt;TAB&gt;ProjectileTemplate&lt;TAB&gt;PrimaryFireAbility`.
    ///
    ///     WHY THIS IS THE INTERESTING JOIN. The server currently gives every thrown consumable the
    ///     FRAG GRENADE's numbers - 100 to a player, 375 to a building - because that is the one
    ///     ability whose values were read. A smoke grenade and a boogie bomb therefore kill people.
    ///     The real numbers are per item and they are not on the item: `WeaponStatHandle` is a
    ///     DataTableRowHandle naming a row in Balance/DataTables/UtilityItemDamage, and that row
    ///     carries DmgPB / EnvDmgPB and the rest. Non-damaging throwables point at rows that say so.
    ///
    ///     usage: pakreader weaponstats &lt;fileOfItemPaths&gt;
    /// </summary>
    private static int WeaponStats(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader weaponstats <fileOfItemPaths>"); return 2; }

        var found = 0;

        foreach (var raw in File.ReadAllLines(args[1])) {
            var path = raw.Trim();
            if (path.Length == 0 || path.StartsWith('#')) continue;

            CUE4Parse.UE4.Assets.Exports.UObject? item = null;
            try { item = provider.LoadPackageObject(path); } catch { continue; }
            if (item == null) continue;

            // The row NAME is what matters; the table it points at is UtilityItemDamage for every
            // throwable, and naming it here as well would just be noise repeated 200 times.
            var row = item.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback?>("WeaponStatHandle", null)
                ?.GetOrDefault<FName>("RowName").Text ?? "";

            if (row.Length == 0) continue;

            found++;
            Console.WriteLine($"{path}\t{row}\t" +
                              $"{Soft(item, "ProjectileTemplate")}\t{Soft(item, "PrimaryFireAbility")}");
        }

        Console.Error.WriteLine($"weaponstats: {found} item(s) with a stat row.");
        return 0;
    }

    /// <summary>A soft object path property as a plain string, or "" when unset.</summary>
    private static string Soft(CUE4Parse.UE4.Assets.Exports.UObject item, string name) =>
        item.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FSoftObjectPath>(name).AssetPathName.Text ?? "";

    /// <summary>
    ///     A Blueprint class's OWN replicated fields - `class&lt;TAB&gt;property|function&lt;TAB&gt;name`.
    ///
    ///     WHY A BLUEPRINT'S OWN FIELDS MATTER. `FClassNetCache` numbers an RPC's field by walking the
    ///     whole class chain, and the index travels as a bounded int whose WIDTH comes from
    ///     `GetMaxIndex()+1`. A Blueprint vehicle adds its own on top of the native class -
    ///     ShoppingCartVehicleSK_C adds five (AttachedPickups, FortPickup, ImpulseVector, ApplyImpulse,
    ///     "Update Damage State") - so a server that knows only the native fields reads the index at
    ///     the wrong width and every bit after it is garbage. The BP's fields do not shift the native
    ///     ones, which sit below them; they change how many bits the number takes.
    ///
    ///     usage: pakreader netfields &lt;fileOfClassPaths&gt;
    /// </summary>
    private static int NetFields(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader netfields <fileOfClassPaths>"); return 2; }

        foreach (var raw in File.ReadAllLines(args[1])) {
            var path = raw.Trim();
            if (path.Length == 0 || path.StartsWith('#')) continue;

            // The class asset, not the generated class object - the fields are exports of the package.
            var package = path.Contains('.') ? path[..path.LastIndexOf('.')] : path;

            // "/Game/..." is how the game spells a package and "FortniteGame/Content/..." is how the
            // mounted file index does. Everything else in this tool takes the mounted spelling, so a
            // list copied out of the server's own generated tables silently matched nothing.
            if (package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
                package = "FortniteGame/Content/" + package["/Game/".Length..];

            CUE4Parse.UE4.Assets.Exports.UObject[] exports;
            try {
                if (!provider.TryLoadPackage(package, out var loaded)) continue;
                exports = loaded.GetExports().ToArray();
            } catch (Exception ex) {
                Console.Error.WriteLine($"  !! {package}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            foreach (var export in exports) {
                var type = export.ExportType;

                var isProperty = type.EndsWith("Property", StringComparison.Ordinal);
                if (!isProperty && type != "Function") continue;

                // FLAGS COME FROM THE SERIALISER, NOT FROM A TAGGED PROPERTY. PropertyFlags and
                // FunctionFlags are fields CUE4Parse writes when it serialises a UProperty/UFunction
                // export - they are not values GetOrDefault can look up, and asking for them that way
                // returns an empty string for every export, which reads as "nothing is replicated".
                var json = JsonConvert.SerializeObject(export);
                var flagged = isProperty
                    ? json.Contains("\"PropertyFlags\"") && json.Contains("Net")
                    : json.Contains("FUNC_Net");

                if (flagged) Console.WriteLine($"{path}	{(isProperty ? "property" : "function")}	{export.Name}");
            }
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
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader tileinfo <umapPath|pathPrefix>"); return 2; }

        // One map, or every map under a prefix. The batch form exists because mounting the paks costs
        // seconds and there are 453 Athena umaps - asking about them one process at a time takes half
        // an hour to answer a question that is really "which of these are tiles, and where".
        if (args[1].EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) {
            if (PrintTileInfo(provider, args[1], withPath: false)) return 0;
            Console.Error.WriteLine("not a composition tile (or not a legacy .umap package)");
            return 1;
        }

        var scanned = 0;
        var tiles = 0;
        foreach (var file in provider.Files.Keys
                     .Where(f => f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) &&
                                 f.StartsWith(args[1], StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            scanned++;
            if (PrintTileInfo(provider, file, withPath: true)) tiles++;
        }

        // Counted on stderr so an empty result is DISTINGUISHABLE from a scan that never ran. A batch
        // that prints nothing is the interesting answer here, and without this line it is impossible
        // to tell "no map is a composition tile" from "the prefix matched no files".
        Console.Error.WriteLine($"tileinfo: {scanned} .umap scanned, {tiles} carried WorldTileInfo.");
        return 0;
    }

    /// <summary>
    ///     Prints one tile's offset, and says whether there was one to print. `withPath` prefixes the
    ///     package path so a batch run is greppable; a single-map query keeps the bare `X,Y,Z` that
    ///     scripts already parse.
    /// </summary>
    private static bool PrintTileInfo(DefaultFileProvider provider, string umapPath, bool withPath) {
        CUE4Parse.UE4.Assets.Package? package;
        try {
            package = provider.LoadPackage(umapPath) as CUE4Parse.UE4.Assets.Package;
        } catch {
            return false;
        }

        if (package == null) return false;

        var offset = package.Summary.WorldTileInfoDataOffset;
        if (offset <= 0) return false; // not a composition tile

        try {
            var bytes = provider.SaveAsset(umapPath);
            var ar = new CUE4Parse.UE4.Assets.Readers.FAssetArchive(
                new CUE4Parse.UE4.Readers.FByteArchive(umapPath, bytes, provider.Versions), package);
            ar.Position = offset;

            var info = new CUE4Parse.UE4.Assets.Exports.Engine.FWorldTileInfo(ar);
            Console.WriteLine(withPath
                ? $"{umapPath}	{info.Position.X},{info.Position.Y},{info.Position.Z}"
                : $"{info.Position.X},{info.Position.Y},{info.Position.Z}");
            return true;
        } catch (Exception ex) {
            Console.Error.WriteLine($"  !! {umapPath}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
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
    /// <summary>
    ///     `ClassName&lt;TAB&gt;SuperName` for every Blueprint class under a path prefix, in ONE mount.
    ///
    ///     Exists because a cooked asset only stores properties that DIFFER from its archetype, so
    ///     "the property is absent" means "ask my parent", not "it is unset" - and a per-asset query
    ///     cannot answer that without re-mounting 690k files each time. The case that forced it:
    ///     GA_Athena_FragGrenade_WithTrajectory_C carries no ReplicationPolicy at all, and the
    ///     ReplicateYes that decides whether a grenade can be thrown lives two classes up, on
    ///     GA_Athena_Grenade_WithTrajectory_C. Joining this against a MapActorDump props dump
    ///     resolves any inherited property without a second pak pass.
    /// </summary>
    private static int Supers(DefaultFileProvider provider, string[] args) {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pakreader supers <pathPrefix>"); return 2; }

        var prefix = args[1];
        var printed = 0;
        var failed = 0;

        foreach (var path in provider.Files.Keys) {
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.Contains(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            // A package that fails to load is not fatal here - this walks whole directories, and one
            // unreadable asset should not cost the other few hundred.
            try {
                foreach (var export in provider.LoadPackage(path).GetExports()) {
                    if (export is not UStruct { SuperStruct.IsNull: false } str) continue;
                    Console.WriteLine($"{export.Name}\t{str.SuperStruct.ResolvedObject?.Name.Text ?? "?"}");
                    printed++;
                }
            } catch (Exception ex) {
                failed++;
                Console.Error.WriteLine($"pakreader supers: {path}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.Error.WriteLine($"pakreader: {printed} class(es) with a super, {failed} package(s) unreadable");
        return 0;
    }

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
