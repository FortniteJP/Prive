using AFortOnlineBeacon.Core.Objects;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     The weapon a pawn is currently holding - /Script/FortniteGame.FortWeapon, spawned as the
///     Blueprint subclass named by the item definition's WeaponActorClass (see
///     FortWeaponActorClasses).
///
///     This is the second actor in the project that only exists mid-match (after AFortPickup), and
///     the first whose CLASS is not fixed: an assault rifle is a B_Assault_Auto_Athena_C and the
///     pickaxe is a B_Athena_Pickaxe_Generic_C, both replicated through this one C# type. That
///     split is fine because the class only decides what the CLIENT constructs - every property
///     below lives on AFortWeapon, which both of them inherit, so one RepLayout covers both (see
///     NativeRepLayouts.WeaponProps).
///
///     Equipping is two links, not one. The pawn points at the weapon (AFortPawn::CurrentWeapon,
///     handle 66) and the weapon points back at its item entry (ItemEntryGuid) and its data
///     (WeaponData). Sending only the pawn's half gives the client a weapon actor it cannot match to
///     an inventory row - which is what the quickbar highlights and what the ammo counter reads.
/// </summary>
public class AFortWeapon : AActor {
    /// <summary>
    ///     Everyone can see what you are holding, so this is not owner-only - unlike the inventory
    ///     that produced it (see AFortInventory). With no distance culling in this project,
    ///     "relevant to whoever asks" is the honest answer.
    /// </summary>
    public AFortWeapon() => bAlwaysRelevant = true;

    /// <summary>
    ///     AFortWeapon::WeaponData (handle 19) - the UFortWeaponItemDefinition this weapon was made
    ///     from, i.e. the same WID_ asset that sits in the inventory entry. The client drives
    ///     essentially all of its presentation off this one reference (mesh, name, reticle, ammo
    ///     type), which is why an injected server sets it by hand right after EquipWeaponDefinition
    ///     even though the native call already did (Project-Reboot-3.0 FortPawn.cpp,
    ///     raider3.5 Inventory.h:281).
    /// </summary>
    public UObject? WeaponData { get; set; }

    /// <summary>
    ///     AFortWeapon::ItemEntryGuid (handles 23-26, one per FGuid member) - which inventory row
    ///     this weapon IS. Without it the client has a weapon in its hands that matches no quickbar
    ///     slot, so nothing highlights and the item's own state (ammo, durability) has nowhere to
    ///     go.
    /// </summary>
    public Guid ItemEntryGuid { get; set; }

    /// <summary>AFortWeapon::WeaponLevel (handle 27).</summary>
    public int WeaponLevel { get; set; }

    /// <summary>
    ///     AFortWeapon::AmmoCount (handle 28) - the magazine, not the reserve. Seeded from the item
    ///     entry's LoadedAmmo, exactly as a real server does
    ///     (raider3.5 Inventory.h:290, `Instance->ItemEntry.LoadedAmmo = Weapon->AmmoCount`).
    /// </summary>
    public int AmmoCount { get; set; }
}
