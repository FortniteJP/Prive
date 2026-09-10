namespace AFortOnlineBeacon.Net;

/// <summary>
///     Where every destructible MAP actor is, and what the client calls it.
///
///     WHY THIS HAS TO BE BAKED. This server can already damage and destroy a piece of map scenery -
///     see NativeRpcHandlers.DamageLevelActor - but only ever in ANSWER to a client, because the
///     actor's path is something the client supplies when it reports a hit. The actors themselves
///     live in streaming sublevels this server never loads, so at runtime it cannot know a tree
///     exists at a given place, let alone what to call it. Anything the SERVER wants to destroy on
///     its own initiative - a shockwave grenade throwing somebody through a wall - has to have been
///     told in advance. That is this file: 59,722 actors and their paths, swept out of the shipped
///     maps by `pakreader mapprops` and Tools/MapProps/gen_map_props.py.
///
///     EACH PROP CARRIES ITS SIZE, and version 1 of this file did not - which is what "the floor
///     above me still does not break" was. With only a position, "did the sweep hit it" has to become
///     "is the pivot within some fixed radius of the line", and a 512-unit floor tile's pivot is up
///     to 362 units away (half its diagonal) while the tile itself is directly overhead. Measured
///     misses of 361, 382 and 394 against a 320 radius were exactly that, and no single radius fixes
///     it without also destroying barrels four metres away.
///
///     So a prop is a CYLINDER - a horizontal radius and a Z half-height, the union of its own
///     StaticMeshComponents' mesh bounds, baked by `pakreader mapprops`. A cylinder rather than a box
///     because it needs no rotation: an actor's yaw does not change it, so nothing here has to carry
///     or compose one. It over-reports at the corners of a square tile by at most 40%, which is the
///     recoverable direction.
///
///     The sweep is still approximated as a BOX around that cylinder, tested with the same slab test
///     the building sweep uses. <see cref="PropMargin" /> is what the capsule itself adds.
///
///     Loaded lazily and never reloaded, exactly as the other TerrainHeightMap bakes are - see
///     WorldCollision, whose file layout and broadphase this follows deliberately.
/// </summary>
public static class FortMapProps {
    private static readonly string[] CandidatePaths =
        Environment.GetEnvironmentVariable("TERRAIN_HEIGHTMAP_PROPS") is { Length: > 0 } configured
            ? new[] { configured }
            : new[] {
                Path.Combine(AppContext.BaseDirectory, "TerrainHeightMap.props.bin"),
                "TerrainHeightMap.props.bin"
            };

    /// <summary>
    ///     One placed actor: its COOKED package, its actor name, and where it is.
    ///
    ///     THE PATH IS BUILT PER CLIENT, not stored, and that is the whole reason this record has
    ///     three fields instead of one. Fortnite streams POI sublevels as INSTANCES, so the package
    ///     the client is holding is not the one on disk - see
    ///     <see cref="PathFor" /> and UNetConnection.ClientLevelInstances.
    /// </summary>
    public sealed record FMapProp(string Package, string Name, FVector Location,
                                  float RadiusXY, float CenterZ, float HalfZ) {
        /// <summary>The cooked path, for logs and for a level that is not instanced.</summary>
        public string CookedPath => Build(Package);

        /// <summary>
        ///     The path THIS CONNECTION would resolve, which is the only one worth sending it.
        ///
        ///     Only the PACKAGE is swapped. The world object inside it keeps its original name -
        ///     `/Temp/Game/.../Athena_POI_Lobby_004_3f5ab45c.Athena_POI_Lobby_004:PersistentLevel.Foo`
        ///     - which is not a detail, it is the shape a real client-supplied path has and the one
        ///     that resolves.
        ///
        ///     Null when the client has not reported this level at all: there is nothing there for it
        ///     to resolve, and opening a channel anyway is exactly the ActorChannelFailure this
        ///     lookup exists to stop.
        /// </summary>
        public string? PathFor(UNetConnection connection) {
            if (connection.ClientLevelInstances.TryGetValue(Package, out var instanced)) return Build(instanced);

            // Not instanced: the cooked package IS what the client loaded, so it only has to say it
            // has it. Ordinary grid sublevels arrive this way (package and file names identical).
            return connection.ClientVisibleLevelNames.Contains(Package) ? Build(Package) : null;
        }

        private string Build(string package) {
            var leaf = Package[(Package.LastIndexOf('/') + 1)..];
            return $"{package}.{leaf}:PersistentLevel.{Name}";
        }
    }

    private static FMapProp[]? _props;

    /// <summary>
    ///     BROADPHASE, for the same reason WorldCollision has one: without it every destruction sweep
    ///     would measure 59,722 distances. Props are points, so a plain point grid is enough - no
    ///     instance is ever in two cells.
    /// </summary>
    private static Dictionary<(int X, int Y), List<int>>? _grid;

    /// <summary>The biggest prop radius in the bake - what the broadphase has to reach out by.</summary>
    private static float _largestRadius;

    private const float CellSize = 2048f;

    private static bool _loadAttempted;

    /// <summary>
    ///     What the SWEEP adds to each prop's own size - the capsule's radius, because that is what
    ///     it is. Defaults to the projectile Blueprint's own 80.
    ///
    ///     DELIBERATELY NOT CALLED SHOCKWAVE_PROP_RADIUS, which is what the old knob was: that one
    ///     meant "the whole radius, for every prop alike", and a value tuned for it (320) would read
    ///     here as 320 units of slack ON TOP of a floor tile's own 362 and clear half a POI. A knob
    ///     whose meaning changed is worse than a knob that was renamed.
    /// </summary>
    public static float PropMargin =>
        float.TryParse(Environment.GetEnvironmentVariable("SHOCKWAVE_PROP_MARGIN"), out var margin)
            ? margin
            : 80f;

    /// <summary>
    ///     Every destructible map actor the swept capsule touches, nearest first. Empty when the bake
    ///     is missing, which is a degraded server rather than a broken one - player builds are
    ///     unaffected.
    /// </summary>
    public static List<FMapProp> Along(FVector from, FVector to, float margin) =>
        Along(from, to, margin, out _);

    /// <summary>
    ///     As <see cref="Along(FVector, FVector, float)" />, and also hands back the CLOSEST few that
    ///     did not qualify.
    ///
    ///     A miss has to be quantified or it cannot be told from an empty bake: "no props within 320
    ///     of the line" and "the nearest prop was 40 units away and something else is wrong" are
    ///     different problems, and an empty list says both.
    /// </summary>
    public static List<FMapProp> Along(FVector from, FVector to, float margin,
                                       out List<(FMapProp Prop, float Distance)> nearMisses) {
        nearMisses = new List<(FMapProp, float)>();

        Load();
        if (_props is not { Length: > 0 } props || _grid is not { } grid) return new List<FMapProp>();

        // The broadphase has to cover the BIGGEST prop's reach, not the sweep's own - a floor tile
        // 362 units off the line is still touched by it. Anything narrower would reintroduce the
        // very miss the extents were baked to fix, one level up.
        var reach = margin + _largestRadius;

        var minX = MathF.Min(from.X, to.X) - reach;
        var maxX = MathF.Max(from.X, to.X) + reach;
        var minY = MathF.Min(from.Y, to.Y) - reach;
        var maxY = MathF.Max(from.Y, to.Y) + reach;

        var hits = new List<(FMapProp Prop, float Distance)>();

        for (var cellX = (int) MathF.Floor(minX / CellSize); cellX <= (int) MathF.Floor(maxX / CellSize); cellX++)
        for (var cellY = (int) MathF.Floor(minY / CellSize); cellY <= (int) MathF.Floor(maxY / CellSize); cellY++) {
            if (!grid.TryGetValue((cellX, cellY), out var bucket)) continue;

            foreach (var index in bucket) {
                var prop = props[index];
                var distance = DistanceToSegment(prop.Location, from, to);

                if (Touches(prop, from, to, margin)) hits.Add((prop, distance));
                else nearMisses.Add((prop, distance));
            }
        }

        nearMisses = nearMisses.OrderBy(miss => miss.Distance).Take(5).ToList();
        return hits.OrderBy(hit => hit.Distance).Select(hit => hit.Prop).ToList();
    }

    /// <summary>
    ///     Whether the swept capsule reaches this prop: the ordinary slab test against the prop's own
    ///     box, grown by the capsule. The same reduction BuildingStructuralSupportSystem uses for a
    ///     player build, so the two populations are judged the same way.
    /// </summary>
    private static bool Touches(FMapProp prop, FVector from, FVector to, float margin) {
        var reachXY = prop.RadiusXY + margin;
        var reachZ = prop.HalfZ + margin;

        // CENTRED ON THE BOX, NOT ON THE PIVOT. A wall or a tree is authored with its pivot on the
        // floor, so its bounds sit entirely above it; a box hung on the pivot would be half
        // underground and stop halfway up the real thing. See FMapProp.CenterZ.
        var centreZ = prop.Location.Z + prop.CenterZ;

        var d = new[] { to.X - from.X, to.Y - from.Y, to.Z - from.Z };
        var o = new[] { from.X, from.Y, from.Z };
        var lo = new[] { prop.Location.X - reachXY, prop.Location.Y - reachXY, centreZ - reachZ };
        var hi = new[] { prop.Location.X + reachXY, prop.Location.Y + reachXY, centreZ + reachZ };

        var enter = 0f;
        var exit = 1f;

        for (var axis = 0; axis < 3; axis++) {
            if (MathF.Abs(d[axis]) < 1e-6f) {
                if (o[axis] < lo[axis] || o[axis] > hi[axis]) return false;
                continue;
            }

            var t1 = (lo[axis] - o[axis]) / d[axis];
            var t2 = (hi[axis] - o[axis]) / d[axis];
            if (t1 > t2) (t1, t2) = (t2, t1);

            enter = MathF.Max(enter, t1);
            exit = MathF.Min(exit, t2);
            if (enter > exit) return false;
        }

        return true;
    }

    /// <summary>How many props the bake holds - 0 when it never loaded, which the caller reports.</summary>
    public static int Count {
        get {
            Load();
            return _props?.Length ?? 0;
        }
    }

    private static float DistanceToSegment(FVector point, FVector from, FVector to) {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;

        var lengthSquared = dx * dx + dy * dy + dz * dz;
        if (lengthSquared < 1e-6f) return MathF.Sqrt(FVector.DistSquared(point, from));

        var t = ((point.X - from.X) * dx + (point.Y - from.Y) * dy + (point.Z - from.Z) * dz) / lengthSquared;
        t = MathF.Max(0f, MathF.Min(1f, t));

        var closest = new FVector { X = from.X + dx * t, Y = from.Y + dy * t, Z = from.Z + dz * t };
        return MathF.Sqrt(FVector.DistSquared(point, closest));
    }

    private static void Load() {
        if (_loadAttempted) return;
        _loadAttempted = true;

        var path = CandidatePaths.FirstOrDefault(File.Exists);
        if (path == null) {
            Console.WriteLine("FortMapProps: no TerrainHeightMap.props.bin " +
                              $"(looked in {string.Join(", ", CandidatePaths)}) - the server can still destroy " +
                              "PLAYER builds, but nothing on the map. Bake it with Tools/MapProps/gen_map_props.py.");
            return;
        }

        try {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (new string(reader.ReadChars(4)) != "THMP") {
                Console.WriteLine($"FortMapProps: '{path}' is not a prop bake (bad magic) - ignoring it.");
                return;
            }

            // EVERY VERSION HAS CHANGED THE ENTRY SIZE - v2 added the extents, v3 the Z centre -
            // so refusing an older file outright is the point: read as the current version, an older
            // one is not "slightly wrong sizes", it is every field after the first prop shifted.
            var version = reader.ReadInt32();
            if (version != 3) {
                Console.WriteLine($"FortMapProps: '{path}' is version {version}, this build reads 3 - " +
                                  "re-bake it with Tools/MapProps/gen_map_props.py. Map props are off.");
                return;
            }

            var packages = new string[reader.ReadInt32()];
            for (var i = 0; i < packages.Length; i++) {
                packages[i] = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
            }

            var props = new FMapProp[reader.ReadInt32()];
            var grid = new Dictionary<(int X, int Y), List<int>>();

            for (var i = 0; i < props.Length; i++) {
                var package = packages[reader.ReadInt32()];
                var location = new FVector { X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle() };
                var radiusXY = reader.ReadSingle();
                var centerZ = reader.ReadSingle();
                var halfZ = reader.ReadSingle();
                var name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));

                props[i] = new FMapProp(package, name, location, radiusXY, centerZ, halfZ);
                if (radiusXY > _largestRadius) _largestRadius = radiusXY;

                var cell = ((int) MathF.Floor(location.X / CellSize), (int) MathF.Floor(location.Y / CellSize));
                if (!grid.TryGetValue(cell, out var bucket)) grid[cell] = bucket = new List<int>();
                bucket.Add(i);
            }

            _props = props;
            _grid = grid;

            Console.WriteLine($"FortMapProps: loaded {props.Length} destructible map actor(s) in " +
                              $"{packages.Length} package(s) from {path} " +
                              $"(largest radius {_largestRadius:F0})");
        } catch (Exception ex) {
            Console.WriteLine($"FortMapProps: could not read '{path}' - {ex.Message}. Player builds are unaffected.");
        }
    }
}
