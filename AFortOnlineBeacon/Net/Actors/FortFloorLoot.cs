using AFortOnlineBeacon.Core.Math;
using AFortOnlineBeacon.Core.Objects;
using AFortOnlineBeacon.Net.Rpc;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Floor loot - the items lying around the map, rolled from Fortnite's own
///     `Loot_AthenaFloorLoot` tier group at the 932 spawner positions the map actually places.
///
///     SPAWNED LAZILY, NEAR PLAYERS, AND ONCE. Real Fortnite spawns all of it up front and leans on
///     network relevancy to keep it off the wire. This server has no distance culling, so doing the
///     same would open something like two thousand actor channels at match start and drown everything
///     else. Rolling each point only when a player comes near it, once, gives the same result from the
///     player's point of view at a fraction of the cost - and it is bounded per tick as well, so a
///     player landing in a POI does not cause a hundred channel opens in a single frame.
///
///     The consequence, stated plainly: loot is decided when a player first approaches rather than at
///     match start, so two players converging on the same building see the same items (each point is
///     rolled once and remembered), but a point nobody ever visits is never rolled at all.
/// </summary>
internal static partial class FortFloorLoot {

    /// <summary>This world's share of FortFloorLoot's state - see FWorldSubsystem.</summary>
    private sealed class FFloorLootState : FWorldSubsystem {
        public bool Enabled => Options.Get("FLOOR_LOOT_ENABLED") is not "0";

        /// <summary>
        ///     One entry per spawn point; set once it has been rolled so it never repeats.
        ///
        ///     Built on first use, NOT in a field initializer, and that is not a style choice. SpawnPoints
        ///     lives in this partial class's OTHER file (FortFloorLoot.Generated.cs), and a partial class's
        ///     static field initializers run in the order the compiler happens to feed it the files - so
        ///     `new bool[SpawnPoints.Length / 3]` as an initializer read SpawnPoints while it was still
        ///     null and killed the very first world tick with a TypeInitializationException. By the time
        ///     Tick runs the static constructor has finished, whatever order it ran in.
        /// </summary>
        public bool[]? _spawned;

        public bool[] Spawned => _spawned ??= new bool[SpawnPoints.Length / 3];

        public float _nextSweep;
        public Random Rng = default!;
        protected internal override void Initialize() {
            Rng = new(
                int.TryParse(Options.Get("LOOT_SEED"), out var seed) ? seed : 20191001);
        }
    }

    private static FFloorLootState StateOf(UWorld world) => world.GetSubsystem<FFloorLootState>();

    /// <summary>How often the sweep runs. Loot does not need to appear the instant a player is in range.</summary>
    private const float SweepIntervalSeconds = 1f;

    public static void Tick(UWorld world, float now) {
        var state = StateOf(world);

        if (!state.Enabled || now < state._nextSweep) return;
        state._nextSweep = now + SweepIntervalSeconds;

        if (world.NetDriver is not { } netDriver) return;

        var radius = world.Options.Float("FLOOR_LOOT_RADIUS", 12000f);
        var radiusSquared = radius * radius;
        var budget = world.Options.Int("FLOOR_LOOT_PER_SWEEP", 6);
        var spawned = state.Spawned;

        foreach (var connection in netDriver.ClientConnections) {
            if (connection.PlayerController is not { } pc) continue;
            // Nothing to be near while still on the battle bus.
            if (pc.PlayerState is { bInAircraft: true }) continue;
            if (pc.Pawn is not { } pawn) continue;

            var origin = pawn.GetActorLocation();

            for (var i = 0; i < spawned.Length && budget > 0; i++) {
                if (spawned[i]) continue;

                var x = SpawnPoints[i * 3];
                var y = SpawnPoints[i * 3 + 1];
                var z = SpawnPoints[i * 3 + 2];

                var dx = x - origin.X;
                var dy = y - origin.Y;
                if (dx * dx + dy * dy > radiusSquared) continue;

                // AND NOT FROM THE SKY. The test above is X/Y only, which was fine while a player
                // could only ever walk around - but a skydiver crosses the whole map at Z=15000, so
                // every spawner they FLY OVER came into range at once. Jumping out of the battle bus
                // opened 55 pickup channels in a single burst on top of everything else a teleport
                // to the far side of the map already makes relevant.
                //
                // Height rather than 3D distance on purpose: 3D would also stop loot appearing
                // across a valley, which is a legitimate case. This only refuses to deal loot out to
                // someone who is nowhere near the ground it sits on, and they get it on the next
                // sweep once they land.
                if (z - origin.Z < -world.Options.Float("FLOOR_LOOT_MAX_HEIGHT", 2500f)) continue;

                spawned[i] = true;
                budget--;

                var drops = FortLootTables.Roll(FortLootTables.FloorLootGroup, state.Rng);
                foreach (var drop in drops) {
                    Spawn(world, drop, x, y, z);
                }
            }
        }
    }

    /// <summary>
    ///     One pickup, at rest where the map put the spawner.
    ///
    ///     Deliberately not NativeRpcHandlers.SpawnDroppedPickup: that one tosses an item in front of a
    ///     PLAYER and needs a pawn to toss from. Floor loot has a fixed world position and no thrower,
    ///     so it sets RestLocation directly - which is also what makes it sit still rather than
    ///     replicate a toss the client would animate.
    /// </summary>
    private static void Spawn(UWorld world, FortLootTables.FLootDrop drop, float x, float y, float z) {
        var pickup = world.SpawnActor<AFortPickup>(GUClassArray.StaticClass<AFortPickup>(), new FActorSpawnParameters {
            ObjectFlags = EObjectFlags.RF_Transient
        });
        if (pickup == null) return;

        // Slightly above the spawner: the spawner's own Z is the floor it sits on, and a pickup buried
        // in the floor is one the client's interaction query cannot see - the same reason
        // SpawnDroppedPickup lifts its toss.
        var rest = new FVector { X = x, Y = y, Z = z + world.Options.Float("FLOOR_LOOT_Z_OFFSET", 40f) };

        pickup.SetActorLocation(rest);
        pickup.RestLocation = rest;
        pickup.SetRole(ENetRole.ROLE_Authority);

        // Before SetReplicates, for the reason SpawnDroppedPickup spells out: every getter in
        // PickupProps reads through PrimaryPickupItemEntry, and a replication pass that caught it
        // null would throw from inside the world tick.
        // Loaded, same as a chest's contents - see FortWeaponActorClasses.WorldLootEntry. Floor
        // loot had the same empty-magazine problem and would have kept it if only chests were fixed.
        pickup.PrimaryPickupItemEntry = FortWeaponActorClasses.WorldLootEntry(drop.ItemPath, drop.Count);

        pickup.SetReplicates(true);
    }
}
