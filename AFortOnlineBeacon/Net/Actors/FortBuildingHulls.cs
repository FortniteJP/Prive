using AFortOnlineBeacon.Core.Math;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     The real shape of a player-built piece, and the segment test that uses it.
///
///     WHAT THIS REPLACES. Every built piece used to be one axis-aligned box chosen by its TYPE: a
///     wall was a solid slab, a stair was a solid cell, and every variant of a type shared the same
///     box. So a doorway was solid whether the door was open or shut, a window was solid glass, and
///     editing a piece into a different shape changed nothing about what it blocked.
///
///     None of that has to be approximated. These meshes carry simple collision and are flagged
///     `CTF_UseSimpleAsComplex`, so the convex hulls in the asset ARE what the client traces against
///     - a doorway and a window are genuine HOLES in them. The bake (Tools/TerrainHeightMapBaker
///     --buildpieces) reads them straight out of the paks, and the leaf of an openable door is kept
///     apart from the wall's body so it can be included only while the door is SHUT.
///
///     Hulls are half-spaces in the piece's LOCAL space. A segment is brought into that space -
///     translate, un-rotate by the piece's yaw, un-mirror - and clipped, which gives an exact entry
///     point and the surface normal in closed form. That is the same method WorldCollision uses for
///     the map, and for the same reason: a sampled sweep steps over a thin wall, and a box can only
///     ever say "somewhere in this volume".
/// </summary>
public static partial class FortBuildingHulls {
    /// <summary>
    ///     The hulls and local bounds for a class, or null when the bake has never heard of it - a
    ///     map piece, or a class added after the bake ran. The caller falls back to the coarse box.
    /// </summary>
    public static (float[][] Body, float[][] Door, FVector Min, FVector Max)? For(string? className) {
        if (className == null) return null;
        return Lookup.TryGetValue(className, out var piece) ? piece : null;
    }

    /// <summary>
    ///     Where the segment first enters this piece, as the fraction along it and the world-space
    ///     surface normal there - or null when it misses.
    ///
    ///     <paramref name="includeDoorLeaf"/> is what an open door means: the wall's own hulls always
    ///     apply, the leaf's do not while it is swung out of the way. Nothing else about an open door
    ///     changes, because nothing else about it moves.
    /// </summary>
    public static (float T, FVector Normal)? Sweep(ABuildingActor piece, FVector from, FVector to,
                                                   bool includeDoorLeaf) {
        if (For(piece.ClassName) is not { } shape) return null;

        var origin = piece.GetActorLocation();
        var yaw = piece.GetActorRotation().Yaw * MathF.PI / 180f;
        var cos = MathF.Cos(yaw);
        var sin = MathF.Sin(yaw);

        // World -> local: subtract the origin, rotate by -yaw, then undo the mirror. A mirrored piece
        // is the same mesh with its local X negated (see ABuildingActor.bMirrored), so reflecting the
        // QUERY is exactly equivalent to reflecting the shape and costs nothing.
        var flip = piece.bMirrored ? -1f : 1f;

        FVector ToLocal(FVector p) {
            var dx = p.X - origin.X;
            var dy = p.Y - origin.Y;
            return new FVector {
                X = (dx * cos + dy * sin) * flip,
                Y = -dx * sin + dy * cos,
                Z = p.Z - origin.Z
            };
        }

        var localFrom = ToLocal(from);
        var localTo = ToLocal(to);

        (float T, FVector Normal)? best = null;

        void Clip(float[][] hulls) {
            foreach (var planes in hulls) {
                if (ClipSegment(planes, localFrom, localTo) is not { } hit) continue;
                if (best is { } b && b.T <= hit.T) continue;
                best = hit;
            }
        }

        Clip(shape.Body);
        if (includeDoorLeaf) Clip(shape.Door);

        if (best is not { } found) return null;

        // The normal back into world space: undo the mirror, then the rotation.
        var nx = found.Normal.X * flip;
        var ny = found.Normal.Y;

        return (found.T, new FVector {
            X = nx * cos - ny * sin,
            Y = nx * sin + ny * cos,
            Z = found.Normal.Z
        });
    }

    /// <summary>
    ///     Clips a segment against one convex hull's half-spaces: the standard slab walk, which ends
    ///     with the entry parameter and the plane that produced it.
    ///
    ///     A plane is (Nx, Ny, Nz, D) with the inside at N.p &lt;= D. A segment that starts INSIDE
    ///     returns 0 - which is the honest answer for "where does it first touch" and is what a
    ///     projectile spawned inside a piece should get.
    /// </summary>
    private static (float T, FVector Normal)? ClipSegment(float[] planes, FVector from, FVector to) {
        float enter = 0f, exit = 1f;
        FVector normal = new();

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;

        for (var i = 0; i + 3 < planes.Length; i += 4) {
            float nx = planes[i], ny = planes[i + 1], nz = planes[i + 2], d = planes[i + 3];

            var distance = nx * from.X + ny * from.Y + nz * from.Z - d;
            var along = nx * dx + ny * dy + nz * dz;

            if (MathF.Abs(along) < 1e-6f) {
                // Parallel to this face: outside it means outside the hull, whatever the rest say.
                if (distance > 0f) return null;
                continue;
            }

            var t = -distance / along;

            if (along < 0f) {
                // Entering through this face.
                if (t > enter) {
                    enter = t;
                    normal = new FVector { X = nx, Y = ny, Z = nz };
                }
            } else if (t < exit) {
                exit = t;
            }

            if (enter > exit) return null;
        }

        return enter > 1f ? null : (enter, normal);
    }

    /// <summary>Whether the table is loaded - touching it is also how the startup warm-up triggers it.</summary>
    public static bool Ready => Lookup.Count > 0;

    /// <summary>
    ///     Proves at startup that a doorway is actually a HOLE, and that shutting the door fills it.
    ///
    ///     WHY AN ASSERTION AND NOT A COMMENT. "The collision still looks like it did before the
    ///     edit" is a report that cannot be acted on: it could be the hulls, the transform, the piece
    ///     failing to register, or the old piece failing to unregister, and nothing in a log tells
    ///     them apart. This settles the first two offline, in the first lines of the log, so a report
    ///     that survives it is a report about the other two.
    ///
    ///     The numbers are the door wall's own: PBWA_W1_DoorC_C is three hulls leaving X -72..72 open
    ///     below Z 216, and the leaf fills exactly that.
    /// </summary>
    public static void VerifyDoorway() {
        if (For("PBWA_W1_DoorC_C") is not { } door) {
            Console.WriteLine("FortBuildingHulls: PBWA_W1_DoorC_C is not in the bake - re-run " +
                              "TerrainHeightMapBaker --buildpieces. Every piece falls back to its coarse " +
                              "per-type box until then.");
            return;
        }

        // Straight through the middle of the doorway at knee height, in the piece's local space.
        var from = new FVector { X = 0f, Y = -100f, Z = 100f };
        var to = new FVector { X = 0f, Y = 100f, Z = 100f };

        // ...and through a post, which must always be solid.
        var postFrom = new FVector { X = -180f, Y = -100f, Z = 100f };
        var postTo = new FVector { X = -180f, Y = 100f, Z = 100f };

        var throughOpening = door.Body.Any(h => ClipSegment(h, from, to) != null);
        var throughShut = door.Door.Any(h => ClipSegment(h, from, to) != null);
        var throughPost = door.Body.Any(h => ClipSegment(h, postFrom, postTo) != null);

        var problems = new List<string>();
        if (throughOpening) problems.Add("the doorway is solid even with the door open");
        if (!throughShut) problems.Add("a SHUT door does not block its own doorway");
        if (!throughPost) problems.Add("the wall beside the doorway is not solid");

        if (problems.Count == 0) {
            Console.WriteLine($"FortBuildingHulls: a doorway is a real hole ({door.Body.Length} wall hull(s), " +
                              $"{door.Door.Length} leaf hull(s)) - open passes, shut blocks, the posts are solid.");
            return;
        }

        Console.WriteLine("FortBuildingHulls: DOORWAY GEOMETRY IS WRONG - " + string.Join("; ", problems));
    }

    private static Dictionary<string, (float[][] Body, float[][] Door, FVector Min, FVector Max)> Lookup =>
        _lookup ??= BuildLookup();

    private static Dictionary<string, (float[][] Body, float[][] Door, FVector Min, FVector Max)>? _lookup;

    /// <summary>
    ///     Built on first use rather than in a field initializer: <see cref="Pieces" /> lives in the
    ///     other half of this partial class, and the order the compiler visits a partial class's files
    ///     is not something this code gets to decide. FortGameplayTags was caught by exactly that.
    /// </summary>
    private static Dictionary<string, (float[][] Body, float[][] Door, FVector Min, FVector Max)> BuildLookup() {
        var map = new Dictionary<string, (float[][], float[][], FVector, FVector)>(
            Pieces.Length, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, body, door, minX, minY, minZ, maxX, maxY, maxZ) in Pieces) {
            map[name] = (body, door,
                         new FVector { X = minX, Y = minY, Z = minZ },
                         new FVector { X = maxX, Y = maxY, Z = maxZ });
        }

        var hulls = Pieces.Sum(p => p.Body.Length + p.Door.Length);
        Console.WriteLine($"FortBuildingHulls: {map.Count} player-build class(es), {hulls} convex hull(s) - " +
                          "doorways and windows are real holes now");
        return map;
    }
}
