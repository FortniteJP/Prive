using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;

// Bakes real Athena ground heights into the THM1 grid file that AFortOnlineBeacon's
// AFortOnlineBeacon/Net/TerrainHeightMap.cs reads at runtime - the offline half of the "is this
// building piece touching real ground" question BuildingStructuralSupportSystem's IsSupportedByWorld
// needs (see that class's doc comment). This is the counterpart to Tools/MapActorDump: same provider
// setup, same "scan every sublevel .umap under the Maps directory" approach (Athena's landscape, like
// its placed actors, lives in the ~19 LevelStreamingAlwaysLoaded sublevels, not in the top-level
// Athena_Terrain.umap itself), extended to landscape geometry instead of actor placements.
//
// WHERE THE HEIGHT DATA ACTUALLY COMES FROM - this took a wrong turn worth recording so nobody
// re-walks it:
//
// The obvious source is a ULandscapeHeightfieldCollisionComponent's CollisionHeightData - a raw,
// uncompressed uint16 grid built specifically for physics/collision (see the engine's own
// LandscapeDataAccess.h comment). It is NOT available here. Per UE 4.23 source
// (Engine/Source/Runtime/Landscape/Classes/LandscapeHeightfieldCollisionComponent.h line ~124,
// "The collision height values. Stripped from cooked content") CollisionHeightData is
// WITH_EDITORONLY_DATA. LandscapeCollision.cpp's Serialize (~line 1403-1456) confirms exactly what
// ships instead: a cooked build serializes bCooked=true and then only CookedCollisionData - an
// opaque PhysX-cooked binary blob, not a height grid. CUE4Parse's own
// ULandscapeHeightfieldCollisionComponent.Deserialize (checked directly against the source at
// PriveDev/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Component/Landscape/
// ULandscapeHeightfieldCollisionComponent.cs) knows this and does not even try: for a cooked asset it
// calls Ar.SkipBulkArrayData() over CookedCollisionData and exposes nothing. There is no property, no
// bulk array, nothing to read for this component in a shipped pak.
//
// What DOES ship is the RENDER heightmap - a plain UTexture2D referenced by
// ULandscapeComponent.GetHeightmap() - and, unlike almost every other texture in the game, it is not
// BCn-compressed. ALandscapeProxy::CreateLandscapeTexture's bCompress parameter defaults to false
// (LandscapeProxy.h ~line 950) and every call site that builds a *height* map (LandscapeEdit.cpp
// ~2289 and ~5153) uses that default - compressing height data would introduce exactly the kind of
// block artifacts a heightfield can't tolerate. So the texture is stored raw BGRA8, and the same
// per-texel encoding the collision component would have used applies to it directly (see
// LandscapeDataAccess.h): a texel's 16-bit height is (R &lt;&lt; 8) | G, and
// LocalHeight = (Height - 32768) / 128. This tool decodes that texture (via CUE4Parse-Conversion's
// UTexture2D.Decode(), which no-ops for an already-uncompressed format like this one) instead of
// trying to read collision data that was never shipped.
//
// WORLD-SPACE PLACEMENT: LandscapeDataAccess.cpp's FLandscapeComponentDataInterface confirms the
// per-vertex formula given a component's local heightmap block:
//     LocalPos  = (LocalX * ScaleFactor, LocalY * ScaleFactor, LocalHeight(Height))
//     WorldPos  = Component->GetComponentTransform().TransformPosition(LocalPos)
// where ScaleFactor = ComponentSizeQuads / (ComponentSizeVerts - 1), which is always exactly 1 at mip
// 0 (ComponentSizeVerts is defined as ComponentSizeQuads + 1). GetComponentTransform() is just the
// component's placement composed with its owning actor's placement (and that sublevel's own
// LevelTransform, the same offset Tools/MapActorDump prints but does not apply) - RelativeLocation/
// RelativeRotation/RelativeScale3D at each level, read the same generic way MapActorDump's own
// LocationOf() reads RelativeLocation off an arbitrary actor/component. FRotator -> FQuat and
// FQuat.RotateVector are used verbatim from CUE4Parse's own math types (PriveDev/CUE4Parse/CUE4Parse/
// UE4/Objects/Core/Math/{FRotator,FQuat}.cs) rather than reimplemented, since both already exist there.
//
// Which texel block within a (possibly shared/atlased) heightmap texture belongs to a given
// component is HeightmapScaleBias (ZW = the texel offset as a fraction of the texture's full size);
// LandscapeDataAccess.cpp's GetHeightmapTextureData confirms the block is copied as a flat
// contiguous (SubsectionSizeQuads+1)*NumSubsections square with NO per-subsection remapping needed
// for a whole-component read, only the LOCAL QUAD coordinate a given texel represents needs the
// subsection correction (adjacent subsections share a border vertex, so the texel grid is one column/
// row larger per subsection than the actual distinct vertex count).
//
// ASSUMPTION, not verified against real data: FLandscapeComponentDataInterface::GetXYOffset (an
// optional secondary texture landscapes rarely use, for overhangs/caves) is ignored - LocalPos's X/Y
// is taken as exactly (LocalX, LocalY) with no XY offset. Fortnite's Athena terrain is not known to
// use this feature; if a future map does, affected columns would read as a fixed few units off in X/Y,
// not in height, and IsSupportedByWorld only cares about height.

if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase)) {
    return RunSelfTest(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "thm1_selftest.bin"));
}

// CUE4Parse has no logger wired up unless the caller sets one (see CUE4Parse.Example/Unpacker.cs) -
// without this, a real per-export parse failure (like the VirtualTextures one below, or any other
// asset CUE4Parse can't fully read) is caught internally and silently leaves that object half-built,
// with nothing printed anywhere. Warning-level rather than Debug: Debug is a firehose (every single
// bulk-data load path taken, for every asset) that was only worth it while actively bisecting the
// "every heightmap decodes to nothing" bug below - set the env var TERRAIN_BAKER_DEBUG_LOG=1 to get
// that firehose back if a future map/version needs the same kind of bisection.
{
    var minLevel = Environment.GetEnvironmentVariable("TERRAIN_BAKER_DEBUG_LOG") == "1"
        ? Serilog.Events.LogEventLevel.Debug
        : Serilog.Events.LogEventLevel.Warning;
    var loggerConfiguration = new Serilog.LoggerConfiguration().MinimumLevel.Is(minLevel);
    Serilog.ConsoleLoggerConfigurationExtensions.Console(loggerConfiguration.WriteTo);
    Serilog.Log.Logger = loggerConfiguration.CreateLogger();
    CUE4Parse.CUE4ParseLog.UseLogger(Serilog.Log.Logger);
}

if (args.Length < 3) {
    Console.WriteLine("Usage: TerrainHeightMapBaker <PaksDirectory> <AesKeyHex> <OutputPath> [MapPrefix] [CellSize] [EGame]");
    Console.WriteLine("       TerrainHeightMapBaker --selftest [TempOutputPath]");
    Console.WriteLine();
    Console.WriteLine("  OutputPath  where to write the THM1 file. Copy it next to the AFortOnlineBeacon");
    Console.WriteLine("              server binary as TerrainHeightMap.bin, or point the server at it with");
    Console.WriteLine("              the TERRAIN_HEIGHTMAP environment variable - see AFortOnlineBeacon/Net/");
    Console.WriteLine("              TerrainHeightMap.cs for exactly how it is looked up.");
    Console.WriteLine("  MapPrefix   default \"FortniteGame/Content/Athena/Maps\" - every .umap under this");
    Console.WriteLine("              prefix is scanned for landscape components, the same sublevel-scanning");
    Console.WriteLine("              approach Tools/MapActorDump uses for placed actors.");
    Console.WriteLine("  CellSize    world units per output grid cell; default 0 auto-detects it from the");
    Console.WriteLine("              first landscape component found (one grid cell per landscape quad).");
    Console.WriteLine("              A larger value downsamples (nearest-sample-wins per cell, not averaged).");
    Console.WriteLine("  EGame       default GAME_UE4_23");
    Console.WriteLine("  --meshes    also raise the grid over PLACED STATIC MESHES - the warmup island, POI");
    Console.WriteLine("              floors, bridges and rocks, none of which are landscape and none of which");
    Console.WriteLine("              the server could otherwise stand anything on. Costs a second scan.");
    Console.WriteLine("  --walls     also bake WALL faces (near-vertical triangles) into <output>.walls.bin,");
    Console.WriteLine("              so the server can stop projectiles going through a POI's walls. Needs");
    Console.WriteLine("              --meshes, since it reads the same triangles.");
    Console.WriteLine("  --hulls     bake the meshes' OWN simple collision (convex hulls, boxes, spheres,");
    Console.WriteLine("              capsules) plus a placement list into <output>.hulls.bin - the exact");
    Console.WriteLine("              shapes the client uses, instead of a voxel approximation. Needs --meshes.");
    Console.WriteLine("  --wall-cell N    world units per WALL grid cell (default 32). A wall is as thick");
    Console.WriteLine("              as a cell, so this is the wall's collision thickness; the height grid");
    Console.WriteLine("              keeps its own, coarser CellSize.");
    Console.WriteLine("  --wall-normal-z N  how horizontal a face's normal must be to count as a wall");
    Console.WriteLine("              (default 0.5 = 60 degrees from horizontal).");
    Console.WriteLine("  --mesh-aspect N  how much taller than wide a mesh may be and still count as ground");
    Console.WriteLine("              (default 1). Guards against putting \"the ground\" at treetop height.");
    Console.WriteLine();
    Console.WriteLine("  --selftest  round-trips a synthetic grid through this tool's own THM1 writer and");
    Console.WriteLine("              a copy of TerrainHeightMap.cs's reader, with no PAK/AES needed - the");
    Console.WriteLine("              one part of this tool verifiable without real game data.");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("""  TerrainHeightMapBaker "C:\Fortnite\FortniteGame\Content\Paks" 0x3ff2... TerrainHeightMap.bin""");
    return 1;
}

var paksDir = args[0];
var aesKey = args[1];
var outputPath = args[2];
var mapPrefix = args.Length > 3 ? args[3] : "FortniteGame/Content/Athena/Maps";
var cellSizeOverride = args.Length > 4 ? float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 0f;
var gameVersion = args.Length > 5 ? Enum.Parse<EGame>(args[5]) : EGame.GAME_UE4_23;

// Placed meshes are OPT-IN so an existing bake can be reproduced byte for byte, and so the far more
// expensive second scan is only paid for when it is wanted. See Phase 3.
var bakeMeshes = args.Contains("--meshes", StringComparer.OrdinalIgnoreCase);
// Walls ride along with --meshes (they come from the same triangles and the same scan) but are
// their own opt-in, because they are a separate output file and a separate server capability.
var bakeWalls = args.Contains("--walls", StringComparer.OrdinalIgnoreCase);

// THE GAME'S OWN COLLISION, rather than an approximation of it. Measured across three POIs, 86-96% of
// placed meshes carry SIMPLE collision - a handful of convex hulls - and not one of them sets a trace
// flag, so they are all CTF_UseDefault and a projectile sweep hits those hulls, not the render mesh.
// Athena_POI_Lobby_004 is 597 primitives against 202,943 render triangles: 340x less data describing
// the shape the CLIENT actually uses. Everything else in this file approximates; this is exact.
var bakeHulls = args.Contains("--hulls", StringComparer.OrdinalIgnoreCase);
var wallNormalZ = 0.5f;
var wallZStep = 32f;

// WALLS GET THEIR OWN, FINER GRID. A cell is solid or it is not, so a wall's collision is as thick as
// a cell - and at the height field's 100 units that is a wall two to five times thicker than the real
// thing, which shows up as an archway whose edges grab a projectile half a metre before it reaches
// them. 32 is close to a real Fortnite wall panel. The wall file carries its own CellSize/Width/Height
// in its header, so this costs nothing anywhere else.
var wallCellSize = 32f;
var meshAspect = 1f;
var meshMaxSpan = 2048f;
float? probeX = null, probeY = null;
for (var i = 0; i < args.Length - 2; i++)
    if (string.Equals(args[i], "--probe", StringComparison.OrdinalIgnoreCase)) {
        probeX = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
        probeY = float.Parse(args[i + 2], System.Globalization.CultureInfo.InvariantCulture);
    }
for (var i = 0; i < args.Length - 1; i++)
    if (string.Equals(args[i], "--wall-cell", StringComparison.OrdinalIgnoreCase))
        wallCellSize = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
for (var i = 0; i < args.Length - 1; i++)
    if (string.Equals(args[i], "--wall-normal-z", StringComparison.OrdinalIgnoreCase))
        wallNormalZ = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
for (var i = 0; i < args.Length - 1; i++)
    if (string.Equals(args[i], "--mesh-aspect", StringComparison.OrdinalIgnoreCase))
        meshAspect = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
for (var i = 0; i < args.Length - 1; i++)
    if (string.Equals(args[i], "--mesh-max-span", StringComparison.OrdinalIgnoreCase))
        meshMaxSpan = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);

if (!Directory.Exists(paksDir)) {
    Console.WriteLine($"PAK directory not found: {paksDir}");
    return 1;
}

// Neither Tools/MapActorDump nor an earlier version of this tool called these, and it didn't matter
// there - ordinary UPROPERTY reads decode fine either way. It DOES matter here: a landscape's
// heightmap mip is Oodle-compressed bulk data (see CUE4Parse.Example/Unpacker.cs for the same two
// calls, in the same order, before DefaultFileProvider.Initialize()), and without an initialized
// codec CUE4Parse.Compression.OodleHelper/ZlibHelper silently leave FTexture2DMipMap.BulkData.Data
// empty rather than throwing - which is exactly why every component below was logging "heightmap
// decoded to nothing" (UTexture.GetFirstMipIndex found no mip with EnsureValidBulkData==true and
// returned -1) on the first real run against this project's actual PAK set. OodleHelper.Initialize
// downloads the Oodle DLL from GitHub if none is given and none of CUE4Parse-Natives' bundled
// libraries cover it - point it at a copy this repo already has (PacketReplayDecoder needs the same
// codec for a different reason) so this works offline.
CUE4Parse.Compression.ZlibHelper.Initialize();
CUE4Parse.Compression.OodleHelper.Initialize(FindOodleDll());

// ROOT CAUSE of "every heightmap texture decodes to nothing" (found by bisecting a real run against
// Fortnite 10.40's actual paks - see PriveDev/CUE4Parse/CUE4Parse/UE4/Versions/VersionContainer.cs
// line 86, `Options["VirtualTextures"] = Game >= GAME_UE4_23`): CUE4Parse gates whether a texture's
// serialized FTexturePlatformData ends with an extra "is this a virtual texture" bool purely on
// engine version >= UE4.23, because that's when Virtual Texturing was ADDED as an engine capability.
// But Fortnite 10.40 predates the exact point in the 4.23 dev cycle where Epic's branch actually
// started WRITING that bool for every texture (VT wasn't used by any Fortnite content yet at 10.40) -
// so this flag being true here makes CUE4Parse read one extra bool that was never written, landing on
// garbage bytes belonging to the NEXT export and throwing ("Invalid bool value (501)", confirmed via
// a locally patched CUE4Parse build with position tracing - every single mip read perfectly correctly
// right up to that point). CUE4Parse swallows the per-export exception and moves on, leaving that
// texture's PlatformData at its all-default, zero-mips value - which is exactly what made
// GetFirstMipIndex() return -1 and Decode() return null for every landscape heightmap texture.
// optionOverrides is VersionContainer's own supported mechanism for exactly this kind of per-game
// correction (see its constructor/InitOptions) - no CUE4Parse source patch needed. Confirmed fixed
// against real 10.40 data: 0 texture-read errors, real mip pixel data, plausible height ranges.
var versionOverrides = new Dictionary<string, bool> { ["VirtualTextures"] = false };
var provider = new DefaultFileProvider(paksDir, SearchOption.TopDirectoryOnly,
    new VersionContainer(gameVersion, optionOverrides: versionOverrides));
provider.Initialize();

foreach (var vfs in provider.UnloadedVfs.ToList()) {
    provider.SubmitKey(vfs.EncryptionKeyGuid, new FAesKey(aesKey));
}

Console.WriteLine($"Mounted files: {provider.Files.Count}");

// A SECOND BAKE OUT OF THE SAME PROVIDER: the collision shapes of the PLAYER BUILDING pieces.
//
// It lives here rather than in Tools/PakReader for one reason - BuildHulls, at the bottom of this
// file, is the routine that turns an asset's convex vertex soup into half-space planes, and it is
// not worth having twice. Everything else about the two bakes differs: this one reads Blueprint
// CDOs rather than levels, and emits a per-CLASS table rather than a placed world.
if (args.Length > 3 && args[3].Equals("--buildpieces", StringComparison.OrdinalIgnoreCase)) {
    return BakeBuildPieces(provider, outputPath, args.Length > 4 ? args[4] : "FortniteGame/Content/Building/ActorBlueprints/Player/");
}

// A composed world-space placement: translation + rotation + per-axis scale, no shear. Every level
// of the hierarchy (sublevel LevelTransform, actor placement, component's own RelativeLocation) is
// exactly this shape, so the whole chain is just repeated TransformPosition/Compose calls. Collected
// once here (Phase 1) and reused for both the bounds pass and the texture-decode pass (Phase 2) below,
// so no sublevel is ever loaded or walked twice.
var samples = new List<(ULandscapeComponent Comp, Placement World)>();

// --- Phase 0: sublevel world offsets -----------------------------------------------------------
// Mirrors Tools/MapActorDump's own LevelStreaming handling, but actually CAPTURES the offset instead
// of only printing it - MapActorDump's prefix-scan mode never applies it, on the observed assumption
// that Athena's AlwaysLoaded sublevels carry an identity LevelTransform (an absent property means
// identity, since cooked packages only serialize non-default properties). We do better here since
// landscape height correctness matters more than a spawn point being off by a translate: if the top
// map is findable we read the real offset per sublevel; if a sublevel isn't listed there, or the top
// map itself can't be found, we fall back to that same identity assumption and say so.
var levelTranslations = new Dictionary<string, Placement>(StringComparer.OrdinalIgnoreCase);
const string topLevelMap = "FortniteGame/Content/Athena/Maps/Athena_Terrain.umap";

if (provider.Files.ContainsKey(topLevelMap)) {
    try {
        foreach (var export in provider.LoadPackageObjects(topLevelMap)) {
            if (!export.ExportType.Contains("LevelStreaming", StringComparison.Ordinal)) continue;

            var packagePath = export.TryGetValue(out FSoftObjectPath worldAsset, "WorldAsset")
                ? NormalizeAssetPath(worldAsset.AssetPathName.Text)
                : NormalizeAssetPath(export.GetOrDefault<FName>("PackageNameToLoad").Text);
            if (packagePath.Length == 0) continue;

            var placement = export.TryGetValue(out FStructFallback levelTransform, "LevelTransform")
                ? ReadPlacement(levelTransform)
                : Placement.Identity;

            levelTranslations[packagePath] = placement;
        }

        Console.WriteLine($"Top-level map {topLevelMap}: {levelTranslations.Count} sublevel offset(s) captured.");
    } catch (Exception ex) {
        Console.WriteLine($"  !! failed to read {topLevelMap} for sublevel offsets ({ex.GetType().Name}: " +
                          $"{ex.Message}) - every sublevel will be assumed to sit at an identity offset.");
    }
} else {
    Console.WriteLine($"Top-level map {topLevelMap} not found under this mount - every sublevel will be " +
                      "assumed to sit at an identity offset (see the Phase 0 comment above for why that's " +
                      "usually correct for Athena's AlwaysLoaded sublevels).");
}

// --- Phase 1: find every landscape component and its world transform (cheap - no texture decode) ---
var sublevelPaths = provider.Files.Keys
    .Where(f => f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) &&
                f.StartsWith(mapPrefix, StringComparison.OrdinalIgnoreCase))
    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
    .ToList();

if (sublevelPaths.Count == 0) {
    Console.WriteLine($"No .umap found under '{mapPrefix}' - nothing to bake.");
    return 1;
}

// LEVEL FOUNDATIONS - how a POI, and the warmup island, are actually placed.
//
// The sublevel offsets captured from the top-level map cover LevelStreaming entries only. Athena's
// POIs are not streamed that way: an LF_* Blueprint actor sitting in some sublevel names ANOTHER
// sublevel in AdditionalWorlds and places it at the foundation's own transform. Without this, every
// mesh inside a POI is baked at its AUTHORED origin instead of where the POI stands - which is
// exactly why the first triangle bake produced ground at the warmup island's foundation and none at
// all where the player actually spawns on it.
//
// FOUNDATIONS NEST (a POI foundation places a building-kit foundation), so the map is resolved to a
// fixed point rather than in one pass - the same nesting Tools/MapActorDump had to walk to find loot
// spawners inside buildings.
// ONE SUBLEVEL, MANY PLACEMENTS. Keyed to a single placement this silently dropped 95 of the 313
// foundations: Athena builds its POIs out of shared kit sublevels (one shack umap standing in forty
// places), and the last foundation to be resolved won. Every distinct placement is kept, and the
// sublevel's meshes are baked once per placement.
var foundationPlacements = new Dictionary<string, List<Placement>>(StringComparer.OrdinalIgnoreCase);

if (bakeMeshes) {
    var rawFoundations = new List<(string Streamed, string Host, Placement Local)>();

    foreach (var path in sublevelPaths) {
        List<UObject> exports;
        try { exports = provider.LoadPackageObjects(path).ToList(); } catch { continue; }

        var host = path[..path.LastIndexOf('.')];

        foreach (var export in exports) {
            if (!export.ExportType.StartsWith("LF_", StringComparison.OrdinalIgnoreCase)) continue;

            var worlds = export.GetOrDefault<FSoftObjectPath[]?>("AdditionalWorlds", null);
            if (worlds is not { Length: > 0 }) continue;

            var streamed = worlds[0].AssetPathName.Text;
            if (string.IsNullOrEmpty(streamed)) continue;

            // "/Game/..." as the package paths in sublevelPaths are spelled.
            if (streamed.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
                streamed = "FortniteGame/Content/" + streamed["/Game/".Length..];
            var dot = streamed.LastIndexOf('.');
            if (dot >= 0) streamed = streamed[..dot];

            rawFoundations.Add((streamed, host, GetActorPlacement(export, exports)));
        }
    }

    // Resolve to a fixed point: a foundation inside an already-placed sublevel inherits that
    // placement, and a sublevel placed in five different POIs ends up with five placements.
    //
    // PER-SUBLEVEL CAP because this graph is data, not something this tool controls: a kit sublevel
    // that (directly or through a chain) contains a foundation naming an ancestor would otherwise
    // multiply placements every pass. The cap bounds the work and says so out loud rather than
    // hanging.
    const int maxPlacementsPerSublevel = 256;
    var placementsCapped = 0;

    for (var pass = 0; pass < 8; pass++) {
        var changed = false;

        foreach (var (streamed, host, local) in rawFoundations) {
            // A SNAPSHOT: the host's list can be the very list being appended to when a sublevel
            // nests into itself, and enumerating a list while it grows throws.
            var hostPlacements = foundationPlacements.TryGetValue(host, out var hp)
                ? hp.ToList()
                : new List<Placement> { levelTranslations.GetValueOrDefault(host, Placement.Identity) };

            if (!foundationPlacements.TryGetValue(streamed, out var list))
                list = foundationPlacements[streamed] = new List<Placement>();

            foreach (var hostPlacement in hostPlacements) {
                var world = Placement.Compose(hostPlacement, local);

                // Same spot, already known - compared at whole-unit resolution because the same
                // placement reached through two different chains differs in the last float bit.
                if (list.Any(existing => MathF.Abs(existing.Translation.X - world.Translation.X) < 1f &&
                                         MathF.Abs(existing.Translation.Y - world.Translation.Y) < 1f &&
                                         MathF.Abs(existing.Translation.Z - world.Translation.Z) < 1f))
                    continue;

                if (list.Count >= maxPlacementsPerSublevel) { placementsCapped++; continue; }

                list.Add(world);
                changed = true;
            }
        }

        if (!changed) break;
    }

    var totalPlacements = foundationPlacements.Values.Sum(v => v.Count);
    Console.WriteLine($"{rawFoundations.Count} level foundation(s) placing {foundationPlacements.Count} " +
                      $"sublevel(s) at {totalPlacements} placement(s)" +
                      (placementsCapped > 0 ? $" ({placementsCapped} dropped at the {maxPlacementsPerSublevel} cap)" : "") + ".");
}

Console.WriteLine($"Scanning {sublevelPaths.Count} .umap under '{mapPrefix}' for landscape components...");

var landscapeActorsFound = 0;
var componentsSkipped = 0;

foreach (var path in sublevelPaths) {
    List<UObject> exports;
    try {
        exports = provider.LoadPackageObjects(path).ToList();
    } catch (Exception ex) {
        Console.WriteLine($"  !! {path}: {ex.GetType().Name}: {ex.Message}");
        continue;
    }

    var basePath = path[..path.LastIndexOf('.')];
    // Same lookup the mesh pass uses, and for the same reason: a sublevel placed by a level
    // foundation appears in no LevelStreaming list, so without this its landscape is baked at the
    // authored origin. A sublevel standing in several places contributes its landscape several times.
    var levelPlacements = foundationPlacements.TryGetValue(basePath, out var foundedLevel)
        ? foundedLevel
        : new List<Placement> { levelTranslations.GetValueOrDefault(basePath, Placement.Identity) };

    foreach (var actor in exports) {
        if (actor is not ALandscapeProxy landscape) continue;
        landscapeActorsFound++;
        if (foundedLevel != null)
            Console.WriteLine($"  landscape in a foundation-placed sublevel: {basePath} " +
                              $"({levelPlacements.Count} placement(s))");

        var actorPlacement = GetActorPlacement(landscape, exports);

        foreach (var levelPlacement in levelPlacements) {
        var worldPlacement = Placement.Compose(levelPlacement, actorPlacement);

        // ALandscapeProxy.LandscapeComponents is a real strongly-typed CUE4Parse field (not just a
        // generic property), per PriveDev/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Actor/ALandscape.cs
        // (which despite the filename is where ALandscapeProxy/ALandscape/ALandscapeStreamingProxy
        // all live).
        foreach (var index in landscape.LandscapeComponents) {
            if (index.Load() is not ULandscapeComponent comp) { componentsSkipped++; continue; }

            var compPlacement = GetComponentPlacement(comp);
            var finalPlacement = Placement.Compose(worldPlacement, compPlacement);
            samples.Add((comp, finalPlacement));
        }
        }
    }
}

// DOES THIS LANDSCAPE HAVE HOLES IN IT? A mine shaft, a cave mouth or a doorway cut into terrain is
// a landscape HOLE - painted into the visibility weightmap layer - and a height field that ignores it
// bakes a lid over the hole, which is exactly what a grenade bouncing off the top of a mine shaft
// looks like. Before writing a decoder for that layer it is worth knowing whether Athena uses one at
// all, so --layers prints the distinct weightmap layer names and stops.
if (args.Contains("--layers", StringComparer.OrdinalIgnoreCase)) {
    var layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    foreach (var (comp, _) in samples)
        foreach (var allocation in comp.WeightmapLayerAllocations) {
            var name = allocation.LayerInfo.Name;
            if (string.IsNullOrEmpty(name)) name = "<unnamed>";
            layerCounts[name] = layerCounts.GetValueOrDefault(name) + 1;
        }

    Console.WriteLine($"{samples.Count} landscape component(s), {layerCounts.Count} distinct weightmap layer(s):");
    foreach (var (name, count) in layerCounts.OrderByDescending(l => l.Value))
        Console.WriteLine($"  {count,6}  {name}");

    return 0;
}

Console.WriteLine($"{landscapeActorsFound} landscape actor(s), {samples.Count} component(s) " +
                  $"({componentsSkipped} unresolved LandscapeComponents index(es) skipped).");

if (samples.Count == 0) {
    Console.WriteLine("No landscape components found - nothing to bake.");
    return 1;
}

// --- Determine grid bounds and cell size from geometry alone (still no texture decode) ---
var minX = float.MaxValue;
var minY = float.MaxValue;
var maxX = float.MinValue;
var maxY = float.MinValue;
float? autoCellSize = null;

foreach (var (comp, placement) in samples) {
    var csq = comp.ComponentSizeQuads;
    foreach (var corner in new[] { (0, 0), (csq, 0), (0, csq), (csq, csq) }) {
        var world = placement.TransformPosition(new FVector(corner.Item1, corner.Item2, 0));
        minX = MathF.Min(minX, world.X); maxX = MathF.Max(maxX, world.X);
        minY = MathF.Min(minY, world.Y); maxY = MathF.Max(maxY, world.Y);
    }

    // One landscape quad's world size along X, from this component's own composed scale - used to
    // auto-size the grid at "one cell per quad" when the caller doesn't override CellSize.
    if (autoCellSize is null && MathF.Abs(placement.Scale.X) > 0.0001f) autoCellSize = MathF.Abs(placement.Scale.X);
}

var cellSize = cellSizeOverride > 0 ? cellSizeOverride : autoCellSize ?? 1f;
if (cellSizeOverride <= 0 && autoCellSize is null) {
    Console.WriteLine("warning: could not auto-detect a cell size from any component's transform " +
                      "(zero/unreadable scale) - falling back to 1 world unit per cell.");
}

var width = (int) MathF.Ceiling((maxX - minX) / cellSize) + 1;
var height = (int) MathF.Ceiling((maxY - minY) / cellSize) + 1;

if ((long) width * height > 300_000_000L) {
    Console.WriteLine($"Computed grid is {width}x{height} = {(long) width * height:N0} cells, which is " +
                      "unreasonably large (probably a bad CellSize) - refusing to allocate it. Pass an " +
                      "explicit, larger CellSize.");
    return 1;
}

Console.WriteLine($"Grid: {width}x{height} cells, {cellSize:F2} units/cell, " +
                  $"origin=({minX:F0},{minY:F0}), bounds X[{minX:F0}..{maxX:F0}] Y[{minY:F0}..{maxY:F0}]");

var heights = new short[(long) width * height];
Array.Fill(heights, short.MinValue); // THM1's "no data" sentinel

// --- Phase 2: decode each component's heightmap texture and rasterize into the grid ---
var componentsRasterized = 0;
var componentsFailed = 0;
var rangeWarned = false;

foreach (var (comp, placement) in samples) {
    var texture = comp.GetHeightmap();
    if (texture == null) {
        Console.WriteLine($"  skip: component at SectionBase=({comp.SectionBaseX},{comp.SectionBaseY}) has no heightmap texture");
        componentsFailed++;
        continue;
    }

    CTexture? decoded;
    try {
        decoded = texture.Decode();
    } catch (Exception ex) {
        Console.WriteLine($"  skip: component at SectionBase=({comp.SectionBaseX},{comp.SectionBaseY}) - " +
                          $"failed to decode heightmap '{texture.Name}': {ex.GetType().Name}: {ex.Message}");
        componentsFailed++;
        continue;
    }

    if (decoded == null) {
        Console.WriteLine($"  skip: component at SectionBase=({comp.SectionBaseX},{comp.SectionBaseY}) - " +
                          $"heightmap '{texture.Name}' decoded to nothing");
        componentsFailed++;
        continue;
    }

    // Channel order depends on the decoded pixel format - see the header comment for why this is
    // expected to always be PF_B8G8R8A8 (raw, uncompressed) for a landscape heightmap specifically.
    // Handled defensively rather than assumed: PF_R8G8B8A8 is the only other layout any decode path
    // in CUE4Parse-Conversion's TextureDecoder ever normalizes uncompressed-ish output to.
    int rOffset, gOffset;
    switch (decoded.PixelFormat) {
        case EPixelFormat.PF_B8G8R8A8: rOffset = 2; gOffset = 1; break;
        case EPixelFormat.PF_R8G8B8A8: rOffset = 0; gOffset = 1; break;
        default:
            Console.WriteLine($"  skip: component at SectionBase=({comp.SectionBaseX},{comp.SectionBaseY}) - " +
                              $"unexpected decoded pixel format {decoded.PixelFormat} (expected an uncompressed " +
                              "BGRA/RGBA heightmap - see the CreateLandscapeTexture bCompress=false evidence " +
                              "in the header comment)");
            componentsFailed++;
            continue;
    }

    var subsectionSizeVerts = comp.SubsectionSizeQuads + 1;
    var heightmapSize = subsectionSizeVerts * comp.NumSubsections;

    var offsetX = (int) MathF.Round(decoded.Width * comp.HeightmapScaleBias.Z);
    var offsetY = (int) MathF.Round(decoded.Height * comp.HeightmapScaleBias.W);

    if (offsetX < 0 || offsetY < 0 || offsetX + heightmapSize > decoded.Width || offsetY + heightmapSize > decoded.Height) {
        Console.WriteLine($"  skip: component at SectionBase=({comp.SectionBaseX},{comp.SectionBaseY}) - " +
                          $"computed texel block [{offsetX},{offsetY}]+{heightmapSize} doesn't fit the " +
                          $"{decoded.Width}x{decoded.Height} heightmap '{texture.Name}' (bad HeightmapScaleBias?)");
        componentsFailed++;
        continue;
    }

    for (var ty = 0; ty < heightmapSize; ty++) {
        var subNumY = ty / subsectionSizeVerts;
        var subY = ty % subsectionSizeVerts;
        var localQuadY = subNumY * comp.SubsectionSizeQuads + subY;

        for (var tx = 0; tx < heightmapSize; tx++) {
            var subNumX = tx / subsectionSizeVerts;
            var subX = tx % subsectionSizeVerts;
            var localQuadX = subNumX * comp.SubsectionSizeQuads + subX;

            var texelIndex = ((offsetY + ty) * decoded.Width + (offsetX + tx)) * 4;
            var rawHeight = (decoded.Data[texelIndex + rOffset] << 8) | decoded.Data[texelIndex + gOffset];
            var localHeight = (rawHeight - 32768) * (1f / 128f); // LANDSCAPE_ZSCALE, LandscapeDataAccess.h

            var world = placement.TransformPosition(new FVector(localQuadX, localQuadY, localHeight));

            var cx = (int) MathF.Round((world.X - minX) / cellSize);
            var cy = (int) MathF.Round((world.Y - minY) / cellSize);
            if (cx < 0 || cy < 0 || cx >= width || cy >= height) continue; // shouldn't happen, bounds came from these same corners

            heights[(long) cy * width + cx] = ToGridHeight(world.Z, ref rangeWarned);
        }
    }

    componentsRasterized++;
}

Console.WriteLine($"Rasterized {componentsRasterized} component(s), {componentsFailed} failed/skipped.");

// --- Phase 3: raise the grid over PLACED STATIC MESHES -------------------------------------------
//
// WHY. The bake used to cover the LANDSCAPE ONLY, so as far as the server was concerned there was no
// ground under the warmup island, a POI floor, a bridge or a rock - all of which are placed meshes,
// not landscape. Projectiles fell straight through them, and the stand-in for that (an infinite plane
// at the thrower's own feet) is wrong the moment the ground is not flat or the thrower is not
// standing on it.
//
// WHAT IS USED: LOD0's TRIANGLES, transformed by the component's full world placement and rasterised
// into the grid. Bounding boxes were tried first and are not usable - the warmup island's own ground
// mesh has a box top at Z 9395 against a surface at ~3845, so a box is 5,500 units wrong for exactly
// the meshes that matter, and no aspect-ratio filter separates the two (a big tree is WIDER than it
// is tall). These meshes are CTF_UseComplexAsSimple: their real collision IS the render mesh, so the
// triangles are not an approximation of the collision - they are it.
//
// The remaining heuristics are stated where they are applied: a span cap for backdrops and sky, and a
// name filter for foliage.

// THE MESH PASS FILLS A SECOND GRID AS WELL AS RAISING THE FIRST, and that second grid is the one
// worth having. Merging meshes into the landscape was measured against 2,397 cells players had walked
// and it made the map WORSE where the landscape already knew the answer (worse in 106 cells, better
// in 39) while being much better where it did not (761 newly covered, 72% within 128 units): whatever
// prop stands in a cell becomes "the ground" there. Kept apart, the server can ask for the surface
// below a given height instead - a POI roof for something on the roof, the terrain for something
// beside the building. See AFortOnlineBeacon/Net/TerrainHeightMap.cs.
var meshHeights = new short[(long) width * height];
Array.Fill(meshHeights, short.MinValue);

// WALLS ARE THE TRIANGLES A HEIGHT FIELD THROWS AWAY. A grid holds one surface per cell, so a POI's
// walls simply do not exist to the server and grenades fly through them - and the cheap trick of
// treating "the baked surface is above me" as a wall was measured against walked ground and misfires
// in 13% of walkable cells, because indoors the ROOF is above the player.
//
// The fix is to separate walls from floors the way the geometry already does: by NORMAL. A triangle
// whose normal is close to horizontal is a wall face; a floor or a roof is not, whatever height it
// sits at. Those are exactly the triangles the height-field rasteriser skips (its barycentric
// denominator is zero for a face seen edge-on), so nothing here competes with it.
//
// Stored SPARSELY as Z spans per cell, because walls are a thin subset of the map - a dense grid of
// spans over 12M cells would be gigabytes for something that is mostly empty.
var wallSpans = new Dictionary<long, List<(short Min, short Max)>>();
var wallTriangles = 0L;

// The wall grid covers the same ground as the height grid, at its own resolution.
var wallWidth = (int) MathF.Ceiling(width * cellSize / wallCellSize) + 1;
var wallHeight = (int) MathF.Ceiling(height * cellSize / wallCellSize) + 1;

// THE SHAPE LIBRARY: each distinct mesh's collision hulls, stored once, in the mesh's own local
// space - exactly how the game stores it, and why this is small. Instances then reference a shape id
// and carry only a transform.
var hullShapes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var hullShapeData = new List<List<float[]>>();          // shape -> hull -> plane quadruples
var hullShapeBounds = new List<(FVector Min, FVector Max)>();
var hullInstances = new List<(int Shape, Placement Where)>();
var hullMeshesWithout = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

// Scratch for ONE triangle's per-cell Z ranges - see AddWall for why the range has to be per cell.
// Declared here rather than inside the helper because it is reused for every one of 36 million
// triangles and reallocating it that many times is the difference between minutes and hours.
var triangleCells = new Dictionary<long, (float Min, float Max)>();

// Set per component: whether this mesh's own collision hulls already describe it, in which case the
// voxel wall pass skips it. See the hull collection below.
var hullMeshCovered = false;

if (bakeMeshes) {
    Console.WriteLine($"Scanning {sublevelPaths.Count} .umap for placed static meshes...");

    var meshesConsidered = 0;
    var meshesRaised = 0;
    var meshesSkippedTall = 0;
    var meshesSkippedHuge = 0;
    var meshesSkippedFoliage = 0;
    var instancedComponents = 0;
    var biggestSkipped = new List<(float Span, string Name, float Top)>();
    // THE ONE CELL THE QUESTION IS ABOUT. Every other probe number is an aggregate, and an aggregate
    // cannot distinguish "no mesh covers the spawn" from "a mesh covers it but wrote a lower Z than
    // the landscape already had" from "the cell is outside the grid". This watches the exact cell.
    long probeCell = -1;
    if (probeX is { } pcx && probeY is { } pcy) {
        var pcxi = (int) MathF.Round((pcx - minX) / cellSize);
        var pcyi = (int) MathF.Round((pcy - minY) / cellSize);
        if (pcxi >= 0 && pcxi < width && pcyi >= 0 && pcyi < height) {
            probeCell = (long) pcyi * width + pcxi;
            var before = heights[probeCell];
            Console.WriteLine($"  probe cell [{pcxi},{pcyi}] before the mesh pass: " +
                              (before == short.MinValue ? "no data" : $"Z {before}"));
        } else {
            Console.WriteLine($"  probe ({pcx:F0},{pcy:F0}) is OUTSIDE the grid - nothing can cover it.");
        }
    }

    var meshesLoadFailed = 0;
    var meshesNoAsset = 0;
    var meshesNoRenderData = 0;
    var meshesNoTriangles = 0;
    var unreadableExamples = new List<string>();
    var cellsRaised = 0L;

    foreach (var path in sublevelPaths) {
        List<UObject> exports;
        try {
            exports = provider.LoadPackageObjects(path).ToList();
        } catch (Exception ex) {
            Console.WriteLine($"  !! {path}: {ex.GetType().Name}: {ex.Message}");
            continue;
        }

        var basePath = path[..path.LastIndexOf('.')];
        // A sublevel placed by a FOUNDATION is not in levelTranslations at all - see the foundation
        // pass above. Its placement wins when both exist, because a foundation is the more specific
        // statement about where this particular copy of the sublevel stands.
        var levelPlacements = foundationPlacements.TryGetValue(basePath, out var founded)
            ? founded
            : new List<Placement> { levelTranslations.GetValueOrDefault(basePath, Placement.Identity) };

        foreach (var export in exports) {
            if (export is not UStaticMeshComponent smc) continue;
            meshesConsidered++;

            // "UNREADABLE" WAS THREE DIFFERENT FAILURES sharing one counter, which is why 799 of them
            // could not be acted on: a mesh whose asset fails to load, a StaticMesh property that is
            // simply not set, and a mesh that loads but carries no cooked render data are three
            // separate problems with three separate fixes. Counted apart, with examples.
            UStaticMesh? mesh;
            try {
                mesh = smc.GetLoadedStaticMesh();
            } catch (Exception ex) {
                meshesLoadFailed++;
                Note(unreadableExamples, $"load threw {ex.GetType().Name}: {smc.Name}");
                continue;
            }

            if (mesh == null) {
                meshesNoAsset++;
                Note(unreadableExamples, $"no StaticMesh set: {smc.Name} in {basePath[(basePath.LastIndexOf('/') + 1)..]}");
                continue;
            }

            if (mesh.RenderData?.Bounds is not { } bounds) {
                meshesNoRenderData++;
                Note(unreadableExamples, $"no render data: {mesh.Name}");
                continue;
            }

            // The component's placement is relative to its OWNING ACTOR, which is itself placed in the
            // sublevel, which the level transform then offsets - the same three-step compose the
            // landscape pass does.
            // The component's owning ACTOR, found among this package's own exports - the same way the
            // landscape pass reaches an actor's root component. Outer is a ResolvedObject rather than
            // a loaded UObject, so it is matched by name instead of cast.
            var ownerName = smc.Outer?.Name.Text;
            var owner = ownerName != null ? exports.FirstOrDefault(e => e.Name == ownerName) : null;

            // DO NOT COMPOSE THE ROOT COMPONENT WITH ITSELF. GetActorPlacement returns the actor's
            // ROOT component's transform, and for the very common case where this mesh component IS
            // that root, composing it with its own relative transform applies the whole thing twice -
            // squaring the scale. It showed up as meshes spanning hundreds of millions of units
            // (SM_Auroras at Z 158,000,000), which is what made the first bake flatten the map: those
            // impossible boxes covered everything.
            var isRoot = owner != null && ReferenceEquals(RootComponentOf(owner, exports), smc);
            var actorPlacement = owner != null && !isRoot ? GetActorPlacement(owner, exports) : Placement.Identity;
            var componentLocal = Placement.Compose(actorPlacement, GetComponentPlacement(smc));

            // INSTANCED MESHES ARE MANY MESHES. A UInstancedStaticMeshComponent holds one mesh and a
            // transform PER INSTANCE, and Fortnite's POIs are built out of them - treating one as a
            // single placement collapses every instance onto the component's own origin, which is why
            // the first triangle bake covered the island's foundation and not the ground the player
            // was standing on a few thousand units away.
            var instanceData = smc is UInstancedStaticMeshComponent ism && ism.PerInstanceSMData is { Length: > 0 } perInstance
                ? perInstance
                : null;
            if (instanceData != null) instancedComponents++;

            var placements = new List<Placement>();
            foreach (var levelPlacement in levelPlacements) {
                var componentWorld = Placement.Compose(levelPlacement, componentLocal);

                if (instanceData == null) { placements.Add(componentWorld); continue; }

                foreach (var inst in instanceData) {
                    var t = inst.TransformData;
                    placements.Add(Placement.Compose(componentWorld,
                                                     new Placement(t.Translation, t.Rotation, t.Scale3D)));
                }
            }

            // RESET PER COMPONENT. Left over from the previous mesh, this would silently exclude an
            // arbitrary set of meshes from the voxel pass - the kind of state bug that shows up as
            // "some cliffs are solid and some are not" and is untraceable from the outside.
            hullMeshCovered = false;

            // COLLECTED BEFORE THE FILTERS BELOW, deliberately. The foliage and backdrop filters exist
            // because a height field cannot tell a tree's canopy from the ground; a hull has no such
            // problem - a tree's own collision hull IS its trunk, which is exactly what should stop a
            // grenade. A shape is only skipped when the mesh has no simple collision at all.
            if (bakeHulls) {
                if (!hullShapes.TryGetValue(mesh.Name, out var shapeId)) {
                    var built = BuildHulls(mesh);
                    if (built.Hulls.Count == 0) {
                        hullMeshesWithout.Add(mesh.Name);
                        shapeId = -1;
                    } else {
                        shapeId = hullShapeData.Count;
                        hullShapeData.Add(built.Hulls);
                        hullShapeBounds.Add(built.Bounds);
                    }

                    hullShapes[mesh.Name] = shapeId;
                }

                if (shapeId >= 0)
                    foreach (var world in placements)
                        hullInstances.Add((shapeId, world));

                // A MESH WITH HULLS DOES NOT NEED VOXELS. The two passes describe the same solid, and
                // the hull describes it exactly - keeping both means the coarse one decides first and
                // the archway rim gets its thickness back. What is left for the voxel pass is what
                // genuinely has no simple collision: the terrain shells, cliffs, cave mouths and
                // merged HLOD ground meshes, which in the game use COMPLEX collision, i.e. their
                // render triangles. That is exactly the geometry the voxel pass reads.
                hullMeshCovered = shapeId >= 0;
            }

            foreach (var world in placements) {

            // The eight corners of the local box, transformed, then an axis-aligned box around them.
            // Rotation is why the corners are transformed rather than the extent being scaled.
            float bMinX = float.MaxValue, bMinY = float.MaxValue, bMaxX = float.MinValue, bMaxY = float.MinValue;
            float bMinZ = float.MaxValue, bMaxZ = float.MinValue;

            for (var corner = 0; corner < 8; corner++) {
                var local = new FVector(
                    bounds.Origin.X + ((corner & 1) == 0 ? -bounds.BoxExtent.X : bounds.BoxExtent.X),
                    bounds.Origin.Y + ((corner & 2) == 0 ? -bounds.BoxExtent.Y : bounds.BoxExtent.Y),
                    bounds.Origin.Z + ((corner & 4) == 0 ? -bounds.BoxExtent.Z : bounds.BoxExtent.Z));

                var w = world.TransformPosition(local);
                bMinX = MathF.Min(bMinX, w.X); bMaxX = MathF.Max(bMaxX, w.X);
                bMinY = MathF.Min(bMinY, w.Y); bMaxY = MathF.Max(bMaxY, w.Y);
                bMinZ = MathF.Min(bMinZ, w.Z); bMaxZ = MathF.Max(bMaxZ, w.Z);
            }

            var spanX = bMaxX - bMinX;
            var spanY = bMaxY - bMinY;
            var spanZ = bMaxZ - bMinZ;

            // --probe X Y: name every mesh whose BOUNDS cover a world point, then report how many
            // cells its TRIANGLES actually raised. Those are different claims and only the second one
            // matters - a box can cover a point the mesh itself is nowhere near.
            var probing = probeX is { } probePointX && probeY is { } probePointY &&
                          probePointX >= bMinX && probePointX <= bMaxX &&
                          probePointY >= bMinY && probePointY <= bMaxY;
            var cellsBeforeThisMesh = cellsRaised;

            if (probing) {
                var lodForCount = mesh.RenderData?.LODs?.FirstOrDefault();
                Console.WriteLine($"  under ({probeX:F0},{probeY:F0}): {mesh.Name} " +
                                  $"span {spanX:F0}x{spanY:F0}x{spanZ:F0} Z {bMinZ:F0}..{bMaxZ:F0}, " +
                                  $"{(lodForCount?.IndexBuffer?.Length ?? 0) / 3} triangle(s)");
            }

            // BACKDROPS AND SKY, dropped by size. The ocean plane, the background mountains and the
            // aurora dome all legitimately span millions of units and none of them is ground anyone
            // stands on; rasterising them would blanket the map. The warmup island's own ground mesh
            // spans 34,207 units, so the cap has to be well above that - it is a filter for scenery
            // that is not part of the playable surface, not a quality cap.
            if (MathF.Max(spanX, spanY) > meshMaxSpan) {
                meshesSkippedHuge++;
                if (biggestSkipped.Count < 12) biggestSkipped.Add((MathF.Max(spanX, spanY), mesh.Name, bMaxZ));
                continue;
            }

            // FOLIAGE, dropped by name. A tree's bounding box is WIDER THAN IT IS TALL, so no aspect
            // test catches it, and its canopy triangles would put "the ground" at treetop height over
            // a forty-metre square. Fortnite's own collision for foliage is essentially the trunk, so
            // dropping it entirely is closer than including the canopy. Named, not measured - the one
            // heuristic in this pass, and the first thing to revisit if grenades sit on nothing.
            if (mesh.Name.Contains("Tree", StringComparison.OrdinalIgnoreCase) ||
                mesh.Name.Contains("Bush", StringComparison.OrdinalIgnoreCase) ||
                mesh.Name.Contains("Foliage", StringComparison.OrdinalIgnoreCase) ||
                mesh.Name.Contains("Grass", StringComparison.OrdinalIgnoreCase)) {
                meshesSkippedFoliage++;
                continue;
            }

            var lod = mesh.RenderData?.LODs?.FirstOrDefault();
            var positions = lod?.PositionVertexBuffer?.Verts;
            var indices = lod?.IndexBuffer;

            if (positions == null || indices == null || indices.Length < 3) {
                meshesNoTriangles++;
                Note(unreadableExamples, $"no LOD triangles: {mesh.Name}");
                continue;
            }

            // Transform every vertex once, then rasterise each triangle. This is the whole reason the
            // pass moved off bounding boxes: the island's ground mesh has a box top 5,500 units above
            // the surface people actually stand on, and 4,076 triangles that describe it exactly.
            var world3 = new FVector[positions.Length];
            for (var i = 0; i < positions.Length; i++) world3[i] = world.TransformPosition(positions[i]);

            var raisedAny = false;

            // The bounds and the vertices must agree about where this mesh IS. They are separate
            // serialised things and a cooked mesh can carry QUANTIZED positions that only become world
            // space once de-quantized through those same bounds - so if these two boxes disagree, the
            // triangles are being rasterised somewhere the mesh is not.
            if (probing) {
                float vMinX = float.MaxValue, vMaxX = float.MinValue;
                float vMinY = float.MaxValue, vMaxY = float.MinValue;
                float vMinZ = float.MaxValue, vMaxZ = float.MinValue;

                foreach (var v in world3) {
                    vMinX = MathF.Min(vMinX, v.X); vMaxX = MathF.Max(vMaxX, v.X);
                    vMinY = MathF.Min(vMinY, v.Y); vMaxY = MathF.Max(vMaxY, v.Y);
                    vMinZ = MathF.Min(vMinZ, v.Z); vMaxZ = MathF.Max(vMaxZ, v.Z);
                }

                Console.WriteLine($"      bounds box  X {bMinX:F0}..{bMaxX:F0}  Y {bMinY:F0}..{bMaxY:F0}  Z {bMinZ:F0}..{bMaxZ:F0}");
                Console.WriteLine($"      vertex box  X {vMinX:F0}..{vMaxX:F0}  Y {vMinY:F0}..{vMaxY:F0}  Z {vMinZ:F0}..{vMaxZ:F0}");
            }

            for (var t = 0; t + 2 < indices.Length; t += 3) {
                var a = world3[indices[t]];
                var b = world3[indices[t + 1]];
                var c = world3[indices[t + 2]];

                var tMinX = MathF.Min(a.X, MathF.Min(b.X, c.X));
                var tMaxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
                var tMinY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
                var tMaxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));

                // WALL FACE? Decided by the normal, not by height: cross(b-a, c-a) normalised, and a
                // |Z| under the threshold means the face stands up rather than lying flat. 0.5 is
                // 60 degrees from horizontal - a steep ramp still counts as floor, a leaning wall
                // still counts as wall.
                if (bakeWalls && !hullMeshCovered) {
                    var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z;
                    var vx = c.X - a.X; var vy = c.Y - a.Y; var vz = c.Z - a.Z;

                    var nx = uy * vz - uz * vy;
                    var ny = uz * vx - ux * vz;
                    var nz = ux * vy - uy * vx;
                    var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);

                    if (len > 0.0001f && MathF.Abs(nz / len) < wallNormalZ) {
                        wallTriangles++;
                        AddWall(a, b, c);
                    }
                }

                // Barycentric denominator - zero for a triangle with no area seen from above (a wall
                // face), which contributes nothing to a height field and is skipped rather than
                // dividing by zero.
                var den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(den) < 0.0001f) continue;

                var cx0 = System.Math.Max(0, (int) MathF.Floor((tMinX - minX) / cellSize));
                var cx1 = System.Math.Min(width - 1, (int) MathF.Ceiling((tMaxX - minX) / cellSize));
                var cy0 = System.Math.Max(0, (int) MathF.Floor((tMinY - minY) / cellSize));
                var cy1 = System.Math.Min(height - 1, (int) MathF.Ceiling((tMaxY - minY) / cellSize));

                for (var cy = cy0; cy <= cy1; cy++)
                for (var cx = cx0; cx <= cx1; cx++) {
                    var px = minX + cx * cellSize;
                    var py = minY + cy * cellSize;

                    var w0 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / den;
                    var w1 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / den;
                    var w2 = 1f - w0 - w1;
                    if (w0 < 0f || w1 < 0f || w2 < 0f) continue;

                    var z = ToGridHeight(w0 * a.Z + w1 * b.Z + w2 * c.Z, ref rangeWarned);
                    var i = (long) cy * width + cx;

                    // The mesh-only grid is raised against ITSELF, not against the landscape, so a
                    // POI floor that sits below the terrain around it is still recorded. Written
                    // BEFORE the landscape's raise-only test, which would otherwise discard it.
                    if (meshHeights[i] == short.MinValue || meshHeights[i] < z) meshHeights[i] = z;

                    // RAISE ONLY: a mesh in a valley must not lower the ground around it, and the
                    // landscape under a bridge stays where it is for anything not on the bridge.
                    if (heights[i] != short.MinValue && heights[i] >= z) continue;

                    if (i == probeCell)
                        Console.WriteLine($"      probe cell raised to Z {z} " +
                                          $"by {mesh.Name}");

                    heights[i] = z;
                    cellsRaised++;
                    raisedAny = true;
                }
            }

            if (raisedAny) meshesRaised++;
            if (probing) Console.WriteLine($"      -> raised {cellsRaised - cellsBeforeThisMesh} cell(s)");
            }
        }
    }

    Console.WriteLine($"{meshesConsidered} placed mesh component(s): {meshesRaised} raised the grid " +
                      $"({cellsRaised:N0} cell(s)), {instancedComponents} instanced.");
    Console.WriteLine($"  contributed nothing: {meshesSkippedHuge} backdrop/sky, {meshesSkippedFoliage} foliage, " +
                      $"{meshesNoAsset} no mesh set, {meshesLoadFailed} failed to load, " +
                      $"{meshesNoRenderData} no render data, {meshesNoTriangles} no LOD triangles.");

    foreach (var example in unreadableExamples.Take(12)) Console.WriteLine($"    {example}");

    if (probeCell >= 0) {
        var after = heights[probeCell];
        Console.WriteLine($"  probe cell after the mesh pass: " +
                          (after == short.MinValue ? "STILL NO DATA" : $"Z {after}"));
    }

    foreach (var (span, name, top) in biggestSkipped.OrderByDescending(b => b.Span).Take(12))
        Console.WriteLine($"    too large: {name} spans {span:F0} units, top Z {top:F0}");
}


// A wall triangle's XY footprint is a SLIVER - seen from above it has almost no area, so the
// barycentric fill the height field uses would miss it entirely. Its three edges are walked instead,
// at half a cell per step, which is what actually marks the line of cells a wall runs along.
// A wall triangle's XY footprint is a SLIVER - seen from above a vertical face is a LINE, so the
// barycentric fill the height field uses would miss it entirely. Its three edges are walked instead,
// which for a vertical face covers the whole projection.
//
// THE Z RANGE IS PER CELL, NOT PER TRIANGLE, and that is the difference between a wall and a wall
// with a hole in it. Taking the triangle's own min/max Z for every cell it touches fills in openings:
// the geometry above an arch is a fan of long thin triangles reaching from the arch's curve up to the
// wall top, and each of those, painted as one span, closes the archway it is describing. Collecting
// the range PER CELL from the samples that actually land in that cell follows the curve instead, and
// leaves the opening open.
void AddWall(FVector a, FVector b, FVector c) {
    triangleCells.Clear();

    Edge(a, b);
    Edge(b, c);
    Edge(c, a);

    foreach (var (key, range) in triangleCells) {
        var zLo = ToGridHeight(range.Min, ref rangeWarned);
        var zHi = ToGridHeight(range.Max, ref rangeWarned);

        if (!wallSpans.TryGetValue(key, out var spans)) wallSpans[key] = spans = new List<(short, short)>();

        // MERGED ON INSERT, because a wall is thousands of triangles and one cell would otherwise
        // collect one span per triangle. Two spans that touch or overlap are one wall - and a span
        // that merges into an existing one is DONE, not a reason to stop looking at the rest of this
        // triangle (an earlier version returned here, which quietly left most of every wall unmarked).
        var merged = false;
        for (var j = 0; j < spans.Count; j++) {
            if (zLo > spans[j].Max + 1 || zHi < spans[j].Min - 1) continue;

            spans[j] = ((short) System.Math.Min(spans[j].Min, zLo), (short) System.Math.Max(spans[j].Max, zHi));
            merged = true;
            break;
        }

        if (!merged) spans.Add((zLo, zHi));
    }
}

void Edge(FVector p, FVector q) {
    var dx = q.X - p.X;
    var dy = q.Y - p.Y;
    var dz = q.Z - p.Z;

    // Stepped by BOTH axes: half a cell horizontally so no cell is skipped, and wallZStep vertically
    // so a perfectly vertical edge - which covers no horizontal distance at all - still contributes
    // its whole height rather than just its two ends.
    var horizontal = MathF.Sqrt(dx * dx + dy * dy);
    var steps = (int) MathF.Ceiling(MathF.Max(horizontal / (wallCellSize * 0.5f), MathF.Abs(dz) / wallZStep));
    if (steps < 1) steps = 1;

    for (var i = 0; i <= steps; i++) {
        var t = (float) i / steps;
        var cx = (int) MathF.Round((p.X + dx * t - minX) / wallCellSize);
        var cy = (int) MathF.Round((p.Y + dy * t - minY) / wallCellSize);
        if (cx < 0 || cy < 0 || cx >= wallWidth || cy >= wallHeight) continue;

        var z = p.Z + dz * t;
        var key = (long) cy * wallWidth + cx;

        // Widened by half a step so consecutive samples along a steep edge join up instead of leaving
        // a ladder of gaps a projectile could thread.
        var half = MathF.Abs(dz) / steps * 0.5f + 1f;

        if (triangleCells.TryGetValue(key, out var range))
            triangleCells[key] = (MathF.Min(range.Min, z - half), MathF.Max(range.Max, z + half));
        else
            triangleCells[key] = (z - half, z + half);
    }
}

var written = WriteThm1(outputPath, minX, minY, cellSize, width, height, heights);
Console.WriteLine($"Wrote {written:N0} bytes to {outputPath}");

if (bakeMeshes) {
    // Named for the landscape file it accompanies, because the server looks for exactly that:
    // TerrainHeightMap.bin -> TerrainHeightMap.meshes.bin, or TERRAIN_HEIGHTMAP_MESHES.
    var meshPath = System.IO.Path.ChangeExtension(outputPath, null) + ".meshes" +
                   System.IO.Path.GetExtension(outputPath);
    var meshCells = meshHeights.Count(h => h != short.MinValue);
    var meshWritten = WriteThm1(meshPath, minX, minY, cellSize, width, height, meshHeights);

    Console.WriteLine($"Wrote {meshWritten:N0} bytes to {meshPath} ({meshCells:N0} cell(s) of placed " +
                      "mesh - copy it next to the server as TerrainHeightMap.meshes.bin, or point " +
                      "TERRAIN_HEIGHTMAP_MESHES at it).");
}

if (bakeHulls) {
    var hullPath = System.IO.Path.ChangeExtension(outputPath, null) + ".hulls" +
                   System.IO.Path.GetExtension(outputPath);
    var planeCount = hullShapeData.Sum(shape => shape.Sum(h => h.Length / 4));
    var hullWritten = WriteThmh(hullPath, hullShapeData, hullShapeBounds, hullInstances);

    Console.WriteLine($"Wrote {hullWritten:N0} bytes to {hullPath} - {hullShapeData.Count:N0} shape(s) " +
                      $"({planeCount:N0} plane(s)) placed {hullInstances.Count:N0} time(s); " +
                      $"{hullMeshesWithout.Count:N0} mesh(es) have no simple collision and still need the " +
                      "voxel path. Copy it next to the server as TerrainHeightMap.hulls.bin, or point " +
                      "TERRAIN_HEIGHTMAP_HULLS at it.");
}

if (bakeWalls) {
    var wallPath = System.IO.Path.ChangeExtension(outputPath, null) + ".walls" +
                   System.IO.Path.GetExtension(outputPath);
    var spanCount = wallSpans.Values.Sum(v => v.Count);
    var wallWritten = WriteThmw(wallPath, minX, minY, wallCellSize, wallWidth, wallHeight, wallSpans);

    Console.WriteLine($"Wrote {wallWritten:N0} bytes to {wallPath} - {wallTriangles:N0} wall triangle(s) " +
                      $"became {spanCount:N0} span(s) in {wallSpans.Count:N0} cell(s) of {wallCellSize:F0} " +
                      "units. Copy it next to the " +
                      "server as TerrainHeightMap.walls.bin, or point TERRAIN_HEIGHTMAP_WALLS at it.");
}
Console.WriteLine("Copy this file next to the AFortOnlineBeacon server binary as TerrainHeightMap.bin, " +
                  "or set TERRAIN_HEIGHTMAP to its full path - see AFortOnlineBeacon/Net/TerrainHeightMap.cs.");
return 0;

// ================================================================================================

// Walks up from this tool's own build output looking for a copy of the Oodle codec DLL this repo
// already carries for PacketReplayDecoder's unrelated needs, so OodleHelper.Initialize doesn't have
// to reach GitHub. Returns null (letting OodleHelper try its own download) if none is found - keeps
// this working even if PacketReplayDecoder's copy ever moves or is renamed.
static string? FindOodleDll() {
    const string dllName = "oo2core_5_win64.dll";
    var dir = new DirectoryInfo(AppContext.BaseDirectory);

    for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent) {
        var candidate = Path.Combine(dir.FullName, "PacketReplayDecoder", dllName);
        if (File.Exists(candidate)) return candidate;
    }

    return null;
}

static string NormalizeAssetPath(string assetPathName) {
    if (assetPathName.Length == 0) return "";
    var dot = assetPathName.IndexOf('.');
    var withoutObject = dot >= 0 ? assetPathName[..dot] : assetPathName;
    return withoutObject.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)
        ? "FortniteGame/Content" + withoutObject["/Game".Length..]
        : withoutObject.TrimStart('/');
}

// Reads an FTransform-shaped struct (Translation/Rotation/Scale3D - what LevelTransform actually is)
// generically, the same TryGetValue-off-a-property-bag approach MapActorDump's own LocationOf uses.
// One example per DISTINCT kind of failure, first occurrence wins. A flat list of 799 lines is not
// evidence anyone reads; one named example of each is what makes a counter actionable.
static void Note(List<string> examples, string line) {
    var kind = line[..line.IndexOf(':')];
    if (!examples.Any(e => e.StartsWith(kind, StringComparison.Ordinal))) examples.Add(line);
}

static Placement ReadPlacement(FStructFallback fallback) {
    var translation = fallback.TryGetValue(out FVector t, "Translation") ? t : new FVector(0, 0, 0);
    var rotation = fallback.TryGetValue(out FQuat r, "Rotation") ? r : FQuat.Identity;
    var scale = fallback.TryGetValue(out FVector s, "Scale3D") ? s : new FVector(1, 1, 1);
    return new Placement(translation, rotation, scale);
}

// An actor's own placement lives on its RootComponent (RelativeLocation/RelativeRotation/
// RelativeScale3D), same resolution MapActorDump's LocationOf uses for RelativeLocation alone -
// extended here to rotation and scale, which location-only spawn points never needed.
// The actor's root component object, or null - shared with GetActorPlacement so the two can never
// disagree about which component is the root.
static UObject? RootComponentOf(UObject actor, List<UObject> siblings) {
    var rootComponent = actor.GetOrDefault<FPackageIndex?>("RootComponent", null);
    if (rootComponent is { IsNull: false } && rootComponent.Load() is { } loaded) return loaded;

    var name = actor.GetOrDefault<FName?>("RootComponent", null)?.Text;
    return name != null ? siblings.FirstOrDefault(e => e.Name == name) : null;
}

static Placement GetActorPlacement(UObject actor, List<UObject> siblings) {
    var rootComponent = actor.GetOrDefault<FPackageIndex?>("RootComponent", null);
    UObject? component = null;

    if (rootComponent is { IsNull: false }) component = rootComponent.Load();
    if (component == null) {
        var name = actor.GetOrDefault<FName?>("RootComponent", null)?.Text;
        if (name != null) component = siblings.FirstOrDefault(e => e.Name == name);
    }

    return GetComponentPlacement(component ?? actor);
}

// A USceneComponent's own placement relative to its parent. ULandscapeComponent doesn't declare
// RelativeLocation/RelativeRotation/RelativeScale3D as strongly-typed C# fields (see
// PriveDev/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Component/Landscape/ULandscapeComponent.cs), but
// they're ordinary inherited USceneComponent tagged properties, so they're still on the generic
// Properties bag - read the exact way MapActorDump's LocationOf reads RelativeLocation off an
// arbitrary component.
static Placement GetComponentPlacement(UObject component) {
    var location = component.TryGetValue(out FVector loc, "RelativeLocation") ? loc : new FVector(0, 0, 0);
    var rotation = component.TryGetValue(out FRotator rot, "RelativeRotation") ? rot.Quaternion() : FQuat.Identity;
    var scale = component.TryGetValue(out FVector scl, "RelativeScale3D") ? scl : new FVector(1, 1, 1);
    return new Placement(location, rotation, scale);
}

// Raw world Z -> the grid's int16 cell, clamped away from the short.MinValue "no data" sentinel.
// Fortnite Athena's vertical range is nowhere near int16 overflow (see TerrainHeightMap.cs's own doc
// comment), so this is a defensive backstop, not an expected code path - hence the log-once.
static short ToGridHeight(float worldZ, ref bool warnedRange) {
    var rounded = MathF.Round(worldZ);
    if (rounded <= short.MinValue || rounded > short.MaxValue) {
        if (!warnedRange) {
            Console.WriteLine($"warning: world Z {worldZ:F1} is outside the int16 grid's range - clamping " +
                              "(further occurrences of this won't be logged individually)");
            warnedRange = true;
        }
        rounded = System.Math.Clamp(rounded, short.MinValue + 1, short.MaxValue);
    }
    return (short) rounded;
}

// A mesh's SIMPLE COLLISION, converted to half-space planes.
//
// WHY PLANES AND NOT VERTICES. A convex body is the intersection of half-spaces, and clipping a
// segment against half-spaces is a dozen lines with an exact hit point AND the surface normal - which
// is what a bounce needs and what a voxel grid can never give. The vertices are what the asset
// stores, so the faces are recovered here, once, offline.
//
// Boxes, spheres and capsules are converted to planes too: a box exactly, a sphere and a capsule as
// their bounding box. Those two are approximations and they are the only ones in this path; they are
// also rare (a handful per POI) and always convex, so the error is bounded and outward.
static (List<float[]> Hulls, (FVector Min, FVector Max) Bounds) BuildHulls(UStaticMesh mesh) {
    var hulls = new List<float[]>();
    var min = new FVector(float.MaxValue, float.MaxValue, float.MaxValue);
    var max = new FVector(float.MinValue, float.MinValue, float.MinValue);

    if (mesh.BodySetup?.Load() is not { } bodySetup) return (hulls, (min, max));
    if (bodySetup.GetOrDefault<FStructFallback?>("AggGeom", null) is not { } agg) return (hulls, (min, max));

    void Grow(FVector v) {
        min = new FVector(MathF.Min(min.X, v.X), MathF.Min(min.Y, v.Y), MathF.Min(min.Z, v.Z));
        max = new FVector(MathF.Max(max.X, v.X), MathF.Max(max.Y, v.Y), MathF.Max(max.Z, v.Z));
    }

    void AddBox(FVector centre, FVector extent) {
        // Axis-aligned in the mesh's own space; an oriented box element is handled by rotating its
        // corners first and taking their bounds, which is what the +/- extents below already are.
        hulls.Add(new[] {
            1f, 0f, 0f, centre.X + extent.X, -1f, 0f, 0f, -(centre.X - extent.X),
            0f, 1f, 0f, centre.Y + extent.Y, 0f, -1f, 0f, -(centre.Y - extent.Y),
            0f, 0f, 1f, centre.Z + extent.Z, 0f, 0f, -1f, -(centre.Z - extent.Z)
        });
        Grow(new FVector(centre.X - extent.X, centre.Y - extent.Y, centre.Z - extent.Z));
        Grow(new FVector(centre.X + extent.X, centre.Y + extent.Y, centre.Z + extent.Z));
    }

    foreach (var convex in agg.GetOrDefault<FStructFallback[]>("ConvexElems", [])) {
        var verts = convex.GetOrDefault<FVector[]>("VertexData", []);
        if (verts.Length < 4) continue;

        foreach (var v in verts) Grow(v);

        var planes = HullPlanes(verts);
        if (planes.Count > 0) {
            hulls.Add(planes.SelectMany(pl => pl).ToArray());
            continue;
        }

        // Degenerate or too many vertices to face-fit - its own box is still a sound outer shape.
        var box = convex.GetOrDefault<FBox>("ElemBox");
        AddBox(new FVector((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, (box.Min.Z + box.Max.Z) / 2),
               new FVector((box.Max.X - box.Min.X) / 2, (box.Max.Y - box.Min.Y) / 2, (box.Max.Z - box.Min.Z) / 2));
    }

    foreach (var elem in agg.GetOrDefault<FStructFallback[]>("BoxElems", [])) {
        var centre = elem.GetOrDefault("Center", new FVector(0, 0, 0));
        AddBox(centre, new FVector(elem.GetOrDefault("X", 0f) / 2, elem.GetOrDefault("Y", 0f) / 2,
                                   elem.GetOrDefault("Z", 0f) / 2));
    }

    foreach (var elem in agg.GetOrDefault<FStructFallback[]>("SphereElems", [])) {
        var r = elem.GetOrDefault("Radius", 0f);
        AddBox(elem.GetOrDefault("Center", new FVector(0, 0, 0)), new FVector(r, r, r));
    }

    foreach (var elem in agg.GetOrDefault<FStructFallback[]>("SphylElems", [])) {
        var r = elem.GetOrDefault("Radius", 0f);
        var half = elem.GetOrDefault("Length", 0f) / 2 + r;
        AddBox(elem.GetOrDefault("Center", new FVector(0, 0, 0)), new FVector(r, r, half));
    }

    return (hulls, (min, max));
}

// The faces of a convex hull, recovered from its vertices by brute force: every triple of vertices
// spans a candidate plane, and it is a face when every other vertex lies on ONE side of it. O(n^3),
// which is nothing for the 8-to-32 vertex hulls these assets use (a wall panel is 8 vertices and 6
// faces) and is skipped outright above that.
static List<float[]> HullPlanes(FVector[] verts) {
    var planes = new List<float[]>();
    if (verts.Length > 64) return planes;

    for (var i = 0; i < verts.Length; i++)
    for (var j = i + 1; j < verts.Length; j++)
    for (var k = j + 1; k < verts.Length; k++) {
        var ux = verts[j].X - verts[i].X; var uy = verts[j].Y - verts[i].Y; var uz = verts[j].Z - verts[i].Z;
        var vx = verts[k].X - verts[i].X; var vy = verts[k].Y - verts[i].Y; var vz = verts[k].Z - verts[i].Z;

        var nx = uy * vz - uz * vy;
        var ny = uz * vx - ux * vz;
        var nz = ux * vy - uy * vx;
        var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 0.001f) continue;

        nx /= len; ny /= len; nz /= len;
        var d = nx * verts[i].X + ny * verts[i].Y + nz * verts[i].Z;

        var above = false;
        var below = false;
        foreach (var v in verts) {
            var side = nx * v.X + ny * v.Y + nz * v.Z - d;
            if (side > 0.1f) above = true;
            else if (side < -0.1f) below = true;
            if (above && below) break;
        }

        if (above && below) continue;         // cuts through the body - not a face
        if (above) { nx = -nx; ny = -ny; nz = -nz; d = -d; }

        // Same face reached through a different triple of its own vertices.
        if (planes.Any(pl => MathF.Abs(pl[0] - nx) < 0.001f && MathF.Abs(pl[1] - ny) < 0.001f &&
                             MathF.Abs(pl[2] - nz) < 0.001f && MathF.Abs(pl[3] - d) < 0.5f)) continue;

        planes.Add(new[] { nx, ny, nz, d });
        if (planes.Count > 64) return [];     // not convex enough to be worth it - caller falls back
    }

    return planes.Count >= 4 ? planes : [];
}

// The THMH (collision hull) writer.
//
//     char[4]  magic = "THMH"
//     int32    ShapeCount
//       per shape: float6 local bounds, int32 HullCount
//         per hull: int32 PlaneCount, then PlaneCount * float4 (nx, ny, nz, d); inside is n.p <= d
//     int32    InstanceCount
//       per instance: int32 ShapeId, float3 Translation, float4 Rotation (xyzw), float3 Scale
static long WriteThmh(string path, List<List<float[]>> shapes, List<(FVector Min, FVector Max)> bounds,
                      List<(int Shape, Placement Where)> instances) {
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);

    writer.Write("THMH"u8.ToArray());
    writer.Write(shapes.Count);

    for (var i = 0; i < shapes.Count; i++) {
        writer.Write(bounds[i].Min.X); writer.Write(bounds[i].Min.Y); writer.Write(bounds[i].Min.Z);
        writer.Write(bounds[i].Max.X); writer.Write(bounds[i].Max.Y); writer.Write(bounds[i].Max.Z);
        writer.Write(shapes[i].Count);

        foreach (var hull in shapes[i]) {
            writer.Write(hull.Length / 4);
            foreach (var f in hull) writer.Write(f);
        }
    }

    writer.Write(instances.Count);
    foreach (var (shape, where) in instances) {
        writer.Write(shape);
        writer.Write(where.Translation.X); writer.Write(where.Translation.Y); writer.Write(where.Translation.Z);
        writer.Write(where.Rotation.X); writer.Write(where.Rotation.Y);
        writer.Write(where.Rotation.Z); writer.Write(where.Rotation.W);
        writer.Write(where.Scale.X); writer.Write(where.Scale.Y); writer.Write(where.Scale.Z);
    }

    return stream.Length;
}

// The THMW (wall span) writer. Sparse by construction - only cells that carry a wall appear - and
// sorted by cell index so the server can binary-search rather than build a dictionary of millions of
// entries at startup.
//
//     char[4]   magic = "THMW"
//     float32   OriginX, OriginY, CellSize
//     int32     Width, Height
//     int32     SpanCount
//     { int32 CellIndex, int16 ZMin, int16 ZMax } * SpanCount, ascending by CellIndex
static long WriteThmw(string path, float originX, float originY, float cellSize, int width, int height,
                      Dictionary<long, List<(short Min, short Max)>> spans) {
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);

    writer.Write("THMW"u8.ToArray());
    writer.Write(originX);
    writer.Write(originY);
    writer.Write(cellSize);
    writer.Write(width);
    writer.Write(height);
    writer.Write(spans.Values.Sum(v => v.Count));

    foreach (var (cell, list) in spans.OrderBy(e => e.Key))
        foreach (var (min, max) in list.OrderBy(sp => sp.Min)) {
            writer.Write((int) cell);
            writer.Write(min);
            writer.Write(max);
        }

    return stream.Length;
}

// The THM1 writer - shared between the real bake above and RunSelfTest below, so the self-test
// actually exercises the exact bytes a real run would produce.
static long WriteThm1(string path, float originX, float originY, float cellSize, int width, int height, short[] heights) {
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);

    writer.Write("THM1"u8.ToArray());
    writer.Write(originX);
    writer.Write(originY);
    writer.Write(cellSize);
    writer.Write(width);
    writer.Write(height);
    foreach (var h in heights) writer.Write(h);

    return stream.Length;
}

// --- Self-test: round-trips a synthetic grid through WriteThm1 above and a copy of
// TerrainHeightMap.cs's own Load() logic, so the binary format is checked byte-for-byte against the
// actual consumer without needing a PAK/AES key at all. This is item 4 of the task - the one part of
// this tool verifiable without real game data.
static int RunSelfTest(string path) {
    Console.WriteLine($"--selftest: writing a synthetic grid to {path}");

    const int width = 5, height = 3;
    const float originX = -1234.5f, originY = 6789f, cellSize = 100f;
    var written = new short[width * height];
    for (var i = 0; i < written.Length; i++) written[i] = (short) (i * 37 - 500);
    written[3] = short.MinValue; // one deliberate "no data" hole, at (col=3,row=0)

    WriteThm1(path, originX, originY, cellSize, width, height, written);

    // --- Copied from AFortOnlineBeacon/Net/TerrainHeightMap.cs's Load()/GetGroundHeight() - kept
    // deliberately parallel (same field order, same NoData sentinel, same nearest-cell rounding) so
    // a change to the real reader that isn't mirrored here will show up as a self-test diff instead
    // of silently drifting.
    using (var stream = File.OpenRead(path))
    using (var reader = new BinaryReader(stream)) {
        var magic = reader.ReadChars(4);
        if (magic is not ['T', 'H', 'M', '1']) { Console.WriteLine("FAIL: bad magic"); return 1; }

        var readOriginX = reader.ReadSingle();
        var readOriginY = reader.ReadSingle();
        var readCellSize = reader.ReadSingle();
        var readWidth = reader.ReadInt32();
        var readHeight = reader.ReadInt32();
        var readHeights = new short[readWidth * readHeight];
        for (var i = 0; i < readHeights.Length; i++) readHeights[i] = reader.ReadInt16();

        var ok = true;
        void Check(string what, bool cond) { if (!cond) { Console.WriteLine($"FAIL: {what}"); ok = false; } }

        Check("OriginX", readOriginX == originX);
        Check("OriginY", readOriginY == originY);
        Check("CellSize", readCellSize == cellSize);
        Check("Width", readWidth == width);
        Check("Height", readHeight == height);
        Check("heights match", readHeights.SequenceEqual(written));

        // GetGroundHeight's own nearest-cell lookup, exercised end to end.
        float? GetGroundHeight(float x, float y) {
            var cx = (int) MathF.Round((x - readOriginX) / readCellSize);
            var cy = (int) MathF.Round((y - readOriginY) / readCellSize);
            if (cx < 0 || cy < 0 || cx >= readWidth || cy >= readHeight) return null;
            var v = readHeights[cy * readWidth + cx];
            return v == short.MinValue ? null : v;
        }

        Check("lookup at cell (0,0)", GetGroundHeight(originX, originY) == written[0]);
        Check("lookup at cell (2,1) matches nearest-cell rounding",
            GetGroundHeight(originX + 2 * cellSize + 10, originY + 1 * cellSize - 5) == written[1 * width + 2]);
        Check("lookup over the deliberate hole returns null", GetGroundHeight(originX + 3 * cellSize, originY) == null);
        Check("lookup outside the grid returns null", GetGroundHeight(originX - 10_000, originY) == null);

        Console.WriteLine(ok ? "PASS: THM1 round-trip matches TerrainHeightMap.cs's reader exactly." : "SELF-TEST FAILED");
        return ok ? 0 : 1;
    }
}

// A composed world-space placement (translation + rotation + non-shear per-axis scale), matching
// UE's own FTransform::TransformPosition for the no-shear case:
//     TransformPosition(V) = Rotation.RotateVector(Scale3D * V) + Translation
// and composing two such placements (child relative to parent) the same way. FQuat.RotateVector and
// FQuat operator* are CUE4Parse's own (PriveDev/CUE4Parse/CUE4Parse/UE4/Objects/Core/Math/FQuat.cs),
// used verbatim rather than reimplemented.
//
// Declared at the very end of the file, after every top-level statement and local function above -
// C# requires type declarations in a top-level-statements file to come after all top-level
// statements (CS8803), so this can't sit next to the functions that use it despite being logically
// grouped with them.
/// <summary>
///     Bakes the real collision shape of every PLAYER BUILDING class into a C# table.
///
///     WHY. The server's own idea of a built piece was one axis-aligned box per piece TYPE: a wall
///     was a slab, a stair was a solid cell, and every variant of a type shared it. So a doorway was
///     solid whether the door was open or shut, a window was solid glass, and editing a piece into
///     another shape changed nothing at all - which is exactly the list of things reported as
///     missing.
///
///     The game itself does not approximate any of that, and neither does it need parsing effort to
///     recover: these meshes carry SIMPLE collision and are flagged CTF_UseSimpleAsComplex, so the
///     convex hulls in the asset ARE the collision the client traces against. PBW_W1_DoorC has
///     THREE (two posts and a lintel - the doorway is a real hole), PBW_W1_WindowC has FOUR, a plain
///     wall has one, and the door LEAF is a separate mesh with one more.
///
///     That last split is what makes an openable door work: the wall's own hulls always apply, and
///     the leaf's hull is added only while the door is shut.
///
///     A cooked Blueprint stores only what it OVERRIDES, so the mesh is looked up through the super
///     chain - the same trap that has caught attribute keys and cosmetic parts in this project
///     before.
/// </summary>
static int BakeBuildPieces(DefaultFileProvider provider, string outPath, string prefix) {
    var classes = provider.Files.Keys
        .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToList();

    Console.WriteLine($"buildpieces: {classes.Count} class asset(s) under {prefix}");

    var meshCache = new Dictionary<string, (List<float[]> Hulls, (FVector Min, FVector Max) Bounds)>(
        StringComparer.OrdinalIgnoreCase);

    // className -> (body hulls, door-leaf hulls, the pair's local-space bounds)
    var rows = new SortedDictionary<string, (List<float[]> Body, List<float[]> Door, FVector Min, FVector Max)>(
        StringComparer.OrdinalIgnoreCase);

    (List<float[]>, (FVector, FVector)) HullsOf(UStaticMesh mesh) {
        if (meshCache.TryGetValue(mesh.Name, out var known)) return (known.Hulls, known.Bounds);
        var built = BuildHulls(mesh);
        meshCache[mesh.Name] = built;
        return (built.Hulls, built.Bounds);
    }

    // The property, looked up through the SUPER CHAIN. A cooked Blueprint serialises only its
    // overrides, so a variant that inherits its mesh has no such property of its own.
    UStaticMesh? MeshProperty(UObject cdo, CUE4Parse.UE4.Assets.IPackage package, string name, int depth = 0) {
        if (depth > 8) return null;
        if (cdo.GetOrDefault<UStaticMesh?>(name, null) is { } direct) return direct;

        // Up to the PARENT class. Reached through the package's own BlueprintGeneratedClass export
        // rather than through cdo.Class, which resolves to a ResolvedObject that cannot be pattern
        // matched to a UStruct - the same walk `pakreader supers` does.
        var generated = package.GetExports()
            .OfType<UStruct>()
            .FirstOrDefault(e => e.Name.Equals(cdo.Name["Default__".Length..], StringComparison.Ordinal));

        if (generated?.SuperStruct is not { IsNull: false } superRef) return null;
        if (superRef.Load<UStruct>() is not { Owner: { } superPackage } superClass) return null;

        var superCdo = superPackage.GetExports()
            .FirstOrDefault(e => e.Name.Equals("Default__" + superClass.Name, StringComparison.Ordinal));

        return superCdo == null ? null : MeshProperty(superCdo, superPackage, name, depth + 1);
    }

    var withBody = 0;
    var withDoor = 0;

    foreach (var path in classes) {
        if (!provider.TryLoadPackage(path, out var package)) continue;

        foreach (var export in package.GetExports()) {
            if (!export.Name.StartsWith("Default__", StringComparison.Ordinal)) continue;

            var className = export.Name["Default__".Length..];
            if (rows.ContainsKey(className)) continue;

            List<float[]> body = new(), door = new();
            var min = new FVector(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new FVector(float.MinValue, float.MinValue, float.MinValue);

            void Widen((FVector Min, FVector Max) b) {
                min = new FVector(MathF.Min(min.X, b.Min.X), MathF.Min(min.Y, b.Min.Y), MathF.Min(min.Z, b.Min.Z));
                max = new FVector(MathF.Max(max.X, b.Max.X), MathF.Max(max.Y, b.Max.Y), MathF.Max(max.Z, b.Max.Z));
            }

            try {
                if (MeshProperty(export, package, "StaticMesh") is { } bodyMesh) {
                    var built = HullsOf(bodyMesh);
                    body = built.Item1;
                    if (body.Count > 0) Widen(built.Item2);
                }

                // The LEAF, MOVED INTO THE WALL'S FRAME. A door mesh is authored in its own space
                // with the HINGE at the origin - PBW_W1_Door runs X 0..144 - while the doorway it
                // fills is centred on the wall's origin (PBW_W1_DoorC's posts leave X -72..72 open,
                // exactly 144 wide). The wall creates the DoorComponent natively and places it, so
                // there is no relative transform in the asset to read; what there is instead is that
                // exact correspondence, which fixes the offset with nothing left to guess: shifting
                // the leaf so its own X extent is centred puts it precisely in the hole.
                //
                // Left unshifted the leaf sits half over the doorway and half inside a post, which
                // is neither open nor shut.
                if (MeshProperty(export, package, "DoorMesh") is { } doorMesh) {
                    var built = HullsOf(doorMesh);
                    door = built.Item1;

                    if (door.Count > 0) {
                        var (leafMin, leafMax) = built.Item2;
                        var shift = -(leafMin.X + leafMax.X) / 2f;

                        // COPIED BEFORE SHIFTING, because the mesh cache hands out the same arrays to
                        // every class that uses this leaf - and 46 classes share a handful of door
                        // meshes. Mutating them in place applied the shift once PER CLASS: the leaf
                        // came out at X -360..-216 instead of -72..72, five doors' worth of drift.
                        door = door.Select(hull => {
                            var moved = (float[]) hull.Clone();
                            // A plane (N, D) translated along X by `shift` keeps N and gains N.x*shift.
                            for (var i = 0; i + 3 < moved.Length; i += 4) moved[i + 3] += moved[i] * shift;
                            return moved;
                        }).ToList();

                        Widen((new FVector(leafMin.X + shift, leafMin.Y, leafMin.Z),
                               new FVector(leafMax.X + shift, leafMax.Y, leafMax.Z)));
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine($"buildpieces: {className}: {ex.GetType().Name}: {ex.Message}");
            }

            if (body.Count == 0 && door.Count == 0) continue;

            rows[className] = (body, door, min, max);
            if (body.Count > 0) withBody++;
            if (door.Count > 0) withDoor++;
        }
    }

    var planeCount = rows.Sum(r => r.Value.Body.Sum(h => h.Length / 4) + r.Value.Door.Sum(h => h.Length / 4));
    Console.WriteLine($"buildpieces: {rows.Count} class(es) - {withBody} with a body mesh, {withDoor} with a " +
                      $"door leaf, {planeCount:N0} plane(s) total");

    using var w = new StreamWriter(outPath, false, new System.Text.UTF8Encoding(true));

    w.WriteLine("// <auto-generated> Tools/TerrainHeightMapBaker --buildpieces - do not edit by hand. </auto-generated>");
    w.WriteLine("//");
    w.WriteLine("// The REAL collision shape of every player-buildable piece, as half-space planes in the piece's");
    w.WriteLine($"// own local space: {rows.Count} classes, {planeCount:N0} planes.");
    w.WriteLine("//");
    w.WriteLine("// These are the game's OWN shapes, not an approximation of them. The meshes carry simple");
    w.WriteLine("// collision and are flagged CTF_UseSimpleAsComplex, so what the client traces against is exactly");
    w.WriteLine("// these convex hulls. A doorway and a window are real HOLES in them - PBW_W1_DoorC is three hulls");
    w.WriteLine("// (two posts and a lintel) and PBW_W1_WindowC is four - which is what a single per-type box could");
    w.WriteLine("// never express.");
    w.WriteLine("//");
    w.WriteLine("// DOOR is the leaf, kept apart from the body on purpose: it is collision only while the door is");
    w.WriteLine("// SHUT. See BuildingStructuralSupportSystem.");
    w.WriteLine("//");
    w.WriteLine("// A plane is (Nx, Ny, Nz, D) with the inside at Nx*x + Ny*y + Nz*z <= D, four floats per plane,");
    w.WriteLine("// hulls concatenated - the same encoding TerrainHeightMap.hulls.bin uses for the map.");
    w.WriteLine();
    w.WriteLine("namespace AFortOnlineBeacon.Net.Actors;");
    w.WriteLine();
    w.WriteLine("public static partial class FortBuildingHulls {");
    w.WriteLine("    /// <summary>");
    w.WriteLine("    ///     Class name -> its hulls, the door leaf's hulls when it has one, and the local-space");
    w.WriteLine("    ///     bounds of both together - the broad phase needs the bounds and the exact test the hulls.");
    w.WriteLine("    /// </summary>");
    w.WriteLine("    private static readonly (string Class, float[][] Body, float[][] Door," +
                " float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ)[] Pieces = {");

    string Hulls(List<float[]> hulls) => hulls.Count == 0
        ? "System.Array.Empty<float[]>()"
        : "new[] { " + string.Join(", ", hulls.Select(h =>
            "new[] { " + string.Join(", ", h.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f")) + " }")) + " }";

    string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";

    foreach (var (className, shape) in rows) {
        w.WriteLine($"        (\"{className}\", {Hulls(shape.Body)}, {Hulls(shape.Door)}, " +
                    $"{F(shape.Min.X)}, {F(shape.Min.Y)}, {F(shape.Min.Z)}, " +
                    $"{F(shape.Max.X)}, {F(shape.Max.Y)}, {F(shape.Max.Z)}),");
    }

    w.WriteLine("    };");
    w.WriteLine("}");

    Console.WriteLine($"buildpieces: wrote {outPath}");
    return 0;
}

readonly record struct Placement(FVector Translation, FQuat Rotation, FVector Scale) {
    public static readonly Placement Identity = new(new FVector(0, 0, 0), FQuat.Identity, new FVector(1, 1, 1));

    public FVector TransformPosition(FVector local) => Rotation.RotateVector(local * Scale) + Translation;

    public static Placement Compose(Placement parent, Placement child) => new(
        parent.TransformPosition(child.Translation),
        parent.Rotation * child.Rotation,
        parent.Scale * child.Scale);
}
