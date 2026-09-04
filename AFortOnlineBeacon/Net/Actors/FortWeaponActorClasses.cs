using AFortOnlineBeacon.Core.Objects;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Resolves an item definition to the AFortWeapon subclass a real server would spawn for it.
///
///     On an injected server this is one call - UFortWeaponItemDefinition::GetWeaponActorClass()
///     reads the WID asset that is already loaded in the game's own memory. AFortOnlineBeacon runs
///     outside the game and never opens a pak at runtime: every asset it names (an item definition,
///     a hero type, a HUD class) is a path string exported to the client, with no properties behind
///     it. So the WeaponActorClass property has to be read AHEAD of time and baked in - see
///     FortWeaponActorClasses.Generated.cs and Tools/WeaponClasses/gen_weapon_actor_classes.py.
///
///     The class matters more than it looks. A weapon actor is what carries the mesh, the animation
///     blueprint and the firing abilities; spawning a bare /Script/FortniteGame.FortWeapon would
///     hand the client an actor with nothing to draw and no ability to fire, the same way pointing
///     the pawn at /Script/Engine.Pawn once did (see GUClassArray.NativePackagePaths).
/// </summary>
internal static partial class FortWeaponActorClasses {
    /// <summary>
    ///     The weapon actor class path for an item definition, or null if the item is not a weapon
    ///     (or is one the generated table does not cover - anything outside Athena's own weapon
    ///     directory, e.g. the Save the World building tools).
    /// </summary>
    /// <summary>
    ///     The building tools, which the generated table does not and cannot cover: it is built from
    ///     a dump of `WID_*` under Athena/Items/Weapons, and these are FortBuildingItemDefinitions
    ///     living somewhere else entirely.
    ///
    ///     Selecting a building piece is an EQUIP, exactly like picking up a rifle - the piece's own
    ///     WeaponActorClass is the tool the pawn holds, and the build menu is that tool being in your
    ///     hands. With no entry here the equip resolved to null, nothing was spawned, and the pawn
    ///     silently kept whatever it was already holding: pieces in the quickbar, no build mode.
    ///
    ///     Read from the cooked assets rather than guessed - MapActorDump "props:" over
    ///     FortniteGame/Content/Items/Weapons/BuildingTools/ prints the WeaponActorClass of each,
    ///     along with the PreferredQuickbarSlot that orders them (Wall 0, Floor 1, Stair 2, Roof 3).
    ///     All four pieces share one generic tool; the edit tool is its own.
    /// </summary>
    private static readonly Dictionary<string, string> BuildingToolClasses = new(StringComparer.OrdinalIgnoreCase) {
        ["BuildingItemData_Wall"] = "/Game/Weapons/FORT_BuildingTools/Blueprints/DefaultBuildingTool.DefaultBuildingTool_C",
        ["BuildingItemData_Floor"] = "/Game/Weapons/FORT_BuildingTools/Blueprints/DefaultBuildingTool.DefaultBuildingTool_C",
        ["BuildingItemData_Stair_W"] = "/Game/Weapons/FORT_BuildingTools/Blueprints/DefaultBuildingTool.DefaultBuildingTool_C",
        ["BuildingItemData_RoofS"] = "/Game/Weapons/FORT_BuildingTools/Blueprints/DefaultBuildingTool.DefaultBuildingTool_C",
        ["EditTool"] = "/Game/Weapons/FORT_BuildingTools/Blueprints/DefaultEditingTool.DefaultEditingTool_C"
    };

    public static string? PathFor(UObject? itemDefinition) {
        if (itemDefinition == null) return null;

        var name = itemDefinition.GetFName().ToString();
        return Table.GetValueOrDefault(name)
               ?? BuildingToolClasses.GetValueOrDefault(name)
               // Consumables live under Athena/Items/Consumables, not Athena/Items/Weapons, so the
               // generated weapon table never saw them - see FortConsumables.Generated.cs. They are
               // FortWeaponRangedItemDefinitions like any rifle and go through exactly this path.
               ?? FortConsumables.ActorClassFor(name);
    }

    /// <summary>
    ///     The UGameplayAbility class this weapon fires with, as a path-exported asset reference.
    ///     Null for an item with no fire ability (or one outside the generated table).
    /// </summary>
    public static UObject? FireAbilityFor(UObject? itemDefinition) {
        if (itemDefinition == null) return null;

        var name = itemDefinition.GetFName().ToString();
        var classPath = AbilityTable.GetValueOrDefault(name) ?? FortConsumables.AbilityFor(name);
        if (classPath == null) return null;

        // FGameplayAbilitySpec::Ability is a UGameplayAbility POINTER, not a class - the spec
        // constructor stores `InAbilityClass->GetDefaultObject<UGameplayAbility>()`. Sending the
        // class instead makes the client resolve a UClass, fail the cast, and refuse the shot with
        //     LogAbilitySystem: Warning: TryActivateAbility called with invalid Ability
        // which is the SECOND check in UAbilitySystemComponent::TryActivateAbility - the first,
        // FindAbilitySpecFromHandle, had already succeeded, so the handle itself was fine.
        //
        // A CDO is a SIBLING of its class, both direct children of the package (see
        // UClass.CreateDefaultObject for the same rule) - so
        // "/Game/.../GA_X.GA_X_C" becomes "/Game/.../GA_X.Default__GA_X_C". The real
        // Project-Reboot-3.0 capture exports exactly that pair: the package path
        // "/Game/Abilities/Weapons/Ranged/GA_Ranged_GenericDamage" plus the object name
        // "Default__GA_Ranged_GenericDamage_C".
        var dot = classPath.LastIndexOf('.');
        if (dot < 0) return null;

        var cdoPath = $"{classPath[..dot]}.Default__{classPath[(dot + 1)..]}";
        return UAssetRegistry.GetOrCreate(cdoPath);
    }

    /// <summary>
    ///     The FortAmmoItemDefinition this weapon reloads from, as a path-exported asset, or null
    ///     for a weapon with no magazine. Reloading draws from a SEPARATE inventory item - a player
    ///     carrying only the weapon is correctly told there is not enough ammo.
    /// </summary>
    public static UObject? AmmoItemFor(UObject? itemDefinition) {
        if (itemDefinition == null) return null;
        var path = AmmoTable.GetValueOrDefault(itemDefinition.GetFName().ToString());
        return path == null ? null : UAssetRegistry.GetOrCreate(path);
    }

    /// <summary>
    ///     How many rounds a full magazine holds, or 0 for a weapon with no magazine. Read from the
    ///     weapon's stat-table row rather than guessed - the assault rifle is 30, a pistol 16, a
    ///     shotgun 5.
    /// </summary>
    public static int ClipSizeFor(UObject? itemDefinition) {
        if (itemDefinition == null) return 0;
        return ClipSizeTable.GetValueOrDefault(itemDefinition.GetFName().ToString());
    }

    /// <summary>
    ///     The item entry for something spawned into the WORLD as loot - a chest's contents, floor
    ///     loot, anything the player has not handled yet.
    ///
    ///     Exists so that "world loot arrives LOADED" is stated once instead of at every spawn site.
    ///     A weapon a real server puts on the ground comes with a full magazine, and LoadedAmmo is
    ///     what APawn.EquipWeapon copies straight into AFortWeapon::AmmoCount (handle 28) - so an
    ///     entry left at the default 0 hands the player a gun that has to be reloaded before it can
    ///     fire a single shot. Anything without a magazine (a consumable, ammo itself, a resource)
    ///     has no row in the clip-size table and correctly gets 0.
    /// </summary>
    public static FFortItemEntry WorldLootEntry(string itemPath, int count) {
        var definition = UAssetRegistry.GetOrCreate(itemPath);

        return new FFortItemEntry {
            ItemDefinition = definition,
            Count = count,
            LoadedAmmo = ClipSizeFor(definition)
        };
    }

    /// <summary>
    ///     The UClass to hand UWorld.SpawnActor for this item definition. One UClass per distinct
    ///     weapon class path, all backed by the same C# AFortWeapon type: the class is what the
    ///     client is told to spawn (its CDO is the archetype in the spawn header), while the C# type
    ///     only decides which RepLayout and property getters this server uses.
    /// </summary>
    public static UClass? ClassFor(UObject? itemDefinition) {
        var path = PathFor(itemDefinition);
        return path == null ? null : GUClassArray.StaticClassForPath<AFortWeapon>(path);
    }

    /// <summary>
    ///     UFortBuildingItemDefinition::BuildingMetaData - a soft reference (10.40 SDK, offset 0x910)
    ///     to the UBuildingEditModeMetadata asset that shapes this piece. AFortWeap_BuildingTool
    ///     mirrors it into its own replicated DefaultMetadata (wire handle 36, rep_handles.py
    ///     AFortWeap_BuildingTool), and OnRep_DefaultMetadata is what the client's ghost/pencil
    ///     preview actually reads to know what to draw. With no value there, DefaultMetadata stays
    ///     null forever and the client silently draws no ghost - exactly the reported symptom, and
    ///     the same shape as jump: a client-side gate fed by a property this server never sent.
    ///
    ///     Read from the cooked assets rather than guessed (MapActorDump "props:BuildingItemData"
    ///     over FortniteGame/Content/Items/Weapons/BuildingTools/), since the four pieces do not
    ///     share one metadata asset the way they share one WeaponActorClass.
    /// </summary>
    private static readonly Dictionary<string, string> BuildingMetadataTable = new(StringComparer.OrdinalIgnoreCase) {
        ["BuildingItemData_Wall"] = "/Game/Building/EditModePatterns/Wall/EMP_Wall_Solid.EMP_Wall_Solid",
        ["BuildingItemData_Floor"] = "/Game/Building/EditModePatterns/Floor/EMP_Floor_Floor.EMP_Floor_Floor",
        ["BuildingItemData_Stair_W"] = "/Game/Building/EditModePatterns/Stair/EMP_Stair_StairW.EMP_Stair_StairW",
        ["BuildingItemData_RoofS"] = "/Game/Building/EditModePatterns/Roof/EMP_Roof_RoofC.EMP_Roof_RoofC"
        // EditTool has no entry: AFortWeap_EditingTool does not inherit AFortWeap_BuildingTool and
        // has no DefaultMetadata to fill.
    };

    public static UObject? BuildingMetadataFor(UObject? itemDefinition) {
        if (itemDefinition == null) return null;
        var path = BuildingMetadataTable.GetValueOrDefault(itemDefinition.GetFName().ToString());
        return path == null ? null : UAssetRegistry.GetOrCreate(path);
    }

    /// <summary>
    ///     The real building ACTOR class (not the item/tool the player holds) a piece places as -
    ///     what ServerCreateBuildingActor needs to SpawnActor. Wood tier 1 only for now (material
    ///     switching doesn't yet change what this returns - see NativeRpcHandlers.ServerCreateBuildingActor's
    ///     doc comment for the rest of that story).
    ///
    ///     Wall/Floor/Stair_W paths are straight from a real Project-Reboot-3.0 capture's own NetGUID
    ///     exports (PriveDev/PacketProxy/decoded_new.txt, 2026-08-29:
    ///     "/Game/Building/ActorBlueprints/Player/Wood/L1/PBWA_W1_Solid" etc.) - RoofS's Wood-tier
    ///     path was never itself captured (only the Metal-tier PBWA_M1_RoofC turned up), but the
    ///     naming is completely systematic across all three confirmed pieces
    ///     (.../{Material}/L1/PBWA_{MaterialCode}1_{Piece}), so it is inferred by the same pattern,
    ///     not guessed from nothing.
    /// </summary>
    private static readonly Dictionary<string, string> BuildingActorClassTable = new(StringComparer.OrdinalIgnoreCase) {
        ["BuildingItemData_Wall"] = "/Game/Building/ActorBlueprints/Player/Wood/L1/PBWA_W1_Solid.PBWA_W1_Solid_C",
        ["BuildingItemData_Floor"] = "/Game/Building/ActorBlueprints/Player/Wood/L1/PBWA_W1_Floor.PBWA_W1_Floor_C",
        ["BuildingItemData_Stair_W"] = "/Game/Building/ActorBlueprints/Player/Wood/L1/PBWA_W1_StairW.PBWA_W1_StairW_C",
        ["BuildingItemData_RoofS"] = "/Game/Building/ActorBlueprints/Player/Wood/L1/PBWA_W1_RoofC.PBWA_W1_RoofC_C" // inferred, see doc comment
    };

    public static UClass? BuildingActorClassFor(UObject? itemDefinition) {
        if (itemDefinition == null) return null;
        var path = BuildingActorClassTable.GetValueOrDefault(itemDefinition.GetFName().ToString());
        // Same pattern as ClassFor above - must use the SAME C# type (ABuildingActor) that resolves
        // this path everywhere else, or the same content path would export as two different NetGUIDs
        // to the client depending on which lookup found it first (StaticClassForPath is cached per
        // (Type, path) key - see its own doc comment).
        return path == null ? null : GUClassArray.StaticClassForPath<ABuildingActor>(path);
    }
}
