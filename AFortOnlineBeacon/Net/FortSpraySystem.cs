using AFortOnlineBeacon.Core.Objects;
using AFortOnlineBeacon.Net.Actors;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     Paints a spray on the world - the SERVER half of GAB_Spray_Generic, and the only half that
///     produces anything visible.
///
///     WHY THIS EXISTS AT ALL. Sprays reached the emote wheel, activated their ability, played their
///     montage and ended cleanly, and painted nothing, for exactly one reason: the whole body of
///     `GAB_Spray_Generic`'s event graph sits behind `EX_JumpIfNot IsServer`. The sprayer's own client
///     runs the animation and stops there; the decal is a replicated actor the AUTHORITY spawns. So
///     no amount of ability plumbing was ever going to make a spray appear.
///
///     Everything below is transcribed from that graph (Tools/BlueprintDump on
///     Abilities/Sprays/GAB_Spray_Generic.uasset), not inferred:
///
///         MySpray = Cast&lt;UAthenaSprayItemDefinition&gt;(GetCurrentSourceObject())
///         TargetLineTrace:
///             start = ActorLocation with Z += CapsuleHalfHeight
///             end   = start + GetBaseAimRotation().GetForwardVector() * DecalTraceDistance(600)
///         the hit actor must answer AcceptsEmoteSprays
///         CanPlaceInstanceOfClass(PlayerController, BP_SprayDecal_C, HitLocation)
///         right = ActivatingPawn.CapsuleComponent.GetRightVector()
///         rot   = MakeRotationFromAxes(HitNormal, -right, Cross(HitNormal, -right))
///         decal = BeginDeferredActorSpawnFromClass(BP_SprayDecal_C, MakeTransform(HitLocation, rot, 1))
///             DecalSize = 96, SprayAsset = MySpray, SpawningPlayerController = PC, Instigator = pawn
///         FinishSpawningActor()
///
///     Of the four deferred property sets, only SprayAsset is REPLICATED (see
///     AFortSprayDecalInstance), so it is the only one this server can hand over - the rest are
///     Blueprint variables the client fills from its own CDO.
/// </summary>
internal static class FortSpraySystem {
    /// <summary>GAB_Spray_Generic::DecalTraceDistance, off the ability's own CDO.</summary>
    private const float TraceDistance = 600f;

    /// <summary>
    ///     Where the trace starts: the pawn's replicated location is the CAPSULE CENTRE, and the
    ///     Blueprint adds the capsule half-height on top of it, so this is the top of the capsule
    ///     rather than the eye. Same 96 the projectile system uses, and overridable the same way -
    ///     see FortProjectileSystem.CapsuleHalfHeight for why it is a convention and not read from
    ///     the asset.
    /// </summary>
    private static float CapsuleHalfHeight =>
        float.TryParse(Environment.GetEnvironmentVariable("PAWN_CAPSULE_HALF_HEIGHT"), out var s) && s > 0 ? s : 96f;

    /// <summary>
    ///     How far INTO a player-built surface to push the decal, along -normal.
    ///
    ///     A player build's collision here is a coarse box - 64 units thick for a wall
    ///     (BuildingStructuralSupportSystem.WallHalfThickness = 32 either side of the pivot plane),
    ///     while the real wall MESH is a fraction of that. So a hit on the box lands in mid-air a
    ///     couple of dozen units in front of the wall the player can see, and a decal projecting from
    ///     there may never reach it. A hull hit has no such gap: those ARE the game's own shapes.
    ///
    ///     SNAPPED TO THE PIECE, NOT PUSHED BY A CONSTANT. The first attempt at this was a fixed
    ///     inset, which is only right for a wall; asking the piece where its body is works for every
    ///     type - see SnapToPiece. SPRAY_BUILD_INSET adds a further nudge on top for tuning, and is 0
    ///     by default because the snap should not need one.
    ///
    ///     THE LIVE SYMPTOM THIS FIXES: spraying a built wall straight on showed nothing, and spraying
    ///     the same wall at an ANGLE showed PART of the decal - which is exactly what a projection box
    ///     floating just off a surface does. Hull hits are never adjusted: those ARE the game's own
    ///     shapes and have been pixel-perfect since the first live test.
    /// </summary>
    private static float BuildInset =>
        float.TryParse(Environment.GetEnvironmentVariable("SPRAY_BUILD_INSET"), out var inset) ? inset : 0f;

    /// <summary>SPRAY_DECALS=0 turns the decal off while leaving the emote itself working.</summary>
    private static bool Enabled => Environment.GetEnvironmentVariable("SPRAY_DECALS") is not "0";

    /// <summary>
    ///     How many decals one player may have standing. Real Fortnite fades the oldest out
    ///     (BP_SprayDecal's StartSprayFadeOutDueToNewPlacement, and the controller keeps an
    ///     ActiveSprayInstances array); this destroys it, which is the same thing without the fade.
    ///     Unbounded is not an option - a player can spray as fast as the emote ends.
    /// </summary>
    private static int MaxPerPlayer =>
        int.TryParse(Environment.GetEnvironmentVariable("SPRAY_DECAL_LIMIT"), out var n) && n > 0 ? n : 3;

    private static readonly Dictionary<APlayerController, List<AFortSprayDecalInstance>> Active = new();

    /// <summary>
    ///     Called from FortEmoteSystem the moment a spray ability is granted. Silently does nothing
    ///     when the trace finds no surface - which is correct, and is the Blueprint's own behaviour:
    ///     spraying at the sky paints nothing.
    /// </summary>
    public static void Paint(APlayerController controller, UObject sprayAsset) {
        if (!Enabled) return;
        if (controller.Pawn is not { } pawn) return;
        if (pawn.GetWorld() is not { } world) return;

        var origin = pawn.GetActorLocation();
        var start = new FVector { X = origin.X, Y = origin.Y, Z = origin.Z + CapsuleHalfHeight };

        // GetBaseAimRotation, and THE CAMERA IS THE BETTER SOURCE FOR IT. For a player-controlled
        // pawn that call resolves to the control rotation, which is exactly what
        // APlayerController::ServerUpdateCamera reports - the most frequent RPC this connection
        // sends, decoded already into LastClientCameraRotation. The move RPC's own View is the
        // fallback: it is the same rotation, but a move is only sent when the character MOVES, so a
        // player who stands still and looks down leaves it stale, which is what the first live test
        // looked like (pitch reported as -6.75 degrees while aiming at the floor).
        var aim = controller.LastClientCameraRotation ?? pawn.LastClientViewRotation ?? new FRotator();

        // WHICH source the aim came from, in both log lines. The two disagree in exactly the case
        // that matters - a player standing still and looking down - so a pitch that looks wrong is
        // only diagnosable if it says where it came from.
        var aimSource = controller.LastClientCameraRotation != null ? "camera"
            : pawn.LastClientViewRotation != null ? "moveView" : "NOTHING"; 
        var forward = aim.GetForwardVector();
        var end = new FVector {
            X = start.X + forward.X * TraceDistance,
            Y = start.Y + forward.Y * TraceDistance,
            Z = start.Z + forward.Z * TraceDistance
        };

        // THREE SEPARATE WORLDS, AND A SPRAY CAN LAND ON ANY OF THEM. This server keeps the map's
        // geometry in three unrelated places, and the first two live tests each hit a different gap:
        //
        //   * WorldCollision  - the game's own convex hulls for STATIC MESHES: POI walls, rocks,
        //                       trees, props. The landscape is NOT in it.
        //   * TerrainHeightMap - the landscape, as a height field ([[map-collision-bake]]).
        //   * BuildingStructuralSupportSystem - pieces PLAYERS built this match, which exist only at
        //                       runtime and can be in neither bake by definition.
        //
        // Aiming at bare ground missed because of the first gap; aiming at a wall the player had just
        // built missed because of the third. Nearest wins rather than first-to-answer, because they
        // overlap: a player build standing on terrain must beat the ground behind it.
        var hull = WorldCollision.Sweep(start, end);
        // AN EXACT HIT NEEDS NO SNAPPING. The build sweep now clips against the piece's own convex
        // hulls (FortBuildingHulls), so the point it returns is already ON the mesh and the normal is
        // the real surface's - SnapToPiece exists for the older coarse-box answer, where the hit was
        // in mid-air in front of the wall, and applying it to an exact hit would push the decal off
        // the surface instead of onto it.
        var built = BuildingStructuralSupportSystem.SweepToBuildSurface(start, end) is { } build
            ? build.Exact
                ? (build.Point, build.Normal)
                : Inset(SnapToPiece(build.Point, AxisOf(build.Normal), build.Piece, forward), BuildInset)
            : ((FVector Point, FVector Normal)?) null;
        var ground = SweepTerrain(start, end);

        var hit = Nearest(start, Nearest(start, hull, built), ground);

        // WHICH of the three won, named in the success line too. "It painted" and "it painted on the
        // thing I was aiming at" are different claims, and only this separates them: a decal that
        // lands on the TERRAIN behind a player-built wall looks, from the server's side, exactly like
        // one that landed on the wall.
        var source = hit == null ? "none"
            : ReferenceEquals(hit, hull) || (hull != null && hit.Value.Point.Equals(hull.Value.Point)) ? "hull"
            : built != null && hit.Value.Point.Equals(built.Value.Point) ? "playerBuild"
            : "terrain";

        if (hit is not { } surface) {
            // EVERY SOURCE REPORTS, because "nothing was hit" has three different causes and they
            // need three different fixes. The ground height under the far end is printed too: a null
            // there means the terrain bake is simply not loaded, which no amount of aiming will fix.
            var groundAtEnd = SurfaceAt(end.X, end.Y, start.Z);

            // "landscape height HERE" and "the bake is loaded" are DIFFERENT questions, and the first
            // version of this line conflated them. The warmup island is a placed foundation sitting
            // off the landscape entirely, so there is no height under it and that reads exactly like
            // a missing file - which is how one report of "terrainLoaded=False" nearly sent this
            // hunting for a bake that was present and fine all along.
            Console.WriteLine($"FortSpraySystem: nothing within {TraceDistance:0} units - no decal. " +
                              $"from={start} to={end} aim={aim} (from the {aimSource}) " +
                              $"| hulls={(hull == null ? "miss" : "hit")} " +
                              $"playerBuilds={(built == null ? "miss" : "hit")} terrain={(ground == null ? "miss" : "hit")} " +
                              $"| surfaceZ at the end={(groundAtEnd?.ToString("F1") ?? "nothing baked here")}, " +
                              $"landscapeBakeLoaded={TerrainHeightMap.LandscapeLoaded}, " +
                              $"meshGridLoaded={TerrainHeightMap.HasMeshGrid}, " +
                              $"landscapeExtent={Extent()}, " +
                              $"hullShapes={WorldCollision.ShapeCount}, hullInstances={WorldCollision.InstanceCount}, " +
                              $"solidJustBelowTheFeet={SolidBelow(origin)}, " +
                              $"buildsNearby={BuildingStructuralSupportSystem.PiecesWithin(start, 1024f)}");
            return;
        }

        // MakeRotationFromAxes(Normal, -right, Cross(Normal, -right)): the decal's FORWARD axis is
        // the surface normal, so it faces out of the wall, and its up/right come from the player's
        // own facing - which is what keeps a spray upright rather than rolled to some arbitrary
        // angle on a slanted surface.
        // The capsule's right vector, which follows the pawn's YAW only - a capsule does not pitch
        // with the aim. Y axis of a yaw-only rotation: forward is (cos y, sin y, 0), so right is
        // that turned 90 degrees, (-sin y, cos y, 0).
        var yawRadians = aim.Yaw * MathF.PI / 180f;
        var negRight = new FVector { X = MathF.Sin(yawRadians), Y = -MathF.Cos(yawRadians), Z = 0f };
        var rotation = MakeRotationFromAxes(surface.Normal, negRight);

        var decal = world.SpawnActor<AFortSprayDecalInstance>(AFortSprayDecalInstance.Class,
            new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient });

        if (decal == null) return;

        decal.SetRole(ENetRole.ROLE_Authority);
        decal.SetActorLocation(surface.Point);
        decal.SetActorRotation(rotation);
        decal.MarkAsLevelActor();          // not player-placed: it must never enter the damage path
        decal.SprayAsset = sprayAsset;
        decal.SetOwner(controller);        // BP_SprayDecal::SpawningPlayerController's nearest equivalent
        decal.SetReplicates(true);

        world.NetDriver?.AddNetworkActor(decal);
        Remember(controller, decal);

        Console.WriteLine($"FortSpraySystem: painted {sprayAsset.GetFName()} on {source} at {surface.Point} " +
                          $"normal={surface.Normal} rot={rotation} (aim {aim} from the {aimSource}, from {start}, " +
                          $"candidates hull={(hull == null ? "-" : "hit")} build={(built == null ? "-" : "hit")} " +
                          $"terrain={(ground == null ? "-" : "hit")})");
    }

    /// <summary>The dominant axis of a normal (0 = X, 1 = Y, 2 = Z), for the coarse-box fallback.</summary>
    private static int AxisOf(FVector normal) {
        float x = MathF.Abs(normal.X), y = MathF.Abs(normal.Y), z = MathF.Abs(normal.Z);
        return x >= y && x >= z ? 0 : y >= z ? 1 : 2;
    }

    /// <summary>
    ///     Puts the decal ON the player-built piece rather than on the coarse box around it.
    ///
    ///     THE BOX IS NOT THE PIECE. A wall's collision here is 64 units thick, 32 either side of the
    ///     pivot plane, while the wall the player can see is a fraction of that - so the ray's entry
    ///     point is in mid-air in front of the wall, and a decal projected from there reaches the
    ///     surface only where the projection box happens to clip it. That is why spraying a built wall
    ///     STRAIGHT ON showed nothing while spraying it at an ANGLE showed part of the picture.
    ///
    ///     The fix is the one the box cannot give and the piece can: FBuildingSupportCellIndex.CentroidOf is read
    ///     from the real CDOs and says where a piece's BODY sits. Replacing just the hit axis'
    ///     component with the centroid's lands the decal on the piece's own plane - flush against a
    ///     wall, in the middle of a floor slab - for every piece type, with no per-type constant.
    ///
    ///     The other two axes keep the ray's own values, so WHERE on the wall the player aimed is
    ///     preserved exactly; only the depth is corrected.
    /// </summary>
    private static (FVector Point, FVector Normal) SnapToPiece(
        FVector point, int axis, ABuildingActor piece, FVector direction) {
        var centroid = FBuildingSupportCellIndex.CentroidOf(piece.GetActorLocation(), piece.GetActorRotation().Yaw,
                                                 piece.BuildingType);

        var snapped = axis switch {
            0 => new FVector { X = centroid.X, Y = point.Y, Z = point.Z },
            1 => new FVector { X = point.X, Y = centroid.Y, Z = point.Z },
            _ => new FVector { X = point.X, Y = point.Y, Z = centroid.Z }
        };

        return (snapped, AxisNormal(axis, direction));
    }

    /// <summary>Moves a hit `by` units along -normal, i.e. into the surface. See BuildInset.</summary>
    private static (FVector Point, FVector Normal) Inset((FVector Point, FVector Normal) hit, float by) =>
        by == 0f
            ? hit
            : (new FVector {
                X = hit.Point.X - hit.Normal.X * by,
                Y = hit.Point.Y - hit.Normal.Y * by,
                Z = hit.Point.Z - hit.Normal.Z * by
            }, hit.Normal);

    /// <summary>
    ///     WHETHER THE SERVER KNOWS ABOUT THE FLOOR THE PLAYER IS STANDING ON, which is the one
    ///     question a downward miss cannot answer on its own. A player standing at capsule centre Z
    ///     has their feet a half-height below; if nothing is solid just under that, this server has
    ///     no geometry for the surface they are visibly standing on - and no aim will ever make a
    ///     downward trace hit it.
    /// </summary>
    private static string SolidBelow(FVector capsuleCentre) {
        var feetZ = capsuleCentre.Z - CapsuleHalfHeight;
        var probe = new FVector { X = capsuleCentre.X, Y = capsuleCentre.Y, Z = feetZ - 8f };

        var hull = WorldCollision.IsSolid(probe);
        var build = BuildingStructuralSupportSystem.IsSolid(probe);
        var landscape = TerrainHeightMap.GetGroundHeight(probe.X, probe.Y);
        var surface = SurfaceAt(probe.X, probe.Y, capsuleCentre.Z);

        return $"hull={hull} build={build} landscapeZ={(landscape?.ToString("F0") ?? "-")} " +
               $"surfaceZ={(surface?.ToString("F0") ?? "-")} (feet at {feetZ:F0})";
    }

    /// <summary>The landscape bake's own bounds, so "no height here" can be told from "outside the bake".</summary>
    private static string Extent() =>
        TerrainHeightMap.TryGetExtent(out var minX, out var minY, out var maxX, out var maxY)
            ? $"X {minX:F0}..{maxX:F0} Y {minY:F0}..{maxY:F0}"
            : "none";

    /// <summary>
    ///     The highest baked surface at (x, y) at or below where the trace began - landscape or placed
    ///     mesh, whichever is higher. `fromZ` is what keeps a POI's ROOF from being handed to someone
    ///     spraying the ground beside the building; only the caller knows which it is asking about.
    ///
    ///     Tolerance 0 rather than the default 128: a descending ray wants the surface it is about to
    ///     cross, and letting one that is already ABOVE the ray count would stop the march early.
    /// </summary>
    private static float? SurfaceAt(float x, float y, float fromZ) =>
        TerrainHeightMap.GetSurfaceUnder(x, y, SurfaceSampleRadius, fromZ, tolerance: 0f);

    /// <summary>One grid cell's worth, so a sample between cells still finds the surface it sits on.</summary>
    private const float SurfaceSampleRadius = 100f;

    /// <summary>Whichever of two candidate hits is closer to the trace's start; either may be null.</summary>
    private static (FVector Point, FVector Normal)? Nearest(
        FVector from, (FVector Point, FVector Normal)? a, (FVector Point, FVector Normal)? b) {
        if (a is not { } first) return b;
        if (b is not { } second) return a;

        return DistanceSquared(from, first.Point) <= DistanceSquared(from, second.Point) ? a : b;
    }

    private static float DistanceSquared(FVector a, FVector b) {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>
    ///     A box face's outward normal, from the axis the slab test entered on (0=X, 1=Y, 2=Z) and the
    ///     direction of travel - the face a ray enters always points back AGAINST it. That is all the
    ///     information an axis-aligned box hit carries, and it is exactly enough: a player-built wall
    ///     really is axis-aligned to its own placement grid.
    /// </summary>
    private static FVector AxisNormal(int axis, FVector direction) {
        var component = axis switch { 0 => direction.X, 1 => direction.Y, _ => direction.Z };
        var sign = component > 0f ? -1f : 1f;

        return axis switch {
            0 => new FVector { X = sign },
            1 => new FVector { Y = sign },
            _ => new FVector { Z = sign }
        };
    }

    /// <summary>
    ///     The baked height field - marched rather than solved, because a height field has no closed
    ///     form to clip a segment against.
    ///
    ///     GetSurfaceUnder, NOT GetGroundHeight, AND THAT IS THE WHOLE FIX FOR SPRAYING A FLOOR. There
    ///     are two grids: the LANDSCAPE and the PLACED MESHES (1.8 million cells of it). Athena's POIs
    ///     stand on foundation meshes, so 78% of the landscape grid is NoData and every POI floor
    ///     reads as empty through GetGroundHeight - which is precisely what the live diagnostic showed:
    ///     `landscapeBakeLoaded=True` with `landscapeZ=no landscape here` and the player visibly
    ///     standing on a floor at Z 3845. The mesh grid is where that floor lives.
    ///
    ///     Steps along the ray looking for the first sample that is UNDER the surface, then bisects
    ///     that step to place the point on it. The normal comes from the field's own slope, sampled
    ///     either side of the hit, so a spray on a hillside lies along the hill.
    ///
    ///     Returns null when nothing is baked here, when the ray never goes below the surface, or when
    ///     it STARTS below it - the last of which means the shot began inside geometry, and a surface
    ///     behind the player is not what was aimed at.
    /// </summary>
    private static (FVector Point, FVector Normal)? SweepTerrain(FVector from, FVector to) {
        const float step = 25f;

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;
        var length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length <= 1e-3f) return null;

        var steps = Math.Max(1, (int) MathF.Ceiling(length / step));
        var previous = 0f;

        for (var i = 1; i <= steps; i++) {
            var t = (float) i / steps;
            var z = from.Z + dz * t;
            var ground = SurfaceAt(from.X + dx * t, from.Y + dy * t, from.Z);

            if (ground == null) { previous = t; continue; }

            if (z > ground.Value) { previous = t; continue; }

            // Crossed between `previous` and `t`. Ten bisections put the point within a millimetre
            // of the surface over a 600-unit ray, which is far finer than a 96-unit decal needs.
            var lo = previous;
            var hi = t;
            for (var b = 0; b < 10; b++) {
                var mid = (lo + hi) * 0.5f;
                var midGround = SurfaceAt(from.X + dx * mid, from.Y + dy * mid, from.Z);
                if (midGround != null && from.Z + dz * mid <= midGround.Value) hi = mid; else lo = mid;
            }

            if (hi <= 0f) return null; // started underground

            var point = new FVector {
                X = from.X + dx * hi,
                Y = from.Y + dy * hi,
                Z = from.Z + dz * hi
            };

            return (point, TerrainNormal(point));
        }

        return null;
    }

    /// <summary>
    ///     The landscape's normal at a point, from central differences over a 50-unit span - the
    ///     gradient of a height field IS its normal, as (-dz/dx, -dz/dy, 1) normalised. Falls back to
    ///     straight up wherever a neighbour is outside the bake.
    /// </summary>
    private static FVector TerrainNormal(FVector at) {
        const float span = 50f;

        var xPlus = SurfaceAt(at.X + span, at.Y, at.Z + span);
        var xMinus = SurfaceAt(at.X - span, at.Y, at.Z + span);
        var yPlus = SurfaceAt(at.X, at.Y + span, at.Z + span);
        var yMinus = SurfaceAt(at.X, at.Y - span, at.Z + span);

        if (xPlus == null || xMinus == null || yPlus == null || yMinus == null) return new FVector { Z = 1f };

        return Normalize(new FVector {
            X = -(xPlus.Value - xMinus.Value) / (2f * span),
            Y = -(yPlus.Value - yMinus.Value) / (2f * span),
            Z = 1f
        });
    }

    /// <summary>Drops this player's oldest decals once they are over the limit.</summary>
    private static void Remember(APlayerController controller, AFortSprayDecalInstance decal) {
        if (!Active.TryGetValue(controller, out var list)) Active[controller] = list = new List<AFortSprayDecalInstance>();

        list.Add(decal);

        while (list.Count > MaxPerPlayer) {
            var oldest = list[0];
            list.RemoveAt(0);
            oldest.Destroy();

            // Said out loud, because "the spray I just painted is gone" and "the spray never
            // appeared" look identical in the game and are opposite problems. SPRAY_DECAL_LIMIT
            // raises the cap.
            Console.WriteLine($"FortSpraySystem: over the {MaxPerPlayer}-decal limit - destroyed the " +
                              "oldest one (SPRAY_DECAL_LIMIT raises it)");
        }
    }

    /// <summary>
    ///     FMatrix(X, Y, Z, 0).Rotator(), for the two axes that decide it.
    ///
    ///     Pitch and yaw come from the X axis alone; roll is then the angle between the given Y axis
    ///     and the Y axis a roll-free rotation would have produced, which for Roll=0 is simply
    ///     (-sin(yaw), cos(yaw), 0). That is UE's own FMatrix::Rotator, with the FRotationMatrix
    ///     round-trip written out rather than rebuilt - the Z axis it also consults is the cross
    ///     product of the other two, so it carries no information the first two do not.
    /// </summary>
    private static FRotator MakeRotationFromAxes(FVector x, FVector y) {
        var xAxis = Normalize(x);
        var yAxis = Normalize(y);
        var zAxis = Cross(xAxis, yAxis);

        var pitch = MathF.Atan2(xAxis.Z, MathF.Sqrt(xAxis.X * xAxis.X + xAxis.Y * xAxis.Y));
        var yaw = MathF.Atan2(xAxis.Y, xAxis.X);

        var syAxis = new FVector { X = -MathF.Sin(yaw), Y = MathF.Cos(yaw), Z = 0f };
        var roll = MathF.Atan2(Dot(zAxis, syAxis), Dot(yAxis, syAxis));

        const float toDegrees = 180f / MathF.PI;
        return new FRotator { Pitch = pitch * toDegrees, Yaw = yaw * toDegrees, Roll = roll * toDegrees };
    }

    private static FVector Normalize(FVector v) {
        var length = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return length <= 1e-6f ? new FVector { X = 1f } : new FVector { X = v.X / length, Y = v.Y / length, Z = v.Z / length };
    }

    private static FVector Cross(FVector a, FVector b) => new() {
        X = a.Y * b.Z - a.Z * b.Y,
        Y = a.Z * b.X - a.X * b.Z,
        Z = a.X * b.Y - a.Y * b.X
    };

    private static float Dot(FVector a, FVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
