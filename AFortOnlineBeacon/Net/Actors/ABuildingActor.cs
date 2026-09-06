namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Server-side stand-in for a placed building piece (real Fortnite's ABuildingSMActor). Spawned
///     from ServerCreateBuildingActor instead of a bare AActor so a hit against it can carry health
///     and, on depletion, feed BuildingStructuralSupportSystem's destruction cascade (building
///     placement itself was already complete and confirmed before this work started).
///
///     Health is not server-side bookkeeping any more, and it travels TWO ways, because real
///     Fortnite has two:
///
///       * ABuildingActor::ReplicatedBuildingAttributeSet (handle 19) - a full GAS attribute set,
///         sent as a sub-object by UActorChannel.ReplicateBuildingAttributeSet. This is the
///         Save-The-World-shaped path, and on its own it produced a health bar that only refreshed
///         when the player looked away and back: the value arrives, but nothing tells the UI.
///       * ABuildingSMActor::MinimalReplicationProxy (handles 59-62) - a compact Health/MaxHealth
///         int16 pair that is Net + RepNotify on the ACTOR itself. This is what Battle Royale
///         actually uses (a full attribute set per piece would be absurd at a thousand builds a
///         match) and, being a RepNotify, it is what makes a health bar update live.
///
///     Destruction and placement are likewise not just "the actor appeared/vanished":
///     bDestroyed (24) says the piece was destroyed rather than merely going out of relevance, and
///     BuildingAnimation (57, EBuildingAnim) together with bUnderConstruction (54) /
///     bIsInitiallyBuilding (56) is what drives the client's build-in and destruction animations.
///     Without those a piece pops into existence at full strength and pops back out again.
///
///     CurrentHitPoints/MaxHitPoints stay the authority and everything above is their mirror,
///     rather than the other way round: the cascade, the damage path and the build-in ramp all work
///     in whole hit points, and only the wire needs floats and int16s.
/// </summary>
public class ABuildingActor : AActor {
    /// <summary>
    ///     A DELIBERATELY LARGER cull radius than the engine default, and the number is a chosen
    ///     safety margin rather than anything derived - say so plainly. AActor's 15000 units (150 m)
    ///     is right for a small object on the ground; a build is a large, shootable, load-bearing
    ///     thing seen across a valley, and the same class also carries destructible world scenery,
    ///     which a player can shoot from further away than they can walk. 40000 units (400 m) keeps
    ///     the win where the counts actually are (2895 floor-loot pickups, see AFortPickup) while
    ///     leaving structures alone.
    ///
    ///     BUILDING_CULL_DISTANCE overrides it in UNITS, not squared. NET_CULL=0 disables culling
    ///     entirely. AFortPickupAthena's and ABuildingSMActor's REAL NetCullDistanceSquared values
    ///     are in their native CDOs and could be read out of the memory dump
    ///     (see [[re-and-capture-techniques]]) - that has not been done, and doing it would retire
    ///     both of these guesses at once.
    /// </summary>
    public ABuildingActor() =>
        NetCullDistanceSquared =
            float.TryParse(Environment.GetEnvironmentVariable("BUILDING_CULL_DISTANCE"), out var units) && units > 0
                ? units * units
                : 40000f * 40000f;

    public EBuildingMaterial Material { get; private set; } = EBuildingMaterial.Unknown;
    public EFortBuildingType BuildingType { get; private set; } = EFortBuildingType.None;

    public int MaxHitPoints { get; private set; } = DefaultHitPoints;
    public int CurrentHitPoints { get; private set; } = DefaultHitPoints;

    /// <summary>
    ///     ABuildingActor::ReplicatedBuildingAttributeSet (wire handle 19) - where the health a
    ///     client draws its health bar from actually lives. Created here rather than by the client,
    ///     because the real property is Transient/RepNotify and the real server makes it at runtime;
    ///     UActorChannel.ReplicateBuildingAttributeSet is what puts it on the wire. Kept in step with
    ///     CurrentHitPoints/MaxHitPoints by <see cref="SyncAttributeSet"/> - those two ints stay the
    ///     server-side authority, and this is their wire mirror.
    /// </summary>
    public UFortBuildingActorSet? BuildingAttributeSet { get; private set; }

    /// <summary>
    ///     ABuildingActor::ReplicatedAbilitySystemComponent (wire handle 20), and the missing half of
    ///     the health bar. The attribute set on its own carries the VALUE but notifies nothing: a
    ///     GAS attribute's OnRep runs GAMEPLAYATTRIBUTE_REPNOTIFY, which calls
    ///     GetOwningAbilitySystemComponent()->SetBaseAttributeValueFromReplication(), and that is
    ///     what fires the attribute-changed delegates a health bar binds to. With no ASC on the
    ///     building that call has nothing to go through, so the number is correct whenever something
    ///     re-reads it and never updates on its own - which is exactly how it behaved in-game.
    ///
    ///     Same failure this project already hit once from the other direction: an empty
    ///     SpawnedAttributes made GetNumericAttribute read every attribute as zero and left walk
    ///     speed clamped at 1 uu/s. See UFortAttributeSet.
    /// </summary>
    public UFortAbilitySystemComponent? AbilitySystemComponent { get; private set; }

    /// <summary>
    ///     ABuildingActor::bDestroyed (wire handle 24) - Net + RepNotify on the real class. Closing
    ///     the channel already removes the piece from every client, but it does so indistinguishably
    ///     from the piece merely going out of relevance; this is the flag that says it was actually
    ///     destroyed, and it is what the client's own OnRep has to see to treat it as one. Set by
    ///     <see cref="MarkDestroyed"/>, which deliberately does NOT destroy the actor in the same
    ///     breath - see there.
    /// </summary>
    public bool bDestroyed { get; private set; }

    /// <summary>ABuildingActor::bPlayerPlaced (wire handle 25). Everything this server spawns through ServerCreateBuildingActor is, by definition, player-placed.</summary>
    public bool bPlayerPlaced { get; private set; } = true;

    /// <summary>
    ///     Flips bPlayerPlaced off for the stand-in NativeRpcHandlers.DamageLevelActor builds for a
    ///     piece of MAP geometry (a tree, a rock - never something ServerCreateBuildingActor spawned).
    ///     Not just cosmetic: NativeRpcHandlers' hit dispatch keys the player-building damage/cascade
    ///     path on bPlayerPlaced:true specifically so a resolved hit on one of these stand-ins - which
    ///     starts happening the moment it has a NetGUID - keeps landing in DamageLevelActor instead.
    ///     Routing a stand-in through BuildingStructuralSupportSystem's cascade crashes: its outer
    ///     chain is UAssetRegistry's synthetic Package/World/Level objects, none of which carry a
    ///     UClass, and AActor.Destroy walks it via GetTypedOuter/IsA.
    /// </summary>
    public void MarkAsLevelActor() => bPlayerPlaced = false;

    /// <summary>
    ///     ABuildingActor::bMirrored (wire handle 45) - whether this piece's mesh is mirrored, e.g. a
    ///     half-wall or stair whose open side depends on which way the player faced the edit pattern.
    ///     Was `Reserved` (declared, never sent) until Round 44: ServerEditBuildingActor's own
    ///     `bMirrored` parameter is exactly what a real server would forward here, and until it was
    ///     wired in every edited piece silently ignored the client's mirror choice. Set at construction
    ///     time only (<see cref="SetMirrored"/>) - real Fortnite never flips an already-placed piece.
    /// </summary>
    public bool bMirrored { get; private set; }

    /// <summary>
    ///     What real `ABuildingSMActor::SetMirrored` does, and it is not just storing the flag:
    ///     disassembled from the client dump (static 0x1413D3710, reached through the CDO's vtable at
    ///     +0xA20 since the exec thunk only forwards to it), the whole body is "force the SIGN of the
    ///     root component's RelativeScale3D.X" - negative when mirrored, positive when not, magnitude
    ///     untouched - and then re-register the component.
    ///
    ///     That matters because `bMirrored` has NO OnRep. Replicating the bool tells a client the
    ///     value and nothing else; it will never flip a mesh on its own. The negative scale, carried in
    ///     the actor's spawn bunch (see UPackageMapClient.SerializeNewActor), is the entire mechanism
    ///     by which a mirrored piece looks mirrored. Round 44 wired up the bool alone, which is why
    ///     asymmetric edit pieces - half-stairs above all - kept arriving unmirrored and reading as a
    ///     rotation that would not take.
    ///
    ///     Set at construction time only - real Fortnite never flips an already-placed piece.
    /// </summary>
    public void SetMirrored(bool mirrored) {
        bMirrored = mirrored;

        var scale = GetActorScale3D();
        SetActorScale3D(new FVector {
            X = mirrored ? -MathF.Abs(scale.X) : MathF.Abs(scale.X),
            Y = scale.Y,
            Z = scale.Z
        });
    }

    /// <summary>
    ///     The pivot location and yaw this piece's SLOT was ORIGINALLY placed at
    ///     (RotationIterations=0), carried forward UNCHANGED through every later edit rather than
    ///     recomputed from whatever the immediately-preceding edit left behind.
    ///
    ///     Round 50/51's edit-rotation math computed each edit's yaw/position relative to the piece's
    ///     CURRENT (already-edited) state - a live test showed exactly what that does: toggling a
    ///     floor between its plain shape and a corner pattern walked the piece further off its
    ///     original tile with every single edit, never returning to where it started even when the
    ///     net rotation should have been zero. Real Fortnite's RotationIterations is therefore an
    ///     ABSOLUTE index (which of the pattern's up-to-four fixed orientations is selected), not a
    ///     relative delta from the last confirmed edit - so every edit has to compute its target
    ///     yaw/position from the same FIXED reference, not from the last result. See
    ///     NativeRpcHandlers.ServerEditBuildingActor and ServerCreateBuildingActor for where this gets
    ///     set (once, at first placement) and carried forward (every edit, unchanged).
    /// </summary>
    public FVector AnchorLocation { get; private set; }

    public float AnchorYaw { get; private set; }

    /// <summary>
    ///     The piece's blueprint class name - the last segment of its class path, e.g.
    ///     "PBWA_W1_Solid_C". Kept because it is the key into both generated tables
    ///     (FortBuildingAttributes and FortBuildingConnectivity) and re-deriving it from the path on
    ///     every structural query would be wasteful. Empty for a stand-in that was never initialised
    ///     from a class, such as the ones DamageLevelActor builds for map scenery.
    /// </summary>
    public string ClassName { get; private set; } = string.Empty;

    public void SetAnchor(FVector location, float yaw) {
        AnchorLocation = location;
        AnchorYaw = yaw;
    }

    /// <summary>
    ///     Forces BuildingType rather than trusting InitializeFromClass's own name-substring guess
    ///     (BuildingTypeFromClassPath) for an EDITED piece. That guess is right for every class a
    ///     piece is originally PLACED as (the four base BuildingActorClassTable paths all literally
    ///     contain "Floor"/"Wall"/etc.), but an edited variant's class name does not reliably follow
    ///     the same convention - "PBWA_W1_BalconyI" (a floor's raise-one-corner pattern) contains
    ///     neither "Floor" nor anything else the heuristic recognises, so it silently classified as
    ///     None. Chaining through Round 51's oldBuilding.BuildingType fix inherited that wrong
    ///     classification on every SUBSEQUENT edit of the same piece, which is what broke a
    ///     Floor-to-Balcony-back-to-Floor round trip: the return edit read BalconyI's own (wrong) type
    ///     and skipped rotation entirely. See NativeRpcHandlers.ServerEditBuildingActor, which calls
    ///     this with the OLD piece's type immediately after every edit-spawn.
    /// </summary>
    public void OverrideBuildingType(EFortBuildingType type) => BuildingType = type;

    /// <summary>
    ///     ABuildingSMActor::EditingPlayer (wire handle 64) - who currently has this piece locked
    ///     into the edit tool, or null. Set by ServerBeginEditingBuildingActor, cleared by
    ///     ServerEditBuildingActor/ServerEndEditingBuildingActor - see NativeRpcHandlers. Real UE
    ///     also nudges NetDormancy when this changes (SetEditingPlayer's own ForceNetUpdate, per
    ///     Project-Reboot-3.0's BuildingSMActor.h); this project always replicates every tick, so
    ///     that part has nothing to add.
    /// </summary>
    public APlayerState? EditingPlayer { get; private set; }

    public void SetEditingPlayer(APlayerState? player) => EditingPlayer = player;

    private float? _firstHitAt;
    private bool _weakSpotSpawned;

    /// <summary>
    ///     True the FIRST time this is called at least <paramref name="delaySeconds"/> after this
    ///     piece's first recorded hit - i.e. "is it time to reveal a weak spot on this piece". Only
    ///     ever checked from a hit (there is no independent per-actor tick for a level-actor
    ///     stand-in), so the marker actually appears on whichever hit happens to land AFTER the delay
    ///     has elapsed, not proactively while the player is doing something else - an accepted
    ///     approximation of Fortnite's own timed reveal, not the real mechanism. See
    ///     NativeRpcHandlers.DamageLevelActor for the caller and NativeRpcHandlers.WeakSpotRevealDelaySeconds
    ///     for how well-founded that delay is (not at all - it is a guess).
    /// </summary>
    public bool ShouldSpawnWeakSpot(float timeSeconds, float delaySeconds) {
        if (_weakSpotSpawned) return false;

        _firstHitAt ??= timeSeconds;
        if (timeSeconds - _firstHitAt < delaySeconds) return false;

        _weakSpotSpawned = true;
        return true;
    }

    /// <summary>
    ///     Clears the "already spawned" latch so <see cref="ShouldSpawnWeakSpot"/> can fire again -
    ///     real Fortnite relocates the weak spot to a new position once the current one is actually
    ///     hit, rather than leaving one marker up for the piece's whole life. Called from
    ///     NativeRpcHandlers.DamageLevelActor whenever IsWeakspotHit(hit) is true for the hit just
    ///     processed, i.e. the CURRENT marker was the one just struck. Resets `_firstHitAt` too, so
    ///     the reveal delay restarts from this hit exactly like the very first one did.
    /// </summary>
    public void ResetWeakSpot() {
        _weakSpotSpawned = false;
        _firstHitAt = null;
    }

    /// <summary>
    ///     ABuildingSMActor::BuildingAnimation (wire handle 57) - Net + RepNotify, and the single
    ///     switch that drives the client's build-in and destruction animations. Nothing else on the
    ///     actor asks for them: without this a piece simply pops into existence and later pops out.
    /// </summary>
    public EBuildingAnim BuildingAnimation { get; private set; } = EBuildingAnim.EBA_None;

    /// <summary>ABuildingSMActor::bUnderConstruction (wire handle 54), Net + RepNotify - true while the build-in animation runs.</summary>
    public bool bUnderConstruction { get; private set; }

    /// <summary>ABuildingSMActor::bIsInitiallyBuilding (wire handle 56), Net - set for a piece that is building for the FIRST time, as opposed to being repaired or upgraded.</summary>
    public bool bIsInitiallyBuilding { get; private set; }

    /// <summary>
    ///     ABuildingSMActor::MinimalReplicationProxy.BuildTime (wire handle 59) - how long the client
    ///     should run the build-in animation for. The server owns this number, so the client's
    ///     animation and the server's health ramp below finish together.
    ///
    ///     THIS IS THE ANIMATION LENGTH, NOT THE HARDEN TIME - and conflating the two broke building
    ///     badly enough to be worth spelling out. FortBuildingActorSet.BuildTime in the shipped data
    ///     is wood 4 s / stone 12 s / metal 25 s; feeding those numbers to handle 59 did not give a
    ///     long build-in animation, it made the animation stop happening at all and the piece appear
    ///     finished immediately. Whatever the client does with this value, a 25 is outside the range
    ///     it will act on. The shipped per-class figure now drives <see cref="HardenTime"/> instead,
    ///     which never touches the wire.
    /// </summary>
    public float BuildTime { get; private set; } = BuildInDurationDefault;

    /// <summary>
    ///     FortBuildingActorSet::RepairTime - wire handle 60, and the repair half of what handle 59
    ///     is for a placement. See where it is assigned.
    /// </summary>
    public float RepairTime { get; private set; } = BuildInDurationDefault;

    /// <summary>
    ///     FALLBACK build time only, for a class FortBuildingAttributes has no row for. A piece the
    ///     table knows gets its real per-class value, and the replicated <see cref="BuildTime" />
    ///     (handle 59) is set to the same number the health ramp uses - see where HardenTime is
    ///     assigned. BUILD_IN_TIME overrides this fallback.
    ///
    ///     0.5 was never a measured build time: it was the value a client was observed to accept
    ///     back when the real table had not been resolved, and it stayed as the REPLICATED value for
    ///     every piece long after the real one was known.
    /// </summary>
    private static readonly float BuildInDurationDefault = Env("BUILD_IN_TIME", 0.5f);

    /// <summary>
    ///     How long the piece takes to reach full strength - a SERVER-SIDE number, never replicated.
    ///     Real, per class, out of FortBuildingActorSet.BuildTime: wood 4 s, stone 12 s, metal 25 s.
    ///     A placed wall starts at BuildInStartHealthPct of its health (wood 0.6, stone 0.333, metal
    ///     0.22) and climbs to full over this window, which is why a metal wall thrown up in a fight
    ///     is worth far less than its 500 HP for the first several seconds. The two shipped tables
    ///     agree with each other - the material that starts weakest is the one that takes longest -
    ///     which is what says they are one mechanic and not two unrelated numbers.
    ///
    ///     The client is told about it only through the health it sees climbing, which is exactly how
    ///     it learns about damage too, so nothing here depends on a wire format being right.
    /// </summary>
    public float HardenTime { get; private set; } = BuildInDurationDefault;

    /// <summary>Overrides the per-class harden time when >= 0, the same arrangement BUILD_IN_START_HEALTH_PCT uses.</summary>
    private static readonly float HardenTimeOverride = Env("BUILD_HARDEN_TIME", -1f);

    /// <summary>
    ///     Overrides the real per-material figure when set; unset, each material uses its own
    ///     Default.BuildingInitialHealthPercent_* from the shipped game data (wood 0.6, stone 0.333,
    ///     metal 0.22) rather than the single 0.15 this used for everything.
    /// </summary>
    private static readonly float BuildInStartHealthPctOverride = Env("BUILD_IN_START_HEALTH_PCT", -1f);

    private float BuildInStartHealthPct => BuildInStartHealthPctOverride >= 0f
        ? BuildInStartHealthPctOverride
        : FortBuildingAttributes.InitialHealthPercent(Material);

    private static float Env(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    /// <summary>
    ///     ABuildingSMActor::ProxyGameplayCueDamagePhysical's magnitude (wire handle 67). The struct
    ///     is a "proxy" in the GAS sense: the standard UE pattern for telling simulated proxies to
    ///     run a GameplayCue without replicating a whole GameplayEffect, and it has its own
    ///     OnRep_ProxyGameplayCueDamagePhysical on the client. Its sibling member EffectContext
    ///     (handle 68) is an FGameplayEffectContextHandle whose format is not decoded, so it is
    ///     deliberately never written - skipping a handle is always safe, sending one at the wrong
    ///     width is not (see ERepPropertyKind.QuantizedBuildingAttribute for what that cost).
    /// </summary>
    public float DamageMagnitude { get; private set; }

    private float _constructionEndsAt;
    private float _constructionStartedAt;
    private int _constructionStartHealth;
    private int _constructionTargetHealth;
    private float _breakingUntil;

    /// <summary>
    ///     How long the EBA_Breaking pulse below lasts. Long enough for the change to reach a client
    ///     at any plausible update rate, short enough that the piece is not left sitting in a damage
    ///     animation between hits.
    /// </summary>
    private const float BreakingPulseSeconds = 0.35f;

    /// <summary>
    ///     Reports a non-fatal hit to the client. Two independent signals, because which one the
    ///     client actually reacts to is not yet established:
    ///
    ///       * BuildingAnimation = EBA_Breaking. This rides handle 57, the ONE building property
    ///         already proven to reach the client and produce a visible result - the same handle that
    ///         carries EBA_Destruction, which works in-game.
    ///       * DamageMagnitude, i.e. the damage cue proper (handle 67).
    ///
    ///     Deliberately a PULSE, reverted by <see cref="TickDamageState"/>: a health value that
    ///     happens to repeat produces no property change and therefore no push and no RepNotify, and
    ///     this server currently deals a flat 40 per hit (see NativeRpcHandlers.BuildingDamagePerHit),
    ///     so every hit after the first would otherwise be silent. Cycling None -> Breaking -> None
    ///     guarantees a change per hit regardless of the numbers.
    /// </summary>
    public void OnDamaged(int magnitude, float timeSeconds) {
        if (bDestroyed || bUnderConstruction) return;

        DamageMagnitude = magnitude;
        BuildingAnimation = EBuildingAnim.EBA_Breaking;
        _breakingUntil = timeSeconds + BreakingPulseSeconds;
    }

    /// <summary>Ends the EBA_Breaking pulse. Returns true while one is still running.</summary>
    public bool TickDamageState(float timeSeconds) {
        if (_breakingUntil <= 0f) return false;
        if (timeSeconds < _breakingUntil) return true;

        _breakingUntil = 0f;
        if (BuildingAnimation == EBuildingAnim.EBA_Breaking) BuildingAnimation = EBuildingAnim.EBA_None;
        return false;
    }

    /// <summary>
    ///     Starts the build-in: flags the piece as constructing so the client plays EBA_Building, and
    ///     drops its health to the starting fraction so the ramp below has somewhere to climb from.
    ///     Real Fortnite does the same thing - a piece is not at full strength until it has finished
    ///     building, which is why rushing a wall up in someone's face can still be shot through.
    /// </summary>
    public void BeginConstruction(float timeSeconds) {
        bIsInitiallyBuilding = true;
        CurrentHitPoints = Math.Max(1, (int) (MaxHitPoints * BuildInStartHealthPct));
        StartHardening(timeSeconds, HardenTime, MaxHitPoints);

        Console.WriteLine($"ABuildingActor.BeginConstruction: {GetFName()} anim={BuildTime:0.00}s " +
                          $"harden={HardenTime:0.0}s {CurrentHitPoints}->{MaxHitPoints} HP " +
                          $"(t={timeSeconds:0.0} -> {_constructionEndsAt:0.0})");
    }

    /// <summary>
    ///     Repairing runs THE SAME ramp as building in, which is what bIsInitiallyBuilding exists to
    ///     distinguish - false here, true above. That is not a convenience: the harden curve is the
    ///     only model this project has for a piece gaining health over time, and reusing it is what
    ///     makes a repaired wall behave like a freshly placed one rather than snapping to full.
    ///
    ///     The duration is the REMAINDER of that same curve, not a fresh BuildTime. The curve runs
    ///     linearly from BuildInStartHealthPct to 1.0 over BuildTime, so a piece sitting at fraction
    ///     f has (1 - f) / (1 - BuildInStartHealthPct) of it left to travel - a metal wall on its
    ///     last sliver takes nearly the full 25 s back, one that is barely scratched takes an
    ///     instant. Falling off the bottom of the curve (a piece damaged BELOW its starting
    ///     fraction) just clamps to the whole duration.
    /// </summary>
    /// <param name="targetHitPoints">
    ///     Where the ramp stops - NOT necessarily full health. A player who could only afford part of
    ///     the repair gets a ramp that ends part-way up, which is the whole reason this is a
    ///     parameter: charging for a partial repair and then delivering a full one would let anyone
    ///     rebuild a metal wall for one unit of metal.
    /// </param>
    /// <returns>False if there is nothing to do - already at or above the target, or mid-cascade at zero.</returns>
    public bool BeginRepair(float timeSeconds, int targetHitPoints) {
        var target = Math.Clamp(targetHitPoints, 0, MaxHitPoints);
        if (CurrentHitPoints <= 0 || target <= CurrentHitPoints) return false;

        var fraction = MaxHitPoints <= 0 ? 1f : CurrentHitPoints / (float) MaxHitPoints;
        var span = 1f - BuildInStartHealthPct;
        var travel = span <= 0f ? 1f : Math.Clamp((1f - fraction) / span, 0f, 1f);

        bIsInitiallyBuilding = false;
        StartHardening(timeSeconds, HardenTime * travel, target);
        return true;
    }

    /// <summary>
    ///     Shared by both entry points above. Ramps from WHATEVER health the piece has right now, so
    ///     the caller sets the starting health (or leaves it alone, for a repair) before calling.
    /// </summary>
    private void StartHardening(float timeSeconds, float duration, int targetHitPoints) {
        bUnderConstruction = true;

        // EBA_Placement, not EBA_Building - a hypothesis, and the switch is here to settle it.
        //
        // The enum has both (verified against the 10.40 SDK, values identical to ours), and this
        // has always sent EBA_Building. Reported twice: a freshly placed piece looks SOLID for an
        // instant, then plays what reads as a crumbling animation, then finishes assembling. That
        // is what EBA_Building is - Save The World's build-up, a piece coming together out of
        // nothing - whereas Battle Royale places a wall with a quick pop, which is what EBA_Placement
        // is named for and what this project has never once sent.
        //
        // The ordering fix that came before this (SetReplicates last, so no 150 HP is ever seen)
        // was necessary and did not change the look, which is what points at the animation rather
        // than the health. BUILD_PLACEMENT_ANIM=1 restores EBA_Building for a side-by-side.
        //
        // A REPAIR IS THE OTHER ONE. This method serves both entry points and used to give them the
        // same animation, so a repaired piece got the placement pop - which is why repairing showed
        // no build-up effect at all, only health climbing. bIsInitiallyBuilding already tells them
        // apart (BeginConstruction sets it true, BeginRepair false) and was simply not being read
        // here. EBA_Building is the assemble-out-of-nothing animation, which is what a piece
        // regaining strength should play and what placement was wrongly using.
        //
        // No repair GameplayCue exists to send instead: the paks carry Fort_Build_RepairStart_Cue
        // (a SOUND) and a HUD crosshair, and no GC_/GameplayCue asset for it. The building's only
        // proxy cue property is ProxyGameplayCueDamagePhysical (handles 67/68) - damage, with no
        // repair sibling. So the animation is the whole of the visible effect.
        BuildingAnimation = bIsInitiallyBuilding ? (EBuildingAnim) PlacementAnim : (EBuildingAnim) RepairAnim;

        _constructionStartedAt = timeSeconds;
        _constructionEndsAt = timeSeconds + MathF.Max(0f, duration);
        _constructionStartHealth = CurrentHitPoints;
        _constructionTargetHealth = targetHitPoints;
        SyncAttributeSet();
    }

    /// <summary>
    ///     Advances the harden ramp. Returns true while still constructing, so the caller knows to
    ///     keep ticking this piece. Health climbs linearly from wherever it started to full, and the
    ///     animation flags clear together with it, which is what makes the client's own animation and
    ///     the replicated health agree at the moment construction completes.
    ///
    ///     Interpolating between two REMEMBERED endpoints rather than recomputing from
    ///     BuildInStartHealthPct is what lets a repair - which starts part-way up the curve and runs
    ///     for a partial duration - use this unchanged.
    /// </summary>
    public bool TickConstruction(float timeSeconds) {
        if (!bUnderConstruction) return false;

        _lastPublishTime = timeSeconds;

        if (timeSeconds >= _constructionEndsAt) {
            bUnderConstruction = false;
            bIsInitiallyBuilding = false;
            BuildingAnimation = EBuildingAnim.EBA_None;
            CurrentHitPoints = _constructionTargetHealth;
            SyncAttributeSet();
            return false;
        }

        var duration = _constructionEndsAt - _constructionStartedAt;
        var progress = duration <= 0f ? 1f : (timeSeconds - _constructionStartedAt) / duration;
        var health = _constructionStartHealth + (_constructionTargetHealth - _constructionStartHealth) * progress;

        CurrentHitPoints = Math.Clamp((int) health, 1, MaxHitPoints);

        // PUBLISHED, BUT THROTTLED - and the interval is measured, not chosen. Replicating every
        // tick made the client count 90, 91, 92 ... 150 one hit point at a time; not replicating the
        // ramp at all was the other extreme and equally wrong. PR3.0 was watched placing a wall and
        // updates roughly twice a second, which for a 4-second wood build is about eight steps of
        // ~7 HP - visibly a climb, nowhere near a per-tick stream.
        //
        // Damage is unaffected: ApplyDamage publishes directly, so a hit still lands instantly even
        // in the middle of an interval.
        if (timeSeconds - _lastHealthPublishAt >= HealthPublishInterval) SyncAttributeSet();

        return true;
    }

    /// <summary>
    ///     The support-grid cell this piece's pivot falls in. Maintained by
    ///     BuildingStructuralSupportSystem.Register, which uses it as the key of its spatial hash so
    ///     a neighbour query only has to look at the 27 cells around a piece instead of at every
    ///     piece in the match.
    /// </summary>
    public FBuildingSupportCellIndex CellIndex { get; internal set; }

    /// <summary>
    ///     Fills in everything derivable from the resolved building class - the piece's material
    ///     tier, which of the real EFortBuildingType slots it occupies, and the HP that follows from
    ///     the material. Kept here rather than spelled out at the ServerCreateBuildingActor call site
    ///     so that path stays one line and every future field derived from the class lands in one
    ///     place.
    /// </summary>
    public void InitializeFromClass(UClass buildingClass) {
        var path = buildingClass.NativePackagePath;
        Material = MaterialFromClassPath(path);
        BuildingType = BuildingTypeFromClassPath(path);
        // REAL per-class health, no longer a per-material guess. FortBuildingAttributes.Generated.cs
        // carries what Fortnite itself resolves at runtime through the piece's AttributeInitKeys and
        // the GAS attribute-defaults table - so a wood wall is 150 and a wood floor is 140, which a
        // single per-material number could never express. BaseHitPointsFor remains as the fallback for
        // a class the table does not cover.
        ClassName = path[(path.LastIndexOf('.') + 1)..];
        var className = ClassName;
        MaxHitPoints = FortBuildingAttributes.ByClass.TryGetValue(className, out var attributes)
            ? attributes.MaxHealth
            : BaseHitPointsFor(Material);
        CurrentHitPoints = MaxHitPoints;

        // Likewise the build-in time, which is per MATERIAL in the real data (wood 4s, stone 12s,
        // metal 25s) rather than the one duration this used for everything.
        HardenTime = HardenTimeOverride >= 0f ? HardenTimeOverride
            : attributes.BuildTime > 0f ? attributes.BuildTime
            : BuildInDurationDefault;

        // AND THE REPLICATED ONE IS THE SAME NUMBER. It never used to be: BuildTime stayed at the
        // 0.5s default for every piece while the health ramp ran for the real 4/12/25, so the client
        // was told the build finished half a second in and then watched the health go on climbing
        // for up to another twenty-five seconds. That is the "health at placement and the way it
        // increases are a bit off" this had left.
        //
        // They are the same number in the game data too, which is what makes this a fix rather than
        // a tuning choice: handle 59 replicates `FortBuildingActorSet.BuildTime`, and that is the
        // exact attribute FortBuildingAttributes resolves per class through AttributeInitKeys. The
        // old 0.5 predates that table - it was the value a client was seen to accept back when the
        // real one was unknown.
        BuildTime = HardenTime;

        // AND THE REPAIR WINDOW, handle 60, which had been Reserved since this class was written -
        // declared, correctly numbered, never sent. So a client was told a repair takes ZERO
        // seconds, and there is no window in which to play anything: the build-up animation
        // appeared (that rides BuildingAnimation) while the heal part of the effect did not.
        // Exactly the bug BuildTime had, in the property immediately beside it.
        //
        // Equal to BuildTime, and that is READ rather than assumed. AthenaAttributesBuildingSection
        // carries `<category>.FortBuildingActorSet.RepairTime` next to the BuildTime row this
        // already uses, and for every material a player can build with they are the same number:
        //
        //     Wood 4/4    Stone 12/12    Metal 25/25    (Permanite 4/20 - not player-buildable)
        //
        // So one value serves both here. If Permanite ever becomes reachable, this needs its own
        // baked column.
        RepairTime = HardenTime;

        BuildingAttributeSet = UObjectGlobals.NewObject<UFortBuildingActorSet>(
            this, GUClassArray.StaticClass<UFortBuildingActorSet>(), new FName("BuildingAttributeSet"),
            EObjectFlags.RF_Transient);

        // The set has to be reachable THROUGH an ability system component, not merely present - see
        // AbilitySystemComponent's doc comment. OwnerActor and AvatarActor are both this piece: a
        // building owns its own abilities and acts through itself, unlike a player whose ASC lives
        // on the PlayerState and acts through the pawn.
        AbilitySystemComponent = UObjectGlobals.NewObject<UFortAbilitySystemComponent>(
            this, GUClassArray.StaticClass<UFortAbilitySystemComponent>(), new FName("AbilitySystemComponent"),
            EObjectFlags.RF_Transient);

        if (AbilitySystemComponent != null) {
            AbilitySystemComponent.OwnerActor = this;
            AbilitySystemComponent.AvatarActor = this;
            if (BuildingAttributeSet != null) AbilitySystemComponent.SpawnedAttributes.Add(BuildingAttributeSet);
        }

        SyncAttributeSet();
    }

    /// <summary>Mirrors this piece's authoritative int HP into the float attribute set the client reads. Called on every change, so the ordinary shadow-state comparison picks it up - no MarkPropertyDirty needed, the value genuinely differs.</summary>
    private void SyncAttributeSet() {
        // THE RAMP IS NOT REPLICATED ONE HIT POINT AT A TIME. CurrentHitPoints is recomputed every
        // tick while a piece builds or repairs, so mirroring it here put a new health value on the
        // wire ~60 times a second and the client counted up 90, 91, 92 ... 150 over four seconds.
        //
        // The other extreme - publishing only on events and letting the client draw its own ramp -
        // was tried and is ALSO wrong: PR3.0 was watched placing a wall and it updates roughly twice
        // a second, so a real server does send the climb, just not every tick. TickConstruction
        // therefore calls this on a throttle (HealthPublishInterval) while every EVENT - placement,
        // damage, repair, completion, destruction - calls it directly and lands immediately.
        //
        // Both replicated paths read ReplicatedHitPoints, so they cannot disagree with each other.
        ReplicatedHitPoints = CurrentHitPoints;
        _lastHealthPublishAt = _lastPublishTime;

        if (BuildingAttributeSet == null) return;
        BuildingAttributeSet.Health = ReplicatedHitPoints;
        BuildingAttributeSet.MaxHealth = MaxHitPoints;
    }

    /// <summary>
    ///     The health this piece REPLICATES - see SyncAttributeSet for why it is not simply
    ///     <see cref="CurrentHitPoints" />. Wire handle 61.
    /// </summary>
    public int ReplicatedHitPoints { get; private set; } = DefaultHitPoints;

    private float _lastHealthPublishAt = float.NegativeInfinity;

    /// <summary>
    ///     World time as of the last ramp tick, so SyncAttributeSet can stamp the throttle without
    ///     every one of its six callers having to pass a clock it does not otherwise need.
    /// </summary>
    private float _lastPublishTime;

    /// <summary>
    ///     How often the harden ramp may put a new health value on the wire. MEASURED against PR3.0,
    ///     which updates a building it is placing about twice a second - not a comfort setting.
    ///     BUILD_HEALTH_PUBLISH_INTERVAL overrides it.
    /// </summary>
    private static readonly float HealthPublishInterval = Env("BUILD_HEALTH_PUBLISH_INTERVAL", 0.5f);

    /// <summary>
    ///     Which EBuildingAnim a piece plays while it builds in. 4 = EBA_Placement (the default now),
    ///     1 = EBA_Building. See StartHardening for why this is a switch rather than a constant.
    /// </summary>
    private static readonly byte PlacementAnim =
        byte.TryParse(Environment.GetEnvironmentVariable("BUILD_PLACEMENT_ANIM"), out var anim)
            ? anim
            : (byte) EBuildingAnim.EBA_Placement;

    /// <summary>
    ///     Which EBuildingAnim a REPAIR plays. 1 = EBA_Building, the assemble-out-of-nothing
    ///     build-up - see StartHardening. BUILD_REPAIR_ANIM overrides it.
    /// </summary>
    private static readonly byte RepairAnim =
        byte.TryParse(Environment.GetEnvironmentVariable("BUILD_REPAIR_ANIM"), out var repair)
            ? repair
            : (byte) EBuildingAnim.EBA_Building;

    /// <summary>
    ///     Applies damage and returns true once this drops the piece to 0. Never goes negative and
    ///     never re-triggers the destroyed transition once already at 0 (mirrors AActor.Destroy's own
    ///     "already being destroyed" guard) - a second hit landing in the same tick as the cascade's
    ///     own Destroy() call must not double-report.
    /// </summary>
    public bool ApplyDamage(int amount) {
        if (CurrentHitPoints <= 0 || amount <= 0) return false;
        CurrentHitPoints = Math.Max(0, CurrentHitPoints - amount);

        // DAMAGE TAKEN DURING A RAMP HAS TO MOVE THE RAMP, or the next tick undoes it.
        //
        // TickConstruction does not decrement health, it RECOMPUTES it from two remembered endpoints
        // and the elapsed fraction - which is exactly what lets a partial repair reuse the same
        // code. The consequence is that anything else writing CurrentHitPoints while
        // bUnderConstruction is true survives only until the next tick, and then the ramp puts its
        // own value back. Live symptom, reported precisely: "health updates in real time EXCEPT
        // while it is building or being repaired".
        //
        // Both endpoints move by the damage, not just the start: lowering only the start would let
        // the ramp climb back to a target that no longer reflects the hit, so shooting a wall while
        // it built would cost the shooter nothing at all once the ramp finished.
        if (bUnderConstruction) {
            _constructionStartHealth = Math.Max(0, _constructionStartHealth - amount);
            _constructionTargetHealth = Math.Max(1, _constructionTargetHealth - amount);
        }

        SyncAttributeSet();
        return CurrentHitPoints <= 0;
    }

    /// <summary>
    ///     Puts health back, capped at MaxHitPoints, and returns how much was actually restored -
    ///     which is what the caller charges for, so a piece that was nearly full is never billed for
    ///     the overflow. The mirror of <see cref="ApplyDamage"/> in every other respect, including
    ///     going through <see cref="SyncAttributeSet"/> so both replicated health paths move together.
    ///
    ///     A piece already at zero is NOT repairable: it is on its way out via MarkDestroyed and the
    ///     structural cascade, and reviving it here would leave the client's own destroyed piece
    ///     behind. Real Fortnite's ServerRepairBuildingActor is likewise only reachable while the
    ///     piece is still standing.
    /// </summary>
    public int Repair(int amount) {
        if (amount <= 0 || bDestroyed || CurrentHitPoints <= 0) return 0;

        var healed = Math.Min(amount, MaxHitPoints - CurrentHitPoints);
        if (healed <= 0) return 0;

        CurrentHitPoints += healed;
        SyncAttributeSet();
        return healed;
    }

    /// <summary>
    ///     Flags this piece as destroyed WITHOUT tearing it down yet, and returns true the first
    ///     time. The split is the whole point: Destroy() closes the channel, and a closed channel
    ///     carries no more property updates, so setting bDestroyed and destroying in the same breath
    ///     would guarantee the client never receives the flag it is supposed to react to. The actual
    ///     Destroy() is left to BuildingStructuralSupportSystem's tick, one deferral later, by which
    ///     point the flag has gone out on an ordinary property push.
    /// </summary>
    public bool MarkDestroyed() {
        if (bDestroyed) return false;

        bDestroyed = true;
        bUnderConstruction = false;
        bIsInitiallyBuilding = false;
        _breakingUntil = 0f;
        // The client's own destruction animation. EBA_Breaking is the partially-damaged look;
        // EBA_Destruction is the piece actually coming apart, which is what a destroyed piece needs.
        BuildingAnimation = EBuildingAnim.EBA_Destruction;
        CurrentHitPoints = 0;
        SyncAttributeSet();
        return true;
    }

    /// <summary>
    ///     Leaves the support grid however this piece died - shot down, cascaded, or torn down by
    ///     some future path that just calls Destroy() directly. Registering in one place and
    ///     unregistering in another was a leak waiting to happen: the registry is what the cascade
    ///     floods over, so a destroyed piece left in it would go on "supporting" its neighbours
    ///     forever.
    /// </summary>
    protected override void Destroyed() => BuildingStructuralSupportSystem.Unregister(this);

    private const int DefaultHitPoints = 200;

    /// <summary>
    ///     Base HP per material tier. NOT measured from a live client or read off a real
    ///     BuildingActorData/HealthMax property - Fortnite ships those in a DataTable this project
    ///     does not parse yet (see Tools/MapActorDump's "props:" mode, which could pull the real
    ///     numbers off a WID-adjacent building data asset given the exact row name). Picked in the
    ///     right relative order (wood weakest, metal strongest) so destruction has SOME grounded
    ///     number to subtract from; treat every value here as a placeholder pending that extraction.
    /// </summary>
    public static int BaseHitPointsFor(EBuildingMaterial material) => material switch {
        EBuildingMaterial.Wood => 200,
        EBuildingMaterial.Stone => 300,
        EBuildingMaterial.Metal => 400,
        _ => DefaultHitPoints
    };

    /// <summary>Same "read it off the asset path" approach as NativeRpcHandlers.BuildingResourcePathFor - the material tier is baked into the class's own content path.</summary>
    public static EBuildingMaterial MaterialFromClassPath(string? classPath) {
        if (classPath == null) return EBuildingMaterial.Unknown;
        if (classPath.Contains("/Wood/", StringComparison.OrdinalIgnoreCase)) return EBuildingMaterial.Wood;
        if (classPath.Contains("/Stone/", StringComparison.OrdinalIgnoreCase)) return EBuildingMaterial.Stone;
        if (classPath.Contains("/Metal/", StringComparison.OrdinalIgnoreCase)) return EBuildingMaterial.Metal;
        return EBuildingMaterial.Unknown;
    }

    /// <summary>
    ///     Which support-grid slot kind a piece occupies, read off the class's own file name (the
    ///     part BuildingClassHandles.txt's alias table already spells out per line, e.g.
    ///     "Wood:Stair" - this reads the same signal back off the resolved NativePackagePath instead
    ///     of needing the alias threaded through separately). The ~40 real PBWA_* suffixes are all
    ///     EDIT results of the four base pieces, so each still lands in a base slot: a
    ///     Door/Window/Archway/HalfWall is a Wall, a RoofO/RoofI/RoofS is a Roof, and so on.
    ///
    ///     Order matters - "Solid" (the plain full wall) shares no substring with the rest, so it is
    ///     named explicitly rather than left to a default, and Stair has to be tested before Wall
    ///     because StairW would otherwise never be reached.
    /// </summary>
    public static EFortBuildingType BuildingTypeFromClassPath(string? classPath) {
        if (classPath == null) return EFortBuildingType.None;
        var name = classPath[(classPath.LastIndexOf('.') + 1)..];

        bool Has(string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);

        if (Has("Floor")) return EFortBuildingType.Floor;
        if (Has("Roof")) return EFortBuildingType.Roof;
        if (Has("Stair")) return EFortBuildingType.Stairs;
        if (Has("Pillar")) return EFortBuildingType.Pillar;
        if (Has("Corner")) return EFortBuildingType.Corner;
        if (Has("Solid") || Has("Wall") || Has("Door") || Has("Window") || Has("Archway") || Has("Brace"))
            return EFortBuildingType.Wall;

        return EFortBuildingType.None;
    }
}

public enum EBuildingMaterial { Unknown, Wood, Stone, Metal }

/// <summary>
///     FortniteGame.EBuildingAnim, verbatim - ABuildingSMActor::BuildingAnimation's type (wire handle
///     57). Values are the real ones, including EBA_MAX, because a TEnumAsByte serialises in
///     CeilLogTwo(GetMaxEnumValue()) bits and getting that width wrong misaligns every later handle.
/// </summary>
public enum EBuildingAnim : byte {
    EBA_None = 0,
    EBA_Building = 1,
    EBA_Breaking = 2,
    EBA_Destruction = 3,
    EBA_Placement = 4,
    EBA_DynamicLOD = 5,
    EBA_DynamicShrink = 6,
    EBA_MAX = 7
}

/// <summary>
///     FortniteGame.EFortBuildingType, trimmed to the values a player-placed piece can actually be.
///     Numbering matches the real enum so a future wire or native comparison lines up; the values
///     this project never classifies (Deco, Prop, SpawnedItem, Container, Trap,
///     GenericCenterCellActor) are simply absent rather than renumbered.
/// </summary>
public enum EFortBuildingType : byte {
    Wall = 0,
    Floor = 1,
    Corner = 2,
    Stairs = 5,
    Roof = 6,
    Pillar = 7,
    None = 12
}

/// <summary>
///     A cell of the building support grid - real Fortnite's FBuildingSupportCellIndex, the key
///     UBuildingStructuralSupportSystem hangs every placed piece off (GetGridBox, GetWallActor,
///     GetFloorActor, GetCenterCellActor, AreGridIndicesValid).
///
///     THE GRID'S DIMENSIONS ARE MEASURED, not assumed. Source: 370 unique real placements recovered
///     from this project's own ServerCreateBuildingActor console logs (AFortOnlineBeacon.Test's
///     Captures directory), each carrying the client's own already-snapped BuildLoc and Yaw:
///
///       * Z is an exact multiple of 384 for every Floor (107 samples) and every full Wall (69) -
///         zero exceptions - so A STOREY IS 384 UNITS, not the 512 a naive "cubic tile" reading
///         assumes. Roof and Stair pivots additionally appear at storey+128, so those two carry
///         their own vertical pivot offset within the same storey.
///       * X and Y are always multiples of 256, and which of the two is a multiple of 512 tracks Yaw
///         exactly: Yaw 0/180 always gives (X mod 512 == 0, Y mod 512 == 256) and Yaw +-90 always
///         gives (X mod 512 == 256, Y mod 512 == 0), across all 350 pieces of the three types with
///         enough samples to be worth counting. That is the signature of a pivot sitting at the
///         MIDPOINT OF A CELL EDGE, 256 out along one of the piece's own local axes.
///
///     WHICH local axis, and which sign, is no longer inferred at all (Round 56). Those are two of
///     ABuildingActor's own properties, and they were READ OUT OF THE REAL CDOs in the client memory
///     dump - `PriveDev\dumpwork\pivotoffsets.py`, 132 building classes, values in
///     `BuildingPivotOffsets.json`:
///
///       * `SnapGridSize` = 512 and `VertSnapGridSize` = 384, on every class without exception -
///         independent confirmation of the tile and storey sizes measured above.
///       * `BaseLocToPivotOffset` = **(0, -256, 0) on all 132 classes**. Not per-class, and along
///         local **Y**, not local X. So `pivot = baseLoc + Rotate((0,-256,0), Yaw)`, where baseLoc is
///         the grid point the client snapped to. Checked against 1620 real placements from this
///         project's own logs: baseLoc lands on an EXACT multiple of 512 in both X and Y for 1565 of
///         them (96.6%), and every miss is a piece the client dropped onto terrain rather than the
///         storey grid (its Z is not a multiple of 384 either). So CELL CENTRES ARE AT (512i, 512j),
///         not the (512i + 256, 512j + 256) an earlier local-X reading of the same parity signature
///         suggested.
///       * `CentroidOffset` - where the piece's BODY sits relative to its pivot - is uniform per
///         family: Wall (0, 0, 192), Floor/Roof (0, 256, 0), Stair/Pillar (0, 256, 192). Note what
///         that means: for Floor/Roof/Stair it exactly cancels `BaseLocToPivotOffset`, so their
///         centroid IS baseLoc, the cell centre. For a WALL it does not cancel - a wall's body sits
///         256 out from the cell centre, ON the cell edge, which is what a wall is.
///
///     That last distinction is the whole reason <see cref="CentroidOf"/> /
///     <see cref="PivotForCentroid"/> exist rather than a single "rotate about the cell centre":
///     rotating a floor has to hold its CELL still, and rotating a wall has to hold ITS OWN EDGE
///     still, and both fall out of holding the centroid still. See NativeRpcHandlers'
///     ServerEditBuildingActor.
/// </summary>
public readonly record struct FBuildingSupportCellIndex(int X, int Y, int Z) {
    /// <summary>Cell size in X and Y. Measured - see this type's doc comment.</summary>
    public const float TileSize = 512f;

    /// <summary>Cell size in Z: one storey, NOT one tile. Measured - see this type's doc comment.</summary>
    public const float StoreyHeight = 384f;

    /// <summary>
    ///     How far a piece's pivot sits from its cell's centre, along the piece's own local X.
    ///     Measured - see this type's doc comment.
    /// </summary>
    public const float PivotEdgeOffset = TileSize / 2f;

    /// <summary>
    ///     Extra vertical slack a Roof or Stair pivot can carry inside its own storey - the
    ///     storey+128 offset those two types show in the measured placements. Anything comparing two
    ///     pivots' heights has to allow for one piece carrying it and the other not.
    /// </summary>
    public const float PivotStoreyOffset = 128f;

    /// <summary>
    ///     The cell a pivot falls in. Pivots sit exactly ON a cell boundary in one horizontal axis by
    ///     construction, so which side of that knife edge this rounds to is arbitrary - callers must
    ///     therefore treat the result as a spatial-hash bucket and search the neighbouring cells too,
    ///     which is what BuildingStructuralSupportSystem does.
    /// </summary>
    public static FBuildingSupportCellIndex FromLocation(FVector location) => new(
        (int) MathF.Floor(location.X / TileSize),
        (int) MathF.Floor(location.Y / TileSize),
        (int) MathF.Floor(location.Z / StoreyHeight)
    );

    /// <summary>
    ///     `ABuildingActor::BaseLocToPivotOffset`, read from the real CDOs - identical on all 132
    ///     building classes, so this is a constant rather than a per-class lookup. See this type's
    ///     doc comment.
    /// </summary>
    public static readonly (float X, float Y, float Z) BaseLocToPivotOffset = (0f, -256f, 0f);

    /// <summary>
    ///     `ABuildingActor::CentroidOffset` for a piece of this type, read from the real CDOs. Uniform
    ///     within each family, and an edit never changes a piece's family, so a type is enough to name
    ///     it. See this type's doc comment.
    /// </summary>
    public static (float X, float Y, float Z) CentroidOffsetFor(EFortBuildingType type) => type switch {
        EFortBuildingType.Wall => (0f, 0f, 192f),
        EFortBuildingType.Floor or EFortBuildingType.Roof => (0f, 256f, 0f),
        _ => (0f, 256f, 192f)
    };

    /// <summary>
    ///     Where a piece's BODY actually sits, given the pivot the actor is replicated at. This is the
    ///     part of a piece that must not move when it is edited: for a Floor, Roof or Stair it is the
    ///     centre of the cell; for a Wall it is the middle of the cell edge the wall spans.
    ///
    ///     This and <see cref="PivotForCentroid"/> assume the yaw is a multiple of 90, which every
    ///     value the client has ever sent for a placement or an edit is - the rotation is rounded to
    ///     exact 0/+-1 so a round trip through the pair returns the input bit-for-bit rather than
    ///     accumulating float noise over a chain of edits.
    /// </summary>
    public static FVector CentroidOf(FVector pivot, float yaw, EFortBuildingType type) {
        var (dx, dy, dz) = Rotate(CentroidOffsetFor(type), yaw);
        return new FVector { X = pivot.X + dx, Y = pivot.Y + dy, Z = pivot.Z + dz };
    }

    /// <summary>
    ///     The inverse of <see cref="CentroidOf"/>: where a piece's pivot has to be for its body to sit
    ///     here while facing this yaw.
    /// </summary>
    public static FVector PivotForCentroid(FVector centroid, float yaw, EFortBuildingType type) {
        var (dx, dy, dz) = Rotate(CentroidOffsetFor(type), yaw);
        return new FVector { X = centroid.X - dx, Y = centroid.Y - dy, Z = centroid.Z - dz };
    }

    /// <summary>
    ///     The grid point the client snapped to when it placed a piece here - `pivot` minus the
    ///     rotated <see cref="BaseLocToPivotOffset"/>. Lands on an exact (512i, 512j, 384k) for every
    ///     placement the client has ever sent that wasn't snapped to terrain instead; not used by the
    ///     support system yet (<see cref="FromLocation"/> still buckets), but it is what an exact cell
    ///     index would be built from.
    /// </summary>
    public static FVector BaseLocationOf(FVector pivot, float yaw) {
        var (dx, dy, dz) = Rotate(BaseLocToPivotOffset, yaw);
        return new FVector { X = pivot.X - dx, Y = pivot.Y - dy, Z = pivot.Z - dz };
    }

    /// <summary>A local-space offset turned world-space by a yaw. UE yaw: 0 is +X, 90 is +Y.</summary>
    private static (float X, float Y, float Z) Rotate((float X, float Y, float Z) offset, float yaw) {
        var radians = yaw * MathF.PI / 180f;
        var cos = MathF.Round(MathF.Cos(radians));
        var sin = MathF.Round(MathF.Sin(radians));
        return (offset.X * cos - offset.Y * sin, offset.X * sin + offset.Y * cos, offset.Z);
    }

    /// <summary>This cell and the 26 around it - the search volume any neighbour query has to cover, per FromLocation's caveat.</summary>
    public IEnumerable<FBuildingSupportCellIndex> WithNeighbors() {
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
            yield return new FBuildingSupportCellIndex(X + dx, Y + dy, Z + dz);
    }
}
