using AFortOnlineBeacon.Core.Math;
using AFortOnlineBeacon.Core.Objects;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     One pickup of every THROWABLE, laid out on the warmup island - a test fixture, in the same
///     spirit as SPAWN_AT_WARMUP for llamas and vehicles.
///
///     WHY IT EXISTS. Every thrown item now does something different when it goes off - a shockwave
///     throws you, a boogie bomb makes you dance, a clinger sticks and waits, a stink bomb leaves a
///     cloud - and there is no way to try any of that without first finding one in a chest. Waiting
///     on floor loot to roll a boogie bomb is not a test, it is a lottery, and a feature that can
///     only be exercised by luck is one that stays untested.
///
///     WHAT COUNTS AS A THROWABLE, and it is asked of the data rather than listed here: an item is
///     throwable when its own fire ability is a **WithTrajectory** (or Throw_) one. That is the
///     grenade-throw ability family, and it separates the cases a name never could - Athena_Bandage
///     and Athena_Medkit both carry an inherited ProjectileTemplate they never use, while
///     Athena_SneakySnowman is a throwable whose ability is called Throw_SneakySnowman. The Playset
///     grenades are excluded by the same test failing them plus their own name: they are Creative
///     building props, 189 of them, and they would bury the island.
///
///     ON BY DEFAULT because it was asked for; WARMUP_THROWABLES=0 turns it off, and the log says
///     which mode is running so it cannot be left on unnoticed.
/// </summary>
internal static class FortWarmupThrowables {
    private static bool Enabled => Environment.GetEnvironmentVariable("WARMUP_THROWABLES") is not "0";

    /// <summary>How far out the ring of pickups sits. Far enough not to be inside the spawn crowd.</summary>
    private static float Radius =>
        float.TryParse(Environment.GetEnvironmentVariable("WARMUP_THROWABLE_RADIUS"), out var radius)
            ? radius
            : 700f;

    /// <summary>
    ///     Off the ground, for the same reason floor loot lifts its own: a pickup sunk into the floor
    ///     is one the client's interaction trace cannot see.
    /// </summary>
    private static float ZOffset =>
        float.TryParse(Environment.GetEnvironmentVariable("WARMUP_THROWABLE_Z"), out var z) ? z : 40f;

    private static bool _placed;

    public static void Tick(UWorld world, float now) {
        if (_placed || !Enabled) return;
        _placed = true;

        var throwables = FortConsumables.Names
            .Where(IsThrowable)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (throwables.Count == 0) {
            Console.WriteLine("FortWarmupThrowables: no throwable found in FortConsumables - nothing placed.");
            return;
        }

        var anchor = FortWarmupStarts.Anchor;

        Console.WriteLine($"FortWarmupThrowables: dropping one stack of each of the {throwables.Count} throwable(s) " +
                          $"in a {Radius:F0}u ring around the warmup start " +
                          $"({anchor.X:F0}, {anchor.Y:F0}, {anchor.Z:F0}). WARMUP_THROWABLES=0 turns this off.");

        var placed = 0;
        for (var i = 0; i < throwables.Count; i++) {
            var angle = MathF.Tau * i / throwables.Count;

            if (Spawn(world, throwables[i],
                      anchor.X + MathF.Cos(angle) * Radius,
                      anchor.Y + MathF.Sin(angle) * Radius,
                      anchor.Z + ZOffset)) {
                placed++;
            }
        }

        Console.WriteLine($"FortWarmupThrowables: {placed} of {throwables.Count} placed.");
    }

    /// <summary>
    ///     Whether this consumable is THROWN, asked of its fire ability - see the class comment for
    ///     why the ability and not the name or the projectile template.
    /// </summary>
    private static bool IsThrowable(string itemName) {
        if (itemName.Contains("Playset", StringComparison.OrdinalIgnoreCase)) return false;
        if (FortConsumables.ProjectileClassFor(itemName) == null) return false;
        if (FortConsumables.AbilityFor(itemName) is not { } ability) return false;

        return ability.Contains("WithTrajectory", StringComparison.OrdinalIgnoreCase) ||
               ability.Contains("Throw", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     One pickup, at rest. The same shape as FortFloorLoot.Spawn and deliberately not the
    ///     player-toss path, which needs a pawn to toss from.
    /// </summary>
    private static bool Spawn(UWorld world, string itemName, float x, float y, float z) {
        if (FortConsumables.ItemPathFor(itemName) is not { } itemPath) {
            Console.WriteLine($"FortWarmupThrowables: '{itemName}' has no item path - skipped.");
            return false;
        }

        var pickup = world.SpawnActor<AFortPickup>(GUClassArray.StaticClass<AFortPickup>(),
            new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient });

        if (pickup == null) return false;

        var rest = new FVector { X = x, Y = y, Z = z };

        pickup.SetActorLocation(rest);
        pickup.RestLocation = rest;
        pickup.SetRole(ENetRole.ROLE_Authority);

        // A FULL STACK, from the item's own MaxStack rather than a number picked here - testing a
        // grenade means throwing it more than once. Set before SetReplicates for the reason
        // SpawnDroppedPickup spells out: every getter in PickupProps reads through
        // PrimaryPickupItemEntry, and a replication pass that caught it null would throw from inside
        // the world tick.
        var definition = UAssetRegistry.GetOrCreate(itemPath);
        var count = MathF.Max(1, FortItemStacks.MaxStack(definition));

        pickup.PrimaryPickupItemEntry = FortWeaponActorClasses.WorldLootEntry(itemPath, (int) count);
        pickup.SetReplicates(true);

        Console.WriteLine($"FortWarmupThrowables: {itemName} x{count:F0} at ({x:F0}, {y:F0}, {z:F0})");
        return true;
    }
}
