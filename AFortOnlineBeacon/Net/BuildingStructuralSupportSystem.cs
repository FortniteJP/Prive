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

        while (queue.Count > 0) {
            foreach (var neighbor in NeighborsOf(queue.Dequeue())) {
                if (supported.Add(neighbor)) queue.Enqueue(neighbor);
            }
        }

        // Materialised before destroying anything: Destroy() re-enters this class through
        // ABuildingActor.Destroyed -> Unregister, which mutates both the registry and the buckets
        // NeighborsOf reads.
        var unsupported = Buildings.Where(b => !supported.Contains(b)).ToList();
        if (unsupported.Count == 0) return;

        Console.WriteLine($"BuildingStructuralSupportSystem: cascade - {unsupported.Count} piece(s) lost support");
        foreach (var b in unsupported) BeginDestroy(b);
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
                if (IsWithinReach(loc, other.GetActorLocation())) yield return other;
            }
        }
    }

    private static bool IsWithinReach(FVector a, FVector b) =>
        MathF.Abs(a.X - b.X) <= HorizontalReach
     && MathF.Abs(a.Y - b.Y) <= HorizontalReach
     && MathF.Abs(a.Z - b.Z) <= VerticalReach;

    /// <summary>
    ///     Whether `building` rests on real ground - the flood's entry condition. Prefers
    ///     TerrainHeightMap's baked height where one covers this point; where none does (no baked
    ///     file, or a point outside its extent), falls back to "this piece is the lowest thing in its
    ///     own XY column", which alone reproduces "destroy the base, everything above falls" without
    ///     any terrain data - it is just wrong wherever a structure floats with nothing under it at
    ///     all (a bridge off a cliff reads as self-supporting), which real ground heights fix. No
    ///     code path here changes once a baked heightmap exists; only the file has to.
    /// </summary>
    private static bool IsSupportedByWorld(ABuildingActor building) {
        var loc = building.GetActorLocation();

        // Highest ground anywhere under the piece's own tile, not the single cell nearest its pivot
        // - see TerrainHeightMap.GetGroundHeightUnder for the measurements, and for the live bug
        // that nearest-cell sampling caused (ground-level stairs reading as a storey and a half up,
        // then cascading away as soon as a neighbour went).
        if (TerrainHeightMap.GetGroundHeightUnder(loc.X, loc.Y, FBuildingSupportCellIndex.PivotEdgeOffset) is { } ground) {
            // Strictly less than one storey: if the gap down to the ground is smaller than a whole
            // storey, nothing could fit underneath, so this piece must be resting on the world. The
            // real placement data agrees exactly - ground-level pieces top out at +352 above this
            // sample and the storey above starts at +385, with 384 sitting in the gap between them.
            return loc.Z - ground < FBuildingSupportCellIndex.StoreyHeight;
        }

        var lowest = float.MaxValue;
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++) {
            if (ColumnGround.TryGetValue((building.CellIndex.X + dx, building.CellIndex.Y + dy), out var z))
                lowest = MathF.Min(lowest, z);
        }

        return loc.Z <= lowest + 1f;
    }

}
