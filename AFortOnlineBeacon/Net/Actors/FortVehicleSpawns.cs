using AFortOnlineBeacon.Core.Math;
using AFortOnlineBeacon.Core.Objects;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     The map's drivable vehicles - the runtime half of FortVehicleSpawns.Generated.cs.
///
///     TWO SEPARATE FILTERS, and they answer different questions. WHICH spawners are live is decided
///     once, up front, from the game data's per-vehicle spawn percentages - on 10.40 that is 50% of
///     the Jackals and none of anything else, because Season 10 vaulted the rest (see
///     ChooseActiveSpawners and the generated file). WHEN a live one actually produces its vehicle is
///     decided by proximity, below.
///
///     SPAWNED LAZILY, NEAR PLAYERS, AND ONCE, for exactly the reasons FortFloorLoot spells out and
///     with the same shape of loop: this server has no distance relevancy, so spawning even the ~51
///     live Jackals at match start would open 51 actor channels on every connection during login,
///     on top of everything else login already does. The real server spawns them all and lets its
///     ReplicationGraph decide who sees what.
///
///     Vehicles get a LARGER radius than floor loot (25000 vs 12000 by default) because they are
///     the thing you go and look for from a distance - a car that pops into existence 120m away is
///     a car you were already driving towards. They also get a smaller per-sweep budget, since one
///     vehicle is a heavier actor than one pickup.
///
///     The height guard is the same as floor loot's and exists for the same reason: a skydiver
///     crosses the whole map at Z=15000, and without it every vehicle they fly over would spawn at
///     once during the drop.
/// </summary>
internal static partial class FortVehicleSpawns {
    private static bool Enabled => Environment.GetEnvironmentVariable("VEHICLES_ENABLED") is not "0";

    private static float EnvFloat(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    /// <summary>How many spawn points there are - one per X,Y,Z,Yaw quad.</summary>
    public static int Count => Spawns.Length / 4;

    /// <summary>
    ///     One flag per spawn point, set once it has been used so a vehicle is never spawned twice.
    ///
    ///     Starts TRUE for every spawner the game data does not want used, which is how the spawn
    ///     percentages are enforced - see ChooseActiveSpawners.
    ///
    ///     Built on first use rather than in a field initializer - Spawns lives in this partial
    ///     class's OTHER file, and a partial class's static field initializers run in whatever order
    ///     the compiler fed it the files. FortFloorLoot lost a whole world tick to exactly that.
    /// </summary>
    private static bool[]? _spawned;

    private static bool[] Spawned => _spawned ??= ChooseActiveSpawners();

    private static readonly Random Rng = new(
        int.TryParse(Environment.GetEnvironmentVariable("VEHICLE_SEED"), out var seed) ? seed : 20191001);

    /// <summary>
    ///     Decide, once, which of the 325 placed spawners actually produce a vehicle.
    ///
    ///     THIS IS WHAT THE PERCENTAGES ARE FOR, and skipping it would put a shopping cart and a
    ///     biplane on a Season 10 map - both vaulted, both still placed, both at 0% in the game data.
    ///     The real game rolls a figure between Min and MaxSpawnPercent and activates that share of
    ///     each type's spawners; Min == Max in every row on this build, so the roll collapses to the
    ///     one number and only the CHOICE of spawners is random.
    ///
    ///     Random but SEEDED, for the same reason floor loot is: a fixed order makes a test run
    ///     reproducible, and there is nothing to gain from surprising whoever is debugging it.
    ///     VEHICLE_SEED changes it; VEHICLE_PERCENT_OVERRIDE ignores the table entirely and gives
    ///     every type the same share, which is the switch to flip to see a vaulted vehicle.
    /// </summary>
    private static bool[] ChooseActiveSpawners() {
        // Already-used flags: true here means "never spawn this one".
        var used = new bool[Count];
        var overridePercent = float.TryParse(Environment.GetEnvironmentVariable("VEHICLE_PERCENT_OVERRIDE"),
            out var forced) ? forced : (float?) null;

        for (byte type = 0; type < VehicleClasses.Length; type++) {
            var points = new List<int>();
            for (var i = 0; i < Count; i++)
                if (Types[i] == type) points.Add(i);

            var percent = Math.Clamp(overridePercent ?? SpawnPercents[type], 0f, 100f);
            var keep = (int) MathF.Round(points.Count * percent / 100f);

            // Fisher-Yates over the type's own spawners, then keep the first `keep`.
            for (var i = points.Count - 1; i > 0; i--) {
                var j = Rng.Next(i + 1);
                (points[i], points[j]) = (points[j], points[i]);
            }

            for (var i = keep; i < points.Count; i++) used[points[i]] = true;

            if (points.Count > 0)
                Console.WriteLine($"FortVehicleSpawns: {VehicleClasses[type].Split('.')[^1]} " +
                                  $"{keep}/{points.Count} spawners active ({percent:F0}%)");
        }

        return used;
    }

    /// <summary>
    ///     TEMPORARY, AND ON BY DEFAULT ON PURPOSE - the same switch FortSupplyLlamas reads, so one
    ///     variable moves both. Puts ONE OF EVERY vehicle type on the warmup island instead of
    ///     spawning the real map placement near players.
    ///
    ///     Two reasons it is worth more than the real placement right now. The nearest Jackal spawner
    ///     to the island is about 88,000 units away - four times the spawn radius - so on the warmup
    ///     island the real mode produces NOTHING to look at. And every type is spawned, not just the
    ///     Jackal, which is the only way to find out whether the other six class paths resolve on the
    ///     client at all: they are at 0% in the game data (Season 10 vaulted them), so the real mode
    ///     can never exercise them.
    ///
    ///     SPAWN_AT_WARMUP=0 restores the real, percentage-driven, proximity-spawned placement.
    /// </summary>
    private static bool AtWarmupIsland => FortSupplyLlamas.AtWarmupIsland;

    private static bool _placedAtWarmup;

    private static float _nextSweep;

    /// <summary>Vehicles do not need to appear the instant a player is in range.</summary>
    private const float SweepIntervalSeconds = 1f;

    public static void Tick(UWorld world, float now) {
        if (!Enabled || now < _nextSweep) return;
        _nextSweep = now + SweepIntervalSeconds;

        if (AtWarmupIsland) {
            PlaceOnWarmupIsland(world);
            return;
        }

        if (world.NetDriver is not { } netDriver) return;

        var radius = EnvFloat("VEHICLE_RADIUS", 25000f);
        var radiusSquared = radius * radius;
        var maxHeight = EnvFloat("VEHICLE_MAX_HEIGHT", 2500f);
        var budget = EnvInt("VEHICLES_PER_SWEEP", 3);
        var spawned = Spawned;

        foreach (var connection in netDriver.ClientConnections) {
            if (connection.PlayerController is not { } pc) continue;
            // Nothing to be near while still on the battle bus.
            if (pc.PlayerState is { bInAircraft: true }) continue;
            if (pc.Pawn is not { } pawn) continue;

            var origin = pawn.GetActorLocation();

            for (var i = 0; i < spawned.Length && budget > 0; i++) {
                if (spawned[i]) continue;

                var x = Spawns[i * 4];
                var y = Spawns[i * 4 + 1];
                var z = Spawns[i * 4 + 2];

                var dx = x - origin.X;
                var dy = y - origin.Y;
                if (dx * dx + dy * dy > radiusSquared) continue;
                if (z - origin.Z < -maxHeight) continue;

                spawned[i] = true;
                budget--;

                Spawn(world, i, x, y, z, Spawns[i * 4 + 3]);
            }
        }
    }

    /// <summary>
    ///     The SPAWN_AT_WARMUP placement: one of each of the seven vehicle types, in a line beside
    ///     the island's first player start.
    ///
    ///     A LINE rather than a ring, and on the opposite side from the llamas' ring, so the two
    ///     debug placements do not spawn inside each other. All seven face the same way, which makes
    ///     a wrong YAxisForward obvious at a glance: five of them get +90 and two do not, so if that
    ///     flag is being applied backwards the line will be visibly two groups pointing across each
    ///     other rather than seven vehicles pointing the same way.
    /// </summary>
    private static void PlaceOnWarmupIsland(UWorld world) {
        if (_placedAtWarmup) return;
        _placedAtWarmup = true;

        var anchor = FortWarmupStarts.Anchor;
        var spacing = EnvFloat("WARMUP_VEHICLE_SPACING", 700f);
        var offset = EnvFloat("WARMUP_VEHICLE_OFFSET", 2000f);

        Console.WriteLine($"FortVehicleSpawns: SPAWN_AT_WARMUP - placing one of each of the " +
                          $"{VehicleClasses.Length} vehicle types beside the warmup island start " +
                          $"({anchor.X:F0}, {anchor.Y:F0}, {anchor.Z:F0}), ignoring the spawn percentages. " +
                          "Set SPAWN_AT_WARMUP=0 for the real map placement.");

        for (var type = 0; type < VehicleClasses.Length; type++) {
            SpawnClass(world, VehicleClasses[type],
                anchor.X + offset,
                anchor.Y + (type - (VehicleClasses.Length - 1) / 2f) * spacing,
                anchor.Z,
                yaw: 0f);
        }
    }

    /// <summary>
    ///     One vehicle, at the spawner's world transform.
    ///
    ///     Lifted slightly, for the same reason floor loot is: the spawner's Z is the ground it
    ///     stands on, and a vehicle spawned exactly at ground level starts interpenetrating it. The
    ///     client's own physics settles the rest.
    /// </summary>
    private static void Spawn(UWorld world, int index, float x, float y, float z, float yaw) =>
        SpawnClass(world, VehicleClasses[Types[index]], x, y, z, yaw);

    private static void SpawnClass(UWorld world, string classPath, float x, float y, float z, float yaw) {
        var vehicle = world.SpawnActor<AFortAthenaVehicle>(
            GUClassArray.StaticClassForPath<AFortAthenaVehicle>(classPath),
            new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient });

        if (vehicle == null) return;

        vehicle.SetActorLocation(new FVector { X = x, Y = y, Z = z + EnvFloat("VEHICLE_Z_OFFSET", 50f) });
        vehicle.SetActorRotation(new FRotator { Yaw = yaw });
        vehicle.SetRole(ENetRole.ROLE_Authority);
        vehicle.SetReplicates(true);

        Console.WriteLine($"FortVehicleSpawns: spawned {classPath.Split('.')[^1]} at " +
                          $"({x:F0}, {y:F0}, {z:F0}) yaw {yaw:F0}");
    }
}
