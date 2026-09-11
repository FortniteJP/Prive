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
///     ONE ITEM CAN LEAVE MORE THAN ONE ACTOR, and the shield bubble is why. Deploying only
///     `BGA_Athena_SilverBlazerCore_C` produced the deploy SOUND and nothing else, which read like a
///     replication failure and was not: THE CORE IS THE DEVICE, NOT THE SHIELD. Its own
///     ReceiveBeginPlay does a deferred spawn of `B_BGA_Athena_SilverBlazer_V2_C` at its own
///     location (zero rotation, unit scale), stores it in the replicated `SpawnedBGA`, attaches its
///     own mesh to that actor's root and binds `Die` to its `OnDied`. A server that cannot run the
///     core's Blueprint has to spawn the dome itself, or there is no bubble.
///
///     The dome then needs nothing further from the server: its BeginPlay reads `LifespanTime`, sets
///     its own end timer, and starts the `ScaleSafeZone` timeline that inflates
///     `SM_SafeZone_HighPoly` to `MaxScale` over `ScaleDuration`. All client-side and self-driven -
///     which is the same shape as the air strike's rocket and worth checking FIRST the next time
///     something arrives at the client and is not drawn: the visual may belong to an actor nobody
///     spawned, or to a timeline nobody started.
///
///     WHICH SIDE OF THE BUILDING FORK EACH ONE IS ON DECIDES THE REP LAYOUT, and getting it wrong
///     closes the connection rather than looking odd - see AFortDeployedActor. Checked with
///     `pakreader supers`:
///
///         Athena_Prop_SneakySnowman_C     -> BuildingProp -> ... -> ABuildingSMActor
///         BGA_Athena_SilverBlazerCore_C   -> BGA_Athena_WithGravity_Parent_C -> ABuildingGameplayActor
///         B_BGA_Athena_SilverBlazer_V2_C  -> ABuildingGameplayActor
///         B_BGA_FireworksMortar_Holder_C  -> ABuildingGameplayActor
///
///     THE ZAPPER TRAP IS NOT HERE and that is a finding rather than an omission: its projectile
///     (`B_Prj_Athena_TrapGrenade_ZippyTrout`) has no event graph at all and names a
///     `TrapDefinition` (`TID_ZippyTroutTrap_Context`) instead. It PLACES A TRAP, which is the
///     building system's job and not this one's.
/// </summary>
internal static class FortDeployables {
    /// <summary>
    ///     One actor an item leaves behind.
    ///
    ///     <c>GameplayActor</c> says which fork the class is on: true means ABuildingGameplayActor
    ///     and therefore ABuildingActor's handles ONLY. <c>Lifespan</c> is how long the SERVER keeps
    ///     it, 0 meaning forever - the client runs its own end timer out of the Blueprint, so this
    ///     is only about not leaving an actor on the wire after it has visibly gone.
    /// </summary>
    public readonly record struct FDeployed(string ClassPath, float ZOffset, bool GameplayActor,
                                            float Lifespan) {
        /// <summary>Takes no damage - see AFortDeployedActor.Indestructible.</summary>
        public bool Indestructible { get; init; }

        /// <summary>
        ///     Removed when the row's FIRST actor is, however that one goes - see
        ///     AFortDeployedActor.GoesDownWith. Only meaningful on the second and later entries.
        /// </summary>
        public bool GoesWithFirst { get; init; }

        /// <summary>
        ///     The ACTOR scale the real spawner uses - 1 unless its transform says otherwise. It
        ///     reaches the client through the spawn header's scale field, which multiplies every
        ///     component the class builds.
        /// </summary>
        public float Scale { get; init; } = 1f;
    }

    /// <summary>
    ///     Everything this item leaves at its landing point - empty when it just explodes. In the
    ///     order the real thing creates them, which for the shield bubble means the core before the
    ///     dome the core would itself have spawned.
    /// </summary>
    public static FDeployed[] For(string? itemName) {
        if (itemName == null) return Array.Empty<FDeployed>();

        foreach (var row in Rows) {
            if (!row.Item.Equals(itemName, StringComparison.OrdinalIgnoreCase)) continue;
            return row.Deployed;
        }

        return Array.Empty<FDeployed>();
    }

    /// <summary>
    ///     How an item's projectile decides WHERE it deploys, when that is not "the first thing it
    ///     touches".
    ///
    ///     <c>MinFloorNormalZ</c>: it only comes to rest on a surface whose normal points up by more
    ///     than this; anything steeper it bounces off and keeps flying. <c>FlightYawOffset</c>: the
    ///     deployed actor faces the direction the projectile was travelling, plus this, rather than
    ///     the way the thrower was looking.
    /// </summary>
    public readonly record struct FDeployRule(float? MinFloorNormalZ, float? FlightYawOffset);

    /// <summary>The item's deploy rule, or the default (first contact, thrower's yaw).</summary>
    public static FDeployRule RuleFor(string? itemName) =>
        itemName != null && Rules.TryGetValue(itemName, out var rule) ? rule : default;

    private static readonly Dictionary<string, FDeployRule> Rules = new(StringComparer.OrdinalIgnoreCase) {
        // THE SNOWMAN BOUNCES OFF WALLS, and that is its own Blueprint, not a preference: its
        // projectile's `Should Bounce?` stops the movement component only when
        // Dot(HitNormal, UpVector) > 0.65 and lets it bounce otherwise. Arming on ANY contact - what
        // this server did - put a snowman on the spot where it met a wall, its body half inside the
        // wall. The spawn itself is MakeTransform(Hit.Location, MakeRotator(0, 0, ActorYaw - 90),
        // (1,1,1)): the projectile's own heading, turned a quarter.
        ["Athena_SneakySnowman"] = new(MinFloorNormalZ: 0.65f, FlightYawOffset: -90f)
    };

    /// <summary>
    ///     `Default.SilverBlazer.1` - how long the bubble lasts. The other three rows of that family
    ///     are MaxScale (`.2` = 20), InitialVelocitySlow (`.3` = 0.4) and ScaleDuration (`.4` = 0.5),
    ///     and all three are the CLIENT's business: the dome reads them off the same curve table
    ///     itself. This one is here so the server stops replicating an actor the client has already
    ///     ended, not so it can decide the duration.
    /// </summary>
    private const float ShieldLifespan = 30f;

    private static readonly (string Item, FDeployed[] Deployed)[] Rows = {
        // The snowman a player hides in. A BuildingProp, so it is an ordinary building piece as far
        // as the wire is concerned.
        ("Athena_SneakySnowman", new FDeployed[] {
            new("/Game/Athena/Items/Consumables/SneakySnowman/Athena_Prop_SneakySnowman.Athena_Prop_SneakySnowman_C",
                0f, false, 0f)
        }),

        // THE SHIELD BUBBLE IS TWO ACTORS - see the class note. The core is the device that lands;
        // the dome is the shield.
        ("Athena_SilverBlazer_V2", new FDeployed[] {
            //
            // AT 0.4 SCALE, which is the whole reason the device looked two and a half times too
            // big. B_Prj_Athena_SilverBlazer_V2 spawns it with
            // MakeTransform(Pos, MakeRotFromZ(HitNormal), (0.4, 0.4, 0.4)); the device mesh
            // S_SilverBlazer is authored at RelativeScale3D 6 on a child of the inherited StaticMesh
            // sphere, so what a player sees is 0.4 x 1 x 6 = 2.4 - and at actor scale 1 it was 6.
            new("/Game/Athena/Items/Consumables/SilverBlazer/BGA_Athena_SilverBlazerCore.BGA_Athena_SilverBlazerCore_C",
                0f, true, ShieldLifespan) { Scale = 0.4f },

            // At the core's own location, because that is literally what the core does:
            // MakeTransform(BreakTransform(GetTransform()).Location, ZeroRotator, (1,1,1)) - so the
            // dome stays at scale 1 even though the core it came from is 0.4.
            //
            // Indestructible (`BuildingActor.NonDestructable`) and bound to the core: breaking the
            // DEVICE is how a shield bubble is beaten, and it is what the core's own `Destroyed`
            // event does to its SpawnedBGA.
            new("/Game/Athena/Items/Consumables/SilverBlazer/B_BGA_Athena_SilverBlazer_V2.B_BGA_Athena_SilverBlazer_V2_C",
                0f, true, ShieldLifespan) { Indestructible = true, GoesWithFirst = true }
        }),

        // The firework mortar's emplacement, which then fires on its own timer. PlacementZOffset is
        // the projectile's own property, and it is 5 rather than 0 so the mesh does not z-fight with
        // whatever it landed on.
        ("Athena_FireworksMortar", new FDeployed[] {
            new("/Game/Athena/Items/Consumables/FireworksMortar/B_BGA_FireworksMortar_Holder.B_BGA_FireworksMortar_Holder_C",
                5f, true, 0f)
        }),

        // THE AIR STRIKE, whose name gives nothing away: the item is `Athena_AppleSauce`. Its
        // projectile does NOT explode - it spawns a marker zone and a rocket spawner and destroys
        // itself, and the bombardment is the spawner's, arriving over the next several seconds. This
        // row is the spawner; the damage is FortAirstrike's, because a Blueprint's timer is not
        // something this server can run.
        //
        // 20 s is the spawner's own InitialLifeSpan, and the strike is over well before it.
        ("Athena_AppleSauce", new FDeployed[] {
            new("/Game/Athena/Items/Consumables/AirStrike/B_BGA_AppleSauce_RocketSpawner.B_BGA_AppleSauce_RocketSpawner_C",
                0f, true, 20f)
        })
    };
}
