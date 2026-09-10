namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     The thrown items that end as a PLACED ACTOR instead of an explosion, and what each one leaves
///     behind.
///
///     EVERY ROW IS READ OUT OF THE PROJECTILE'S OWN BLUEPRINT, not chosen. Each of these projectiles
///     spawns a class named by one of its own properties, and the property is what this table holds:
///
///         Athena_SneakySnowman     B_Prj_Athena_SneakySnowman   BeginDeferredActorSpawnFromClass
///                                                               (Athena_Prop_SneakySnowman_C)
///         Athena_SilverBlazer_V2   B_Prj_Athena_SilverBlazer_V2 SpawnBGA of `BlazerToSpawn`
///                                                               = BGA_Athena_SilverBlazerCore_C
///         Athena_FireworksMortar   B_Prj_FireworksMortar_Holder SpawnBuildingGameplayActor of
///                                                               `BGA_FireworksHolder`
///                                                               = B_BGA_FireworksMortar_Holder_C,
///                                                               PlacementZOffset (0,0,5)
///
///     WHICH SIDE OF THE BUILDING FORK EACH ONE IS ON DECIDES THE REP LAYOUT, and getting it wrong
///     closes the connection rather than looking odd - see AFortDeployedActor. Checked with
///     `pakreader supers`:
///
///         Athena_Prop_SneakySnowman_C    -> BuildingProp -> ... -> ABuildingSMActor
///         BGA_Athena_SilverBlazerCore_C  -> BGA_Athena_WithGravity_Parent_C -> ABuildingGameplayActor
///         B_BGA_FireworksMortar_Holder_C -> ABuildingGameplayActor
///
///     THE ZAPPER TRAP IS NOT HERE and that is a finding rather than an omission: its projectile
///     (`B_Prj_Athena_TrapGrenade_ZippyTrout`) has no event graph at all and names a
///     `TrapDefinition` (`TID_ZippyTroutTrap_Context`) instead. It PLACES A TRAP, which is the
///     building system's job and not this one's.
/// </summary>
internal static class FortDeployables {
    /// <summary>
    ///     What this item leaves at its landing point, or null when it just explodes.
    ///
    ///     <c>GameplayActor</c> says which fork the class is on: true means ABuildingGameplayActor
    ///     and therefore ABuildingActor's handles ONLY.
    /// </summary>
    public static (string ClassPath, float ZOffset, bool GameplayActor)? For(string? itemName) {
        if (itemName == null) return null;

        foreach (var row in Rows) {
            if (!row.Item.Equals(itemName, StringComparison.OrdinalIgnoreCase)) continue;
            return (row.ClassPath, row.ZOffset, row.GameplayActor);
        }

        return null;
    }

    private static readonly (string Item, string ClassPath, float ZOffset, bool GameplayActor)[] Rows = {
        // The snowman a player hides in. A BuildingProp, so it is an ordinary building piece as far
        // as the wire is concerned.
        ("Athena_SneakySnowman",
         "/Game/Athena/Items/Consumables/SneakySnowman/Athena_Prop_SneakySnowman.Athena_Prop_SneakySnowman_C",
         0f, false),

        // The shield bubble's core - the dome itself is the Blueprint's, spawned by the client from
        // this class.
        ("Athena_SilverBlazer_V2",
         "/Game/Athena/Items/Consumables/SilverBlazer/BGA_Athena_SilverBlazerCore.BGA_Athena_SilverBlazerCore_C",
         0f, true),

        // The firework mortar's emplacement, which then fires on its own timer. PlacementZOffset is
        // the projectile's own property, and it is 5 rather than 0 so the mesh does not z-fight with
        // whatever it landed on.
        ("Athena_FireworksMortar",
         "/Game/Athena/Items/Consumables/FireworksMortar/B_BGA_FireworksMortar_Holder.B_BGA_FireworksMortar_Holder_C",
         5f, true),

        // THE AIR STRIKE, whose name gives nothing away: the item is `Athena_AppleSauce`. Its
        // projectile does NOT explode - it spawns a marker zone and a rocket spawner and destroys
        // itself, and the bombardment is the spawner's, arriving over the next several seconds. This
        // row is the spawner; the damage is FortAirstrike's, because a Blueprint's timer is not
        // something this server can run.
        ("Athena_AppleSauce",
         "/Game/Athena/Items/Consumables/AirStrike/B_BGA_AppleSauce_RocketSpawner.B_BGA_AppleSauce_RocketSpawner_C",
         0f, true)
    };
}
