namespace AFortOnlineBeacon.Net;

/// <summary>
///     External stand-in for the server half of real Fortnite's UBuildingStructuralSupportSystem (a
///     UObject on the GameState). Chain collapse does not happen on its own: placed pieces are
///     ordinary replicated actors and the client predicts nothing here, so destroying only the base
///     of a tower would leave everything above it floating until the server says otherwise.
///
///     HOW CLOSE THIS IS TO THE REAL THING. The real system keys every piece into an
///     FBuildingSupportCellIndex grid where a cell owns distinct SLOTS - two floor slots
///     (EStructuralFloorPosition Top/Bottom), four wall slots (EStructuralWallPosition
///     Left/Right/Front/Back) and one interior "center cell" actor - which is what
///     GetWallActor/GetFloorActor/GetCenterCellActor index into, and what
///     AreNeighboringBuildingActorsConnected walks to answer "are these two touching". This class
///     reproduces the grid's DIMENSIONS exactly (they are measured from real placements - see
///     FBuildingSupportCellIndex's doc comment) but not yet its slots: the one fact still missing is
///     which side of its pivot edge a floor's body lies on, without which a piece cannot be assigned
///     to a definite cell. So adjacency here is a distance test between pivots, cut to the grid's own
///     anisotropic shape - one tile (512) horizontally, one storey (384, plus a roof/stair pivot's
///     128 of intra-storey offset) vertically.
///
///     That shape is the part that matters. An isotropic threshold generous enough to cover a
///     horizontal tile is also, in Z, more than two storeys (2 x 384 = 768), which silently welds a
///     piece to another one floating two floors above it with nothing in between - a false support
///     link, and false support is the failure mode that makes a cascade not fire at all. Splitting
///     the axes removes that whole class of error even while the slot model is still missing.
///
///     Single registry, no per-UWorld scoping - same convention as BuildingClassHandles/
///     FortHarvestResources, both of which assume one match's worth of state per process. Reset()
///     exists for the case where that stops being true.
/// </summary>
public static class BuildingStructuralSupportSystem {
    /// <summary>
    ///     Every live piece, bucketed by cell so a neighbour query reads 27 buckets instead of the
    ///     whole match. Pieces are looked up by identity often enough (Unregister, and the flood's
    ///     "have I seen this" test) that the flat list is kept alongside as a set rather than
    ///     rebuilt from the buckets.
    /// </summary>
    private static readonly Dictionary<FBuildingSupportCellIndex, List<ABuildingActor>> Cells = new();
    private static readonly HashSet<ABuildingActor> Buildings = new();

    /// <summary>
    ///     The lowest Z ever placed in each XY column, and the no-terrain fallback's whole notion of
    ///     where the ground is - see <see cref="IsSupportedByWorld"/>. It only ever goes DOWN, and a
    ///     destroyed piece never raises it back up. That "never raises" is the entire point: taking
    ///     the lowest piece still standing instead would regenerate ground under whatever survived,
    ///     so shooting the base out of a tower would just promote the next piece up to
    ///     world-supported and the tower would stand there in mid-air - exactly the case this system
    ///     exists to handle.
    /// </summary>
    private static readonly Dictionary<(int X, int Y), float> ColumnGround = new();

    /// <summary>
    ///     Set when something has changed that could have left pieces unsupported; cleared by the
    ///     next Tick that runs the flood. Real Fortnite defers this recheck too rather than running
    ///     it inline (ABuildingSMActor::MarkConnectedBuildingsForStructuralIntegrityCheck queues its
    ///     neighbours, and BuildingRetestSupportedByWorldDelay paces the retest) - the delay itself
    ///     below is this project's own choice, not a measured value, but the deferral it buys is the
    ///     point: a burst of destructions in one tick collapses into a single flood, and the flood
    ///     never runs re-entrantly from inside a Destroy() it caused.
    /// </summary>
    private static bool _recheckPending;
    private static float _recheckAt;

    private const float RecheckDelaySeconds = 0.25f;

    /// <summary>
    ///     Pieces already flagged bDestroyed and waiting for their channel to actually close, with
    ///     the time each is due. The gap is deliberate and load-bearing: Destroy() closes the
    ///     channel, and a closed channel carries no further property updates, so a piece destroyed
    ///     in the same breath as being flagged would never get the flag to the client. One
    ///     replication tick of daylight is enough for the ordinary property push to take it.
    /// </summary>
    private static readonly List<(ABuildingActor Building, float DestroyAt)> PendingDestroy = new();

    private const float DestroyDelaySeconds = 0.35f;

    /// <summary>Horizontal reach of an adjacency link: one tile. Two pivots further apart than this in X or Y cannot be touching.</summary>
    private const float HorizontalReach = FBuildingSupportCellIndex.TileSize;

    /// <summary>
    ///     Vertical reach: one storey, plus the intra-storey pivot offset a Roof or Stair carries, so
    ///     a stair pivot at storey+128 still links to the floor one storey above it. Deliberately
    ///     under two storeys (768) - see this class's doc comment for why that bound is the whole
    ///     point of splitting the axes.
    /// </summary>
    private const float VerticalReach = FBuildingSupportCellIndex.StoreyHeight
                                      + FBuildingSupportCellIndex.PivotStoreyOffset;

    public static void Register(ABuildingActor building) {
        if (!Buildings.Add(building)) return;

        var loc = building.GetActorLocation();
        building.CellIndex = FBuildingSupportCellIndex.FromLocation(loc);

        if (!Cells.TryGetValue(building.CellIndex, out var cell)) Cells[building.CellIndex] = cell = new List<ABuildingActor>();
        cell.Add(building);

        var column = (building.CellIndex.X, building.CellIndex.Y);
        if (!ColumnGround.TryGetValue(column, out var ground) || loc.Z < ground) ColumnGround[column] = loc.Z;

        // Freshly placed: play the build-in animation and ramp its health up from a fraction, the
        // way a real piece does rather than appearing instantly at full strength.
        building.BeginConstruction(_lastTickTime);
        Constructing.Add(building);
    }

    /// <summary>
    ///     Puts an already-standing piece back on the harden ramp - see ABuildingActor.BeginRepair.
    ///     Separate from Register because a repaired piece is already in the grid and must not be
    ///     re-bucketed; all it needs is to start ticking again.
    ///
    ///     Idempotent on the list, so holding the repair input does not queue a piece twice.
    /// </summary>
    public static bool BeginRepair(ABuildingActor building, int targetHitPoints) {
        if (!building.BeginRepair(_lastTickTime, targetHitPoints)) return false;

        if (!Constructing.Contains(building)) Constructing.Add(building);
        return true;
    }

    /// <summary>Pieces still building in - see ABuildingActor.TickConstruction.</summary>
    private static readonly List<ABuildingActor> Constructing = new();

    /// <summary>Pieces mid damage-pulse - see ABuildingActor.OnDamaged.</summary>
    private static readonly List<ABuildingActor> Damaged = new();

    /// <summary>
    ///     Drops a piece out of the grid. Called from ABuildingActor.Destroyed rather than from each
    ///     destruction site, so it holds however the piece died; safe to call for something never
    ///     registered.
    /// </summary>
    public static void Unregister(ABuildingActor building) {
        if (!Buildings.Remove(building)) return;

        if (Cells.TryGetValue(building.CellIndex, out var cell) && cell.Remove(building) && cell.Count == 0)
            Cells.Remove(building.CellIndex);

        // Whatever this piece was holding up may now be holding nothing up.
        _recheckPending = true;
    }

    /// <summary>
    ///     Whether a live piece of the SAME KIND already occupies this exact placement - the client's
    ///     own snapped BuildLoc, yaw and slot kind, because the client sends a grid-snapped transform
    ///     and the server uses it verbatim (see FCreateBuildingActorData). Only pieces still
    ///     registered count: one on its way out has already been unregistered, so its slot is free.
    ///
    ///     THE KIND IS NOT OPTIONAL, and leaving it out was a real bug. Different slot kinds
    ///     legitimately share one pivot: a floor and a roof (or a stair, or a cone) on the same tile
    ///     have the same BuildLoc and, unless the player rotates one of them, the same yaw. Matching
    ///     on transform alone therefore refused the second piece of every such pair - the exact
    ///     symptom reported from a live session, right down to why rotating eventually let it
    ///     through: a rotated piece has a different yaw, so the over-broad test simply stopped
    ///     matching.
    ///
    ///     This test exists to swallow the client's DUPLICATE double-send of one placement, nothing
    ///     more (real Fortnite's own overlap rules live in FStructuralSupportSystem and are not
    ///     modelled here), and a duplicate is by definition the same kind of piece - so narrowing it
    ///     costs nothing it was actually catching.
    /// </summary>
    public static bool IsOccupied(FVector location, float yaw, EFortBuildingType type) {
        var cell = FBuildingSupportCellIndex.FromLocation(location);

        foreach (var cellIndex in cell.WithNeighbors()) {
            if (!Cells.TryGetValue(cellIndex, out var bucket)) continue;

            foreach (var other in bucket) {
                if (other.bDestroyed) continue;
                if (other.BuildingType != type) continue;

                var otherLoc = other.GetActorLocation();
                if (MathF.Abs(otherLoc.X - location.X) > 1f) continue;
                if (MathF.Abs(otherLoc.Y - location.Y) > 1f) continue;
                if (MathF.Abs(otherLoc.Z - location.Z) > 1f) continue;
                if (YawDelta(other.GetActorRotation().Yaw, yaw) > 1f) continue;

                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Smallest angle between two yaws, in degrees. Needed because yaw wraps: a piece stored at
    ///     359.5 and a placement at 0.5 are half a degree apart, not 359, and a plain subtraction
    ///     would let a genuine duplicate through.
    /// </summary>
    private static float YawDelta(float a, float b) {
        var delta = MathF.Abs(a - b) % 360f;
        return delta > 180f ? 360f - delta : delta;
    }

    /// <summary>Forgets every registered piece - for a process that outlives one match, which nothing here does yet.</summary>
    public static void Reset() {
        Buildings.Clear();
        Cells.Clear();
        ColumnGround.Clear();
        PendingDestroy.Clear();
        Constructing.Clear();
        Damaged.Clear();
        _recheckPending = false;
        _recheckAt = 0f;
    }

    /// <summary>
    ///     Applies weapon damage to a placed piece and destroys it once its HP reaches 0. A hit that
    ///     does not finish the piece off is pure bookkeeping: no client-visible property push happens
    ///     (see ABuildingActor's doc comment for why). The cascade that follows a kill is not run
    ///     here - Destroy() marks the grid dirty and <see cref="Tick"/> picks it up, which is what
    ///     keeps a burst of hits from flooding once per hit.
    /// </summary>
    public static void ApplyDamage(ABuildingActor building, int amount) {
        if (!building.ApplyDamage(amount)) {
            // Survived the hit - tell the client something just happened to it. See
            // ABuildingActor.OnDamaged for why a health value alone is not enough.
            building.OnDamaged(amount, _lastTickTime);
            if (!Damaged.Contains(building)) Damaged.Add(building);
            return;
        }

        Console.WriteLine($"BuildingStructuralSupportSystem: {building.GetFName()} " +
                          $"({building.Material}:{building.BuildingType}) destroyed");

        BeginDestroy(building);
    }

    /// <summary>
    ///     Starts a piece's destruction: flags it (so bDestroyed replicates), takes it out of the
    ///     support grid immediately - a piece on its way out supports nothing - and queues the
    ///     actual teardown for <see cref="DestroyDelaySeconds"/> later. Unregistering here is also
    ///     what arms the cascade, so the flood runs against what will still be standing.
    /// </summary>
    private static void BeginDestroy(ABuildingActor building) {
        if (!building.MarkDestroyed()) return;

        Constructing.Remove(building);
        Damaged.Remove(building);
        Unregister(building);
        PendingDestroy.Add((building, _lastTickTime + DestroyDelaySeconds));
    }

    private static float _lastTickTime;

    /// <summary>
    ///     Driven from UWorld.Tick. Runs the deferred support recheck once the delay since the last
    ///     grid change has elapsed - see <see cref="_recheckPending"/>.
    /// </summary>
    public static void Tick(float timeSeconds) {
        _lastTickTime = timeSeconds;

        // Build-in health ramp. Only pieces actually constructing are in this list, so a settled
        // match pays nothing for it.
        for (var i = Constructing.Count - 1; i >= 0; i--) {
            if (!Constructing[i].TickConstruction(timeSeconds)) Constructing.RemoveAt(i);
        }

        for (var i = Damaged.Count - 1; i >= 0; i--) {
            if (!Damaged[i].TickDamageState(timeSeconds)) Damaged.RemoveAt(i);
        }

        for (var i = PendingDestroy.Count - 1; i >= 0; i--) {
            if (timeSeconds < PendingDestroy[i].DestroyAt) continue;

            var building = PendingDestroy[i].Building;
            PendingDestroy.RemoveAt(i);
            building.Destroy();
        }

        if (!_recheckPending) return;

        if (_recheckAt == 0f) _recheckAt = timeSeconds + RecheckDelaySeconds;
        if (timeSeconds < _recheckAt) return;

        _recheckPending = false;
        _recheckAt = 0f;
        RecheckSupport();
    }

    /// <summary>
    ///     Full connectivity flood from every ground-touching piece; anything the flood never reaches
    ///     has lost its support and is destroyed too. Deliberately a from-scratch recompute over the
    ///     whole registry rather than an incremental patch around the piece that went: there is no
    ///     live-verified incremental rule to patch with (see this class's doc comment), so a full
    ///     flood is the only version that cannot drift out of sync with what is actually still
    ///     standing. Each destruction here re-arms the pending flag through Unregister, so a chunk
    ///     that only comes apart once its first layer is gone still resolves - on the next pass.
    /// </summary>
    private static void RecheckSupport() {
        if (Buildings.Count == 0) return;

        var supported = new HashSet<ABuildingActor>();
        var queue = new Queue<ABuildingActor>();

        foreach (var b in Buildings) {
            if (IsSupportedByWorld(b) && supported.Add(b)) queue.Enqueue(b);
        }

        var groundSeeded = new HashSet<ABuildingActor>(supported);

        while (queue.Count > 0) {
            foreach (var neighbor in NeighborsOf(queue.Dequeue())) {
                if (supported.Add(neighbor)) queue.Enqueue(neighbor);
            }
        }

        // Materialised before destroying anything: Destroy() re-enters this class through
        // ABuildingActor.Destroyed -> Unregister, which mutates both the registry and the buckets
        // NeighborsOf reads.
        if (DebugEnabled) DumpSupport(supported, groundSeeded);

        var unsupported = Buildings.Where(b => !supported.Contains(b)).ToList();
        if (unsupported.Count == 0) return;

        Console.WriteLine($"BuildingStructuralSupportSystem: cascade - {unsupported.Count} piece(s) lost support");

        // WHY, for the first few. A player who suddenly falls through their own build reports "the
        // building went see-through", and the count above cannot distinguish the three things that
        // produce it: the piece was genuinely floating, the world-support test refused ground that is
        // really there, or the flood never reached the piece from a ground-seeded one. The world
        // sample IS the discriminator - a piece one storey above the sample is unsupported by the
        // rule, a piece BELOW it means the rule was handed the wrong surface - and it costs one line.
        var explained = 0;
        foreach (var b in unsupported) {
            if (explained++ >= 4) break;

            var loc = b.GetActorLocation();
            var landscape = TerrainHeightMap.GetGroundHeightUnder(loc.X, loc.Y, FBuildingSupportCellIndex.PivotEdgeOffset);
            var surface = TerrainHeightMap.GetSurfaceUnder(loc.X, loc.Y, FBuildingSupportCellIndex.PivotEdgeOffset, loc.Z);
            var walked = TerrainGroundTruth.GetGroundHeightUnder(loc.X, loc.Y, FBuildingSupportCellIndex.PivotEdgeOffset, loc.Z);

            Console.WriteLine($"BuildingStructuralSupportSystem:   {b.GetFName()} at {loc} - " +
                              $"landscape {(landscape is { } l ? $"{l:F0} (gap {loc.Z - l:F0})" : "none")}, " +
                              $"surface {(surface is { } sf ? $"{sf:F0} (gap {loc.Z - sf:F0})" : "none")}, " +
                              $"walked {(walked is { } wk ? $"{wk:F0} (gap {loc.Z - wk:F0})" : "none")}, " +
                              $"neighbours {NeighborsOf(b).Count()}, storey {FBuildingSupportCellIndex.StoreyHeight:F0}");
        }

        foreach (var b in unsupported) BeginDestroy(b);
    }

    // ---------------------------------------------------------------------------------------------
    // COLLISION AGAINST PLAYER-BUILT PIECES
    //
    // NOT built on the ConnectivityCube, and the reason is worth stating: that data is RELATIVE ONLY.
    // The structural cascade never asks where a piece's voxels are in the world - only how two pieces'
    // voxels line up, by cell delta - so a shape sitting one whole cell away from the piece it belongs
    // to passes every connectivity test. Collision is the first thing here that needs an ABSOLUTE
    // answer, and it exposed exactly that: floors collided (their shape fills the cell, so an offset
    // still covers it) while walls never did (a wall is one face, so the same offset put it a cell
    // away). Grenades flew through every wall while the startup self-check reported the geometry fine,
    // because that check was testing the SHAPE and the error was in the PLACE.
    //
    // CentroidOf IS absolute, and is read from the real CDOs: "where a piece's BODY actually sits" -
    // the centre of the cell for a floor, roof or stair, the middle of the spanned cell edge for a
    // wall. A box around that is a coarser shape than the voxels (a half wall is a full wall here) but
    // it is in the right PLACE, which is the property that matters and the one the voxels lack.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     Half-thickness of a wall, and of a floor slab. CHOSEN: the real meshes are not in any table
    ///     this server reads. Everything else about the box comes from CentroidOf and the 512/384 grid.
    ///     Generous is the safe direction for a wall - too thin and a blast leaks past it.
    /// </summary>
    private const float WallHalfThickness = 32f;

    private const float SlabHalfThickness = 24f;

    /// <summary>
    ///     A piece's world-space bounding box. Axis-aligned because every placement yaw the client
    ///     sends is a multiple of 90, so a rotation only ever SWAPS the X and Y extents.
    /// </summary>
    private static (FVector Min, FVector Max) BoxOf(ABuildingActor piece) =>
        BoxOf(piece.GetActorLocation(), piece.GetActorRotation().Yaw, piece.BuildingType);

    /// <summary>The same box from a bare placement, so the startup check can build one without an actor.</summary>
    private static (FVector Min, FVector Max) BoxOf(FVector pivot, float yaw, EFortBuildingType type) {
        var centre = FBuildingSupportCellIndex.CentroidOf(pivot, yaw, type);

        var half = type switch {
            // Spans the cell edge: 512 along the edge, a storey tall, thin across.
            EFortBuildingType.Wall => (X: WallHalfThickness,
                                       Y: FBuildingSupportCellIndex.TileSize / 2f,
                                       Z: FBuildingSupportCellIndex.StoreyHeight / 2f),
            // Fills the cell in plan, thin in Z.
            EFortBuildingType.Floor or EFortBuildingType.Roof => (X: FBuildingSupportCellIndex.TileSize / 2f,
                                                                  Y: FBuildingSupportCellIndex.TileSize / 2f,
                                                                  Z: SlabHalfThickness),
            // Stairs and anything else: the whole cell.
            _ => (X: FBuildingSupportCellIndex.TileSize / 2f,
                  Y: FBuildingSupportCellIndex.TileSize / 2f,
                  Z: FBuildingSupportCellIndex.StoreyHeight / 2f)
        };

        var quadrant = ((int) MathF.Round(yaw / 90f) % 4 + 4) % 4;
        if (quadrant % 2 == 1) half = (half.Y, half.X, half.Z);

        return (new FVector { X = centre.X - half.X, Y = centre.Y - half.Y, Z = centre.Z - half.Z },
                new FVector { X = centre.X + half.X, Y = centre.Y + half.Y, Z = centre.Z + half.Z });
    }

    /// <summary>
    ///     Proves the collision geometry is in the right PLACE at startup, beside NativeClassNetCache's
    ///     field-index check and UActorChannel's condition check.
    ///
    ///     The first version of this checked the SHAPE and passed happily while every wall's collision
    ///     sat a full cell away from the wall - so grenades flew through walls with a green line in the
    ///     log. A geometry bug reports nothing on its own; the assertion has to be about the thing that
    ///     can actually be wrong, which is placement.
    ///
    ///     A wall placed at pivot (0, 256, 0) facing yaw 0 spans the cell edge at X = 0: its body must
    ///     contain that edge at mid-height, and must NOT contain the middle of either cell beside it.
    /// </summary>
    public static void VerifyCollisionGeometry() {
        var pivot = new FVector { X = 0f, Y = 256f, Z = 0f };
        var (min, max) = BoxOf(pivot, 0f, EFortBuildingType.Wall);

        bool Contains(FVector p) =>
            p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;

        var problems = new List<string>();

        if (!Contains(new FVector { X = 0f, Y = 256f, Z = 192f }))
            problems.Add("a wall does not contain the cell edge it stands on");
        if (Contains(new FVector { X = 256f, Y = 256f, Z = 192f }))
            problems.Add("a wall reaches the middle of the cell in front of it");
        if (Contains(new FVector { X = -256f, Y = 256f, Z = 192f }))
            problems.Add("a wall reaches the middle of the cell behind it");
        if (!Contains(new FVector { X = 0f, Y = 256f, Z = 20f }) || !Contains(new FVector { X = 0f, Y = 256f, Z = 360f }))
            problems.Add("a wall is not solid over the full height of its storey");

        if (problems.Count == 0) {
            Console.WriteLine("BuildingStructuralSupportSystem: player-build collision sits on the piece it belongs " +
                              "to (wall box " +
                              $"X {min.X:F0}..{max.X:F0}, Y {min.Y:F0}..{max.Y:F0}, Z {min.Z:F0}..{max.Z:F0}).");
            return;
        }

        Console.WriteLine("BuildingStructuralSupportSystem: COLLISION IS IN THE WRONG PLACE - projectiles will pass " +
                          "through player builds and blasts will not be blocked by them:");
        foreach (var problem in problems) Console.WriteLine($"    - {problem}");
    }

    /// <summary>Every piece whose box could matter near a point - the broad phase.</summary>
    private static IEnumerable<ABuildingActor> Nearby(FVector point, ABuildingActor? ignore) {
        foreach (var neighbour in FBuildingSupportCellIndex.FromLocation(point).WithNeighbors()) {
            if (!Cells.TryGetValue(neighbour, out var pieces)) continue;

            foreach (var piece in pieces)
                if (piece != ignore && !piece.bDestroyed) yield return piece;
        }
    }

    /// <summary>Whether a placed building piece occupies this world point.</summary>
    public static bool IsSolid(FVector point, ABuildingActor? ignore = null) {
        foreach (var piece in Nearby(point, ignore)) {
            var (min, max) = BoxOf(piece);

            if (point.X >= min.X && point.X <= max.X &&
                point.Y >= min.Y && point.Y <= max.Y &&
                point.Z >= min.Z && point.Z <= max.Z) return true;
        }

        return false;
    }

    /// <summary>How many pieces are registered within a radius - a diagnostic, so "nothing was hit" can be told from "nothing was there".</summary>
    public static int PiecesWithin(FVector point, float radius) {
        var radiusSquared = radius * radius;
        var count = 0;

        foreach (var piece in Buildings) {
            if (piece.bDestroyed) continue;
            if (FVector.DistSquared(point, piece.GetActorLocation()) <= radiusSquared) count++;
        }

        return count;
    }

    /// <summary>
    ///     Where a segment first enters a player build, and on which axis - a slab test, EXACT rather
    ///     than sampled.
    ///
    ///     Sampling was the previous approach and it is the wrong tool here: a wall is thinner than a
    ///     grenade's per-tick step, so any sample spacing cheap enough to run is coarse enough to step
    ///     straight over it. A slab test cannot miss a box however thin it is or however fast the
    ///     projectile is moving.
    /// </summary>
    private static (FVector Point, int Axis)? FirstHit(FVector from, FVector to, ABuildingActor? ignore) {
        var d = new[] { to.X - from.X, to.Y - from.Y, to.Z - from.Z };
        var o = new[] { from.X, from.Y, from.Z };

        var bestT = float.MaxValue;
        var bestAxis = -1;

        foreach (var piece in Nearby(from, ignore)) {
            var (min, max) = BoxOf(piece);
            var lo = new[] { min.X, min.Y, min.Z };
            var hi = new[] { max.X, max.Y, max.Z };

            float tEnter = 0f, tExit = 1f;
            var axis = -1;
            var miss = false;

            for (var i = 0; i < 3 && !miss; i++) {
                if (MathF.Abs(d[i]) < 1e-6f) {
                    if (o[i] < lo[i] || o[i] > hi[i]) miss = true;
                    continue;
                }

                var t1 = (lo[i] - o[i]) / d[i];
                var t2 = (hi[i] - o[i]) / d[i];
                if (t1 > t2) (t1, t2) = (t2, t1);

                if (t1 > tEnter) { tEnter = t1; axis = i; }
                if (t2 < tExit) tExit = t2;
                if (tEnter > tExit) miss = true;
            }

            if (miss || axis < 0 || tEnter < 0f || tEnter > 1f || tEnter >= bestT) continue;

            bestT = tEnter;
            bestAxis = axis;
        }

        if (bestAxis < 0) return null;

        return (new FVector { X = from.X + d[0] * bestT, Y = from.Y + d[1] * bestT, Z = from.Z + d[2] * bestT },
                bestAxis);
    }

    /// <summary>The first point along a step that is inside a player build, and the axis it entered on.</summary>
    public static (FVector Point, int Axis)? SweepToBuild(FVector from, FVector to) => FirstHit(from, to, null);

    /// <summary>
    ///     Which piece a step would hit, named - for the bounce diagnostic. The reflection maths was
    ///     verified correct from live data (0.3 perpendicular, 0.6 tangential, exactly); what could
    ///     not be told from that log was WHICH SURFACE it had chosen, and a coarse box (a stair is a
    ///     whole cell here) makes the server bounce off geometry the client does not have.
    /// </summary>
    public static string DescribeHit(FVector from, FVector to) {
        foreach (var piece in Nearby(from, null)) {
            var (min, max) = BoxOf(piece);
            var single = FirstHit(from, to, null);
            if (single == null) return "?";

            var p = single.Value.Point;
            if (p.X >= min.X - 1f && p.X <= max.X + 1f &&
                p.Y >= min.Y - 1f && p.Y <= max.Y + 1f &&
                p.Z >= min.Z - 1f && p.Z <= max.Z + 1f)
                return $"{piece.ClassName} yaw {piece.GetActorRotation().Yaw:F0} " +
                       $"box X {min.X:F0}..{max.X:F0} Y {min.Y:F0}..{max.Y:F0} Z {min.Z:F0}..{max.Z:F0}";
        }

        return "?";
    }

    /// <summary>
    ///     Whether a player-built piece stands between two points - the line-of-sight test an explosion
    ///     needs, and what the real ability expresses as bExcludeObstructedByWorld plus its choice
    ///     between GE_Damage_Explosive_LineOfSight and _NoLineOfSight.
    ///
    ///     <paramref name="ignore"/> is for tracing TO a piece: a building is solid, so a blast right
    ///     against a wall would otherwise be judged as blocked from damaging that very wall.
    /// </summary>
    public static bool IsLineBlocked(FVector from, FVector to, ABuildingActor? ignore = null) =>
        FirstHit(from, to, ignore) != null;

    /// <summary>Which side of its own cell the model believes this piece sits on - see FortBuildingConnectivity.Occupancy.</summary>
    private static string OccupancyOf(ABuildingActor b) =>
        b.ClassName.Length == 0
            ? "?"
            : FortBuildingConnectivity.VoxelsFor(b.ClassName, b.GetActorRotation().Yaw + PatternYawOffsetFor(b),
                                                 b.bMirrored != MirrorFlip) is { } v
                ? FortBuildingConnectivity.Occupancy(v)
                : "?";

    /// <summary>How many voxels the two pieces actually share - the number behind every link, so a false one can be named.</summary>
    private static string SharedWith(ABuildingActor a, ABuildingActor b) {
        if (a.ClassName.Length == 0 || b.ClassName.Length == 0) return "dist";

        var va = FortBuildingConnectivity.VoxelsFor(a.ClassName, a.GetActorRotation().Yaw + PatternYawOffsetFor(a), a.bMirrored != MirrorFlip);
        var vb = FortBuildingConnectivity.VoxelsFor(b.ClassName, b.GetActorRotation().Yaw + PatternYawOffsetFor(b), b.bMirrored != MirrorFlip);
        if (va is null || vb is null) return "dist";

        var (ax, ay, az) = StructuralCellOf(a);
        var (bx, by, bz) = StructuralCellOf(b);
        return FortBuildingConnectivity.SharedVoxels(va.Value, vb.Value, bx - ax, by - ay, bz - az).ToString();
    }

    private static bool DebugEnabled => Environment.GetEnvironmentVariable("STRUCTURAL_DEBUG") is "1";

    /// <summary>
    ///     STRUCTURAL_DEBUG=1. Why did each piece survive the flood?
    ///
    ///     A piece that should have fallen and did not survived for exactly one of two reasons, and
    ///     guessing which has cost more than one round: either the GROUND test seeded it - it is being
    ///     treated as resting on the world when it is three storeys up - or it is still LINKED to
    ///     something that is. The two need completely different fixes, so this prints both, and prints
    ///     the actual neighbours so a link that should not exist can be named.
    /// </summary>
    private static void DumpSupport(HashSet<ABuildingActor> supported, HashSet<ABuildingActor> groundSeeded) {
        Console.WriteLine($"BuildingStructuralSupportSystem: flood over {Buildings.Count} piece(s) using " +
                          $"{(ConnectivityEnabled ? "REAL CONNECTIVITY" : "the distance test")} - " +
                          $"{groundSeeded.Count} ground-seeded, {supported.Count} supported");

        foreach (var b in Buildings) {
            var loc = b.GetActorLocation();
            var ground = TerrainHeightMap.GetGroundHeightUnder(loc.X, loc.Y, FBuildingSupportCellIndex.PivotEdgeOffset);
            var neighbours = NeighborsOf(b).ToList();
            // The CONNECTIVITY cell, not b.CellIndex - the two disagree (b.CellIndex buckets by raw
            // location, this one quantises BaseLocation), and printing the wrong one sent a diagnosis
            // down the wrong path once already.
            var cell = StructuralCellOf(b);

            var why = groundSeeded.Contains(b) ? "GROUND"
                    : supported.Contains(b) ? "linked"
                    : "UNSUPPORTED -> falls";

            Console.WriteLine($"    {b.ClassName,-24} ({loc.X,7:0},{loc.Y,8:0},{loc.Z,6:0}) yaw {b.GetActorRotation().Yaw,4:0} " +
                              $"cell {cell.X},{cell.Y},{cell.Z} " +
                              $"ground {(ground is null ? "none" : $"{loc.Z - ground.Value:0}up")} " +
                              $"occupies {OccupancyOf(b),-6} " +
                              $"{why} via [{string.Join(" ", neighbours.Select(n => $"{n.ClassName}:{SharedWith(b, n)}"))}]");
        }
    }

    /// <summary>
    ///     Every registered piece close enough to `building` to be touching it. Reads only the 27
    ///     cells around the piece's own - see FBuildingSupportCellIndex.WithNeighbors for why the
    ///     neighbours have to be included and not just the piece's own cell.
    /// </summary>
    private static IEnumerable<ABuildingActor> NeighborsOf(ABuildingActor building) {
        var loc = building.GetActorLocation();

        foreach (var cellIndex in building.CellIndex.WithNeighbors()) {
            if (!Cells.TryGetValue(cellIndex, out var cell)) continue;

            foreach (var other in cell) {
                if (ReferenceEquals(other, building)) continue;
                if (AreTouching(building, other)) yield return other;
            }
        }
    }

    /// <summary>
    ///     Whether two pieces count as structurally joined.
    ///
    ///     Prefers Fortnite's OWN answer when both pieces are classes the shipped connectivity data
    ///     covers - see FortBuildingConnectivity, which is the authored ConnectivityCube the real
    ///     UBuildingStructuralSupportSystem::AreNeighborsConnected decides with. That rule knows
    ///     things a distance test cannot: that a half wall does not reach the floor above it, that a
    ///     brace only joins on one side, that two pieces meeting at a single corner are NOT joined.
    ///     Corner-only contact is the specific case that made edits leave structures standing with
    ///     their root severed - a false support link, and false support is what stops a cascade
    ///     firing at all.
    ///
    ///     Falls back to the old anisotropic distance test whenever the real rule has no opinion -
    ///     an uninitialised stand-in (map scenery, which has no blueprint class), or a class outside
    ///     the 321 the patterns cover. Null from AreConnected means "no data", NOT "not connected",
    ///     and treating it as the latter would silently stop cascades on anything unrecognised.
    ///
    ///     STRUCTURAL_CONNECTIVITY=1 turns the new rule on. OFF BY DEFAULT: the connectivity DATA is
    ///     verified, but the two frame conversions in ConnectivitySays are newly derived and have
    ///     never met a client, and a yaw convention that is wrong by 90 degrees would join each wall
    ///     to the wrong neighbours and collapse structures at random - i.e. it would destroy the
    ///     tester's builds rather than merely look wrong. The old rule half-works; that is a better
    ///     default than a new rule that might be inverted.
    /// </summary>
    private static bool AreTouching(ABuildingActor a, ABuildingActor b) {
        if (ConnectivityEnabled && ConnectivitySays(a, b) is { } connected) return connected;

        return IsWithinReach(a.GetActorLocation(), b.GetActorLocation());
    }

    private static bool ConnectivityEnabled =>
        Environment.GetEnvironmentVariable("STRUCTURAL_CONNECTIVITY") is "1";

    /// <summary>
    ///     Fortnite's own connectivity answer, or null when it has none.
    ///
    ///     THE TWO FRAME CONVERSIONS HERE ARE THE WHOLE POINT OF THIS METHOD, and both are measured
    ///     rather than assumed:
    ///
    ///     CELL. A piece's BaseLocation - its pivot with BaseLocToPivotOffset rotated back out - lands
    ///     exactly on the (512, 512, 384) grid for every family and every yaw. That is not an
    ///     assumption: across 1439 real logged placements, BuildLoc mod 512 is (0, 256) at yaw 0/180
    ///     and (256, 0) at yaw 90/270 for walls, floors, roofs and stairs alike, and z mod 384 is
    ///     always 0. So BaseLocation quantised by (512, 512, 384) is the cell, uniformly.
    ///
    ///     YAW. A constant +90 degrees, and it was MEASURED, not reasoned out. The edge-consistency
    ///     search that fixed the pattern axes could only pin them down to within the block's eight
    ///     symmetries, so which world direction "Front" names was never determined and no amount of
    ///     staring at the data settles it. What settles it is that a structure a player actually
    ///     built is CONNECTED: replaying real logged placements through all four candidate offsets,
    ///     +90 leaves almost no piece joined to nothing while the others strand many.
    ///
    ///         session A (49 pieces)   offset 0: 13 stranded   -90: 14   180: 10   +90: 3
    ///         session B (41 pieces)   offset 0:  6 stranded   -90:  4   180:  7   +90: 0
    ///
    ///     Reasoning from the pivot offsets had given -90, which the sweep shows is 180 degrees out.
    ///     PriveDev/dumpwork/ConnCheck replays this; re-run it before trusting any change here.
    /// </summary>
    private static bool? ConnectivitySays(ABuildingActor a, ABuildingActor b) {
        if (a.ClassName.Length == 0 || b.ClassName.Length == 0) return null;

        var (ax, ay, az) = StructuralCellOf(a);
        var (bx, by, bz) = StructuralCellOf(b);

        return FortBuildingConnectivity.AreConnected(
            a.ClassName, a.GetActorRotation().Yaw + PatternYawOffsetFor(a), a.bMirrored != MirrorFlip,
            b.ClassName, b.GetActorRotation().Yaw + PatternYawOffsetFor(b), b.bMirrored != MirrorFlip,
            bx - ax, by - ay, bz - az);
    }

    /// <summary>
    ///     THE OFFSET IS PER FAMILY, NOT GLOBAL - stairs need 90 and everything else needs 0. A single
    ///     global value cannot work, and chasing one cost two rounds.
    ///
    ///     How that was settled. Two half-floors (BalconyS) sat in the SAME cell at opposite yaws -
    ///     they are the two halves of one cell - one cell over from a stair. One linked to the stair
    ///     with 5 voxels, the other with 1, and the one that failed was the half whose PIVOT EDGE
    ///     faces the stair, i.e. the half that physically touches it. So the floor family's shape was
    ///     landing on the wrong side of its own cell. Sweeping each family's offset independently
    ///     against 243 real placements, subject to the four live "these must link" cases, leaves 64
    ///     combinations - and every single one has Wall 0, Floor 0, Stair 90.
    ///
    ///     That a stair is the odd one out is not surprising: it is the only family with an intrinsic
    ///     direction, so it is the only one whose authored frame has anything to be rotated relative
    ///     to. Roof and Pillar are NOT pinned down by the data available (no pillar was ever placed,
    ///     and only one roof pattern appears); they take the common 0 until something discriminates.
    ///
    ///     CONNECTIVITY_YAW_OFFSET / CONNECTIVITY_STAIR_YAW_OFFSET override the two.
    /// </summary>
    private static float PatternYawOffsetFor(ABuildingActor building) => building.BuildingType switch {
        EFortBuildingType.Stairs => StairYawOffset,
        EFortBuildingType.Floor or EFortBuildingType.Roof => FloorYawOffset,
        _ => BaseYawOffset
    };

    private static float BaseYawOffset =>
        float.TryParse(Environment.GetEnvironmentVariable("CONNECTIVITY_YAW_OFFSET"), out var v) ? v : 0f;

    /// <summary>
    ///     Floors (and roofs) need 90 where walls need 0 - confirmed live, and then re-derived: a
    ///     search over every family offset, both mirror handednesses and 243 real placements, subject
    ///     to five live constraints (the four half-floor edits around a stair, exactly one of which
    ///     must fall, plus a corner quadrant that must NOT hold itself up) leaves 64 combinations, and
    ///     every one of them is Wall 0 / Floor 90 / Stair 90 with the mirror flipped.
    ///
    ///     Roof rides the floor value. It is NOT pinned by the data - both 0 and 90 satisfy every
    ///     constraint - so a roof-only failure is the next thing to suspect, not a settled fact.
    /// </summary>
    private static float FloorYawOffset =>
        float.TryParse(Environment.GetEnvironmentVariable("CONNECTIVITY_FLOOR_YAW_OFFSET"), out var v) ? v : 90f;

    private static float StairYawOffset =>
        float.TryParse(Environment.GetEnvironmentVariable("CONNECTIVITY_STAIR_YAW_OFFSET"), out var v) ? v : 90f;

    /// <summary>
    ///     THE SHIPPED PATTERNS ARE THE OPPOSITE HANDEDNESS TO THIS SERVER, so the reflection is applied
    ///     by default and bMirrored INVERTS it. Set CONNECTIVITY_MIRROR_FLIP=0 to go back.
    ///
    ///     This was invisible for a long time because of what a reflection does and does not change:
    ///     mirroring `y -> 4 - y` leaves a HALF floor completely untouched (a half along X is
    ///     symmetric in Y) while swapping which corner a QUADRANT piece occupies. So every experiment
    ///     with full floors, walls and half floors agreed with both handednesses, and only a
    ///     single-quadrant edit could tell them apart - which is exactly the case that stayed broken
    ///     after the yaw offsets were right, and which flipping this fixes without moving any of the
    ///     half-floor results by a single voxel.
    /// </summary>
    private static bool MirrorFlip => Environment.GetEnvironmentVariable("CONNECTIVITY_MIRROR_FLIP") is not "0";

    /// <summary>The piece's cell for the connectivity model - see ConnectivitySays for why this is BaseLocation and not the pivot.</summary>
    private static (int X, int Y, int Z) StructuralCellOf(ABuildingActor building) {
        var b = FBuildingSupportCellIndex.BaseLocationOf(building.GetActorLocation(), building.GetActorRotation().Yaw);

        return ((int) MathF.Round(b.X / FBuildingSupportCellIndex.TileSize),
                (int) MathF.Round(b.Y / FBuildingSupportCellIndex.TileSize),
                (int) MathF.Round(b.Z / FBuildingSupportCellIndex.StoreyHeight));
    }

    private static bool IsWithinReach(FVector a, FVector b) =>
        MathF.Abs(a.X - b.X) <= HorizontalReach
     && MathF.Abs(a.Y - b.Y) <= HorizontalReach
     && MathF.Abs(a.Z - b.Z) <= VerticalReach;

    /// <summary>
    ///     Whether `building` rests on real ground - the flood's entry condition. Asks the surfaces
    ///     players have actually walked on first (TerrainGroundTruth), then TerrainHeightMap's baked
    ///     height where one covers this point; where neither does (nothing measured, no baked file,
    ///     or a point outside its extent), falls back to "this piece is the lowest thing in its
    ///     own XY column", which alone reproduces "destroy the base, everything above falls" without
    ///     any terrain data - it is just wrong wherever a structure floats with nothing under it at
    ///     all (a bridge off a cliff reads as self-supporting), which real ground heights fix. No
    ///     code path here changes once a baked heightmap exists; only the file has to.
    /// </summary>
    private static bool IsSupportedByWorld(ABuildingActor building) {
        var loc = building.GetActorLocation();

        // A SURFACE SOMEONE HAS WALKED ON COUNTS AS WORLD, and it is asked first. This closes a
        // known-open bug rather than adding a feature: the bake covers the LANDSCAPE, so a stair
        // standing on a POI roof, a bridge or a rock read as unsupported and the cascade deleted it.
        // TerrainGroundTruth knows those surfaces exactly wherever a player has stood on them, and it
        // knows them as LEVELS, so the roof is offered to a piece sitting on the roof while the
        // terrain far below is offered to one sitting on the terrain.
        //
        // EITHER SOURCE MAY SAY YES; neither may say no. A wrong "unsupported" DELETES the tester's
        // structures, so where the two disagree the permissive answer is the safe one - and both are
        // still bounded by the same one-storey rule, so neither can hold up a piece that is really
        // floating.
        if (TerrainGroundTruth.GetGroundHeightUnder(
                loc.X, loc.Y, FBuildingSupportCellIndex.PivotEdgeOffset, loc.Z) is { } walked &&
            loc.Z - walked < FBuildingSupportCellIndex.StoreyHeight) return true;

        // Highest baked surface anywhere under the piece's own tile that is not ABOVE the piece -
        // see TerrainHeightMap.GetGroundHeightUnder for the measurements, and for the live bug that
        // nearest-cell sampling caused (ground-level stairs reading as a storey and a half up, then
        // cascading away as soon as a neighbour went). Taking the placed-mesh grid into account is
        // the other half of the POI-roof fix: a stair on a roof now has something under it even
        // where nobody has walked, and the "not above the piece" rule is what stops the same roof
        // from being handed to a piece standing on the ground beside the building.
        // Strictly less than one storey below the piece: if the gap down to the ground is smaller
        // than a whole storey, nothing could fit underneath, so the piece must be resting on the
        // world. The real placement data agrees exactly - ground-level pieces top out at +352 above
        // this sample and the storey above starts at +385, with 384 sitting in the gap between them.
        static bool Rests(float pieceZ, float ground) => pieceZ - ground < FBuildingSupportCellIndex.StoreyHeight;

        // THE LANDSCAPE IS ASKED WITHOUT A CEILING, and that is a REGRESSION FIX, not belt and braces.
        // Switching this to the "highest surface at or below the piece" query quietly made the test
        // STRICTER: a piece standing on ground that slopes up within its own tile has its support
        // sample ABOVE its pivot, the ceiling threw that sample away, and the piece then read as
        // unsupported and was CASCADED AWAY - which is a player falling through their own build, and
        // is the exact bug ("ground-level stairs reading as a storey and a half up") this code was
        // written to fix in the first place.
        if (TerrainHeightMap.GetGroundHeightUnder(loc.X, loc.Y, FBuildingSupportCellIndex.PivotEdgeOffset)
            is { } landscape && Rests(loc.Z, landscape)) return true;

        // The ceiling-filtered query on top of that, which is what brings the PLACED MESH grid in: a
        // stair on a POI roof has something under it even where nobody has walked, and the ceiling is
        // what stops that same roof being offered to a piece standing on the ground beside it.
        if (TerrainHeightMap.GetSurfaceUnder(loc.X, loc.Y, FBuildingSupportCellIndex.PivotEdgeOffset,
                                            loc.Z) is { } ground)
            return Rests(loc.Z, ground);

        var lowest = float.MaxValue;
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++) {
            if (ColumnGround.TryGetValue((building.CellIndex.X + dx, building.CellIndex.Y + dy), out var z))
                lowest = MathF.Min(lowest, z);
        }

        return loc.Z <= lowest + 1f;
    }

}
