namespace AFortOnlineBeacon.Net;

/// <summary>
///     Ground height at an arbitrary (X, Y), read from a baked grid file - the "real ground heights"
///     half of BuildingStructuralSupportSystem's IsSupportedByWorld (see that class's doc comment for
///     why a real height, not an approximation, is worth having). Nothing in this project can compute
///     a height itself: an external server has no landscape geometry, only what an offline tool bakes
///     out of the cooked map ahead of time. That tool is Tools/TerrainHeightMapBaker - and note that
///     it reads the landscape's RENDER heightmap texture, NOT
///     ULandscapeHeightfieldCollisionComponent.CollisionHeightData, which is editor-only and never
///     ships in a cooked pak; its Program.cs header carries the full derivation.
///
///     Same hot-reload convention as BuildingClassHandles: looked for next to the server binary (or
///     TERRAIN_HEIGHTMAP overrides the path outright), and re-read whenever its timestamp/length
///     changes so a freshly baked file picks up without a restart.
///
///     Binary format (little-endian), chosen over a text grid because a whole map at a usable
///     resolution is millions of cells:
///
///         char[4]   magic = "THM1"
///         float32   OriginX, OriginY      - world XY of grid cell (0,0)
///         float32   CellSize              - world units per grid step
///         int32     Width, Height         - grid dimensions
///         int16[Width*Height]             - height in raw world Z units, row-major (y*Width+x);
///                                            int16.MinValue marks "no data" (outside the baked area,
///                                            or a hole CUE4Parse's landscape decode couldn't fill)
///
///     int16 covers roughly -32000..32000, comfortably inside Fortnite Athena's vertical range, so no
///     scaling is applied - a cell's raw value IS its world Z.
/// </summary>
public static class TerrainHeightMap {
    private static readonly string[] CandidatePaths =
        Environment.GetEnvironmentVariable("TERRAIN_HEIGHTMAP") is { Length: > 0 } configured
            ? new[] { configured }
            : new[] {
                Path.Combine(AppContext.BaseDirectory, "TerrainHeightMap.bin"),
                "TerrainHeightMap.bin"
            };

    private static string TablePath => CandidatePaths.FirstOrDefault(File.Exists) ?? CandidatePaths[0];

    private const short NoData = short.MinValue;

    private static float _originX, _originY, _cellSize = 1f;
    private static int _width, _height;
    private static short[]? _heights;

    private static DateTime _loadedStamp = DateTime.MinValue;
    private static long _loadedLength = -1;
    private static bool _reportedMissing;

    /// <summary>
    ///     Ground Z at (x, y), or null when no baked data covers that point (file missing, or outside
    ///     the baked extent / a hole). Nearest-cell, not bilinear - the cascade only needs "does this
    ///     piece's bottom touch the ground", which does not benefit from interpolating between posts
    ///     the way a smooth walking surface would.
    /// </summary>
    public static float? GetGroundHeight(float x, float y) {
        ReloadIfChanged();
        if (_heights == null) return null;

        var cx = (int) MathF.Round((x - _originX) / _cellSize);
        var cy = (int) MathF.Round((y - _originY) / _cellSize);
        if (cx < 0 || cy < 0 || cx >= _width || cy >= _height) return null;

        var value = _heights[cy * _width + cx];
        return value == NoData ? null : value;
    }

    /// <summary>
    ///     The HIGHEST baked ground within `radius` of (x, y), or null where nothing is covered -
    ///     what "is this piece resting on the world" actually has to ask, because a piece rests on
    ///     the highest ground anywhere under its footprint, not on whatever the single cell nearest
    ///     its pivot happens to read.
    ///
    ///     That distinction is not cosmetic; it was a live bug. Sampling the nearest cell only, real
    ///     ground-level placements (measured against this very file, over 317 placements recovered
    ///     from this project's own ServerCreateBuildingActor logs) read anywhere from -167 to +490
    ///     above "the ground" - so a stair standing on a slope read as almost a storey and a half up,
    ///     lost its world support, and got cascaded away the moment a neighbour was destroyed.
    ///     Taking the max over one tile collapses that scatter into a clean band: ground-level
    ///     placements then top out at +352 and the next storey up starts at +385, a gap the storey
    ///     height (384) sits exactly inside.
    /// </summary>
    public static float? GetGroundHeightUnder(float x, float y, float radius) {
        ReloadIfChanged();
        if (_heights == null) return null;

        float? highest = null;
        var step = MathF.Max(_cellSize, 1f);

        for (var dx = -radius; dx <= radius; dx += step)
        for (var dy = -radius; dy <= radius; dy += step) {
            if (GetGroundHeight(x + dx, y + dy) is not { } sample) continue;
            if (highest == null || sample > highest) highest = sample;
        }

        return highest;
    }

    private static void ReloadIfChanged() {
        try {
            var path = TablePath;

            if (!File.Exists(path)) {
                if (_loadedLength != -1) { _heights = null; _loadedLength = -1; }

                if (!_reportedMissing) {
                    _reportedMissing = true;
                    Console.WriteLine($"TerrainHeightMap: no baked heightmap found - looked in " +
                                      $"[{string.Join(", ", CandidatePaths)}]. IsSupportedByWorld falls back to " +
                                      "treating the lowest piece ever placed in each column as ground until one exists.");
                }

                return;
            }

            _reportedMissing = false;
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc == _loadedStamp && info.Length == _loadedLength) return;

            Load(path);
            _loadedStamp = info.LastWriteTimeUtc;
            _loadedLength = info.Length;
        } catch (IOException) {
            // Being written to right now - keep whatever was already loaded and try again next time.
        }
    }

    private static void Load(string path) {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadChars(4);
        if (magic is not ['T', 'H', 'M', '1']) {
            Console.WriteLine($"TerrainHeightMap: {path} does not start with the 'THM1' magic, ignoring");
            return;
        }

        _originX = reader.ReadSingle();
        _originY = reader.ReadSingle();
        _cellSize = reader.ReadSingle();
        _width = reader.ReadInt32();
        _height = reader.ReadInt32();

        if (_cellSize <= 0 || _width <= 0 || _height <= 0) {
            Console.WriteLine($"TerrainHeightMap: {path} has an invalid header (CellSize={_cellSize} " +
                              $"Width={_width} Height={_height}), ignoring");
            _heights = null;
            return;
        }

        var count = (long) _width * _height;
        var heights = new short[count];
        for (var i = 0; i < count; i++) heights[i] = reader.ReadInt16();
        _heights = heights;

        Console.WriteLine($"TerrainHeightMap: loaded {_width}x{_height} cells ({_cellSize:F0} units/cell, " +
                          $"origin=({_originX:F0},{_originY:F0})) from {path}");
    }
}
