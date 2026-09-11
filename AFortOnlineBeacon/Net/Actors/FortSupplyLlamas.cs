using AFortOnlineBeacon.Core.Math;
using AFortOnlineBeacon.Core.Objects;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     The match's supply llamas - five of them, standing on the ground somewhere on the island.
///
///     HOW MANY, AND WHY FIVE. DefaultMapInfo carries LlamaQuantityMin/Max as FScalableFloats whose
///     curve rows are Default.Llamas.QuantityMin / .QuantityMax in AthenaGameData; both read 5 at
///     index 0. So the range collapses to a single number and there is nothing to roll. (The PR3.0
///     capture has six, which is a modded server's business, not the shipped data's - the same
///     divergence the vehicle percentages show.)
///
///     WHERE. Real Fortnite picks navmesh points; this server has no navigation data at all. What it
///     does have is the baked terrain heightmap (see TerrainHeightMap), so a position is chosen by
///     rejection sampling: draw an X/Y inside the heightmap's own extent, ask it for the ground
///     there, and keep the point if there is ground and it is above the sea. That puts llamas on
///     open ground across the island, which is where they belong - and it is honest about what it
///     cannot do: it knows the landscape is there, not whether the spot is inside a rock or on a
///     roof.
///
///     The extent comes from the heightmap rather than from the aircraft's MapCenter/DropZoneExtent
///     or DefaultMapInfo's AircraftDropZone, because both of those describe where the BUS may fly,
///     which is a box around the origin and not the same thing as where the ground is.
///
///     NO HEIGHTMAP, NO LLAMAS. TerrainHeightMap.bin is optional for this project (the building
///     cascade degrades without it), and rather than scatter five llamas at guessed heights this
///     places none and says so - a llama floating in the air or buried in a hill is worse than no
///     llama.
///
///     The alternative considered and rejected was reusing FortFloorLoot's 932 authored spawn
///     points. They are certainly walkable, but they are overwhelmingly INDOORS - they are where the
///     map wants floor loot, i.e. inside buildings - and a llama tucked into a bedroom is a worse
///     approximation than one on a hillside.
///
///     Placed at match start rather than lazily, which is the one place this differs from the
///     vehicles and the floor loot. Five actors is nothing next to their hundreds, and a llama is
///     something players go looking for from across the map - its CDO carries a minimap icon
///     (AthenaSupplyDropIconLlamaMapIcon), which is only any use if the actor has reached a client
///     who is nowhere near it yet.
/// </summary>
internal static class FortSupplyLlamas {

    /// <summary>This world's share of FortSupplyLlamas's state - see FWorldSubsystem.</summary>
    private sealed class FSupplyLlamaState : FWorldSubsystem {
        public bool Enabled => Options.Get("LLAMAS_ENABLED") is not "0";

        /// <summary>Default.Llamas.QuantityMin/Max, both 5 at index 0 in AthenaGameData.</summary>
        public int Quantity =>
            int.TryParse(Options.Get("LLAMA_COUNT"), out var n) ? n : 5;

        /// <summary>
        ///     TEMPORARY, AND ON BY DEFAULT ON PURPOSE. Puts the llamas in a ring on the warmup island
        ///     instead of scattering them over the map.
        ///
        ///     Asked for because the real placement is untestable by hand: five llamas somewhere in a
        ///     390km-square heightmap cannot be walked to, so nothing about them - do they stand on the
        ///     ground, does the interact prompt appear, does opening one drop its 49 items, does the
        ///     pinata burst play - could be checked at all. On the warmup island they are ten seconds
        ///     away.
        ///
        ///     SPAWN_AT_WARMUP=0 restores the real placement. The log says loudly which mode is running,
        ///     so this cannot be left on by accident.
        /// </summary>
        public bool AtWarmupIsland => Options.Get("SPAWN_AT_WARMUP") is not "0";

        public bool _placed;

        /// <summary>The llamas, so anything that needs to enumerate them (channel opening) can.</summary>
        public readonly List<AFortAthenaSupplyDropLlama> Llamas = new();
        public Random Rng = default!;
        protected internal override void Initialize() {
            Rng = new(
                int.TryParse(Options.Get("LLAMA_SEED"), out var seed) ? seed : 20191001);
        }
    }

    private static FSupplyLlamaState StateOf(UWorld world) => world.GetSubsystem<FSupplyLlamaState>();

    /// <summary>
    ///     AAthenaSupplyDrop_Llama_C's own CDO: SpawnOffsetZ = 87. The mesh's origin is at its feet
    ///     and the collision wants clearance, so this is what the real actor is raised by.
    /// </summary>
    private const float SpawnOffsetZ = 87f;

    /// <summary>
    ///     Ground at or below this is water. Athena's sea plane sits around -5000 and this server
    ///     has watched a fallen player come to rest at -5042; 0 leaves a wide margin, since a llama
    ///     on a beach is fine and a llama in the sea is not.
    /// </summary>
    private const float SeaLevel = 0f;

    /// <summary>
    ///     Place them, once, on the first tick that has a world to place them in.
    ///
    ///     Not in InitGameState: the heightmap is loaded lazily on first query, and doing this from
    ///     the tick keeps the "read a file off disk" out of the login path entirely.
    /// </summary>
    public static void Tick(UWorld world, float now) {
        var state = StateOf(world);

        if (state._placed || !state.Enabled) return;
        state._placed = true;

        if (state.AtWarmupIsland) {
            PlaceOnWarmupIsland(world);
            return;
        }

        if (!TerrainHeightMap.TryGetExtent(out var minX, out var minY, out var maxX, out var maxY)) {
            Console.WriteLine("FortSupplyLlamas: no baked terrain heightmap, so there is nowhere to " +
                              "stand a llama - none placed. See TerrainHeightMap.");
            return;
        }

        var wanted = state.Quantity;
        // Bounded so a heightmap that is mostly ocean or mostly holes cannot spin here forever - it
        // gives up and places fewer llamas rather than hanging the world tick, and says so.
        var attempts = wanted * 200;

        while (state.Llamas.Count < wanted && attempts-- > 0) {
            var x = minX + (float) state.Rng.NextDouble() * (maxX - minX);
            var y = minY + (float) state.Rng.NextDouble() * (maxY - minY);

            if (TerrainHeightMap.GetGroundHeight(x, y) is not { } z || z <= SeaLevel) continue;

            Spawn(world, x, y, z + SpawnOffsetZ);
        }

        if (state.Llamas.Count < wanted) {
            Console.WriteLine($"FortSupplyLlamas: only placed {state.Llamas.Count} of {wanted} - ran out of " +
                              "attempts finding ground above sea level in the baked heightmap's extent.");
        }
    }

    /// <summary>
    ///     The SPAWN_AT_WARMUP placement: a ring of llamas around the island's first player start.
    ///
    ///     A RING rather than a heap, and offset from the start itself, so they do not spawn on top
    ///     of the player or of each other - a llama inside a player is not a test of anything. The
    ///     island's own authored Z is used rather than the terrain heightmap: the warmup island is a
    ///     placed sublevel, not landscape, so the heightmap has nothing to say about it.
    /// </summary>
    private static void PlaceOnWarmupIsland(UWorld world) {
        var state = StateOf(world);

        var anchor = FortWarmupStarts.Anchor;
        var radius = world.Options.Float("WARMUP_LLAMA_RADIUS", 1200f);
        var wanted = state.Quantity;

        Console.WriteLine($"FortSupplyLlamas: SPAWN_AT_WARMUP - placing {wanted} llama(s) in a {radius:F0}u " +
                          $"ring around the warmup island start ({anchor.X:F0}, {anchor.Y:F0}, {anchor.Z:F0}). " +
                          "Set SPAWN_AT_WARMUP=0 for the real map-wide placement.");

        for (var i = 0; i < wanted; i++) {
            var angle = MathF.Tau * i / wanted;
            Spawn(world,
                anchor.X + MathF.Cos(angle) * radius,
                anchor.Y + MathF.Sin(angle) * radius,
                anchor.Z + SpawnOffsetZ);
        }
    }

    private static void Spawn(UWorld world, float x, float y, float z) {
        var state = StateOf(world);

        var llama = world.SpawnActor<AFortAthenaSupplyDropLlama>(
            GUClassArray.StaticClass<AFortAthenaSupplyDropLlama>(),
            new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient });

        if (llama == null) return;

        llama.SetActorLocation(new FVector { X = x, Y = y, Z = z });
        llama.SetActorRotation(new FRotator { Yaw = (float) (state.Rng.NextDouble() * 360.0 - 180.0) });
        llama.SetRole(ENetRole.ROLE_Authority);

        llama.SetReplicates(true);

        state.Llamas.Add(llama);

        Console.WriteLine($"FortSupplyLlamas: llama {state.Llamas.Count} at ({x:F0}, {y:F0}, {z:F0})");
    }
}
