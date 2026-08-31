using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
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
    var levelPlacement = levelTranslations.GetValueOrDefault(basePath, Placement.Identity);

    foreach (var actor in exports) {
        if (actor is not ALandscapeProxy landscape) continue;
        landscapeActorsFound++;

        var actorPlacement = GetActorPlacement(landscape, exports);
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

var written = WriteThm1(outputPath, minX, minY, cellSize, width, height, heights);
Console.WriteLine($"Wrote {written:N0} bytes to {outputPath}");
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
static Placement ReadPlacement(FStructFallback fallback) {
    var translation = fallback.TryGetValue(out FVector t, "Translation") ? t : new FVector(0, 0, 0);
    var rotation = fallback.TryGetValue(out FQuat r, "Rotation") ? r : FQuat.Identity;
    var scale = fallback.TryGetValue(out FVector s, "Scale3D") ? s : new FVector(1, 1, 1);
    return new Placement(translation, rotation, scale);
}

// An actor's own placement lives on its RootComponent (RelativeLocation/RelativeRotation/
// RelativeScale3D), same resolution MapActorDump's LocationOf uses for RelativeLocation alone -
// extended here to rotation and scale, which location-only spawn points never needed.
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
readonly record struct Placement(FVector Translation, FQuat Rotation, FVector Scale) {
    public static readonly Placement Identity = new(new FVector(0, 0, 0), FQuat.Identity, new FVector(1, 1, 1));

    public FVector TransformPosition(FVector local) => Rotation.RotateVector(local * Scale) + Translation;

    public static Placement Compose(Placement parent, Placement child) => new(
        parent.TransformPosition(child.Translation),
        parent.Rotation * child.Rotation,
        parent.Scale * child.Scale);
}
