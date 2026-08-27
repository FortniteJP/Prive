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
    public static string? PathFor(UObject? itemDefinition) {
        if (itemDefinition == null) return null;
        return Table.GetValueOrDefault(itemDefinition.GetFName().ToString());
    }

    /// <summary>
    ///     The UGameplayAbility class this weapon fires with, as a path-exported asset reference.
    ///     Null for an item with no fire ability (or one outside the generated table).
    /// </summary>
    public static UObject? FireAbilityFor(UObject? itemDefinition) {
        if (itemDefinition == null) return null;

        var classPath = AbilityTable.GetValueOrDefault(itemDefinition.GetFName().ToString());
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
    ///     The UClass to hand UWorld.SpawnActor for this item definition. One UClass per distinct
    ///     weapon class path, all backed by the same C# AFortWeapon type: the class is what the
    ///     client is told to spawn (its CDO is the archetype in the spawn header), while the C# type
    ///     only decides which RepLayout and property getters this server uses.
    /// </summary>
    public static UClass? ClassFor(UObject? itemDefinition) {
        var path = PathFor(itemDefinition);
        return path == null ? null : GUClassArray.StaticClassForPath<AFortWeapon>(path);
    }
}
