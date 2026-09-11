using AFortOnlineBeacon.Runtime;
using AFortOnlineBeacon.Core.Math;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     The map's WALLS - the one thing a height field cannot hold, and the reason projectiles fly
///     straight through a POI.
///
///     WHY A SECOND STRUCTURE AND NOT A TALLER GRID. TerrainHeightMap answers "how high is the surface
///     here", which is all a floor needs. A wall is not a surface height, it is a VOLUME a moving
///     thing may not enter, and the obvious shortcut - treat "the baked surface is above me" as a wall
///     - was measured against 2,617 cells players had walked and misfires in 13% of them, because
///     indoors the ROOF is above the player. No threshold fixes that; it is the wrong question.
///
///     WHAT IS STORED, AND WHAT IS NOT. Only meshes with NO simple collision - the terrain shells,
///     cliffs, cave mouths and merged HLOD ground that the game itself collides against with complex
///     (render triangle) collision. Everything with a hull belongs to WorldCollision, which describes
///     it exactly; baking both was 95% of this file and gave archways their thickness back, because
///     the coarse cell decided first. The two are complements now, not alternatives, and the
///     projectile sweep asks both.
///
///     Tools/TerrainHeightMapBaker --walls separates walls from floors by NORMAL: a
///     triangle whose normal is within 60 degrees of horizontal stands up, whatever height it is at,
///     and a floor or roof does not however high it sits. Those are exactly the triangles the height
///     field discards (seen from above they have no area), so the two passes partition the geometry
///     between them rather than competing. Athena comes out as 36.8M wall triangles collapsed into
///     1.06M Z spans over 488k cells - 4% of the map's cells, 8.5 MB.
///
///     Binary format (little-endian), sparse and sorted so the whole thing is three flat arrays and a
///     binary search rather than a dictionary of a million entries:
///
///         char[4]   magic = "THMW"
///         float32   OriginX, OriginY, CellSize
///         int32     Width, Height
///         int32     SpanCount
///         { int32 CellIndex, int16 ZMin, int16 ZMax } * SpanCount, ascending by CellIndex
///
///     TERRAIN_HEIGHTMAP_WALLS overrides the path; WORLD_WALL_COLLISION=0 turns the whole thing off
///     without moving files.
/// </summary>
public static class TerrainWalls {
    private static readonly string[] CandidatePaths =
        FBeaconProcess.Options.Get("TERRAIN_HEIGHTMAP_WALLS") is { Length: > 0 } configured
            ? new[] { configured }
            : new[] {
                Path.Combine(AppContext.BaseDirectory, "TerrainHeightMap.walls.bin"),
                "TerrainHeightMap.walls.bin"
            };

    private static float _originX, _originY, _cellSize = 1f;
    private static int _width, _height;

    // Three parallel arrays, ascending by cell - the file's own order, kept as-is.
    private static int[]? _cells;
    private static short[]? _zMin;
    private static short[]? _zMax;

    private static bool _loadAttempted;

    /// <summary>Off with WORLD_WALL_COLLISION=0, or simply by having no baked file.</summary>
    private static bool Enabled => FBeaconProcess.Options.Get("WORLD_WALL_COLLISION") is not "0";

    /// <summary>Whether a wall map is loaded at all - worth a startup line, since its absence is
    /// invisible otherwise and looks exactly like "the walls do not work".</summary>
    public static bool Loaded {
        get {
            EnsureLoaded();
            return _cells != null;
        }
    }

    /// <summary>How many spans are loaded, for that same startup line.</summary>
    public static int SpanCount {
        get {
            EnsureLoaded();
            return _cells?.Length ?? 0;
        }
    }

    /// <summary>
    ///     Whether a world wall occupies this point. `margin` widens the span vertically, which is how
    ///     a projectile with a radius is handled without storing one.
    /// </summary>
    public static bool IsSolid(FVector point, float margin = 0f) {
        EnsureLoaded();
        if (_cells is not { Length: > 0 } cells || _zMin == null || _zMax == null) return false;

        var cx = (int) MathF.Round((point.X - _originX) / _cellSize);
        var cy = (int) MathF.Round((point.Y - _originY) / _cellSize);
        if (cx < 0 || cy < 0 || cx >= _width || cy >= _height) return false;

        var key = cy * _width + cx;
        var index = Array.BinarySearch(cells, key);
        if (index < 0) return false;

        // BinarySearch lands on ANY of the equal entries - a cell usually has several spans (a wall
        // above a doorway and the threshold below it are two), so walk back to the first.
        while (index > 0 && cells[index - 1] == key) index--;

        for (; index < cells.Length && cells[index] == key; index++)
            if (point.Z >= _zMin[index] - margin && point.Z <= _zMax[index] + margin) return true;

        return false;
    }

    /// <summary>
    ///     The first point along `from` -> `to` that is inside a wall, and which axis it entered on
    ///     (0 = X, 1 = Y, 2 = Z) - the same shape BuildingStructuralSupportSystem.SweepToBuild
    ///     returns, so a caller can bounce off a world wall exactly the way it bounces off a player's.
    ///
    ///     SAMPLED, NOT SOLVED. The spans are a cell grid, so a stepped sample at half a cell cannot
    ///     miss a wall that is a whole cell wide, and solving the exact crossing plane would be false
    ///     precision over data that is quantized to 100 units anyway.
    /// </summary>
    public static (FVector Point, int Axis)? Sweep(FVector from, FVector to, float margin = 0f) {
        EnsureLoaded();
        if (_cells == null) return null;

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;

        var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (distance < 0.0001f) return null;

        var steps = (int) MathF.Ceiling(distance / (_cellSize * 0.5f));
        if (steps < 1) steps = 1;

        var previous = from;

        for (var i = 1; i <= steps; i++) {
            var t = (float) i / steps;
            var point = new FVector { X = from.X + dx * t, Y = from.Y + dy * t, Z = from.Z + dz * t };

            if (!IsSolid(point, margin)) { previous = point; continue; }

            // WHICH FACE. The axis whose CELL changed between the last free sample and this one is the
            // one the wall was entered through; when both changed, the faster axis is the one that
            // did it. Z is never chosen - these are vertical faces by construction, and a projectile
            // that is inside one vertically has come in horizontally.
            var cellChangedX = (int) MathF.Round((point.X - _originX) / _cellSize) !=
                               (int) MathF.Round((previous.X - _originX) / _cellSize);
            var cellChangedY = (int) MathF.Round((point.Y - _originY) / _cellSize) !=
                               (int) MathF.Round((previous.Y - _originY) / _cellSize);

            var axis = cellChangedX && cellChangedY
                ? MathF.Abs(dx) >= MathF.Abs(dy) ? 0 : 1
                : cellChangedX ? 0
                : cellChangedY ? 1
                : MathF.Abs(dx) >= MathF.Abs(dy) ? 0 : 1;

            return (previous, axis);
        }

        return null;
    }

    /// <summary>
    ///     Loads once, and makes every OTHER caller wait for that load to finish rather than read a
    ///     half-built grid. The body used to set its "attempted" flag first and then load - fine with
    ///     one world, a race with two: both worlds start together in the in-process host, and the
    ///     second would see the flag, skip the load, and query empty data (a wall that briefly is not
    ///     there). The flag here is only set once the load is over, success or failure.
    /// </summary>
    private static void EnsureLoaded() {
        if (_loadCompleted) return;
        lock (LoadGate) {
            if (_loadCompleted) return;
            try { EnsureLoadedCore(); } finally { _loadCompleted = true; }
        }
    }

    private static readonly object LoadGate = new();
    private static volatile bool _loadCompleted;

    private static void EnsureLoadedCore() {
        if (_loadAttempted) return;
        _loadAttempted = true;

        if (!Enabled) {
            Console.WriteLine("TerrainWalls: WORLD_WALL_COLLISION=0 - the map's walls are not loaded, so " +
                              "projectiles pass through them.");
            return;
        }

        var path = CandidatePaths.FirstOrDefault(File.Exists);
        if (path == null) {
            Console.WriteLine($"TerrainWalls: no baked wall map found - looked in " +
                              $"[{string.Join(", ", CandidatePaths)}]. Projectiles will pass through the " +
                              "map's walls (player builds are unaffected - those have their own collision). " +
                              "Bake one with TerrainHeightMapBaker --meshes --walls.");
            return;
        }

        try {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            var magic = reader.ReadChars(4);
            if (magic is not ['T', 'H', 'M', 'W']) {
                Console.WriteLine($"TerrainWalls: {path} does not start with the 'THMW' magic, ignoring");
                return;
            }

            _originX = reader.ReadSingle();
            _originY = reader.ReadSingle();
            _cellSize = reader.ReadSingle();
            _width = reader.ReadInt32();
            _height = reader.ReadInt32();
            var count = reader.ReadInt32();

            if (_cellSize <= 0 || _width <= 0 || _height <= 0 || count < 0) {
                Console.WriteLine($"TerrainWalls: {path} has an invalid header, ignoring");
                return;
            }

            var cells = new int[count];
            var zMin = new short[count];
            var zMax = new short[count];

            for (var i = 0; i < count; i++) {
                cells[i] = reader.ReadInt32();
                zMin[i] = reader.ReadInt16();
                zMax[i] = reader.ReadInt16();
            }

            _cells = cells;
            _zMin = zMin;
            _zMax = zMax;

            Console.WriteLine($"TerrainWalls: loaded {count:N0} wall span(s) ({_cellSize:F0} units/cell) " +
                              $"from {path}.");
        } catch (Exception ex) {
            Console.WriteLine($"TerrainWalls: could not read {path} ({ex.GetType().Name}: {ex.Message}).");
        }
    }
}
