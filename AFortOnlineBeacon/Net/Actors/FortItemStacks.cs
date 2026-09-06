using AFortOnlineBeacon.Core.Objects;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     How many of an item fit in one inventory slot, and the one place that puts items INTO an
///     inventory according to that.
///
///     THE BUG THIS EXISTS FOR: every pickup added a new row, so two boxes of light ammo sat in two
///     slots showing 30 and 30 instead of one slot showing 60. Resources already merged - harvesting
///     had grown its own stacking, with 999 hardcoded - and nothing else did, which is the usual
///     shape of a rule that lives in one call site instead of one function.
///
///     THE SIZES ARE READ, NOT CHOSEN. Each item definition carries its own `MaxStackSize`, and the
///     numbers differ enough that no convention would survive: bullets and resources 999, rockets 12,
///     bandages 15, frag grenades 10, sticky grenades 6, a mounted turret 50. Tools/StackSizes bakes
///     them out of the paks (see FortItemStacks.Generated.cs).
///
///     AN UNKNOWN ITEM DOES NOT STACK. That is the safe default in both directions: a weapon merging
///     into another weapon would destroy one of them, while failing to merge two stackables is the
///     bug this fixes and is merely untidy. So absence means one per slot, never "unlimited".
/// </summary>
internal static partial class FortItemStacks {
    /// <summary>
    ///     How many of this item fit in one slot, and whether it may ever occupy more than one.
    ///     (1, false) unless the asset says otherwise.
    /// </summary>
    public static (int Max, bool SingleRow) StackRule(UObject definition) =>
        UAssetRegistry.PathOf(definition) is { } path && Sizes.TryGetValue(path, out var rule)
            ? rule
            : (1, false);

    /// <summary>How many of this item fit in one slot.</summary>
    public static int MaxStack(UObject definition) => StackRule(definition).Max;

    /// <summary>
    ///     Puts <paramref name="count" /> of an item into an inventory, filling existing stacks before
    ///     opening new ones, and returns whatever did not fit.
    ///
    ///     FILLS EXISTING STACKS FIRST, and in the order they already sit in - which is what makes
    ///     picking up two boxes of ammo produce one slot rather than two. A remainder that will not
    ///     fit in any of them opens a new stack, and a remainder beyond that is handed back rather
    ///     than silently dropped: the caller knows whether the right answer is to leave it in the
    ///     world (a pickup), to toss it on the ground (a harvest that overflows) or to say so.
    ///
    ///     `template` supplies everything about the item other than its count - durability, level and
    ///     loaded ammo travel with a weapon and mean nothing to a stack of bullets.
    /// </summary>
    public static int Give(AFortInventory inventory, FFortItemEntry template, int count) {
        if (count <= 0 || template.ItemDefinition is not { } definition) return 0;

        var (max, singleRow) = StackRule(definition);
        var remaining = count;
        var alreadyHeld = false;

        foreach (var existing in inventory.Inventory.Items) {
            if (existing.ItemDefinition != definition) continue;
            alreadyHeld = true;

            if (remaining <= 0 || max <= 1) continue;

            var room = max - existing.Count;
            if (room <= 0) continue;

            var moved = System.Math.Min(remaining, room);
            existing.Count += moved;
            remaining -= moved;
            inventory.Inventory.MarkItemDirty(existing);
        }

        // AMMO AND RESOURCES NEVER OPEN A SECOND ROW. Once wood is held, more wood either fits in
        // that stack or does not come in at all - harvesting past 999 drops the excess on the ground
        // (Round 47), which is what the game does and what a first attempt at this broke by adding a
        // second wood stack instead. The flag comes from the item's CLASS, not from its cap:
        // FortAmmoItemDefinition and FortResourceItemDefinition are the single-row ones, while a
        // grenade is a FortWeaponRangedItemDefinition like any other weapon and may sit in two slots.
        if (remaining > 0 && singleRow && alreadyHeld) return remaining;

        // Otherwise AT MOST ONE NEW ROW, and the rest is handed back for the caller to put somewhere.
        if (remaining > 0) {
            var granted = max > 0 ? System.Math.Min(remaining, max) : remaining;

            inventory.Inventory.Add(new FFortItemEntry {
                ItemDefinition = definition,
                Count = granted,
                Durability = template.Durability,
                Level = template.Level,
                LoadedAmmo = template.LoadedAmmo
            });

            remaining -= granted;
        }

        return remaining;
    }
}
