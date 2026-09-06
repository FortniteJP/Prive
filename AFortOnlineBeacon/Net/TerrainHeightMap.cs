namespace AFortOnlineBeacon.Net;

/// <summary>
///     Ground height at an arbitrary (X, Y), read from baked grid files - the "real ground heights"
///     half of BuildingStructuralSupportSystem's IsSupportedByWorld (see that class's doc comment for
///     why a real height, not an approximation, is worth having). Nothing in this project can compute
///     a height itself: an external server has no landscape geometry, only what an offline tool bakes
///     out of the cooked map ahead of time. That tool is Tools/TerrainHeightMapBaker - and note that
///     it reads the landscape's RENDER heightmap texture, NOT
///     ULandscapeHeightfieldCollisionComponent.CollisionHeightData, which is editor-only and never
///     ships in a cooked pak; its Program.cs header carries the full derivation.
///
///     TWO GRIDS, NOT ONE, and that is the important part. The LANDSCAPE is one surface per XY and a
///     grid describes it exactly. Placed MESHES are not: a POI's first storey, its roof and the
///     terrain underneath are three surfaces at one XY, and a single-valued grid has to throw two of
///     them away. Merging the mesh pass into the landscape grid was measured against 2,397 cells that
///     players had actually walked, and it made the map WORSE where the landscape already knew the
///     answer (worse in 106 cells, better in 39) while being much better where it did not (761 cells
///     newly covered, 72% of them within 128 units) - because whatever prop stands in a cell becomes
///     "the ground" there.
///
///     So they are kept apart and the caller says which height it wants by passing the height it is
///     falling FROM: a grenade on a POI roof gets the roof, one beside the building gets the terrain.
///     Same idea as TerrainGroundTruth's levels, one layer coarser.
///
///         TERRAIN_HEIGHTMAP         the landscape grid (default: TerrainHeightMap.bin beside the binary)
///         TERRAIN_HEIGHTMAP_MESHES  the placed-mesh grid, optional (TerrainHeightMap.meshes.bin)
///
///     Both are hot-reloaded when their timestamp or length changes, the same convention
///     BuildingClassHandles uses, so a freshly baked file picks up without a restart.
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
    private const short NoData = short.MinValue;

    /// <summary>
    ///     One baked grid and its hot-reload state. Two of these exist; everything that was once
    ///     file-static lives here so the second grid could be added without a second copy of the
    ///     loader.
    /// </summary>
    private sealed class Grid {
        private readonly string[] _candidates;
        private readonly string _what;

        private float _originX, _originY, _cellSize = 1f;
        private int _width, _height;
        private short[]? _heights;

        private DateTime _loadedStamp = DateTime.MinValue;
        private long _loadedLength = -1;
        private bool _reportedMissing;

        public Grid(string environmentVariable, string defaultFileName, string what, bool announceMissing) {
            _what = what;
            AnnounceMissing = announceMissing;
            _candidates = Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } configured
                ? new[] { configured }
                : new[] { Path.Combine(AppContext.BaseDirectory, defaultFileName), defaultFileName };
        }

        /// <summary>Whether a missing file is worth a line of log - true for the landscape, which is
        /// expected to exist, false for the optional mesh grid.</summary>
        private bool AnnounceMissing { get; }

        public float CellSize => _cellSize;

        /// <summary>
        ///     Whether a file is actually loaded - and it RELOADS FIRST, because loading is lazy.
        ///     Without that this reported "no mesh grid" at startup no matter what was configured,
        ///     since nothing had queried a height yet; the file was fine and the report was wrong.
        /// </summary>
        public bool Loaded {
            get {
                ReloadIfChanged();
                return _heights != null;
            }
        }

        public float? Height(float x, float y) {
            ReloadIfChanged();
            if (_heights == null) return null;

            var cx = (int) MathF.Round((x - _originX) / _cellSize);
            var cy = (int) MathF.Round((y - _originY) / _cellSize);
            if (cx < 0 || cy < 0 || cx >= _width || cy >= _height) return null;

            var value = _heights[cy * _width + cx];
            return value == NoData ? null : value;
        }

        public bool Extent(out float minX, out float minY, out float maxX, out float maxY) {
            ReloadIfChanged();

            minX = minY = maxX = maxY = 0f;
            if (_heights == null) return false;

            minX = _originX;
            minY = _originY;
            maxX = _originX + (_width - 1) * _cellSize;
            maxY = _originY + (_height - 1) * _cellSize;
            return true;
        }

        private void ReloadIfChanged() {
            try {
                var path = _candidates.FirstOrDefault(File.Exists) ?? _candidates[0];

                if (!File.Exists(path)) {
                    if (_loadedLength != -1) { _heights = null; _loadedLength = -1; }

                    if (!_reportedMissing && AnnounceMissing) {
                        _reportedMissing = true;
                        Console.WriteLine($"TerrainHeightMap: no baked {_what} found - looked in " +
                                          $"[{string.Join(", ", _candidates)}]. IsSupportedByWorld falls back to " +
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

        private void Load(string path) {
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

            Console.WriteLine($"TerrainHeightMap: loaded {_width}x{_height} {_what} cells ({_cellSize:F0} " +
                              $"units/cell, origin=({_originX:F0},{_originY:F0})) from {path}");
        }
    }

    private static readonly Grid Landscape =
        new("TERRAIN_HEIGHTMAP", "TerrainHeightMap.bin", "landscape heightmap", announceMissing: true);

    private static readonly Grid Meshes =
        new("TERRAIN_HEIGHTMAP_MESHES", "TerrainHeightMap.meshes.bin", "placed-mesh", announceMissing: false);

    /// <summary>
    ///     Landscape ground Z at (x, y), or null when no baked data covers that point (file missing,
    ///     or outside the baked extent / a hole). Nearest-cell, not bilinear - the cascade only needs
    ///     "does this piece's bottom touch the ground", which does not benefit from interpolating
    ///     between posts the way a smooth walking surface would.
    ///
    ///     LANDSCAPE ONLY, deliberately: this is the answer to "how high is the terrain here", and
    ///     callers that want "what surface would something at this height land on" want
    ///     <see cref="GetSurfaceUnder" /> instead.
    /// </summary>
    public static float? GetGroundHeight(float x, float y) => Landscape.Height(x, y);

    /// <summary>
    ///     The world-space rectangle the baked landscape covers, or false when there is no heightmap.
    ///
    ///     Exists so callers that need "somewhere on the map" can ask the landscape rather than
    ///     hard-coding a box. FortSupplyLlamas is the first: the alternatives were the aircraft's
    ///     MapCenter/DropZoneExtent (which describe the BUS's flight, not the ground) and
    ///     DefaultMapInfo's AircraftDropZone (same thing, and centred on the origin rather than on
    ///     Athena's actual landscape). The baked extent is the one source that is literally the
    ///     terrain.
    /// </summary>
    public static bool TryGetExtent(out float minX, out float minY, out float maxX, out float maxY) =>
        Landscape.Extent(out minX, out minY, out maxX, out maxY);

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
    public static float? GetGroundHeightUnder(float x, float y, float radius) =>
        Highest(Landscape, x, y, radius, float.MaxValue);

    /// <summary>
    ///     The highest baked SURFACE - landscape or placed mesh - within `radius` of (x, y) that is at
    ///     or below `fromZ`, or null when nothing baked qualifies.
    ///
    ///     This is the query anything falling should use. Passing the height it is falling from is
    ///     what keeps a POI's roof from being handed to a grenade rolling along the ground beside the
    ///     building, and the terrain from being handed to one sitting on that roof - the two grids
    ///     hold both answers and only the caller knows which one it is asking about.
    /// </summary>
    public static float? GetSurfaceUnder(float x, float y, float radius, float fromZ, float tolerance = 128f) {
        // HOW FAR ABOVE THE CALLER A SURFACE MAY STILL COUNT, and 128 is measured rather than picked.
        // Swept against 2,397 walked cells, counting how many got an answer and how far off it was:
        //
        //     tolerance   no answer   <=32   <=128   <=512   worse
        //            32        1645    639      69      22      22
        //            96        1233    557     577      18      12
        //           128        1220    555     592      18      12
        //           256        1206    553     589      37      12
        //           384        1194    553     589      49      12
        //
        // A tight 32 refuses to answer in 425 cells where a surface sits just above the sample - a
        // grid post on a slope, a doorstep, the top of a kerb - and 96-128 converts almost all of
        // those into answers within 128 units. Past 128 the gain stops and the errors start growing,
        // which is what a tolerance reaching up to genuinely different surfaces looks like.
        var ceiling = fromZ + tolerance;

        var landscape = Highest(Landscape, x, y, radius, ceiling);
        var meshes = Highest(Meshes, x, y, radius, ceiling);

        if (landscape is not { } l) return meshes;
        if (meshes is not { } m) return landscape;
        return MathF.Max(l, m);
    }

    /// <summary>Whether a placed-mesh grid was found at all - for a startup line, so its absence is
    /// visible rather than silently halving what the server knows about the world.</summary>
    public static bool HasMeshGrid => Meshes.Loaded;

    private static float? Highest(Grid grid, float x, float y, float radius, float ceiling) {
        float? highest = null;
        var step = MathF.Max(grid.CellSize, 1f);

        for (var dx = -radius; dx <= radius; dx += step)
        for (var dy = -radius; dy <= radius; dy += step) {
            if (grid.Height(x + dx, y + dy) is not { } sample) continue;
            if (sample > ceiling) continue;
            if (highest == null || sample > highest) highest = sample;
        }

        return highest;
    }
}
