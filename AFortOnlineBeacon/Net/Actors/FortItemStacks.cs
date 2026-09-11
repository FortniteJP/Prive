using AFortOnlineBeacon.Runtime;
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
        Rule(definition) is { } rule ? (StackCap(rule.Max), rule.SingleRow) : (1, false);

    /// <summary>
    ///     A baked stack size, with 0 - "the asset does not say" - resolved.
    ///
    ///     ZERO IS NOT "DOES NOT STACK", and reading it that way is what made traps take one slot
    ///     each. The four FortContextTrapItemDefinition items (bouncer, campfire, poison dart, the
    ///     generic context trap) override no MaxStackSize at all, so the value they inherit lives in
    ///     a native CDO that is not in the paks and cannot be read from them. Their siblings, the
    ///     three FortTrapItemDefinition floor traps, DO override it: 20, 20 and 50.
    ///
    ///     AthenaGameData was searched for it and does not have it. That table DOES carry runtime
    ///     stack overrides - `Default.MaxStack.Resources.{Wood,Stone,Metal} = 999` (which is exactly
    ///     what the resource assets say, so nothing is being overridden there) and a
    ///     `GroundGame.MaxStack.*` set for the reduced-material LTM - but there is no
    ///     `Default.MaxStack.Traps` of any kind. It does confirm traps stack
    ///     (`Default.ShouldCampfireStack = 1`, `Default.TrapCampFire.ShouldStack = 1`).
    ///
    ///     999 IS THE REAL VALUE, read out of the running client rather than guessed. It started as
    ///     a placeholder and was confirmed in one line at the client console:
    ///
    ///         FortContextTrapItemDefinition /Game/Athena/Items/Traps/TID_Context_BouncePad_Athena
    ///             .TID_Context_BouncePad_Athena.MaxStackSize = 999
    ///
    ///     That is the cheap instrument for any INHERITED default: the pak carries only what an
    ///     asset overrides, while the client has the resolved value in memory and will print it.
    ///     No dump, no offsets. See [[re-and-capture-techniques]].
    ///
    ///     TRAP_STACK_SIZE still overrides it.
    /// </summary>
    private static int StackCap(int baked) => baked > 0 ? baked : UnspecifiedStackSize;

    /// <summary>See StackCap. NOT read from any asset.</summary>
    private static readonly int UnspecifiedStackSize =
        int.TryParse(FBeaconProcess.Options.Get("TRAP_STACK_SIZE"), out var cap) && cap > 0
            ? cap
            : 999;

    /// <summary>The full baked row for an item, or null when the table has never heard of it.</summary>
    private static (int Max, bool SingleRow, bool Carried)? Rule(UObject definition) =>
        UAssetRegistry.PathOf(definition) is { } path && Sizes.TryGetValue(path, out var rule) ? rule : null;

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
    ///
    ///     DRY RUN asks the question without answering it: it returns the same leftover this would
    ///     hand back, having changed nothing. That exists for the pickup flight, where "does any of
    ///     this fit?" has to be decided BEFORE the item starts flying (a full inventory leaves the
    ///     item on the ground and plays no animation at all) while the item itself must not land in
    ///     the inventory until the animation ends. Two callers asking two halves of one question is
    ///     exactly how the slot rule got answered by a proxy last time, so both halves are the same
    ///     walk over the same data - only the three mutations are skipped.
    /// </summary>
    public static int Give(AFortInventory inventory, FFortItemEntry template, int count,
                           bool dryRun = false) {
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
            remaining -= moved;

            if (dryRun) continue;

            existing.Count += moved;
            inventory.Inventory.MarkItemDirty(existing);
        }

        // AMMO AND RESOURCES NEVER OPEN A SECOND ROW. Once wood is held, more wood either fits in
        // that stack or does not come in at all - harvesting past 999 drops the excess on the ground
        // (Round 47), which is what the game does and what a first attempt at this broke by adding a
        // second wood stack instead. The flag comes from the item's CLASS, not from its cap:
        // FortAmmoItemDefinition and FortResourceItemDefinition are the single-row ones, while a
        // grenade is a FortWeaponRangedItemDefinition like any other weapon and may sit in two slots.
        if (remaining > 0 && singleRow && alreadyHeld) return remaining;

        // AND THE QUICKBAR HAS A SIZE, which nothing here used to check. Stacking was right and the
        // SLOT COUNT was simply unlimited, so a player with every slot full kept picking things up:
        // the items went into the inventory, the client drew the five it had room for, and dropping
        // one made a hidden item appear in its place - which is exactly what was reported.
        //
        // WHICH ITEMS OCCUPY A SLOT is already decided by data rather than by a list here.
        // StackRule's SingleRow flag is true for exactly the six ammo types and three resources -
        // the items Fortnite keeps in their own storage and never shows in the quickbar - and false
        // for every weapon, consumable and grenade, which are precisely the ones that do take a
        // slot. The pickaxe is excluded on top of that: it sits in quickbar slot 0 and is not one of
        // the five a player fills.
        //
        // ASKED OF THE INCOMING ITEM, not of its stack rule. This gate used to read `!singleRow`,
        // reusing the "never opens a second stack" flag as a stand-in for "takes a carried slot" -
        // the same substitution that made the game unplayable the first time, made again one line
        // further down. A TRAP is neither single-row nor carried, so a full weapon bar refused every
        // trap in the world: `Athena_PlayerController_C has no room for TID_Floor_Player_Campfire_Athena`,
        // logged 40-odd times in one live session while the player stood on it. OccupiesQuickbarSlot
        // is the question actually being asked, and it is a baked column.
        //
        // AND NOT `!alreadyHeld`, which used to guard it as well. The intent was "topping up
        // something you already have needs no new slot" - true, but the loop above has already done
        // all the topping up there is to do, so reaching this line at all means a NEW ROW is about
        // to be opened. For a WEAPON, `max <= 1` skips that loop entirely, so holding one rifle
        // exempted every further copy of it: a second identical gun went straight into a sixth slot,
        // reported as "the same weapon with the same rarity can be held twice or more". Two
        // identical rifles in two slots is perfectly legal in Fortnite - what is not legal is a
        // SIXTH slot, and that is the only thing this line is here to decide.
        if (remaining > 0 && OccupiesQuickbarSlot(definition) && OccupiedSlots(inventory) >= SlotLimitFor(inventory)) {
            return remaining;
        }

        // Otherwise AT MOST ONE NEW ROW, and the rest is handed back for the caller to put somewhere.
        if (remaining > 0) {
            var granted = max > 0 ? System.Math.Min(remaining, max) : remaining;

            if (!dryRun) {
                inventory.Inventory.Add(new FFortItemEntry {
                    ItemDefinition = definition,
                    Count = granted,
                    Durability = template.Durability,
                    Level = template.Level,
                    LoadedAmmo = template.LoadedAmmo
                });
            }

            remaining -= granted;
        }

        return remaining;
    }

    /// <summary>
    ///     What a full inventory gives up to make room for <paramref name="incoming" />, or null when
    ///     it should give up nothing and the pickup simply does not happen.
    ///
    ///     THE HELD WEAPON IS THE ANSWER, and that is the game's rule rather than a choice: PR3.0's
    ///     ServerHandlePickup hook writes `PickupGuid = Pawn->GetCurrentWeapon()->GetItemEntryGuid()`
    ///     and its CompletePickupAnimation swaps precisely that item out when the bar is full
    ///     (FortPlayerPawn.cpp:359, FortPickup.cpp:342). Walking over a gun with five slots used
    ///     replaces what is in your hands - which is also the only choice a player can make without
    ///     a menu, since they chose it by holding it.
    ///
    ///     THREE THINGS ARE NEVER GIVEN UP, and all three fall out of data already baked rather than
    ///     from a list here. The PICKAXE, because it is not one of the five and swapping it away
    ///     would free nothing. A BUILD TOOL or the edit tool, for the same reason - they are in
    ///     neither table, so OccupiesQuickbarSlot is false and they cannot be what is full. And an
    ///     incoming item that takes no carried slot (ammo, a resource, a trap) never displaces
    ///     anything, because it was never competing for the space; PR3.0 gates its own swap on the
    ///     same question (`ItemDefGoingInPrimary`).
    /// </summary>
    public static FFortItemEntry? SwapCandidate(AFortInventory inventory, APawn pawn, UObject incoming) {
        if (!OccupiesQuickbarSlot(incoming)) return null;
        if (pawn.CurrentWeapon is not { } held) return null;

        var entry = inventory.Inventory.Items.FirstOrDefault(item => item.ItemGuid == held.ItemEntryGuid);
        if (entry?.ItemDefinition is not { } definition) return null;
        if (IsPickaxe(definition) || !OccupiesQuickbarSlot(definition)) return null;

        return entry;
    }

    /// <summary>
    ///     How many quickbar slots this inventory is using.
    ///
    ///     COUNTS ONLY WHAT IS POSITIVELY KNOWN TO TAKE A SLOT, and that direction is the whole
    ///     lesson of the first attempt. That version counted everything EXCEPT ammo and resources,
    ///     on the reasoning that StackRule's SingleRow flag separates "own storage" from "quickbar".
    ///     It does - for the items it knows about. What it does not know about is everything
    ///     unstackable, and the stack table holds three WID_ entries in total: no guns, and no
    ///     building tools.
    ///
    ///     So a starting player's FIVE BUILDING TOOLS (Wall, Floor, Stair, Roof, EditTool - granted
    ///     by AGameModeBase from /Game/Items/Weapons/BuildingTools/) each counted as an occupied
    ///     slot, the limit was reached before the match began, and NOTHING could ever be picked up.
    ///     Dropping the starting rifle did not help, because the tools alone already filled it.
    ///
    ///     Counting the other way round cannot do that. A gun is a gun because Tools/WeaponStats
    ///     baked it (98 of them, keyed by exactly the item-definition name this reads); a consumable
    ///     or grenade is one because the stack table has it with SingleRow false. Anything matching
    ///     neither - a build tool, the edit tool, something this project has never seen - is NOT
    ///     counted, so an item nobody recognises can never block a pickup. The failure mode is a
    ///     limit that is slightly too generous, which is what it was before this existed at all.
    /// </summary>
    private static int OccupiedSlots(AFortInventory inventory) {
        var used = 0;

        foreach (var item in inventory.Inventory.Items) {
            if (item.ItemDefinition is not { } definition) continue;
            if (IsPickaxe(definition)) continue;
            if (!OccupiesQuickbarSlot(definition)) continue;

            used++;
        }

        return used;
    }

    /// <summary>
    ///     Whether this item is one this server can positively identify as quickbar-carried.
    ///
    ///     THE TABLE ANSWERS THIS, from the item's real CLASS: Tools/StackSizes marks ammo,
    ///     resources and TRAPS as not carried, because all three live in Fortnite's secondary
    ///     quickbar row beside the materials, and the build tools and edit tool likewise, because
    ///     they are in the quickbar without being among the five. Traps were reported as eating
    ///     weapon slots, and that is why this is a baked column rather than something inferred from
    ///     the stack rules - `SingleRow` says "never opens a second stack", which a trap does not do
    ///     either, but the two questions are not the same one and answering the first with the
    ///     second is exactly what broke pickups before.
    ///
    ///     AND THE TABLE NOW HOLDS EVERY ITEM DEFINITION, which is a fix to this method rather than
    ///     to the generator. It used to hold only STACKABLE items, so an unstackable one fell
    ///     through to Tools/WeaponStats - a table of the 98 weapons that have a stat ROW, which is
    ///     not the same set as "every weapon". `WID_Assault_Auto_Athena_C_Ore_T02` and
    ///     `WID_Sniper_Auto_Suppressed_Scope_Athena_R` were in neither, so they were invisible to
    ///     the slot limit in BOTH directions - never counted towards the five, never refused by a
    ///     full inventory - and one live session ended up carrying eleven. All 4472 are baked now.
    /// </summary>
    public static bool OccupiesQuickbarSlot(UObject definition) {
        // The table's own answer, whenever it has one - which is now for every item definition in
        // the shipped paks.
        if (Rule(definition) is { } rule) return rule.Carried;

        // A path the bake never saw at all. Falling through to the weapon table is a second line of
        // defence rather than the rule it used to be; anything in neither is not counted, so an
        // item nobody recognises can never block a pickup. That direction is deliberate - counting
        // an unknown item is what made a starting loadout fill the bar and the game unplayable.
        return FortWeaponStats.For(definition.GetFName().ToString()) != null;
    }

    /// <summary>
    ///     The pickaxe, which every player always has and which occupies quickbar slot 0 rather than
    ///     one of the five carried slots. Matched by name rather than by class because it is the one
    ///     weapon this project already keys on by name elsewhere (see NativeRpcHandlers.DeathCauseFor).
    /// </summary>
    public static bool IsPickaxe(UObject definition) =>
        definition.GetFName().ToString().Contains("Pickaxe", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Fortnite Battle Royale's five carried slots. INVENTORY_SLOTS overrides it - the number is
    ///     the game's, not something derived from an asset, so it is worth being able to change
    ///     without a rebuild.
    /// </summary>
    private static int SlotLimitFor(AFortInventory inventory) =>
        // PER WORLD: a playlist can reasonably want a different number, and every check already
        // holds the inventory actor whose world it is.
        int.TryParse(inventory.GetWorld()?.Options.Get("INVENTORY_SLOTS"), out var slots) && slots > 0
            ? slots
            : 5;
}
