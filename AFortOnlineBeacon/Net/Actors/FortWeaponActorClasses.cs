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
