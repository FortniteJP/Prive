using AFortOnlineBeacon.Core.Math;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     Records where players ACTUALLY stand, and measures the baked height map against it.
///
///     WHY THIS EXISTS. Baking the map's collision out of the paks has been an archaeology problem -
///     find the asset, apply the right transform, rasterise the right triangles - and every failure
///     looks identical from the outside: the grid has no data where a player is standing. Reading
///     more assets cannot settle whether the bake is wrong or the asset simply is not there. Only one
///     thing on this server knows the true height of the ground: the CLIENT, which walks on it, and
///     it reports its own position every ServerMoveNoBase. A pawn in EMovementMode Walking has its
///     feet ON the floor by definition, so `ClientLoc.Z - CapsuleHalfHeight` IS the height of the
///     surface at that XY - measured, not inferred, with no pak reading involved at all.
///
///     WHAT IT IS FOR. Two things, and the second is the reason it is worth writing:
///
///       * A VERDICT on a bake. "3.28M cells filled" says nothing about whether the filled cells are
///         the ones players use; "of 812 cells walked, 61% had baked data and it was within 40 units"
///         is an answer. Every walked cell is a place the bake can be checked against.
///       * A SURFACE MAP IN ITS OWN RIGHT. The samples are exactly the data a height field needs, for
///         exactly the places players go. A session's worth of walking around the warmup island
///         produces a usable floor for that island whether or not its meshes are ever found - and it
///         is correct by construction, because it came from the client's own collision.
///
///     THE SURFACE UNDER A PLAYER IS OFTEN NOT THE GROUND, and that is handled rather than assumed
///     away - see "Levels, not a height" below. A roof, a bridge deck and a POI's first floor are all
///     walkable surfaces at an XY where the terrain is far below.
///
///     WHAT IT IS NOT. Not authoritative over a cheating client (a client that lies about its
///     position writes lies in here), and not a substitute for the bake - it only knows places
///     someone has walked. It is a measuring instrument and a fallback, in that order.
///
///     OFF BY DEFAULT. TERRAIN_GROUNDTRUTH=&lt;path&gt; turns it on and names the file to append to;
///     TERRAIN_GROUNDTRUTH_CELL sets the sample grid (default 100 units, matching the bake's cells).
/// </summary>
public static class TerrainGroundTruth {
    /// <summary>
    ///     LEVELS, NOT A HEIGHT. One height per cell is wrong the moment anyone walks onto a building:
    ///     the roof is a real surface at a real XY, but so is the ground twenty metres below it, and a
    ///     single-valued height field has to throw one of them away. Keeping the roof makes grenades
    ///     hover in mid-air beside the building; keeping the ground makes them fall through the roof.
    ///
    ///     So a cell holds a small SET of distinct surface heights, and a query says which one it
    ///     wants by passing the height it is falling from. That is the same thing UE does with a
    ///     multi-level trace, at the resolution this can afford.
    /// </summary>
    private static readonly Dictionary<(int X, int Y), List<float>> Levels = new();

    private static readonly object Gate = new();

    private static string? _path;
    private static bool _resolved;
    private static StreamWriter? _writer;

    private static long _movesSeen;
    private static long _grounded;
    private static long _onPlayerBuilds;
    private static double _nextReportSeconds;

    // Agreement with the baked map, accumulated over every cell a player has stood in.
    private static int _bakedMissing;
    private static int _bakedClose;
    private static int _bakedFar;
    private static float _worstDelta;

    /// <summary>Where to write, or null when the sampler is off. Resolved once.</summary>
    private static string? Path {
        get {
            if (_resolved) return _path;
            _resolved = true;
            _path = Environment.GetEnvironmentVariable("TERRAIN_GROUNDTRUTH") is { Length: > 0 } p ? p : null;
            if (_path != null) LoadExisting(_path);
            return _path;
        }
    }

    private static float CellSize =>
        float.TryParse(Environment.GetEnvironmentVariable("TERRAIN_GROUNDTRUTH_CELL"), out var s) && s > 0 ? s : 100f;

    /// <summary>
    ///     How far apart two samples in one cell must be to count as SEPARATE surfaces rather than
    ///     two readings of the same one. Below this they are merged.
    ///
    ///     150 because a Fortnite floor piece is 384 units tall and a staircase's steps are far
    ///     shorter than that: the number has to be well under a storey so a floor above the ground is
    ///     never merged into it, and well over the wobble of one cell's worth of sloped terrain so a
    ///     hillside does not turn into a stack of levels.
    /// </summary>
    private static float LevelSeparation =>
        float.TryParse(Environment.GetEnvironmentVariable("TERRAIN_GROUNDTRUTH_LEVEL"), out var s) && s > 0 ? s : 150f;

    /// <summary>
    ///     How far a pawn's replicated location sits above its feet - the same 96 the projectile
    ///     system uses, and the same override, because they are the same measurement. See
    ///     FortProjectileSystem.CapsuleHalfHeight for why it is convention rather than read from the
    ///     paks.
    /// </summary>
    private static float CapsuleHalfHeight =>
        float.TryParse(Environment.GetEnvironmentVariable("PAWN_CAPSULE_HALF_HEIGHT"), out var s) && s > 0 ? s : 96f;

    /// <summary>At most this many distinct surfaces per cell; the lowest are kept.</summary>
    private const int MaxLevelsPerCell = 6;

    /// <summary>
    ///     One reported move. `packedMovementMode` is the client's own PackNetworkMovementMode output,
    ///     relayed by ServerMoveNoBase.
    ///
    ///     ONLY WALKING COUNTS, and that is the whole trick: falling, skydiving, swimming and gliding
    ///     all put the capsule at a height that says nothing about any surface. EMovementMode::Walking
    ///     is 1 and NavWalking is 2; both mean "standing on something". Anything at or above 16 is a
    ///     Fortnite custom mode (parachuting, skydiving) - see APawn.TrackMoveFlags for that packing.
    ///
    ///     DYNAMIC BASES ARE ALREADY EXCLUDED, for free: UCharacterMovementComponent::ServerMove
    ///     (CharacterMovementComponent.cpp:8573) only calls the NoBase variant when
    ///     `MovementBaseUtility::IsDynamicBase` is false, so a player riding a vehicle or a moving
    ///     platform reports through ServerMove instead and never reaches this function. What arrives
    ///     here is a player standing on something that is not moving.
    /// </summary>
    public static void Record(FVector location, byte packedMovementMode) {
        if (Path == null) return;

        _movesSeen++;
        if (packedMovementMode is not (1 or 2)) return;
        _grounded++;

        var groundZ = location.Z - CapsuleHalfHeight;

        // PLAYER BUILDS ARE NOT GROUND, and unlike a roof they are not worth remembering either: a
        // ramp is gone the moment someone breaks it, and this file outlives the session that recorded
        // it. Nothing else here can tell a build's surface from a POI floor - but the server placed
        // every build itself, so it can simply ask. Tested just under the feet, which is where a
        // supporting piece's box is.
        if (BuildingStructuralSupportSystem.IsSolid(
                new FVector { X = location.X, Y = location.Y, Z = groundZ - 8f })) {
            _onPlayerBuilds++;
            return;
        }

        lock (Gate) {
            if (!Merge(location.X, location.Y, groundZ)) return;

            Writer()?.WriteLine($"{location.X:F0} {location.Y:F0} {groundZ:F0}");
            Score(location.X, location.Y, groundZ);
        }
    }

    /// <summary>
    ///     Folds one sample into its cell, and says whether the cell's stored state changed - which is
    ///     what decides whether the sample is worth writing to the file.
    ///
    ///     THE LOWEST READING OF A LEVEL WINS. Within one 100-unit cell on a slope the surface varies,
    ///     and taking the lowest means a projectile resting on it can only ever be slightly LOW, never
    ///     hovering. It also makes the file order-independent: replaying the same samples in any order
    ///     rebuilds the same map.
    /// </summary>
    private static bool Merge(float x, float y, float z) {
        var cell = CellSize;
        var key = ((int) MathF.Round(x / cell), (int) MathF.Round(y / cell));

        if (!Levels.TryGetValue(key, out var levels)) {
            Levels[key] = new List<float> { z };
            return true;
        }

        var separation = LevelSeparation;

        for (var i = 0; i < levels.Count; i++) {
            if (MathF.Abs(levels[i] - z) > separation) continue;
            if (z >= levels[i]) return false;

            levels[i] = z;
            levels.Sort();
            return true;
        }

        levels.Add(z);
        levels.Sort();

        // Full: drop the HIGHEST. The lowest surfaces are the ones a falling projectile is most
        // likely to need and the ones least likely to be scenery someone climbed onto.
        if (levels.Count > MaxLevelsPerCell) levels.RemoveAt(levels.Count - 1);

        return true;
    }

    /// <summary>
    ///     Scores the baked map against one newly measured surface. Only the LOWEST level in a cell is
    ///     scored, because that is the one the bake is trying to describe - marking the bake wrong for
    ///     failing to predict the roof a player climbed onto would be scoring it against a different
    ///     question.
    /// </summary>
    private static void Score(float x, float y, float z) {
        var cell = CellSize;
        var key = ((int) MathF.Round(x / cell), (int) MathF.Round(y / cell));
        if (!Levels.TryGetValue(key, out var levels) || levels.Count == 0 || levels[0] != z) return;

        var baked = TerrainHeightMap.GetGroundHeight(x, y);
        if (baked is not { } bakedZ) {
            _bakedMissing++;
            return;
        }

        var delta = bakedZ - z;
        if (MathF.Abs(delta) <= 64f) {
            _bakedClose++;
        } else {
            _bakedFar++;
            if (MathF.Abs(delta) > MathF.Abs(_worstDelta)) _worstDelta = delta;
        }
    }

    /// <summary>
    ///     The measured surface at a world XY that something falling from <paramref name="fromZ" />
    ///     would land on, or null where nobody has walked.
    ///
    ///     THE HIGHEST SURFACE AT OR BELOW THE CALLER. This is the whole reason cells hold levels: a
    ///     grenade dropped onto a roof wants the roof, and one thrown along the ground beside the same
    ///     building wants the ground, and those are two entries in the same cell. When every known
    ///     surface is above the caller it returns null rather than teleporting it upward - the caller
    ///     has fallbacks, and inventing a floor above a projectile is the one answer that is certainly
    ///     wrong.
    ///
    ///     EXACT CELL FIRST, then the eight neighbours, because a projectile a metre outside the last
    ///     cell someone walked through should still land on that floor. Beyond one cell it gives up
    ///     rather than inventing ground: a hard edge is honest.
    /// </summary>
    public static float? GetGroundHeight(float x, float y, float fromZ) {
        if (Path == null) return null;

        lock (Gate) return GetGroundHeightUnlocked(x, y, fromZ);
    }

    /// <summary>
    ///     The highest measured surface at or below <paramref name="fromZ" /> anywhere within
    ///     <paramref name="radius" /> of an XY - the wider-area form
    ///     <see cref="TerrainHeightMap.GetGroundHeightUnder" /> exists for, and for the same reason:
    ///     a building piece rests on the highest ground under its whole TILE, not on the single
    ///     sample nearest its pivot. Sampling one cell is what made ground-level stairs read as a
    ///     storey and a half up, and then cascade away.
    /// </summary>
    public static float? GetGroundHeightUnder(float x, float y, float radius, float fromZ) {
        if (Path == null) return null;

        var cell = CellSize;
        var cx = (int) MathF.Round(x / cell);
        var cy = (int) MathF.Round(y / cell);
        var span = (int) MathF.Ceiling(radius / cell);
        var ceiling = fromZ + 32f;

        lock (Gate) {
            float? best = null;
            for (var dy = -span; dy <= span; dy++)
            for (var dx = -span; dx <= span; dx++) {
                if (!Levels.TryGetValue((cx + dx, cy + dy), out var levels)) continue;
                if (Best(levels, ceiling) is { } candidate && (best is not { } had || candidate > had))
                    best = candidate;
            }

            return best;
        }
    }

    /// <summary>The highest level at or below a ceiling, or null.</summary>
    private static float? Best(List<float> levels, float ceiling) {
        float? best = null;
        foreach (var level in levels)
            if (level <= ceiling && (best is not { } had || level > had)) best = level;

        return best;
    }

    /// <summary>
    ///     Reads a previous session's samples back in, so the ground people walked LAST time is
    ///     already known this time. This is what makes the file worth keeping rather than a log: two
    ///     sessions of walking cover twice the island, and the coverage only ever grows.
    /// </summary>
    private static void LoadExisting(string path) {
        if (!File.Exists(path)) return;

        var lines = 0;
        try {
            foreach (var line in File.ReadLines(path)) {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                if (!float.TryParse(parts[0], out var x) ||
                    !float.TryParse(parts[1], out var y) ||
                    !float.TryParse(parts[2], out var z)) continue;

                Merge(x, y, z);
                lines++;
            }
        } catch (Exception ex) {
            Console.WriteLine($"TerrainGroundTruth: could not read {path} ({ex.GetType().Name}: {ex.Message}).");
            return;
        }

        Console.WriteLine($"TerrainGroundTruth: {lines} sample(s) loaded from {path} into {Levels.Count} cell(s).");
    }

    /// <summary>
    ///     Prints the running verdict, at most every 30 seconds. Called from the world tick so the
    ///     numbers appear without anyone having to ask for them - a measurement nobody reads is not a
    ///     measurement.
    /// </summary>
    public static void Tick(double elapsedSeconds) {
        if (Path == null || Levels.Count == 0 || elapsedSeconds < _nextReportSeconds) return;
        _nextReportSeconds = elapsedSeconds + 30;

        lock (Gate) {
            var scored = _bakedMissing + _bakedClose + _bakedFar;
            if (scored == 0) return;

            // Multi-level cells are reported because they are the direct measure of how often "the
            // surface under a player" is NOT the ground - a building, a bridge, a rock someone stood
            // on. If this number is large, a single-valued height field was never going to work.
            var stacked = Levels.Count(c => c.Value.Count > 1);

            Console.WriteLine($"TerrainGroundTruth: {Levels.Count} cell(s) walked ({stacked} with more than " +
                              $"one surface) from {_grounded:N0} grounded move(s) of {_movesSeen:N0}, " +
                              $"{_onPlayerBuilds:N0} skipped on player builds; against the baked map: " +
                              $"{_bakedClose} within 64 units, {_bakedFar} off by more " +
                              $"(worst {_worstDelta:+0;-0} units), {_bakedMissing} with no data " +
                              $"({100.0 * _bakedMissing / scored:F0}% uncovered).");
        }
    }

    /// <summary>
    ///     Checks the level logic against the cases it exists for, with no client and no map data.
    ///     Run with `AFortOnlineBeacon.Test --groundtruth-selftest`.
    ///
    ///     WORTH HAVING because every failure mode here is silent and looks like something else: a
    ///     grenade that falls through a roof, or hovers beside a building, does not say "the level
    ///     query picked the wrong surface" - it looks like a physics bug, and this project has already
    ///     spent live rounds on exactly that mistake. These are the four behaviours the design claims.
    /// </summary>
    public static bool RunSelfTest() {
        var failures = 0;

        void Check(string what, bool ok) {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
            if (!ok) failures++;
        }

        lock (Gate) {
            Levels.Clear();

            // Ground at 1000 and a roof at 1600 in the same cell - the case a single height cannot
            // hold, and the reason for all of this.
            Merge(500f, 500f, 1000f);
            Merge(500f, 500f, 1600f);
            Check("a roof and the ground in one cell are two levels", Levels[(5, 5)].Count == 2);

            // Two readings of one surface are one level, and the lower one wins.
            Merge(500f, 500f, 1040f);
            Check("a second reading of the same surface does not add a level", Levels[(5, 5)].Count == 2);
            Check("the lower reading of a surface wins", MathF.Abs(Levels[(5, 5)][0] - 1000f) < 0.5f);

            Check("something falling from above the roof lands on the roof",
                  GetGroundHeightUnlocked(500f, 500f, 2000f) is { } a && MathF.Abs(a - 1600f) < 0.5f);
            Check("something below the roof lands on the ground",
                  GetGroundHeightUnlocked(500f, 500f, 1200f) is { } b && MathF.Abs(b - 1000f) < 0.5f);
            Check("something below every known surface gets no floor at all",
                  GetGroundHeightUnlocked(500f, 500f, 500f) is null);
            Check("a neighbouring cell's surface is offered",
                  GetGroundHeightUnlocked(600f, 500f, 2000f) is { } c && MathF.Abs(c - 1600f) < 0.5f);
            Check("two cells away is not",
                  GetGroundHeightUnlocked(900f, 500f, 2000f) is null);

            Levels.Clear();
        }

        Console.WriteLine(failures == 0
            ? "TerrainGroundTruth self-test: all checks passed."
            : $"TerrainGroundTruth self-test: {failures} check(s) FAILED.");
        return failures == 0;
    }

    /// <summary>
    ///     The body of <see cref="GetGroundHeight" /> without the lock or the enabled check, so the
    ///     self-test can run it while holding the lock and without a configured file.
    /// </summary>
    private static float? GetGroundHeightUnlocked(float x, float y, float fromZ) {
        var cell = CellSize;
        var cx = (int) MathF.Round(x / cell);
        var cy = (int) MathF.Round(y / cell);
        // A little tolerance so something that has already sunk a hair below a surface still snaps to
        // it rather than falling through to the next one down.
        var ceiling = fromZ + 32f;

        if (Levels.TryGetValue((cx, cy), out var exact)) return Best(exact, ceiling);

        float? best = null;
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++) {
            if (!Levels.TryGetValue((cx + dx, cy + dy), out var neighbour)) continue;
            if (Best(neighbour, ceiling) is { } candidate && (best is not { } had || candidate > had))
                best = candidate;
        }

        return best;
    }

    private static StreamWriter? Writer() {
        if (_writer != null) return _writer;
        if (Path is not { } path) return null;

        try {
            // APPEND: several sessions of walking around add up to better coverage than any one of
            // them, and there is nothing in a sample that goes stale.
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
            Console.WriteLine($"TerrainGroundTruth: recording walked surfaces to {path} " +
                              $"({CellSize:F0}-unit cells, {LevelSeparation:F0}-unit level separation).");
            return _writer;
        } catch (Exception ex) {
            Console.WriteLine($"TerrainGroundTruth: cannot write {path} ({ex.GetType().Name}: {ex.Message}) - " +
                              "sampling disabled.");
            _path = null;
            return null;
        }
    }
}
