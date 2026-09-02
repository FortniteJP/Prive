namespace AFortOnlineBeacon.Net.Actors;

/// <summary>One weighted row of a loot TIER group - what a container rolls first. See gen_loot.py.</summary>
internal readonly record struct FLootTierRow(
    float Weight,
    string LootPackage,
    float NumLootPackageDrops,
    int[] CategoryWeights,
    int[] CategoryMin,
    int[] CategoryMax);

/// <summary>
///     One weighted row of a loot PACKAGE. Exactly one of <paramref name="Call"/> and
///     <paramref name="ItemPath"/> is set: a call recurses into another package group, an item path is
///     a leaf.
/// </summary>
internal readonly record struct FLootPackageRow(
    float Weight,
    int Count,
    int Category,
    string? Call,
    string? ItemPath);

/// <summary>
///     Rolling Fortnite's real loot tables (see FortLootTables.Generated.cs for the data and
///     gen_loot.py for where it comes from).
///
///     WHAT THIS REPRODUCES, and what it does not. The two-stage weighted structure IS the real one:
///     tier group -> weighted tier row -> its loot package -> weighted rows per CATEGORY -> either an
///     item or a call into another package. Categories are the part that matters most for how loot
///     FEELS, because they are why a chest reliably gives a weapon and ammo and a consumable instead
///     of four shotguns, and they are honoured here.
///
///     Deliberately NOT reproduced: quota levels (ELootQuotaLevel), streak breakers, world-level
///     gating and named weight multipliers. Those shape loot across a whole MATCH - they stop the same
///     rare drop appearing twice in a row, and so on - and none of them can be judged correct from a
///     single container. They are listed here rather than silently dropped so the gap is visible.
/// </summary>
internal static partial class FortLootTables {
    /// <summary>The tier group a chest rolls. Seen on real containers as their `SearchLootTierGroup`.</summary>
    public const string TreasureGroup = "Loot_AthenaTreasure";

    /// <summary>Ammo boxes.</summary>
    public const string AmmoLargeGroup = "Loot_AthenaAmmoLarge";

    /// <summary>Floor loot.</summary>
    public const string FloorLootGroup = "Loot_AthenaFloorLoot";

    /// <summary>One item the roll produced.</summary>
    internal readonly record struct FLootDrop(string ItemPath, int Count);

    /// <summary>
    ///     Rolls one container's worth of loot.
    ///
    ///     Recursion is depth-limited rather than trusted: the tables are data, a cycle in them would
    ///     hang the server, and a container is not worth that risk.
    /// </summary>
    public static List<FLootDrop> Roll(string tierGroup, Random rng) {
        var drops = new List<FLootDrop>();

        if (!TierGroups.TryGetValue(tierGroup, out var tierRows) || tierRows.Length == 0) {
            Console.WriteLine($"FortLootTables: no tier group '{tierGroup}' - nothing to drop");
            return drops;
        }

        var tier = PickWeighted(tierRows, row => row.Weight, rng);
        if (!Packages.TryGetValue(tier.LootPackage, out var packageRows) || packageRows.Length == 0) {
            Console.WriteLine($"FortLootTables: tier row names package '{tier.LootPackage}' which is not in the table");
            return drops;
        }

        // Real Fortnite spends NumLootPackageDrops picks across the categories, weighted by the tier
        // row's own per-category weights, while respecting each category's minimum. Doing the minimums
        // first and then spending what is left on weighted picks gives the same shape.
        var picks = Math.Max(1, (int) MathF.Round(tier.NumLootPackageDrops));
        var categories = packageRows.Select(r => r.Category).Distinct().OrderBy(c => c).ToArray();
        var taken = new Dictionary<int, int>();

        foreach (var category in categories) {
            var min = category < tier.CategoryMin.Length ? tier.CategoryMin[category] : 0;
            for (var i = 0; i < min && picks > 0; i++) {
                if (!DrawFromCategory(packageRows, category, rng, drops)) break;
                taken[category] = taken.GetValueOrDefault(category) + 1;
                picks--;
            }
        }

        while (picks-- > 0) {
            var weighted = categories
                .Where(c => {
                    var max = c < tier.CategoryMax.Length ? tier.CategoryMax[c] : -1;
                    return max < 0 || taken.GetValueOrDefault(c) < max;
                })
                .ToArray();
            if (weighted.Length == 0) break;

            var category = PickWeighted(weighted,
                c => c < tier.CategoryWeights.Length ? tier.CategoryWeights[c] : 0, rng);
            if (!DrawFromCategory(packageRows, category, rng, drops)) break;
            taken[category] = taken.GetValueOrDefault(category) + 1;
        }

        return drops;
    }

    private static bool DrawFromCategory(FLootPackageRow[] rows, int category, Random rng, List<FLootDrop> into) {
        var candidates = rows.Where(r => r.Category == category).ToArray();
        if (candidates.Length == 0) return false;

        var row = PickWeighted(candidates, r => r.Weight, rng);
        return Resolve(row, rng, into, depth: 0);
    }

    private static bool Resolve(FLootPackageRow row, Random rng, List<FLootDrop> into, int depth) {
        if (depth > 8) return false; // see Roll's note on cycles

        if (row.ItemPath is { Length: > 0 } item) {
            into.Add(new FLootDrop(item, Math.Max(1, row.Count)));
            return true;
        }

        if (row.Call is not { Length: > 0 } call) return false;
        if (!Packages.TryGetValue(call, out var called) || called.Length == 0) return false;

        return Resolve(PickWeighted(called, r => r.Weight, rng), rng, into, depth + 1);
    }

    /// <summary>Standard weighted pick. A zero-weight table falls back to uniform rather than failing.</summary>
    private static T PickWeighted<T>(IReadOnlyList<T> items, Func<T, float> weight, Random rng) {
        var total = 0f;
        foreach (var item in items) total += MathF.Max(0f, weight(item));

        if (total <= 0f) return items[rng.Next(items.Count)];

        var roll = (float) rng.NextDouble() * total;
        foreach (var item in items) {
            roll -= MathF.Max(0f, weight(item));
            if (roll <= 0f) return item;
        }

        return items[^1];
    }
}
