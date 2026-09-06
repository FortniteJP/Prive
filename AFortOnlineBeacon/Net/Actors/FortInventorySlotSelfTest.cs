namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Checks which items count towards the five carried quickbar slots.
///
///     WHY IT EXISTS: this classification, got wrong, does not degrade the game - it stops it. The
///     first version counted everything except ammo and resources, which made a starting player's
///     five BUILDING TOOLS fill the limit before the match began, and nothing could be picked up for
///     the rest of the session. That is a one-line rule with a total-failure mode and no log to
///     notice it by, which is exactly the shape that deserves a test.
///
///     The cases below are the ones that broke it, plus the ones it must still get right.
/// </summary>
public static class FortInventorySlotSelfTest {
    /// <summary>The build-mode tools AGameModeBase grants every player - the case that broke it.</summary>
    private static readonly string[] BuildingTools = {
        "/Game/Items/Weapons/BuildingTools/BuildingItemData_Wall.BuildingItemData_Wall",
        "/Game/Items/Weapons/BuildingTools/BuildingItemData_Floor.BuildingItemData_Floor",
        "/Game/Items/Weapons/BuildingTools/BuildingItemData_Stair_W.BuildingItemData_Stair_W",
        "/Game/Items/Weapons/BuildingTools/BuildingItemData_RoofS.BuildingItemData_RoofS",
        "/Game/Items/Weapons/BuildingTools/EditTool.EditTool"
    };

    /// <summary>
    ///     Ammo, resources and TRAPS - the secondary quickbar row, beside the materials. Traps are
    ///     here because they were reported eating one of the five carried slots each.
    /// </summary>
    private static readonly string[] OwnStorage = {
        "/Game/Athena/Items/Ammo/AthenaAmmoDataBulletsLight.AthenaAmmoDataBulletsLight",
        "/Game/Athena/Items/Ammo/AthenaAmmoDataShells.AthenaAmmoDataShells",
        "/Game/Items/ResourcePickups/WoodItemData.WoodItemData",
        "/Game/Items/ResourcePickups/MetalItemData.MetalItemData",
        "/Game/Athena/Items/Traps/TID_Floor_Player_Launch_Pad_Athena.TID_Floor_Player_Launch_Pad_Athena",
        "/Game/Athena/Items/Traps/TID_Floor_Player_Campfire_Athena.TID_Floor_Player_Campfire_Athena",
        "/Game/Athena/Items/Traps/TID_Floor_MountedTurret_Athena.TID_Floor_MountedTurret_Athena",

        // The CONTEXT traps - the ones that were splitting into a slot each. They override no
        // MaxStackSize, so they used to be dropped from the table entirely and were then
        // indistinguishable from an unknown item.
        "/Game/Athena/Items/Traps/TID_Context_BouncePad_Athena.TID_Context_BouncePad_Athena",
        "/Game/Athena/Items/Traps/TID_Context_Freeze_Athena.TID_Context_Freeze_Athena",
        "/Game/Athena/Items/Traps/TID_PoisonDartTrap_Context.TID_PoisonDartTrap_Context"
    };

    /// <summary>Things that genuinely take one of the five.</summary>
    private static readonly string[] Carried = {
        "/Game/Athena/Items/Weapons/WID_Assault_Auto_Athena_R_Ore_T03.WID_Assault_Auto_Athena_R_Ore_T03",
        "/Game/Athena/Items/Weapons/WID_Shotgun_Standard_Athena_C_Ore_T03.WID_Shotgun_Standard_Athena_C_Ore_T03",
        "/Game/Athena/Items/Consumables/Grenade/Athena_Grenade.Athena_Grenade",

        // THE TWO THAT WERE INVISIBLE, and they are here by name because the failure had no symptom
        // of its own: a weapon absent from BOTH tables was never counted towards the five and never
        // refused by a full bar, so one live session simply accumulated eleven carried items with
        // nothing logged. Both are ordinary guns - the starting assault rifle among them - and what
        // set them apart was only that Tools/WeaponStats had no stat row for them. The stack table
        // holds every item definition now; these two are the canaries for that staying true.
        "/Game/Athena/Items/Weapons/WID_Assault_Auto_Athena_C_Ore_T02.WID_Assault_Auto_Athena_C_Ore_T02",
        "/Game/Athena/Items/Weapons/WID_Sniper_Auto_Suppressed_Scope_Athena_R.WID_Sniper_Auto_Suppressed_Scope_Athena_R",
        "/Game/Athena/Items/Consumables/PurpleStuff/Athena_PurpleStuff.Athena_PurpleStuff"
    };

    public static bool RunSelfTest() {
        var failures = 0;

        foreach (var path in BuildingTools) failures += Check(path, false, "a build-mode tool");
        foreach (var path in OwnStorage) failures += Check(path, false, "ammo or a resource");
        foreach (var path in Carried) failures += Check(path, true, "a carried weapon or consumable");

        // The pickaxe is carried, but it lives in quickbar slot 0 and is not one of the five - so it
        // is excluded separately from the "does this take a slot" question rather than by it.
        var pickaxe = Core.Objects.UAssetRegistry.GetOrCreate(
            "/Game/Athena/Items/Weapons/WID_Harvest_Pickaxe_Athena_C_T01.WID_Harvest_Pickaxe_Athena_C_T01");

        if (!FortItemStacks.IsPickaxe(pickaxe)) {
            Console.WriteLine("FortInventorySlotSelfTest: the pickaxe is not recognised as the pickaxe, so it " +
                              "would consume one of the five carried slots.");
            failures++;
        }

        // THE CASE THAT BROKE IT, stated as the number it broke on: a starting loadout must leave
        // every one of the five free.
        var startingLoadout = BuildingTools.Count(path => Occupies(path)) + (FortItemStacks.IsPickaxe(pickaxe) ? 0 : 1);
        if (startingLoadout != 0) {
            Console.WriteLine($"FortInventorySlotSelfTest: a starting loadout already occupies {startingLoadout} " +
                              "of the five carried slots - a player would be unable to pick anything up.");
            failures++;
        }

        // AND THEY MUST STACK. A trap whose asset gives no MaxStackSize used to resolve to 1, which
        // is what put every trap in its own slot - see FortItemStacks.StackCap.
        foreach (var path in new[] {
            "/Game/Athena/Items/Traps/TID_Context_BouncePad_Athena.TID_Context_BouncePad_Athena",
            "/Game/Athena/Items/Traps/TID_Floor_Player_Launch_Pad_Athena.TID_Floor_Player_Launch_Pad_Athena"
        }) {
            var max = FortItemStacks.MaxStack(Core.Objects.UAssetRegistry.GetOrCreate(path));
            if (max > 1) continue;

            Console.WriteLine($"FortInventorySlotSelfTest: {path} has a stack cap of {max}, so every one " +
                              "picked up would open its own slot.");
            failures++;
        }

        failures += CheckDryRun();
        failures += CheckFullBar();

        Console.WriteLine(failures == 0
            ? "FortInventorySlotSelfTest: build tools, ammo and resources take no slot; weapons and " +
              "consumables do; traps stack and take none; a starting loadout leaves all five free; " +
              "a dry-run Give answers the same and changes nothing; a full bar still takes ammo and " +
              "traps, refuses a duplicate weapon, and trades the HELD weapon for a gun. OK."
            : $"FortInventorySlotSelfTest: {failures} FAILURE(S).");

        return failures == 0;
    }

    /// <summary>
    ///     What a FULL carried bar does, which is two separate questions that were both answered
    ///     wrongly by the same substitution.
    ///
    ///     IT STILL TAKES AMMO, RESOURCES AND TRAPS. The slot gate used to read `!singleRow` as a
    ///     stand-in for "takes a carried slot", so a trap - neither single-row nor carried - was
    ///     refused by a full weapon bar. It is in the live logs as
    ///     `has no room for TID_Floor_Player_Campfire_Athena`, forty-odd times in one session, while
    ///     the player stood on it pressing pickup.
    ///
    ///     AND IT TRADES THE HELD WEAPON for a gun, rather than refusing. The pickaxe and the build
    ///     tools must never be what leaves: swapping them away would free nothing, since they are
    ///     not what filled the bar.
    /// </summary>
    private static int CheckFullBar() {
        var failures = 0;

        var rifle = Core.Objects.UAssetRegistry.GetOrCreate(
            "/Game/Athena/Items/Weapons/WID_Assault_Auto_Athena_R_Ore_T03.WID_Assault_Auto_Athena_R_Ore_T03");
        var shotgun = Core.Objects.UAssetRegistry.GetOrCreate(
            "/Game/Athena/Items/Weapons/WID_Shotgun_Standard_Athena_C_Ore_T03.WID_Shotgun_Standard_Athena_C_Ore_T03");
        var pickaxe = Core.Objects.UAssetRegistry.GetOrCreate(
            "/Game/Athena/Items/Weapons/WID_Harvest_Pickaxe_Athena_C_T01.WID_Harvest_Pickaxe_Athena_C_T01");
        var wall = Core.Objects.UAssetRegistry.GetOrCreate(
            "/Game/Items/Weapons/BuildingTools/BuildingItemData_Wall.BuildingItemData_Wall");
        var ammo = Core.Objects.UAssetRegistry.GetOrCreate(
            "/Game/Athena/Items/Ammo/AthenaAmmoDataBulletsLight.AthenaAmmoDataBulletsLight");
        var campfire = Core.Objects.UAssetRegistry.GetOrCreate(
            "/Game/Athena/Items/Traps/TID_Floor_Player_Campfire_Athena.TID_Floor_Player_Campfire_Athena");

        // Five carried weapons, plus the pickaxe and a build tool that are not among the five.
        AFortInventory Full() {
            var inventory = new AFortInventory();
            inventory.Inventory.Add(new FFortItemEntry { ItemDefinition = pickaxe, Count = 1 });
            inventory.Inventory.Add(new FFortItemEntry { ItemDefinition = wall, Count = 1 });
            for (var i = 0; i < 5; i++) inventory.Inventory.Add(new FFortItemEntry { ItemDefinition = shotgun, Count = 1 });
            return inventory;
        }

        foreach (var (definition, what) in new[] { (ammo, "light ammo"), (campfire, "a campfire trap") }) {
            var template = new FFortItemEntry { ItemDefinition = definition, Count = 1 };
            var leftOver = FortItemStacks.Give(Full(), template, 1);
            if (leftOver == 0) continue;

            Console.WriteLine($"FortInventorySlotSelfTest: a full carried bar refused {what}, which does not " +
                              "take a carried slot at all.");
            failures++;
        }

        // A SECOND COPY OF SOMETHING ALREADY HELD is the case the gate used to wave through: the
        // slot check was skipped whenever the item was already in the inventory, and a weapon never
        // stacks, so holding one rifle exempted every further rifle from the limit forever.
        var duplicate = new FFortItemEntry { ItemDefinition = shotgun, Count = 1 };
        if (FortItemStacks.Give(Full(), duplicate, 1) != 1) {
            Console.WriteLine("FortInventorySlotSelfTest: a full bar accepted a SECOND copy of a weapon it " +
                              "already holds, which is a sixth carried slot.");
            failures++;
        }

        // ...and it must still be allowed when there IS room. Two identical rifles in two slots is
        // perfectly legal; only the sixth slot is not.
        var roomForOne = new AFortInventory();
        for (var i = 0; i < 4; i++) roomForOne.Inventory.Add(new FFortItemEntry { ItemDefinition = shotgun, Count = 1 });
        if (FortItemStacks.Give(roomForOne, duplicate, 1) != 0) {
            Console.WriteLine("FortInventorySlotSelfTest: a bar with one slot free refused a second copy of a " +
                              "weapon already held, though duplicates are legal.");
            failures++;
        }

        // The swap: the HELD weapon is what leaves.
        var held = Full();
        var heldEntry = held.Inventory.Items.First(item => item.ItemDefinition == shotgun);
        var pawn = new APawn { CurrentWeapon = new AFortWeapon { ItemEntryGuid = heldEntry.ItemGuid } };

        if (FortItemStacks.SwapCandidate(held, pawn, rifle) != heldEntry) {
            Console.WriteLine("FortInventorySlotSelfTest: a full bar did not offer the HELD weapon in trade for a gun.");
            failures++;
        }

        // Nothing that is not one of the five may ever be traded away, and nothing that does not
        // compete for the five may ever trigger a trade.
        foreach (var (definition, what) in new[] { (pickaxe, "the pickaxe"), (wall, "a build tool") }) {
            var entry = held.Inventory.Items.First(item => item.ItemDefinition == definition);
            var holding = new APawn { CurrentWeapon = new AFortWeapon { ItemEntryGuid = entry.ItemGuid } };

            if (FortItemStacks.SwapCandidate(held, holding, rifle) == null) continue;

            Console.WriteLine($"FortInventorySlotSelfTest: a player holding {what} would trade it away - " +
                              "which frees none of the five.");
            failures++;
        }

        if (FortItemStacks.SwapCandidate(held, pawn, ammo) != null) {
            Console.WriteLine("FortInventorySlotSelfTest: picking up ammo would trade away a weapon, though " +
                              "ammo never competed for a carried slot.");
            failures++;
        }

        return failures;
    }

    /// <summary>
    ///     A DRY-RUN GIVE MUST AGREE WITH THE REAL ONE, and must leave the inventory alone.
    ///
    ///     The pickup flight asks both halves of one question in two places - "may this be picked up
    ///     at all?" before the animation, "what actually fits?" when it lands - and answering the
    ///     first with a proxy for the second is precisely how the slot limit made the game
    ///     unplayable the first time. So the two are the same walk, and this is the test that says
    ///     so: three inventories where the answer differs, each checked for the same leftover AND
    ///     for the dry run having touched nothing.
    /// </summary>
    private static int CheckDryRun() {
        var failures = 0;

        var rifle = Core.Objects.UAssetRegistry.GetOrCreate(
            "/Game/Athena/Items/Weapons/WID_Assault_Auto_Athena_R_Ore_T03.WID_Assault_Auto_Athena_R_Ore_T03");
        var shotgun = Core.Objects.UAssetRegistry.GetOrCreate(
            "/Game/Athena/Items/Weapons/WID_Shotgun_Standard_Athena_C_Ore_T03.WID_Shotgun_Standard_Athena_C_Ore_T03");
        var ammo = Core.Objects.UAssetRegistry.GetOrCreate(
            "/Game/Athena/Items/Ammo/AthenaAmmoDataBulletsLight.AthenaAmmoDataBulletsLight");

        // Room for it.
        failures += Agrees("an empty inventory", new AFortInventory(), rifle, 1, expected: 0);

        // No room at all - five carried weapons already. This is the answer the flight needs BEFORE
        // it starts, because there is no way to un-fly an item.
        var full = new AFortInventory();
        for (var i = 0; i < 5; i++) full.Inventory.Add(new FFortItemEntry { ItemDefinition = shotgun, Count = 1 });
        failures += Agrees("five carried slots in use", full, rifle, 1, expected: 1);

        // A PARTIAL take, which is the case a boolean "does it fit" would have got wrong: ammo is
        // single-row, so 9 of the 20 go into the existing stack and the other 11 come back.
        var nearlyFull = new AFortInventory();
        nearlyFull.Inventory.Add(new FFortItemEntry { ItemDefinition = ammo, Count = 990 });
        failures += Agrees("a stack with room for 9 more", nearlyFull, ammo, 20, expected: 11);

        return failures;
    }

    private static int Agrees(string what, AFortInventory inventory, Core.Objects.UObject definition,
                              int count, int expected) {
        var template = new FFortItemEntry { ItemDefinition = definition, Count = count };

        var before = inventory.Inventory.Items.Select(item => (item.ItemDefinition, item.Count)).ToList();
        var dry = FortItemStacks.Give(inventory, template, count, dryRun: true);
        var after = inventory.Inventory.Items.Select(item => (item.ItemDefinition, item.Count)).ToList();

        var failures = 0;

        if (!before.SequenceEqual(after)) {
            Console.WriteLine($"FortInventorySlotSelfTest: a dry-run Give into {what} CHANGED the inventory " +
                              $"({before.Count} item(s) -> {after.Count}).");
            failures++;
        }

        var real = FortItemStacks.Give(inventory, template, count);

        if (dry != expected || real != expected) {
            Console.WriteLine($"FortInventorySlotSelfTest: giving {count} into {what} should leave {expected} " +
                              $"over - the dry run said {dry} and the real one {real}.");
            failures++;
        }

        return failures;
    }

    private static bool Occupies(string path) =>
        FortItemStacks.OccupiesQuickbarSlot(Core.Objects.UAssetRegistry.GetOrCreate(path));

    private static int Check(string path, bool expected, string what) {
        if (Occupies(path) == expected) return 0;

        Console.WriteLine($"FortInventorySlotSelfTest: {path} is {what}, so it should " +
                          $"{(expected ? "take" : "NOT take")} a carried slot.");
        return 1;
    }
}
