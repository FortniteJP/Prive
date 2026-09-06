namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     A thrown or fired projectile - the actor `Server_SpawnProjectile` asks this server to make.
///
///     THE CLIENT FLIES IT, NOT THIS SERVER. Every projectile Blueprint (B_Prj_Athena_FragGrenade_C
///     and its siblings) carries its own UFortProjectileMovementComponent with the speed, gravity
///     scale, bounciness and fuse already authored into it, plus a ReceiveBeginPlay that starts the
///     fuse and an OnExploded that plays the explosion. On a simulated proxy all of that runs
///     client-side. So the only thing that has to be right on the wire is the SPAWN: the class, the
///     location, the rotation, and the velocity - and the last one is why
///     UPackageMapClient.SerializeNewActor grew its bSerializeVelocity branch. Without a velocity the
///     client draws the grenade appearing at the muzzle and falling straight down.
///
///     Derived from AActor rather than anything more specific for the same reason
///     AFortAthenaVehicle is (see its note): AFortProjectileBase's own handles are not modelled, and
///     handing the client a layout that belongs to a different class is the exact shape of failure
///     that kills a connection with nothing logged. The `_ =>` arm of NativeRepLayouts.Get - Role and
///     RemoteRole - is all this sends, and for a self-simulating projectile that is enough.
///
///     WHAT IS STILL MISSING is the server half: this does not simulate the arc, does not know where
///     the grenade lands, and therefore does no damage. The numbers to do it with are already read
///     out of the paks - GrenadeSpeedMin/Max 4000, GravityScale 0.8 on the ability, and
///     DmgPB 100 / EnvDmgPB 375 / ImpactDmgPB 400 on the item's UtilityItemDamage row - so what is
///     left is the ballistic integration and a radial-damage pass over the building actors, not more
///     reverse engineering.
/// </summary>
public class AFortProjectileBase : AActor {
    /// <summary>
    ///     ALWAYS RELEVANT, deliberately, and unlike almost everything else here.
    ///
    ///     Distance culling measures from a position that is sampled once, when the channel opens;
    ///     that is fine for a chest and wrong for something crossing 50 metres in a second. A
    ///     projectile is also short-lived and there are never many at once, so the cost of skipping
    ///     the cull is a handful of channels for a few seconds. Revisit if projectiles ever become
    ///     numerous (a minigun would).
    /// </summary>
    public AFortProjectileBase() {
        bAlwaysRelevant = true;
    }

    /// <summary>
    ///     WHICH ITEM THREW THIS - server-side only, never replicated.
    ///
    ///     A projectile's damage, and eventually everything else about what it does, belongs to the
    ///     ITEM rather than to the projectile class: several items share one projectile Blueprint and
    ///     several projectile Blueprints share one damage row. The item name is what both tables are
    ///     keyed by, and it is known at spawn (the thrower's held weapon) and needed at explosion,
    ///     which is a whole fuse later.
    /// </summary>
    public string? SourceItemName { get; set; }

    /// <summary>
    ///     AFortGameplayEffectDeliveryActor::bHasExploded - wire handle 16, and the entire mechanism
    ///     by which a grenade goes off.
    ///
    ///     THE CLIENT DOES NOT DECIDE THIS. The projectile Blueprint's own graph arms only an audio
    ///     warning at FuseTime/2; the bang itself comes from this property's OnRep raising the
    ///     OnExploded event, which the Blueprint answers with its gameplay cue (the disassembly's
    ///     ExecuteGameplayCueLocal). A projectile that is spawned and never has this set flies
    ///     perfectly, lands, and then just sits there - which is exactly what the first live throw
    ///     did, and why "it flies but does not explode" was a server bug rather than a missing asset.
    /// </summary>
    public bool bHasExploded { get; set; }

    /// <summary>
    ///     AFortGameplayEffectDeliveryActor::bIsBeingKilled - wire handle 17, the sibling of
    ///     bHasExploded and the other half of how a real one goes away.
    ///
    ///     The real flow is explode -> Kill() -> bIsBeingKilled replicates -> LifespanAfterKill ->
    ///     destroyed; bKillOnExplode is False on the grenade base, so the explosion alone does NOT
    ///     remove it. Sending only bHasExploded left the client with an exploded grenade that was
    ///     still, as far as it was concerned, a live projectile - which is what its own log showed at
    ///     the moment it refused a second throw: the first grenade still replicating and still being
    ///     simulated, just at rest.
    /// </summary>
    public bool bIsBeingKilled { get; set; }

    /// <summary>
    ///     When the fuse runs out, in world seconds. FuseTime is read from the projectile Blueprint
    ///     (B_Prj_Athena_Grenade_Base carries 2.75), not chosen here.
    /// </summary>
    public float ExplodesAtWorldTime { get; set; }

    /// <summary>
    ///     When this projectile should stop existing on the server, in world seconds.
    ///
    ///     Deliberately well after the explosion. bHasExploded has to REACH the client before the
    ///     actor is destroyed: destroying it first would close the channel with the property still
    ///     unsent, and the client would see the grenade vanish silently instead of explode. The gap
    ///     also covers the explosion animation, which is client-side and outlives the property change.
    /// </summary>
    public float ExpiresAtWorldTime { get; set; }

    /// <summary>
    ///     How many surfaces this has bounced off. The grenade base explodes after 5
    ///     (NumberOfBouncesTillExplode), which is why a grenade thrown into a corner goes off early.
    /// </summary>
    public int BounceCount { get; set; }

    /// <summary>How many build contacts this one has already reported - see FortProjectileSystem.</summary>
    public int LoggedBounces { get; set; }

    /// <summary>
    ///     Where the SERVER thinks this is - deliberately separate from the replicated transform,
    ///     which is never updated after the spawn header.
    ///
    ///     The client flies its own copy and is the one the player sees; this parallel simulation
    ///     exists only so the server can decide WHERE the explosion happens and what is near it. The
    ///     two will drift - the server collides against the baked landscape only, so a grenade the
    ///     client bounces off a wall or a roof carries straight on here. Sending this back as
    ///     ReplicatedMovement would fight the client's own simulation and make the flight stutter,
    ///     so it stays server-side.
    /// </summary>
    public FVector SimulatedLocation { get; set; } = new();

    /// <summary>
    ///     A floor for the server's simulation: the Z the thrower was standing at.
    ///
    ///     TerrainHeightMap only bakes the LANDSCAPE, so anything a player can stand on that is not
    ///     landscape - the warmup island, a placed building, a rock, a POI floor - is invisible to
    ///     the flight simulation and a grenade falls straight through it. That is not hypothetical:
    ///     a grenade thrown straight down on the warmup island fell 13,750 units and "exploded"
    ///     nearly ten thousand units below the player.
    ///
    ///     The thrower is DEMONSTRABLY standing on something solid at this height, so it is the one
    ///     piece of collision information the server can be sure of. It is a stand-in for real
    ///     geometry, not a model of it: a grenade thrown off a cliff will stop level with the
    ///     cliff-top instead of falling to the valley. Wrong, but bounded - and much less wrong than
    ///     the alternative.
    /// </summary>
    public float ThrowerGroundZ { get; set; } = float.NegativeInfinity;
}
