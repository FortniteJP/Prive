using AFortOnlineBeacon.Runtime;
using AFortOnlineBeacon.Core.Math;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     The map's REAL collision - the convex shapes the game itself uses, placed where the game places
///     them - and swept exactly rather than sampled.
///
///     WHY THIS REPLACES THE VOXEL WALLS. Everything else this server knows about the world's shape is
///     an approximation baked out of RENDER geometry, and it was built on a note that turned out to be
///     false: that Fortnite's meshes carry no simple collision, so the render mesh must be the
///     collision. Measured, 86-96% of the meshes in a POI carry simple collision, and not one of them
///     sets a trace flag - they are all CTF_UseDefault, which means a projectile sweep hits those
///     SIMPLE shapes. So the voxel wall map approximates, at 340 times the data, a thing the CLIENT
///     does not use for the query being asked. `Athena_POI_Lobby_004` is 597 collision primitives
///     against 202,943 render triangles.
///
///     WHAT THAT BUYS, beyond agreeing with the client:
///
///       * NO CELL THICKNESS. A voxel wall is as thick as a cell, which is why an archway grabbed
///         projectiles half a metre before its edge. A hull is the wall.
///       * A REAL SURFACE NORMAL at the hit, so a bounce can be computed the way
///         UProjectileMovementComponent does instead of being snapped to an axis.
///       * Foliage for free and correct: a tree's own collision hull IS its trunk, so the canopy
///         problem that the height-field bake needs a name filter for does not arise.
///
///     Baked by Tools/TerrainHeightMapBaker --meshes --hulls. TERRAIN_HEIGHTMAP_HULLS overrides the
///     path; WORLD_HULL_COLLISION=0 turns it off.
///
///         char[4]  magic = "THMH"
///         int32    ShapeCount
///           per shape: float6 local bounds, int32 HullCount
///             per hull: int32 PlaneCount, then PlaneCount * float4 (nx, ny, nz, d); inside is n.p &lt;= d
///         int32    InstanceCount
///           per instance: int32 ShapeId, float3 Translation, float4 Rotation (xyzw), float3 Scale
/// </summary>
public static class WorldCollision {
    private static readonly string[] CandidatePaths =
        FBeaconProcess.Options.Get("TERRAIN_HEIGHTMAP_HULLS") is { Length: > 0 } configured
            ? new[] { configured }
            : new[] {
                Path.Combine(AppContext.BaseDirectory, "TerrainHeightMap.hulls.bin"),
                "TerrainHeightMap.hulls.bin"
            };

    /// <summary>One convex body: its face planes, in the shape's own local space.</summary>
    private sealed record Hull(float[] Planes);

    private sealed record Shape(Hull[] Hulls, FVector Min, FVector Max);

    /// <summary>
    ///     One placement. The rotation is four bare floats rather than a quaternion type because this
    ///     project's FQuat is an empty placeholder - the arithmetic is a dozen lines and lives at the
    ///     bottom of this file rather than growing a maths type nothing else needs yet.
    /// </summary>
    private sealed record Instance(int Shape, FVector Translation,
                                   float Qx, float Qy, float Qz, float Qw, FVector Scale,
                                   FVector WorldMin, FVector WorldMax);

    private static Shape[]? _shapes;
    private static Instance[]? _instances;

    /// <summary>
    ///     BROADPHASE. A uniform grid of instance indices - without one, a single grenade step would
    ///     test every placed shape on the map. Cells are deliberately large: an instance is registered
    ///     in every cell its world box touches, so a small cell size multiplies the registrations of
    ///     big shapes rather than saving work.
    /// </summary>
    private static Dictionary<(int X, int Y), List<int>>? _grid;

    private const float GridCell = 1024f;

    /// <summary>How many broadphase cells an instance may occupy before it is treated as "large".</summary>
    private const int MaxGridCellsPerInstance = 256;

    /// <summary>Instances too big for the grid - tested on every query. See the loader.</summary>
    private static int[]? _large;

    private static bool _loadAttempted;

    private static bool Enabled => FBeaconProcess.Options.Get("WORLD_HULL_COLLISION") is not "0";

    public static bool Loaded {
        get {
            EnsureLoaded();
            return _instances is { Length: > 0 };
        }
    }

    public static int InstanceCount {
        get {
            EnsureLoaded();
            return _instances?.Length ?? 0;
        }
    }

    public static int ShapeCount {
        get {
            EnsureLoaded();
            return _shapes?.Length ?? 0;
        }
    }

    /// <summary>
    ///     The first world surface the segment `from` -> `to` enters, as the point just before contact
    ///     and the surface NORMAL there, or null when the segment is clear.
    ///
    ///     Exact, not sampled: the segment is transformed into each candidate shape's local space and
    ///     clipped against that hull's half-spaces, which yields the entry parameter and the plane that
    ///     produced it in closed form. A sampled sweep can step over a thin wall; this cannot.
    /// </summary>
    public static (FVector Point, FVector Normal)? Sweep(FVector from, FVector to) {
        EnsureLoaded();
        if (_instances is not { Length: > 0 } instances || _shapes is not { } shapes || _grid is not { } grid)
            return null;

        var segMinX = MathF.Min(from.X, to.X);
        var segMaxX = MathF.Max(from.X, to.X);
        var segMinY = MathF.Min(from.Y, to.Y);
        var segMaxY = MathF.Max(from.Y, to.Y);
        var segMinZ = MathF.Min(from.Z, to.Z);
        var segMaxZ = MathF.Max(from.Z, to.Z);

        var bestT = float.MaxValue;
        FVector bestNormal = default;
        var hitSomething = false;

        // The oversized instances first - they are outside the grid by construction.
        foreach (var index in _large ?? [])
            if (TestInstance(instances[index], shapes, from, to, ref bestT, ref bestNormal)) hitSomething = true;

        var cx0 = (int) MathF.Floor(segMinX / GridCell);
        var cx1 = (int) MathF.Floor(segMaxX / GridCell);
        var cy0 = (int) MathF.Floor(segMinY / GridCell);
        var cy1 = (int) MathF.Floor(segMaxY / GridCell);

        // A step is short, so this is a handful of cells; a long segment simply looks at more.
        for (var cy = cy0; cy <= cy1; cy++)
        for (var cx = cx0; cx <= cx1; cx++) {
            if (!grid.TryGetValue((cx, cy), out var bucket)) continue;

            foreach (var index in bucket)
                if (TestInstance(instances[index], shapes, from, to, ref bestT, ref bestNormal))
                    hitSomething = true;
        }

        if (!hitSomething) return null;

        // Just BEFORE the surface, not on it - starting the next step exactly on a face is how a
        // projectile ends up inside one.
        //
        // The back-off is a fixed WORLD distance, not a fraction of the segment. As a fraction it
        // scaled with the step: 0.001 of a 2,000-unit sweep is two units short of the wall, which the
        // self-test caught as a hit reported at 948 instead of 950. A tenth of a unit is enough to
        // stay outside the face and small enough to be invisible.
        var length = MathF.Sqrt((to.X - from.X) * (to.X - from.X) +
                                (to.Y - from.Y) * (to.Y - from.Y) +
                                (to.Z - from.Z) * (to.Z - from.Z));
        var backedOff = MathF.Max(0f, bestT - (length > 0.0001f ? 0.1f / length : 0f));
        return (new FVector {
            X = from.X + (to.X - from.X) * backedOff,
            Y = from.Y + (to.Y - from.Y) * backedOff,
            Z = from.Z + (to.Z - from.Z) * backedOff
        }, Normalize(bestNormal));
    }

    /// <summary>One instance against the segment; updates the running best and says whether it hit.</summary>
    private static bool TestInstance(Instance instance, Shape[] shapes, FVector from, FVector to,
                                     ref float bestT, ref FVector bestNormal) {
        var segMinX = MathF.Min(from.X, to.X); var segMaxX = MathF.Max(from.X, to.X);
        var segMinY = MathF.Min(from.Y, to.Y); var segMaxY = MathF.Max(from.Y, to.Y);
        var segMinZ = MathF.Min(from.Z, to.Z); var segMaxZ = MathF.Max(from.Z, to.Z);

        if (instance.WorldMax.X < segMinX || instance.WorldMin.X > segMaxX ||
            instance.WorldMax.Y < segMinY || instance.WorldMin.Y > segMaxY ||
            instance.WorldMax.Z < segMinZ || instance.WorldMin.Z > segMaxZ) return false;

        // Into the shape's own space, where its planes live. Uniform-ish scale is assumed for the
        // normal, which is true of placed props; a non-uniform scale would need the inverse
        // transpose, and getting it slightly wrong costs a slightly rotated bounce.
        var localFrom = ToLocal(instance, from);
        var localTo = ToLocal(instance, to);
        var hitAny = false;

        foreach (var hull in shapes[instance.Shape].Hulls) {
            if (ClipSegment(hull.Planes, localFrom, localTo) is not { } hit || hit.T >= bestT) continue;

            bestT = hit.T;
            bestNormal = Rotate(instance, hit.Normal);
            hitAny = true;
        }

        return hitAny;
    }

    /// <summary>Whether a point is inside any world collision shape.</summary>
    public static bool IsSolid(FVector point) {
        EnsureLoaded();
        if (_instances is not { Length: > 0 } instances || _shapes is not { } shapes || _grid is not { } grid)
            return false;

        var key = ((int) MathF.Floor(point.X / GridCell), (int) MathF.Floor(point.Y / GridCell));
        var candidates = grid.TryGetValue(key, out var bucket)
            ? bucket.Concat(_large ?? [])
            : (_large ?? []).AsEnumerable();

        foreach (var index in candidates) {
            var instance = instances[index];

            if (point.X < instance.WorldMin.X || point.X > instance.WorldMax.X ||
                point.Y < instance.WorldMin.Y || point.Y > instance.WorldMax.Y ||
                point.Z < instance.WorldMin.Z || point.Z > instance.WorldMax.Z) continue;

            var local = ToLocal(instance, point);

            foreach (var hull in shapes[instance.Shape].Hulls) {
                var inside = true;
                for (var i = 0; i < hull.Planes.Length; i += 4) {
                    var side = hull.Planes[i] * local.X + hull.Planes[i + 1] * local.Y +
                               hull.Planes[i + 2] * local.Z - hull.Planes[i + 3];
                    if (side <= 0f) continue;
                    inside = false;
                    break;
                }

                if (inside) return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Clips a segment against a convex body's half-spaces - the standard slab method generalised
    ///     to arbitrary planes. Returns the entry parameter along the segment and the plane normal
    ///     that produced it, or null when the segment misses the body entirely.
    ///
    ///     A segment that STARTS INSIDE the body reports nothing, and that is the behaviour rather
    ///     than an oversight: every plane is already behind it, so there is no entry crossing to
    ///     report. The consequence is worth knowing - something that has somehow got inside a shape
    ///     passes out of it freely instead of being pushed back - and it is the right trade here,
    ///     because the alternative (ejecting toward the nearest face) turns one bad frame into a
    ///     projectile flung across the map.
    /// </summary>
    private static (float T, FVector Normal)? ClipSegment(float[] planes, FVector from, FVector to) {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;

        var enter = 0f;
        var exit = 1f;
        FVector enterNormal = default;
        var haveNormal = false;

        for (var i = 0; i < planes.Length; i += 4) {
            var nx = planes[i];
            var ny = planes[i + 1];
            var nz = planes[i + 2];
            var d = planes[i + 3];

            var distance = nx * from.X + ny * from.Y + nz * from.Z - d;
            var rate = nx * dx + ny * dy + nz * dz;

            if (MathF.Abs(rate) < 0.000001f) {
                if (distance > 0f) return null;    // parallel and outside this face
                continue;
            }

            var t = -distance / rate;

            if (rate < 0f) {
                // Moving INTO this half-space: the latest such crossing is where the body is entered.
                if (t > enter) {
                    enter = t;
                    enterNormal = new FVector { X = nx, Y = ny, Z = nz };
                    haveNormal = true;
                }
            } else {
                // Moving OUT of it: the earliest such crossing is where the body is left.
                if (t < exit) exit = t;
            }

            if (enter > exit) return null;
        }

        if (enter > exit || enter > 1f) return null;
        if (!haveNormal) return null;

        return (enter, enterNormal);
    }

    private static FVector ToLocal(Instance instance, FVector world) {
        var relative = new FVector {
            X = world.X - instance.Translation.X,
            Y = world.Y - instance.Translation.Y,
            Z = world.Z - instance.Translation.Z
        };

        var unrotated = Unrotate(instance, relative);

        return new FVector {
            X = unrotated.X / (MathF.Abs(instance.Scale.X) < 0.0001f ? 1f : instance.Scale.X),
            Y = unrotated.Y / (MathF.Abs(instance.Scale.Y) < 0.0001f ? 1f : instance.Scale.Y),
            Z = unrotated.Z / (MathF.Abs(instance.Scale.Z) < 0.0001f ? 1f : instance.Scale.Z)
        };
    }

    /// <summary>v rotated by the instance's quaternion: v + 2q_xyz x (q_xyz x v + w v).</summary>
    private static FVector Rotate(Instance i, FVector v) => RotateBy(i.Qx, i.Qy, i.Qz, i.Qw, v);

    /// <summary>v rotated by the INVERSE - the conjugate, since these are unit quaternions.</summary>
    private static FVector Unrotate(Instance i, FVector v) => RotateBy(-i.Qx, -i.Qy, -i.Qz, i.Qw, v);

    private static FVector RotateBy(float qx, float qy, float qz, float qw, FVector v) {
        var tx = 2f * (qy * v.Z - qz * v.Y);
        var ty = 2f * (qz * v.X - qx * v.Z);
        var tz = 2f * (qx * v.Y - qy * v.X);

        return new FVector {
            X = v.X + qw * tx + (qy * tz - qz * ty),
            Y = v.Y + qw * ty + (qz * tx - qx * tz),
            Z = v.Z + qw * tz + (qx * ty - qy * tx)
        };
    }

    private static FVector Normalize(FVector v) {
        var length = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return length < 0.0001f ? new FVector { Z = 1f } : new FVector { X = v.X / length, Y = v.Y / length, Z = v.Z / length };
    }

    /// <summary>
    ///     Checks the sweep against a hand-built box, with no baked file. Run with
    ///     `AFortOnlineBeacon.Test --worldcollision-selftest`.
    ///
    ///     WORTH HAVING because plane clipping, quaternion inverses and local-space transforms all
    ///     fail SILENTLY and identically - as "the grenade went through the wall", which is also what
    ///     a missing file, a wrong path and an unbaked mesh look like. These checks separate the maths
    ///     from the data.
    /// </summary>
    public static bool RunSelfTest() {
        var failures = 0;

        void Check(string what, bool ok) {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
            if (!ok) failures++;
        }

        // A 100-unit cube centred on its own origin: six planes, |n.p| <= 50 on each axis.
        var cube = new Hull(new[] {
            1f, 0f, 0f, 50f, -1f, 0f, 0f, 50f,
            0f, 1f, 0f, 50f, 0f, -1f, 0f, 50f,
            0f, 0f, 1f, 50f, 0f, 0f, -1f, 50f
        });

        _shapes = new[] {
            new Shape(new[] { cube }, new FVector { X = -50, Y = -50, Z = -50 },
                                      new FVector { X = 50, Y = 50, Z = 50 })
        };

        // Placed at (1000, 0, 0), unrotated, unscaled.
        var at = new FVector { X = 1000f, Y = 0f, Z = 0f };
        _instances = new[] {
            new Instance(0, at, 0f, 0f, 0f, 1f, new FVector { X = 1, Y = 1, Z = 1 },
                         new FVector { X = 950, Y = -50, Z = -50 },
                         new FVector { X = 1050, Y = 50, Z = 50 })
        };

        _grid = new Dictionary<(int X, int Y), List<int>>();
        for (var gx = 0; gx <= 1; gx++) _grid[(gx, 0)] = new List<int> { 0 };
        _loadAttempted = true;

        Check("a point inside the box is solid", IsSolid(new FVector { X = 1000, Y = 0, Z = 0 }));
        Check("a point outside it is not", !IsSolid(new FVector { X = 800, Y = 0, Z = 0 }));

        var head = Sweep(new FVector { X = 0, Y = 0, Z = 0 }, new FVector { X = 2000, Y = 0, Z = 0 });
        Check("a segment through the box hits it", head is not null);
        Check("it hits the NEAR face, not the far one",
              head is { } h1 && MathF.Abs(h1.Point.X - 950f) < 2f);
        Check("the normal faces back along the segment",
              head is { } h2 && h2.Normal.X < -0.9f);

        Check("a segment that stops short misses",
              Sweep(new FVector { X = 0, Y = 0, Z = 0 }, new FVector { X = 900, Y = 0, Z = 0 }) is null);
        Check("a segment beside the box misses",
              Sweep(new FVector { X = 0, Y = 500, Z = 0 }, new FVector { X = 2000, Y = 500, Z = 0 }) is null);

        var above = Sweep(new FVector { X = 1000, Y = 0, Z = 500 }, new FVector { X = 1000, Y = 0, Z = -500 });
        Check("a segment dropped onto it lands on the TOP face",
              above is { } h3 && MathF.Abs(h3.Point.Z - 50f) < 2f && h3.Normal.Z > 0.9f);

        // Rotated 90 degrees about Z, the box is symmetric - so the answer must not change.
        _instances = new[] {
            new Instance(0, at, 0f, 0f, 0.70710678f, 0.70710678f, new FVector { X = 1, Y = 1, Z = 1 },
                         new FVector { X = 950, Y = -50, Z = -50 },
                         new FVector { X = 1050, Y = 50, Z = 50 })
        };

        var rotated = Sweep(new FVector { X = 0, Y = 0, Z = 0 }, new FVector { X = 2000, Y = 0, Z = 0 });
        Check("a 90-degree rotation of a symmetric box changes nothing",
              rotated is { } h4 && MathF.Abs(h4.Point.X - 950f) < 2f && h4.Normal.X < -0.9f);

        _shapes = null;
        _instances = null;
        _grid = null;
        _loadAttempted = false;

        Console.WriteLine(failures == 0
            ? "WorldCollision self-test: all checks passed."
            : $"WorldCollision self-test: {failures} check(s) FAILED.");
        return failures == 0;
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
            Console.WriteLine("WorldCollision: WORLD_HULL_COLLISION=0 - the map's own collision shapes are " +
                              "not loaded.");
            return;
        }

        var path = CandidatePaths.FirstOrDefault(File.Exists);
        if (path == null) {
            Console.WriteLine($"WorldCollision: no baked collision hulls found - looked in " +
                              $"[{string.Join(", ", CandidatePaths)}]. Bake one with " +
                              "TerrainHeightMapBaker --meshes --hulls.");
            return;
        }

        try {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (reader.ReadChars(4) is not ['T', 'H', 'M', 'H']) {
                Console.WriteLine($"WorldCollision: {path} does not start with the 'THMH' magic, ignoring");
                return;
            }

            var shapeCount = reader.ReadInt32();
            var shapes = new Shape[shapeCount];

            for (var i = 0; i < shapeCount; i++) {
                var min = new FVector { X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle() };
                var max = new FVector { X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle() };

                var hulls = new Hull[reader.ReadInt32()];
                for (var h = 0; h < hulls.Length; h++) {
                    var planes = new float[reader.ReadInt32() * 4];
                    for (var f = 0; f < planes.Length; f++) planes[f] = reader.ReadSingle();
                    hulls[h] = new Hull(planes);
                }

                shapes[i] = new Shape(hulls, min, max);
            }

            var instanceCount = reader.ReadInt32();
            var instances = new Instance[instanceCount];
            var grid = new Dictionary<(int X, int Y), List<int>>();
            var large = new List<int>();

            for (var i = 0; i < instanceCount; i++) {
                var shapeId = reader.ReadInt32();
                var translation = new FVector { X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle() };
                var qx = reader.ReadSingle();
                var qy = reader.ReadSingle();
                var qz = reader.ReadSingle();
                var qw = reader.ReadSingle();
                var scale = new FVector { X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle() };

                // The world box, from the shape's local box's eight corners - rotation is why the
                // corners are transformed rather than the extents scaled.
                var shape = shapes[shapeId];
                float wMinX = float.MaxValue, wMinY = float.MaxValue, wMinZ = float.MaxValue;
                float wMaxX = float.MinValue, wMaxY = float.MinValue, wMaxZ = float.MinValue;

                for (var corner = 0; corner < 8; corner++) {
                    var local = new FVector {
                        X = ((corner & 1) == 0 ? shape.Min.X : shape.Max.X) * scale.X,
                        Y = ((corner & 2) == 0 ? shape.Min.Y : shape.Max.Y) * scale.Y,
                        Z = ((corner & 4) == 0 ? shape.Min.Z : shape.Max.Z) * scale.Z
                    };

                    var world = RotateBy(qx, qy, qz, qw, local);
                    wMinX = MathF.Min(wMinX, world.X + translation.X); wMaxX = MathF.Max(wMaxX, world.X + translation.X);
                    wMinY = MathF.Min(wMinY, world.Y + translation.Y); wMaxY = MathF.Max(wMaxY, world.Y + translation.Y);
                    wMinZ = MathF.Min(wMinZ, world.Z + translation.Z); wMaxZ = MathF.Max(wMaxZ, world.Z + translation.Z);
                }

                instances[i] = new Instance(shapeId, translation, qx, qy, qz, qw, scale,
                                            new FVector { X = wMinX, Y = wMinY, Z = wMinZ },
                                            new FVector { X = wMaxX, Y = wMaxY, Z = wMaxZ });

                // HUGE SHAPES GO IN A LIST, NOT IN THE GRID. Registering an instance in every cell its
                // box touches is fine for a wall panel and catastrophic for a backdrop: the first run
                // of this loader appeared to hang at startup, because a shape spanning the map
                // registers itself in millions of cells. Anything wider than this many cells is kept
                // aside and tested on every query instead - there are only a handful of them, and
                // dropping them outright would silently delete real geometry.
                var gx0 = (int) MathF.Floor(wMinX / GridCell);
                var gx1 = (int) MathF.Floor(wMaxX / GridCell);
                var gy0 = (int) MathF.Floor(wMinY / GridCell);
                var gy1 = (int) MathF.Floor(wMaxY / GridCell);

                if ((long) (gx1 - gx0 + 1) * (gy1 - gy0 + 1) > MaxGridCellsPerInstance) {
                    large.Add(i);
                    continue;
                }

                for (var gy = gy0; gy <= gy1; gy++)
                for (var gx = gx0; gx <= gx1; gx++) {
                    if (!grid.TryGetValue((gx, gy), out var bucket)) grid[(gx, gy)] = bucket = new List<int>();
                    bucket.Add(i);
                }
            }

            _shapes = shapes;
            _instances = instances;
            _grid = grid;
            _large = large.ToArray();

            Console.WriteLine($"WorldCollision: loaded {shapeCount:N0} collision shape(s) placed " +
                              $"{instanceCount:N0} time(s) from {path}" +
                              (large.Count > 0 ? $" ({large.Count} too large to index, tested every query)" : "") +
                              ".");
        } catch (Exception ex) {
            Console.WriteLine($"WorldCollision: could not read {path} ({ex.GetType().Name}: {ex.Message}).");
        }
    }
}
