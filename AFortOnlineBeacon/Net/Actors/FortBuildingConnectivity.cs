namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     The runtime half of Fortnite's real building connectivity - see
///     FortBuildingConnectivity.Generated.cs for the shipped data and for how the geometry below was
///     established.
///
///     A building cell is a 5 x 5 x 4 voxel block. Neighbouring cells step by (4, 4, 3), so adjacent
///     cells overlap by exactly one voxel layer. Two pieces are connected when their voxel sets share
///     at least two voxels - the client's own test is literally `cmp eax, 2 / setge`. One shared voxel
///     means the pieces meet at a single corner, which is exactly what that threshold exists to
///     reject.
/// </summary>
internal static partial class FortBuildingConnectivity {
    public const int VoxelsX = 5;
    public const int VoxelsY = 5;
    public const int VoxelsZ = 4;

    /// <summary>
    ///     Cell-to-cell step in voxels. One less than the extent on each axis, which is what makes
    ///     neighbouring cells overlap by a single layer.
    /// </summary>
    public const int StepX = 4;
    public const int StepY = 4;
    public const int StepZ = 3;

    /// <summary>How many voxels two pieces must share to count as connected.</summary>
    public const int ConnectedVoxelThreshold = 2;

    private static int Index(int x, int y, int z) => z * (VoxelsX * VoxelsY) + y * VoxelsX + x;

    /// <summary>
    ///     A piece's occupied voxels as a 100-bit set - 5 * 5 * 4, bit <see cref="Index"/>. UInt128
    ///     rather than a collection because this sits in the destruction cascade's inner loop.
    /// </summary>
    private static UInt128 Expand(FConnectivityCube cube) {
        var bits = UInt128.Zero;

        // Side faces: 5 columns, rows 0..3 with row 0 at the TOP. Row 4 is deliberately not read -
        // the block is only 4 voxels tall and the shipped data never sets it on a side face.
        void Side(uint mask, int axis, int value, bool flipColumns) {
            for (var row = 0; row < VoxelsZ; row++) {
                for (var col = 0; col < VoxelsX; col++) {
                    if ((mask & (1u << (row * 5 + col))) == 0) continue;

                    var other = flipColumns ? 4 - col : col;
                    var z = 3 - row;
                    bits |= UInt128.One << (axis == 0 ? Index(value, other, z) : Index(other, value, z));
                }
            }
        }

        Side(cube.Front, axis: 0, value: 4, flipColumns: false);
        Side(cube.Back, axis: 0, value: 0, flipColumns: true);
        Side(cube.Left, axis: 1, value: 0, flipColumns: false);
        Side(cube.Right, axis: 1, value: 4, flipColumns: true);

        // Caps: both axes horizontal, and here ROW IS X, COLUMN IS Y - the transpose of the side
        // faces, not the same convention. That is what the edge-consistency search settled on, and it
        // is not something to "tidy up" into matching the sides: getting it the other way round makes
        // a wall and the floor in its own cell share 9 voxels instead of 5.
        void Cap(uint mask, int z) {
            for (var row = 0; row < 5; row++) {
                for (var col = 0; col < 5; col++) {
                    if ((mask & (1u << (row * 5 + col))) != 0) bits |= UInt128.One << Index(row, col, z);
                }
            }
        }

        Cap(cube.Upper, 3);
        Cap(cube.Lower, 0);

        return bits;
    }

    /// <summary>Yaw +90 degrees about the cell's vertical axis: (x, y, z) -> (4 - y, x, z).</summary>
    private static UInt128 Rotate90(UInt128 bits) => Remap(bits, (x, y, z) => (4 - y, x, z));

    /// <summary>
    ///     Mirroring, which in this space is the reflection y -> 4 - y. Proven, not assumed: all four
    ///     shipped mirror pairs reduce to exactly this - see the generated file.
    /// </summary>
    private static UInt128 Mirror(UInt128 bits) => Remap(bits, (x, y, z) => (x, 4 - y, z));

    private static UInt128 Remap(UInt128 bits, Func<int, int, int, (int X, int Y, int Z)> move) {
        var moved = UInt128.Zero;
        for (var z = 0; z < VoxelsZ; z++)
        for (var y = 0; y < VoxelsY; y++)
        for (var x = 0; x < VoxelsX; x++) {
            if ((bits & (UInt128.One << Index(x, y, z))) == 0) continue;

            var (nx, ny, nz) = move(x, y, z);
            moved |= UInt128.One << Index(nx, ny, nz);
        }
        return moved;
    }

    /// <summary>
    ///     Filled on first use rather than by a field initializer: the masks live in the OTHER file of
    ///     this partial class, and a partial class's static initializers run in whatever order the
    ///     compiler feeds it the files - the exact trap that killed FortFloorLoot's first world tick.
    ///     Zero is stored to mean "this class has no shipped pattern", so a miss is not re-derived.
    /// </summary>
    private static readonly Dictionary<(string Class, int Quadrant, bool Mirrored), UInt128> Cache = new();

    /// <summary>
    ///     A piece's voxels in WORLD orientation, or null for a class the shipped patterns do not
    ///     cover - a stand-in for map scenery, say. Callers should read null as "no opinion" rather
    ///     than as "not connected".
    /// </summary>
    public static UInt128? VoxelsFor(string className, float yaw, bool mirrored) {
        var quadrant = ((int) MathF.Round(yaw / 90f) % 4 + 4) % 4;
        var key = (className, quadrant, mirrored);

        lock (Cache) {
            if (Cache.TryGetValue(key, out var cached)) return cached == UInt128.Zero ? null : cached;

            if (For(className) is not { } cube) {
                Cache[key] = UInt128.Zero;
                return null;
            }

            var bits = Expand(cube);

            // Mirror first, then rotate: the piece is authored, then flipped, then placed.
            if (mirrored) bits = Mirror(bits);
            for (var i = 0; i < quadrant; i++) bits = Rotate90(bits);

            Cache[key] = bits;
            return bits;
        }
    }

    /// <summary>
    ///     Which part of its own cell a piece occupies, as a compass reading - "+Y", "-X", "centre".
    ///
    ///     Exists because the remaining unknowns are all "is this shape on the side of the cell we
    ///     think it is", and that question cannot be settled by arguing about whose left is whose.
    ///     Printed in the STRUCTURAL_DEBUG dump so one screenshot of a half-floor plus one log line
    ///     settles the orientation outright: if the player can see the half nearest the stair and the
    ///     log says the model put it on the far side, the offset is wrong by exactly that much.
    /// </summary>
    public static string Occupancy(UInt128 bits) {
        float sx = 0, sy = 0;
        var n = 0;
        for (var z = 0; z < VoxelsZ; z++)
        for (var y = 0; y < VoxelsY; y++)
        for (var x = 0; x < VoxelsX; x++) {
            if ((bits & (UInt128.One << Index(x, y, z))) == 0) continue;
            sx += x;
            sy += y;
            n++;
        }
        if (n == 0) return "empty";

        // The cell's middle is 2,2. Anything within half a voxel of it is not leaning either way.
        var dx = sx / n - 2f;
        var dy = sy / n - 2f;
        if (MathF.Abs(dx) < 0.5f && MathF.Abs(dy) < 0.5f) return "centre";

        return MathF.Abs(dx) >= MathF.Abs(dy)
            ? (dx > 0 ? "+X" : "-X")
            : (dy > 0 ? "+Y" : "-Y");
    }

    /// <summary>
    ///     How many voxels the two pieces share, with <paramref name="other"/> sitting
    ///     (<paramref name="dx"/>, <paramref name="dy"/>, <paramref name="dz"/>) CELLS away.
    /// </summary>
    public static int SharedVoxels(UInt128 bits, UInt128 other, int dx, int dy, int dz) {
        // Beyond one cell the blocks cannot overlap at all: the extent is 5 and the step is 4, so an
        // offset of one leaves a single shared layer and an offset of two leaves nothing. The client
        // guards at 3 before doing any work; this is the tighter bound that actually matters.
        if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || Math.Abs(dz) > 1) return 0;

        var shared = 0;
        for (var z = 0; z < VoxelsZ; z++)
        for (var y = 0; y < VoxelsY; y++)
        for (var x = 0; x < VoxelsX; x++) {
            if ((bits & (UInt128.One << Index(x, y, z))) == 0) continue;

            // Where this voxel falls inside the other piece's own cell.
            var ox = x - dx * StepX;
            var oy = y - dy * StepY;
            var oz = z - dz * StepZ;
            if (ox is < 0 or >= VoxelsX || oy is < 0 or >= VoxelsY || oz is < 0 or >= VoxelsZ) continue;

            if ((other & (UInt128.One << Index(ox, oy, oz))) != 0) shared++;
        }
        return shared;
    }

    /// <summary>
    ///     The whole rule. Null from either lookup means that piece has no shipped pattern, so this
    ///     declines to answer rather than guessing - see <see cref="VoxelsFor"/>.
    /// </summary>
    public static bool? AreConnected(string classA, float yawA, bool mirroredA,
                                     string classB, float yawB, bool mirroredB,
                                     int dx, int dy, int dz) {
        if (VoxelsFor(classA, yawA, mirroredA) is not { } a) return null;
        if (VoxelsFor(classB, yawB, mirroredB) is not { } b) return null;

        return SharedVoxels(a, b, dx, dy, dz) >= ConnectedVoxelThreshold;
    }
}
