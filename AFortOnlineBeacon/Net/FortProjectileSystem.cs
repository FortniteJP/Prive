using AFortOnlineBeacon.Core.Math;
using AFortOnlineBeacon.Core.Objects;
using AFortOnlineBeacon.Net.Actors;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     Throws things. The server half of `Server_SpawnProjectile`.
///
///     A thrown consumable's flight is a client-side simulation of a server-spawned actor: the
///     projectile Blueprint carries its own movement component, fuse and explosion, and runs all of
///     them on a simulated proxy. What the server has to get right is the spawn - the class, where,
///     which way, and how fast - and then stay out of the way. See AFortProjectileBase.
///
///     Nothing here is guessed at. The class comes from the item definition's own ProjectileTemplate
///     (FortConsumables.ProjectileClassFor); the location and direction come from the client, which
///     sends the exact muzzle transform it used; the speed and gravity are read off the ability CDO.
/// </summary>
internal static class FortProjectileSystem {
    /// <summary>
    ///     GA_Athena_Grenade_WithTrajectory_C's GrenadeSpeedMin and GrenadeSpeedMax, which are both
    ///     4000 - the ability interpolates between them by throw pitch (CalcGrenadeSpeedFromPitch)
    ///     and, with the two equal, that always lands on 4000.
    ///
    ///     ONE NUMBER FOR THE WHOLE FAMILY, for now. Every thrown consumable in the loot tables
    ///     inherits from that one ability and does not override the speed, but a future one could,
    ///     and the honest fix then is another generated column beside ProjectileClasses rather than
    ///     a second constant here. PROJECTILE_SPEED overrides it for experiments.
    /// </summary>
    private static float Speed =>
        float.TryParse(Environment.GetEnvironmentVariable("PROJECTILE_SPEED"), out var s) && s > 0 ? s : 4000f;

    /// <summary>
    ///     How long a projectile actor lives on the server before it is destroyed.
    ///
    ///     NOT a fuse - the client owns the fuse and the explosion, and this server sees neither. It
    ///     is only here so the actor and its channel do not leak for the rest of the match. Longer
    ///     than any real fuse on purpose: destroying one early would delete a grenade out from under
    ///     the client's own explosion.
    /// </summary>
    private static float Lifetime =>
        float.TryParse(Environment.GetEnvironmentVariable("PROJECTILE_LIFETIME"), out var s) && s > 0 ? s : 15f;

    /// <summary>
    ///     GA_Athena_Grenade_WithTrajectory_C's PostThrowEndDelay, read from the pak: 0.4 seconds.
    ///
    ///     This is how long AFTER the projectile appears that the real ability ends. Its graph is
    ///     literally `Created -> AthenaProjectileSpawned -> WaitDelay(PostThrowEndDelay) ->
    ///     K2_AbilityCompleted`, and 0.4s is why a real player can throw grenades about twice a
    ///     second rather than once per fuse. PROJECTILE_END_DELAY overrides it.
    /// </summary>
    private static float PostThrowEndDelay =>
        float.TryParse(Environment.GetEnvironmentVariable("PROJECTILE_END_DELAY"), out var s) && s > 0 ? s : 0.4f;

    /// <summary>
    ///     The projectile's gravity scale, as the ABILITY passes it to SpawnProjectileAndWait: 0.8.
    ///
    ///     NOT the 0.7 on B_Prj_Athena_Grenade_Base - that is the Blueprint's own default, and the
    ///     task overwrites it with the ability's value on every spawn. Both are real numbers in the
    ///     paks and picking the wrong one is a plausible-looking arc that is quietly 12% off.
    /// </summary>
    private static float GravityScale =>
        float.TryParse(Environment.GetEnvironmentVariable("PROJECTILE_GRAVITY_SCALE"), out var s) && s > 0 ? s : 0.8f;

    /// <summary>
    ///     The explosion's radius in units, read from the ability's own effect container:
    ///     TargetSelection.List[0] is Shape=Sphere, TestType=Overlap, Range=500.
    /// </summary>
    private static float ExplosionRadius =>
        float.TryParse(Environment.GetEnvironmentVariable("PROJECTILE_RADIUS"), out var s) && s > 0 ? s : 500f;

    /// <summary>
    ///     Damage to a PLAYER at any range inside the radius - the item's UtilityItemDamage row has
    ///     DmgPB, DmgMid, DmgLong and DmgMaxRange all equal to 100, i.e. no falloff at all.
    /// </summary>
    private static float PlayerDamage =>
        float.TryParse(Environment.GetEnvironmentVariable("PROJECTILE_DAMAGE"), out var s) && s > 0 ? s : 100f;

    /// <summary>Damage to a BUILDING - the same row's EnvDmgPB/Mid/Long/MaxRange, all 375.</summary>
    private static float EnvironmentDamage =>
        float.TryParse(Environment.GetEnvironmentVariable("PROJECTILE_ENV_DAMAGE"), out var s) && s > 0 ? s : 375f;

    /// <summary>
    ///     B_Prj_Athena_Grenade_Base's ProjectileComp0.Bounciness (0.3) and Friction (0.4) - the
    ///     coefficient of restitution and the tangential drag its bounces actually use. Read, not
    ///     tuned. PROJECTILE_BOUNCINESS / PROJECTILE_FRICTION override them.
    /// </summary>
    private static float Bounciness =>
        float.TryParse(Environment.GetEnvironmentVariable("PROJECTILE_BOUNCINESS"), out var s) && s >= 0 ? s : 0.3f;

    private static float BounceFriction =>
        float.TryParse(Environment.GetEnvironmentVariable("PROJECTILE_FRICTION"), out var s) && s >= 0 ? s : 0.4f;

    /// <summary>NumberOfBouncesTillExplode on B_Prj_Athena_Grenade_Base - a grenade that hits five surfaces goes off early.</summary>
    private const int BouncesTillExplode = 5;

    /// <summary>
    ///     How long after the explosion the actor is destroyed - AFortGameplayEffectDeliveryActor's
    ///     LifespanAfterKill, in effect.
    ///
    ///     CHOSEN, not read: the value is a default on the native CDO and does not appear in the
    ///     cooked asset, so this is the one number here that is not from the paks. It only has to be
    ///     long enough for bHasExploded and bIsBeingKilled to reach the client and short enough that
    ///     the grenade is not still lying there when the player throws the next one. The 15-second
    ///     PROJECTILE_LIFETIME it replaces was far too long for the latter: the client's own log
    ///     showed the first grenade still replicating, and still being simulated, at the moment it
    ///     refused the second throw. PROJECTILE_KILL_DELAY overrides it.
    /// </summary>
    private static float KillDelay =>
        float.TryParse(Environment.GetEnvironmentVariable("PROJECTILE_KILL_DELAY"), out var s) && s > 0 ? s : 1f;

    /// <summary>
    ///     B_Prj_Athena_Grenade_Base's FuseTime, read from the pak: 2.75 seconds.
    ///
    ///     The same "one number for the whole family" caveat as Speed - every thrown consumable in
    ///     the loot tables inherits this projectile base and none overrides it, and the honest fix
    ///     if one ever does is a generated column. PROJECTILE_FUSE overrides it.
    ///
    ///     NOT the only way a real grenade goes off: the base also carries NumberOfBouncesTillExplode
    ///     = 5, and StepFlight counts bounces, so a grenade that hits five surfaces explodes early.
    ///     It only counts bounces off the ground the server knows about, though - see StepFlight.
    /// </summary>
    private static float FuseTime =>
        float.TryParse(Environment.GetEnvironmentVariable("PROJECTILE_FUSE"), out var s) && s > 0 ? s : 2.75f;

    /// <summary>
    ///     Every projectile currently in flight, so the fuse has something to walk.
    ///
    ///     NOT world.PersistentLevel.Actors - and that mistake cost a whole live round. UWorld's
    ///     level actor array is not where SpawnActor puts a runtime actor; replication finds them
    ///     through UNetDriver.NetworkObjectList instead, which is why the grenade flew perfectly
    ///     (it was replicating) while the fuse never fired once (the tick walked an array it was
    ///     not in). A dedicated list depends on neither registry.
    /// </summary>
    private static readonly List<AFortProjectileBase> InFlight = new();

    /// <summary>
    ///     Spawns the projectile for a throw the client has just asked for, or does nothing if the
    ///     pawn is not holding something that throws.
    ///
    ///     The item is taken from what the pawn IS HOLDING rather than from the RPC, because the RPC
    ///     does not name it - the ability instance implies it. That also means the ProjectileTemplate
    ///     lookup can never fire for a bandage: a bandage's ability sends no Server_SpawnProjectile,
    ///     so this is only ever reached for something that actually throws (see the note on
    ///     FortConsumables.ProjectileClasses, where Bandage carries an inherited default it never uses).
    /// </summary>
    public static void SpawnFor(APawn? pawn, FVector location, FRotator direction) {
        if (pawn?.CurrentWeapon?.WeaponData is not { } itemDefinition) {
            Console.WriteLine("FortProjectileSystem: Server_SpawnProjectile arrived but the pawn is holding " +
                              "nothing this server can identify - no projectile spawned.");
            return;
        }

        var itemName = itemDefinition.GetFName().ToString();
        if (FortConsumables.ProjectileClassFor(itemName) is not { } classPath) {
            Console.WriteLine($"FortProjectileSystem: '{itemName}' asked to spawn a projectile but has no " +
                              "ProjectileTemplate in FortConsumables.Generated.cs - no projectile spawned.");
            return;
        }

        var world = pawn.GetWorld();
        if (world == null) return;

        var projectile = world.SpawnActor<AFortProjectileBase>(
            GUClassArray.StaticClassForPath<AFortProjectileBase>(classPath),
            new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient });

        if (projectile == null) return;

        projectile.SourceItemName = itemName;
        projectile.SetActorLocation(location);
        projectile.SetActorRotation(direction);

        // The velocity is what the client actually flies. Direction is a ROTATOR (see the RPC's own
        // note), so the throw vector is its forward axis scaled by the ability's speed - which is
        // also why the pitch matters: the client sends -9 to -14 degrees, i.e. slightly upward, and
        // that is the entire arc of the throw.
        var forward = direction.GetForwardVector();
        projectile.Velocity = new FVector { X = forward.X * Speed, Y = forward.Y * Speed, Z = forward.Z * Speed };
        if (ReplicateMovement) projectile.ReplicatedMovement.LinearVelocity = projectile.Velocity;

        // WHOSE grenade this is. Instigator (handle 15) is already known to be load-bearing on this
        // project's other spawned actors - a weapon whose Instigator never arrives makes the client
        // log "The instigator pawn is null when it shouldn't be!" every frame - and a projectile has
        // the same need for a stronger reason: the ability that asked for it has to recognise the
        // one that comes back. UFortAbilityTask_SpawnProjectileAndWait is waiting for ITS projectile,
        // and an actor belonging to nobody is not it.
        //
        // Owner as well, for the same reason SpawnProjectileAndWait takes a RequestedBy actor.
        projectile.SetInstigator(pawn);
        projectile.SetOwner(pawn);

        projectile.SimulatedLocation = location;
        projectile.ThrowerGroundZ = pawn.GetActorLocation().Z - CapsuleHalfHeight;

        // SEED IT BEFORE THE CHANNEL OPENS. The open bunch carries whatever ReplicatedMovement holds,
        // and an unset one is Location (0,0,0) - so the client dutifully put every grenade at the world
        // origin the instant it was thrown and it simply vanished, while the server went on simulating
        // and damaging correctly. Filling it here means the first thing the client ever hears about
        // this projectile is where it actually is.
        if (ReplicateMovement) {
            projectile.bReplicateMovement = true;

            // A projectile does not override FRepMovement's quantization, so it uses the ENGINE
            // default rather than the pawn's RoundTwoDecimals. The level is not on the wire - both
            // ends read their own - so getting it wrong is silent and total: the client decodes the
            // position at the wrong scale and the grenade disappears.
            projectile.ReplicatedMovement.LocationQuantization =
                Environment.GetEnvironmentVariable("PROJECTILE_REP_SCALE") is "100"
                    ? (100u, 30u)   // the pawn's RoundTwoDecimals, if a projectile turns out to use it
                    : FRepMovement.RoundWholeNumber;
            projectile.ReplicatedMovement.Location = location;
            projectile.ReplicatedMovement.Rotation = direction;
        }
        projectile.ExplodesAtWorldTime = world.TimeSeconds + FuseTime;
        projectile.ExpiresAtWorldTime = world.TimeSeconds + Lifetime;
        projectile.SetRole(ENetRole.ROLE_Authority);
        projectile.SetReplicates(true);
        InFlight.Add(projectile);

        // END THE ABILITY, ON A TIMER, THE WAY THE ABILITY'S OWN GRAPH WOULD.
        //
        // The client cannot do this for itself. SpawnProjectileAndWait only spawns under
        // IsNetAuthority(), so on the client its Created delegate never fires and the
        // WaitDelay -> K2_AbilityCompleted chain behind it never starts. Until the ability ends,
        // Spec->IsActive() stays true and the client refuses every further throw with
        // "already a currently active instance" - which is exactly what its log said. See
        // UActorChannel.SendClientEndAbility.
        if (pawn.Controller?.PlayerState is { AbilitySystemComponent: { } asc } ps
            && pawn.CurrentWeapon.GrantedAbilitySpecHandle != UnrealConstants.IndexNone) {
            var spec = asc.ActivatableAbilities.Items
                          .FirstOrDefault(item => item.Handle == pawn.CurrentWeapon.GrantedAbilitySpecHandle);

            if (spec?.ActivationPredictionKey is { } key) {
                PendingEnds.Add(new FPendingAbilityEnd {
                    PlayerState = ps,
                    AbilitySystem = asc,
                    Handle = spec.Handle,
                    PredictionKey = key,
                    EndsAtWorldTime = world.TimeSeconds + PostThrowEndDelay,
                    Weapon = pawn.CurrentWeapon,
                    AbilityClass = spec.Ability
                });
            } else {
                Console.WriteLine("FortProjectileSystem: no activation prediction key recorded for the throw " +
                                  "ability - ClientEndAbility would be ignored, so it is not sent. The client " +
                                  "will refuse the next throw.");
            }
        }

        // One off the stack, by the same rule and the same code a healing consumable uses - the
        // ability's AbilityCosts entry is identical. Without it a grenade is infinite.
        if (pawn.Controller?.PlayerState is { } playerState)
            FortConsumableSystem.ConsumeOne(playerState, pawn.CurrentWeapon, itemName);

        Console.WriteLine($"FortProjectileSystem: threw '{itemName}' as {classPath.Split('.')[^1]} " +
                          $"from {location} direction {direction} velocity {projectile.Velocity} " +
                          $"(speed {Speed:F0}, fuse {FuseTime:F2}s, server-side lifetime {Lifetime:F0}s)");
    }

    /// <summary>
    ///     Runs every projectile's fuse, and reaps the ones that have finished. Called once per tick.
    ///
    ///     TWO SEPARATE DEADLINES, and the order between them is the point. bHasExploded (handle 16)
    ///     is what the client turns into a bang, and it has to actually REACH the client - so the
    ///     explosion is a property change on a live actor, and the destroy comes seconds later. Doing
    ///     both at once would close the channel with the property still unsent and the grenade would
    ///     silently disappear instead of exploding.
    ///
    ///     Destroy() reaches clients even for an actor whose channel has since closed, through
    ///     UActorChannel.SendDestructionInfo - so a projectile that went out of relevance is still
    ///     cleaned up rather than left standing.
    /// </summary>
    /// <summary>
    ///     One tick of the server's own ballistic simulation: gravity, then a landscape bounce.
    ///
    ///     WHY SIMULATE AT ALL, when the client already does? Because the server has to know WHERE
    ///     the explosion happens and has no other way to find out. Nothing here is sent - see
    ///     AFortProjectileBase.SimulatedLocation for why replicating it would be worse than useless.
    ///
    ///     WHAT IT COLLIDES AGAINST, in the order the step asks:
    ///
    ///       * A FLOOR, from the baked height grids, measured ground and - as a last resort - the
    ///         height the thrower was standing at. This is the only part that is an approximation.
    ///       * A SURFACE, swept exactly: player builds, the map's own collision hulls
    ///         (WorldCollision - the same convex shapes the client uses), and the voxel wall spans
    ///         for the terrain shells and cliffs that carry no simple collision. The nearest contact
    ///         wins and the bounce uses that surface's real normal.
    ///
    ///     This used to be the landscape and nothing else, which is why a grenade the client bounced
    ///     off a wall carried straight on here. It no longer does.
    /// </summary>
    private static void StepFlight(UWorld world, AFortProjectileBase projectile) {
        var dt = world.DeltaTimeSeconds;
        if (dt <= 0f) return;

        // Already at rest - UProjectileMovementComponent stops simulating below
        // BounceVelocityStopSimulatingThreshold, and so does this. Without it a grenade sitting on a
        // built floor bounces microscopically every tick, and since five bounces detonate a grenade
        // it would go off about a tenth of a second after landing.
        if (projectile.Velocity.IsNearlyZero()) return;

        // Fortnite's world gravity, scaled by the ability. See WorldGravity - it is NOT UE's default.
        projectile.Velocity = new FVector {
            X = projectile.Velocity.X,
            Y = projectile.Velocity.Y,
            Z = projectile.Velocity.Z - WorldGravity * GravityScale * dt
        };

        var next = new FVector {
            X = projectile.SimulatedLocation.X + projectile.Velocity.X * dt,
            Y = projectile.SimulatedLocation.Y + projectile.Velocity.Y * dt,
            Z = projectile.SimulatedLocation.Z + projectile.Velocity.Z * dt
        };

        // THE HIGHER of the baked landscape and the height the thrower was standing at. The bake
        // covers the LANDSCAPE ONLY, so the warmup island, placed buildings, rocks and POI floors
        // are all invisible to it - a grenade thrown straight down on the warmup island fell 13,750
        // units through the world and exploded far below the player. See
        // AFortProjectileBase.ThrowerGroundZ for what this stands in for and where it is wrong.
        // TWO FLOORS, because they fail in opposite ways.
        //
        //   * The thrower's own height is a CONSTANT plane and is clamped against HARD. It cannot
        //     teleport a grenade anywhere, and a hard clamp is what stops the projectile leaving the
        //     world: with a "must have started above the ground" guard instead, one high terrain
        //     sample made the grenade read as already-underground, the guard then failed forever, and
        //     it fell 2270 units past the floor it had bounced off twice.
        //   * The baked landscape is NOISY - the bake has at least one known bogus spike - so it is
        //     only allowed to raise the floor by a plausible STEP. A reading far above the projectile
        //     is a spike and is ignored; one just under it is ground it is about to land on.
        //
        // The cost of the hard plane is stated in AFortProjectileBase.ThrowerGroundZ: a grenade thrown
        // off a cliff stops level with the cliff-top. Bounded, and far better than leaving the map.
        // The highest baked surface AT OR BELOW the projectile - landscape or placed mesh. Asking for
        // "the ground height" instead is what let a grenade sitting on a POI's first storey be told
        // about the terrain twenty metres underneath it; the two are separate grids now precisely so
        // this question can be answered properly. See TerrainHeightMap.
        // Where this step STARTED. Both halves below need it: the floor may only lift the projectile
        // by so much from where it was, and the sweep needs the segment's own origin.
        var from = projectile.SimulatedLocation;

        var baked = TerrainHeightMap.GetSurfaceUnder(next.X, next.Y, 0f, from.Z);
        var measured = TerrainGroundTruth.GetGroundHeight(next.X, next.Y, from.Z);

        // WHAT IS ACTUALLY KNOWN about the ground here, before deciding what to do when nothing is.
        //
        //   * The baked grids are still step-limited: a reading far ABOVE the projectile is a spike or
        //     a roof, not ground it is about to land on.
        //   * A measured cell takes no such limit - it is the client's own collision result for that
        //     spot - but may still only RAISE, because a walked cell records the surfaces people
        //     walked and says nothing about the ones they did not. A grenade crossing a POI's upper
        //     storey must not sink to the ground floor someone walked underneath it.
        //     PROJECTILE_MEASURED_FLOOR_LOWERS=1 restores the other behaviour.
        float? known = null;
        var knownSource = "";

        if (baked is { } bakedZ && bakedZ - next.Z < MaxGroundStep) {
            known = bakedZ;
            knownSource = "the baked height grid";
        }

        if (measured is { } measuredZ && (known is not { } k || measuredZ > k || MeasuredFloorLowers)) {
            known = measuredZ;
            knownSource = "measured ground";
        }

        // THE THROWER'S PLANE IS NOW A LAST RESORT, not a floor under everything - but only once the
        // map's own collision is loaded. It is a constant at the height the thrower was standing, so
        // it cannot follow ground that drops away: it is what stopped a grenade level with a cliff
        // top, and what would hold one at the mouth of a cave whose floor is below the hillside
        // outside. Deleting it was unsafe while the server had nothing else; with collision hulls
        // loaded, a grenade that falls past a grid's knowledge still meets the real geometry on the
        // way down, and the POI-floor case that made this dangerous is caught by the hull sweep.
        //
        // Without hulls the old rule stands exactly as it was, because without them the plane really
        // is the only thing between a projectile and the bottom of the world.
        // ...AND IT ONLY APPLIES WHILE THE PROJECTILE IS STILL ABOVE IT. Once something the server does
        // know about has legitimately let the projectile below the thrower's height, re-imposing that
        // height is fighting reality: it teleports the projectile back UP. That is what "it fell into
        // the cave, then shot back to the top and went off on the far rim" was.
        var planeApplies = from.Z >= projectile.ThrowerGroundZ - 1f;

        // THE THROWER'S PLANE IS A FLOOR AGAIN, and dropping it was a real regression - the ground and
        // player builds "went see-through" the moment it went away. The reason is in the bake's own
        // numbers: measured against cells players had walked, the baked grids answer at all for only
        // half of them and are typically tens of units low where they do. As a MINIMUM the plane hides
        // that; as a fallback used only where the grids are silent, every cell the grids DO answer for
        // let a grenade settle a metre inside the ground.
        //
        // So the grids may only ever RAISE it, exactly as before.
        var planeFloor = planeApplies ? projectile.ThrowerGroundZ : float.MinValue;
        var floor = MathF.Max(planeFloor, known ?? float.MinValue);
        // NO FLOOR AT ALL is a legitimate answer once real collision is loaded - inside a cave the
        // plane no longer applies and the grids know nothing, and the right behaviour is to keep
        // falling until actual geometry stops it. Without hulls that would mean leaving the world, so
        // the plane comes back as the hard backstop it used to be.
        if (floor <= float.MinValue && !WorldCollision.Loaded) floor = projectile.ThrowerGroundZ;

        // MEASURED GROUND IS THE ONE THING ALLOWED TO PULL IT DOWN, and only with the map's real
        // collision loaded. A walked cell is the client's own collision result, so where it says the
        // floor is below the thrower's height - a cave mouth, a pit, the bottom of a shaft - it is
        // right and the plane is not. What made this unsafe before was the POI upper storey, where a
        // walked ground-floor cell would drag a grenade through the floor above it; the hull sweep
        // now catches that floor on the way down, because it is a placed mesh with real collision.
        //
        // The consequence to know: a cave nobody has walked into still holds a grenade at its mouth.
        // Walking in is what teaches the server the floor.
        if (WorldCollision.Loaded && measured is { } below && below < floor) {
            floor = below;
            knownSource = "measured ground";
        }

        // Named by whichever value actually won, so the log line below attributes a stop rather than
        // listing what was available.
        var floorSource = known is { } chosen && MathF.Abs(floor - chosen) < 0.01f
            ? knownSource
            : "the thrower's plane";

        // A FLOOR STOPS A DESCENT; IT DOES NOT PUSH A PROJECTILE UP. Setting next.Z = floor lifts the
        // projectile whenever the floor is above it, and the floor changes from cell to cell as the
        // sources disagree - so a grenade rolling into a cell whose grid has no data was thrown a
        // metre and a half into the air. A rise of up to one step is real ground (a slope, a kerb);
        // more than that means the server's idea of the floor jumped, not the world.
        if (next.Z <= floor && floor - from.Z <= MaxGroundStep) {
            next.Z = floor;

            // NAMED, and capped like the surface bounces. A grenade that refuses to enter a cave
            // mouth looks identical whether a wall span filled the opening or a floor it cannot fall
            // below held it at the entrance; the surface bounces already say which shape they hit, so
            // this line is what separates "something blocked it" from "the ground came up to meet it".
            if (projectile.LoggedBounces < 4) {
                projectile.LoggedBounces++;
                Console.WriteLine($"FortProjectileSystem:   floor contact at {next} - {floorSource} " +
                                  $"at Z {floor:F0} (thrower plane {projectile.ThrowerGroundZ:F0}, " +
                                  $"baked {(baked is { } b2 ? b2.ToString("F0") : "none")})");
            }

            // BOUNCE OR SLIDE, the way UProjectileMovementComponent does. Treating every contact as a
            // bounce was wrong twice over: a shallow touchdown rebounds by almost nothing, so the next
            // tick contacts again - five "bounces" in five ticks, and five bounces detonate a grenade -
            // and nothing bled off the horizontal speed between them, so it skated for kilometres.
            //
            // The factors are READ from B_Prj_Athena_Grenade_Base's ProjectileComp0 (Bounciness 0.3,
            // Friction 0.4) and ComputeBounceResult applies them to DIFFERENT axes: tangential by
            // (1 - Friction), and only perpendicular by Bounciness. The SLIDE THRESHOLD is chosen.
            var rebound = -projectile.Velocity.Z * Bounciness;

            if (rebound < SlideSpeed) {
                var drag = MathF.Pow(1f - BounceFriction, dt);
                projectile.Velocity = new FVector {
                    X = projectile.Velocity.X * drag,
                    Y = projectile.Velocity.Y * drag,
                    Z = 0f
                };
            } else {
                projectile.BounceCount++;
                projectile.Velocity = new FVector {
                    X = projectile.Velocity.X * (1f - BounceFriction),
                    Y = projectile.Velocity.Y * (1f - BounceFriction),
                    Z = rebound
                };
            }
        }

        // THREE SOURCES, ONE CONTACT, AND THEY DO NOT OVERLAP. A player's build is a live actor with a
        // box this server placed itself. The collision hulls are the simple shapes the CLIENT uses,
        // which is 86-96% of a POI's meshes. The voxel wall map now carries ONLY what has no simple
        // collision - the terrain shells, cliffs, cave mouths and merged HLOD ground, which in the
        // game use complex collision, i.e. their render triangles. Baking walls for a mesh that has
        // hulls was 95% of that file (36.8M triangles down to 2.0M) and it did harm as well as waste:
        // the coarse cell decided first and gave an archway its thickness back.
        //
        // Each is asked for the same thing - where the segment first meets something, and the SURFACE
        // NORMAL there - and the nearest answer wins. A box sweep gives an axis rather than a normal,
        // which is turned into one here so a single bounce rule serves all three.
        var buildHit = BuildingStructuralSupportSystem.SweepToBuild(from, next);
        var hullHit = WorldCollision.Sweep(from, next);
        var wallHit = TerrainWalls.Sweep(from, next);

        var contact = Nearest(from,
            buildHit is { } b ? (b.Point, AxisNormal(b.Axis, projectile.Velocity), "a build") : null,
            hullHit is { } h ? (h.Point, h.Normal, "world collision") : null,
            wallHit is { } w ? (w.Point, AxisNormal(w.Axis, projectile.Velocity), "a baked wall") : null);

        if (contact is { } surface) {
            // REST ON IT, do not hover in front of it. This used to leave the projectile where it was
            // and only change the velocity, so a grenade that landed on a built floor never actually
            // came to rest: it re-contacted every tick, and since this branch had no slide rule -
            // only the ground plane got one - each of those counted as a bounce and five bounces
            // detonate a grenade. It went off within a few ticks of touching a floor.
            //
            // Backed off ALONG THE NORMAL by a hair so the next step starts outside the surface
            // rather than exactly on it, where floating point can put it back inside.
            var v = projectile.Velocity;
            var n = surface.Normal;

            projectile.SimulatedLocation = new FVector {
                X = surface.Point.X + n.X * 0.5f,
                Y = surface.Point.Y + n.Y * 0.5f,
                Z = surface.Point.Z + n.Z * 0.5f
            };

            // UProjectileMovementComponent::ComputeBounceResult, in full rather than per axis: the
            // velocity is split into the component along the surface normal and the tangent, the
            // tangent is scaled by (1 - Friction) and only the normal component by Bounciness. The
            // old code did this on whichever axis of a box was entered, which is the same arithmetic
            // when the surface happens to be axis-aligned and simply wrong when it is not - and a
            // real collision hull is a sloped roof, a rock face or a rounded kerb as often as a wall.
            var vDotN = v.X * n.X + v.Y * n.Y + v.Z * n.Z;
            var projected = new FVector { X = n.X * -vDotN, Y = n.Y * -vDotN, Z = n.Z * -vDotN };
            var reboundSpeed = MathF.Abs(vDotN);

            // A rebound too small to matter is a SLIDE rather than one of the five bounces that
            // detonate a grenade.
            var sliding = reboundSpeed * Bounciness < SlideSpeed;
            if (!sliding) projectile.BounceCount++;

            var tangentScale = sliding ? MathF.Pow(1f - BounceFriction, dt) : 1f - BounceFriction;
            var bounce = sliding ? 0f : Bounciness;

            projectile.Velocity = new FVector {
                X = (v.X + projected.X) * tangentScale + projected.X * bounce,
                Y = (v.Y + projected.Y) * tangentScale + projected.Y * bounce,
                Z = (v.Z + projected.Z) * tangentScale + projected.Z * bounce
            };

            // WHAT IT BOUNCED OFF AND WHICH WAY. Capped per projectile: the surface normal and the
            // before/after velocity are the only things that can distinguish "the shape is in the
            // wrong place", "the entry face was picked wrong" and "the reflection itself is wrong",
            // and a summary line cannot tell them apart.
            if (projectile.LoggedBounces < 4) {
                projectile.LoggedBounces++;
                Console.WriteLine($"FortProjectileSystem:   hit {surface.What} at {projectile.SimulatedLocation} " +
                                  $"normal ({n.X:F2},{n.Y:F2},{n.Z:F2}) - velocity {v} -> {projectile.Velocity}" +
                                  $"{(sliding ? " (slide)" : " (bounce)")}");
            }

            if (SpeedSquared(projectile.Velocity) < StopSpeed * StopSpeed) projectile.Velocity = new FVector();
            PublishMovement(projectile);
            return;
        }

        projectile.SimulatedLocation = next;

        PublishMovement(projectile);
    }

    /// <summary>
    ///     Sends the server's own simulated transform, when PROJECTILE_REPLICATE_MOVEMENT is set.
    ///
    ///     This is the switch between the two models: off, the client flies its own grenade with real
    ///     collision and the server guesses in parallel, so the flight looks right and the DAMAGE
    ///     lands wherever the guess ended up; on, the client follows the server, so what the player
    ///     sees IS what the damage is computed from - and whatever the server gets wrong becomes
    ///     visible instead of silently mismatching. It is a debugging tool and the first half of
    ///     moving authority to the server, and those turned out to be the same thing: replicating the
    ///     flight is what made "the arc is too long" observable at a glance.
    /// </summary>
    private static void PublishMovement(AFortProjectileBase projectile) {
        // Settle FIRST, and unconditionally. This is flight state, not presentation - leaving it
        // behind the knob would make the projectile behave differently depending on whether anyone
        // was watching. A hop smaller than the threshold is the end of the flight, not a bounce.
        if (SpeedSquared(projectile.Velocity) < StopSpeed * StopSpeed) projectile.Velocity = new FVector();

        if (!ReplicateMovement) return;

        projectile.bReplicateMovement = true;
        projectile.SetActorLocation(projectile.SimulatedLocation);

        // Not GatherCurrentMovement: that DERIVES velocity from successive positions because a pawn's
        // is never known. Here it is known exactly, so it is copied rather than guessed.
        projectile.ReplicatedMovement.Location = projectile.SimulatedLocation;
        projectile.ReplicatedMovement.LinearVelocity = projectile.Velocity;
        projectile.ReplicatedMovement.Rotation = projectile.GetActorRotation();
    }

    /// <summary>UProjectileMovementComponent's BounceVelocityStopSimulatingThreshold - the engine default.</summary>
    private const float StopSpeed = 5f;

    /// <summary>
    ///     Below this rebound speed a ground contact is a SLIDE rather than a bounce - chosen, and
    ///     the reason a grazing touchdown no longer burns one of the five bounces that detonate a
    ///     grenade. See the ground-contact block in StepFlight.
    /// </summary>
    private const float SlideSpeed = 150f;

    /// <summary>
    ///     How far the baked landscape may raise the simulation floor in one step before it is read as
    ///     a bogus spike rather than ground. Chosen; the bake is known to contain at least one.
    /// </summary>
    private const float MaxGroundStep = 128f;

    /// <summary>
    ///     FORTNITE'S GRAVITY IS -2800, not UE's -980. Read from `DefaultGravityZ` in the shipped
    ///     FortniteGame/Config/DefaultEngine.ini.
    ///
    ///     Assuming the engine default made the server's grenades fall at barely a third of the real
    ///     rate, so they sailed far past where the player's own grenade landed - which is exactly what
    ///     replicating the server's flight made visible the moment it could be seen at all. Worth
    ///     remembering beyond projectiles: anything here that integrates gravity wants this number.
    /// </summary>
    private static float WorldGravity =>
        float.TryParse(Environment.GetEnvironmentVariable("WORLD_GRAVITY_Z"), out var s) && s > 0 ? s : 2800f;

    /// <summary>
    ///     PROJECTILE_REPLICATE_MOVEMENT=1 sends the server's own simulated position to the client, so
    ///     the grenade the player watches is the one the damage is computed from. Off by default: the
    ///     server has less collision than the client does, so turning it on trades a flight that looks
    ///     right for a flight that is HONEST about what the server believes. See where it is used.
    /// </summary>
    private static bool ReplicateMovement =>
        Environment.GetEnvironmentVariable("PROJECTILE_REPLICATE_MOVEMENT") is "1";

    /// <summary>
    ///     How far a pawn's replicated location sits ABOVE its feet.
    ///
    ///     AActor::GetActorLocation on a character is the CAPSULE CENTRE, not the ground it stands on,
    ///     and using it as the simulation floor put that floor about a metre too high everywhere - so
    ///     every grenade "landed" in mid-air, bounced early, and every explosion was biased upward.
    ///     The log showed it plainly: a pawn at Z=3941 standing where the baked landscape reads 3864.
    ///
    ///     96 is the standard Fortnite character capsule half-height. It is NOT read from the cooked
    ///     asset - PlayerPawn_Athena does not override CapsuleHalfHeight, it inherits it - so this is
    ///     the one geometry number here that is taken on convention rather than from the paks.
    ///     PAWN_CAPSULE_HALF_HEIGHT overrides it.
    /// </summary>
    private static float CapsuleHalfHeight =>
        float.TryParse(Environment.GetEnvironmentVariable("PAWN_CAPSULE_HALF_HEIGHT"), out var s) && s > 0 ? s : 96f;

    /// <summary>
    ///     Whether a measured surface may pull the simulation floor DOWN as well as up. Off, because
    ///     a walked cell records the surfaces people walked and says nothing about the ones they did
    ///     not - see the floor block in StepFlight.
    /// </summary>
    private static bool MeasuredFloorLowers =>
        Environment.GetEnvironmentVariable("PROJECTILE_MEASURED_FLOOR_LOWERS") is "1";

    /// <summary>
    ///     The outward normal of an axis-aligned face, chosen to oppose the motion - the bridge
    ///     between the box sweeps, which report which axis was entered, and the one bounce rule.
    /// </summary>
    private static FVector AxisNormal(int axis, FVector velocity) {
        var along = axis == 0 ? velocity.X : axis == 1 ? velocity.Y : velocity.Z;
        var sign = along < 0f ? 1f : -1f;

        return new FVector {
            X = axis == 0 ? sign : 0f,
            Y = axis == 1 ? sign : 0f,
            Z = axis == 2 ? sign : 0f
        };
    }

    /// <summary>The contact closest to where the step started, of however many were found.</summary>
    private static (FVector Point, FVector Normal, string What)? Nearest(
        FVector from, params (FVector Point, FVector Normal, string What)?[] candidates) {
        (FVector Point, FVector Normal, string What)? best = null;
        var bestDistance = float.MaxValue;

        foreach (var candidate in candidates) {
            if (candidate is not { } c) continue;

            var dx = c.Point.X - from.X;
            var dy = c.Point.Y - from.Y;
            var dz = c.Point.Z - from.Z;
            var distance = dx * dx + dy * dy + dz * dz;

            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = c;
        }

        return best;
    }

    /// <summary>
    ///     Whether the map's own geometry stands between a blast and something in its radius.
    ///
    ///     BOTH WORLD SOURCES, because they describe different halves of the map and neither is a
    ///     superset: the collision hulls are every mesh that has simple collision, the voxel spans are
    ///     the terrain shells, cliffs and cave mouths that do not.
    ///
    ///     OFF WITH BLAST_WORLD_LINE_OF_SIGHT=0. Worth having a switch: a false "blocked" here is
    ///     invisible - the grenade simply does no damage and looks weak - so being able to take the
    ///     test out of the picture in one run is how that gets diagnosed rather than argued about.
    /// </summary>
    private static bool WorldLineBlocked(FVector from, FVector to) {
        if (Environment.GetEnvironmentVariable("BLAST_WORLD_LINE_OF_SIGHT") is "0") return false;

        if (WorldCollision.Sweep(from, to) is not null) return true;

        // NOTHING SHIELDS YOU FROM INSIDE ITSELF. The voxel spans are 32-unit cells, so a grenade
        // resting against a cliff face or on a terrain shell is very often INSIDE one - and that
        // sweep reports a hit on its first sample, which would make every blast on a hillside deal no
        // damage at all. The hull sweep needs no equivalent guard: a segment that starts inside a
        // convex body has no entry crossing to report, so it already declines.
        if (TerrainWalls.IsSolid(from)) return false;

        return TerrainWalls.Sweep(from, to) is not null;
    }

    private static float SpeedSquared(FVector v) => v.X * v.X + v.Y * v.Y + v.Z * v.Z;


    /// <summary>
    ///     Radial damage, at wherever the server's own simulation ended up.
    ///
    ///     The SHAPE is the ability's, not an invention: its effect container's TargetSelection is a
    ///     500-unit Sphere with an Overlap test. The MAGNITUDES are the item's UtilityItemDamage row,
    ///     which has no falloff at all - DmgPB, DmgMid, DmgLong and DmgMaxRange are all 100 for a
    ///     player, and all 375 for the environment - so everything inside the sphere takes the full
    ///     amount.
    ///
    ///     Actors come from the net driver's NetworkObjectList, not the level's actor array, for the
    ///     reason recorded on InFlight: a runtime-spawned actor is not in the latter, and a pass that
    ///     walks the wrong one finds nothing and says nothing about it.
    ///
    ///     LINE OF SIGHT IS NOT CHECKED. The real ability picks between GE_Damage_Explosive_LineOfSight
    ///     and _NoLineOfSight, and its target filter sets bExcludeObstructedByWorld; this server has
    ///     nothing to trace against, so a grenade damages through a wall. Same missing capability as
    ///     StepFlight's collision.
    /// </summary>
    private static void Explode(UWorld world, AFortProjectileBase projectile) {
        var origin = projectile.SimulatedLocation;
        var radiusSquared = ExplosionRadius * ExplosionRadius;

        // THE ITEM'S OWN NUMBERS, not the frag grenade's. Everything thrown used to explode for
        // 100/375 because that is the one ability whose values had been read - so a shockwave
        // grenade (really 5), a stink bomb (really 5) and a Playset grenade (really 0) all killed
        // outright. See FortProjectileStats; the frag's numbers remain the fallback for an item the
        // table does not know, and the fallback SAYS SO rather than passing silently.
        var stats = FortProjectileStats.For(projectile.SourceItemName);
        var playerDamage = stats?.Player ?? PlayerDamage;
        var environmentDamage = stats?.Environment ?? EnvironmentDamage;

        if (stats is null && projectile.SourceItemName is { Length: > 0 } unknown)
            Console.WriteLine($"FortProjectileSystem: '{unknown}' has no row in FortProjectileStats - " +
                              $"exploding for the frag grenade's {PlayerDamage:F0}/{EnvironmentDamage:F0}.");
        var instigator = projectile.GetInstigator();

        var pawnsHit = 0;
        var buildingsHit = 0;
        var blocked = 0;
        var blockedByWorld = 0;

        foreach (var actor in world.NetDriver?.NetworkObjectList.ToArray() ?? Array.Empty<AActor>()) {
            // An actor whose position this server never learned reads as (0,0,0) - level pieces
            // registered by PATH are the whole population of that. Without this guard an explosion
            // anywhere near the world origin would damage every one of them at once, which is the
            // same trap the distance culling hit (see AActor.bHasKnownLocation).
            if (!actor.bHasKnownLocation) continue;
            if (FVector.DistSquared(origin, actor.GetActorLocation()) > radiusSquared) continue;

            // LINE OF SIGHT, against player builds AND the map itself. The real ability sets
            // bExcludeObstructedByWorld and picks between GE_Damage_Explosive_LineOfSight and
            // _NoLineOfSight; until this session the server could only answer the half players
            // create, because it had no shape for the map. It has one now - the same collision hulls
            // the client uses - so a grenade behind a POI wall stops killing through it.
            //
            // The BUILDING BEING TESTED is excluded from its own trace: a piece is solid, so a blast
            // right against a wall would otherwise be judged as blocked from damaging that wall. The
            // world trace needs no such exclusion, since nothing here damages world geometry.
            if (actor is not ABuildingActor
                && BuildingStructuralSupportSystem.IsLineBlocked(origin, actor.GetActorLocation())) {
                blocked++;
                continue;
            }

            if (actor is not ABuildingActor && WorldLineBlocked(origin, actor.GetActorLocation())) {
                blockedByWorld++;
                continue;
            }

            switch (actor) {
                case ABuildingActor building when !building.bDestroyed
                        && !BuildingStructuralSupportSystem.IsLineBlocked(origin, building.GetActorLocation(), building)
                        && !WorldLineBlocked(origin, building.GetActorLocation()):
                    BuildingStructuralSupportSystem.ApplyDamage(building, (int) environmentDamage);
                    buildingsHit++;
                    break;

                // bExcludeInstigator is FALSE on this ability's target filter: a real grenade hurts
                // the player who threw it, which is exactly what the ability's own
                // DistanceFromInstigatorCheck (512 units) exists to warn about.
                //
                // BOTH ways to the PlayerState, because they are populated separately and either can
                // be the one that is set: AController.Possess assigns pawn.PlayerState directly AND
                // pawn.SetController, so reaching only through the controller was one link too many.
                // The first live test hit 0 pawns with the thrower 390 units away.
                case APawn pawn:
                    var victim = pawn.PlayerState ?? pawn.Controller?.PlayerState;
                    if (victim == null) {
                        Console.WriteLine($"FortProjectileSystem:   pawn '{pawn.GetFName()}' is in range but has no " +
                                          "PlayerState on either route - not damaged.");
                        break;
                    }

                    FortDamageSystem.ApplyDamage(victim, playerDamage, EDeathCause.Grenade);
                    pawnsHit++;
                    if (pawn == instigator)
                        Console.WriteLine("FortProjectileSystem:   ...including the thrower - the real ability does not exclude them either.");
                    break;
            }
        }

        Console.WriteLine($"FortProjectileSystem: explosion of {projectile.SourceItemName ?? "?"} at {origin} " +
                          $"radius {ExplosionRadius:F0} hit " +
                          $"{pawnsHit} pawn(s) for {playerDamage:F0} and {buildingsHit} building(s) for {environmentDamage:F0}" +
                          $"{(blocked > 0 ? $", {blocked} shielded by player builds" : "")}" +
                          $"{(blockedByWorld > 0 ? $", {blockedByWorld} shielded by the map" : "")}.");

        // WHY THE THROWER WAS OR WAS NOT HIT, unconditionally. "0 pawns" has three completely
        // different causes - too far, not in the net driver's list at all, or no known location -
        // and the summary line above cannot tell them apart. This one names which, so a miss costs
        // a reading rather than a round.
        if (instigator != null) {
            var inList = world.NetDriver?.NetworkObjectList.Contains(instigator) ?? false;
            var distance = MathF.Sqrt(FVector.DistSquared(origin, instigator.GetActorLocation()));
            var baked = TerrainHeightMap.GetGroundHeight(origin.X, origin.Y);
            Console.WriteLine($"FortProjectileSystem:   thrower '{instigator.GetFName()}' at " +
                              $"{instigator.GetActorLocation()} is {distance:F0} units away " +
                              $"(radius {ExplosionRadius:F0}), inNetworkObjectList={inList}, " +
                              $"knownLocation={instigator.bHasKnownLocation}; " +
                              $"{BuildingStructuralSupportSystem.PiecesWithin(origin, 1000f)} build(s) within 1000 " +
                              $"of the blast, {BuildingStructuralSupportSystem.PiecesWithin(instigator.GetActorLocation(), 1000f)} " +
                              "near the thrower; baked landscape under " +
                              $"the blast = {(baked is { } b ? b.ToString("F0") : "NONE")}, " +
                              $"simulation floor = {projectile.ThrowerGroundZ:F0}, " +
                              $"bounces = {projectile.BounceCount}.");
        }
    }

    /// <summary>One throw waiting for its ability to be told it is over.</summary>
    private sealed class FPendingAbilityEnd {
        public required APlayerState PlayerState;
        public required UFortAbilitySystemComponent AbilitySystem;
        public required int Handle;
        public required FPredictionKey PredictionKey;
        public required float EndsAtWorldTime;
        public AFortWeapon? Weapon;
        public required UObject AbilityClass;
    }

    /// <summary>
    ///     PROJECTILE_REGRANT=0 turns the re-grant workaround off, leaving only the ClientEndAbility
    ///     handshake - which is the right thing to run when working out why that handshake does not
    ///     land on its own.
    /// </summary>
    private static bool PROJECTILE_REGRANT => Environment.GetEnvironmentVariable("PROJECTILE_REGRANT") is not "0";

    private static readonly List<FPendingAbilityEnd> PendingEnds = new();

    public static void Tick(UWorld world) {
        for (var i = PendingEnds.Count - 1; i >= 0; i--) {
            var pending = PendingEnds[i];
            if (world.TimeSeconds < pending.EndsAtWorldTime) continue;

            PendingEnds.RemoveAt(i);
            world.NetDriver?.SendClientEndAbility(pending.PlayerState, pending.AbilitySystem,
                                                  pending.Handle, pending.PredictionKey);

            // AND RE-GRANT THE SPEC, which is a workaround and is labelled as one.
            //
            // ClientEndAbility above is the CORRECT mechanism and its payload has been verified bit
            // for bit against the engine's reader (handle, ActivationMode=Confirmed, and the same
            // prediction key the client activated with, which is what RemoteEndOrCancelAbility
            // matches on). It nevertheless did not free the ability in testing, and the reason is
            // not visible from this side of the wire.
            //
            // What DOES work, repeatedly and observably, is a FRESH SPEC: re-equipping the grenade
            // by hand restores the throw every time, and FortEmoteSystem - the one ability in this
            // project that can be used over and over - clears its spec after every use for what is
            // very likely the same underlying reason. Clearing and re-granting reproduces that
            // deliberately instead of asking the player to do it.
            //
            // The cost is a spec churn per throw: a new handle, a new replicated ability instance,
            // and one FastArray delta. Remove this the moment the end handshake is understood.
            if (pending.Weapon is { } weapon && PROJECTILE_REGRANT) {
                pending.AbilitySystem.ClearAbility(pending.Handle);

                var regranted = pending.AbilitySystem.GrantAbility(
                    pending.AbilityClass, weapon, replicateInstance: true);
                weapon.GrantedAbilitySpecHandle = regranted.Handle;

                Console.WriteLine($"FortProjectileSystem: re-granted the throw ability as spec {regranted.Handle} " +
                                  $"(was {pending.Handle}) - a fresh spec is what makes the next throw possible today.");
            }

            Console.WriteLine($"FortProjectileSystem: told the client to end throw ability spec {pending.Handle} " +
                              $"after {PostThrowEndDelay:F2}s.");
        }

        if (InFlight.Count == 0) return;

        List<AFortProjectileBase>? expired = null;

        foreach (var projectile in InFlight) {
            if (!projectile.bHasExploded) StepFlight(world, projectile);

            var bouncedOut = projectile.BounceCount >= BouncesTillExplode;
            if (!projectile.bHasExploded && (bouncedOut || world.TimeSeconds >= projectile.ExplodesAtWorldTime)) {
                // No MarkPropertyDirty: the per-tick layout diff finds this by itself, the same way
                // every other replicated property on this server is sent.
                projectile.bHasExploded = true;

                // Kill() in the same breath. bKillOnExplode is False on the grenade base, so the
                // explosion by itself leaves a live projectile behind, and the client goes on
                // replicating and simulating it. (This was once suspected of being what blocked a
                // second throw - it was not; see the re-grant note below for what actually did.)
                projectile.bIsBeingKilled = true;

                // Bring the destroy forward to just after the explosion instead of the flight-time
                // safety net. Not at the same instant: both properties have to actually reach the
                // client, and destroying the actor first would close the channel with them unsent.
                projectile.ExpiresAtWorldTime = world.TimeSeconds + KillDelay;

                Explode(world, projectile);

                Console.WriteLine($"FortProjectileSystem: '{projectile.GetFName()}' exploded at " +
                                  $"{projectile.SimulatedLocation} " +
                                  $"({(bouncedOut ? $"{projectile.BounceCount} bounces" : "fuse")}) - " +
                                  $"destroying it in {KillDelay:F1}s.");
            }

            if (world.TimeSeconds >= projectile.ExpiresAtWorldTime)
                (expired ??= new List<AFortProjectileBase>()).Add(projectile);
        }

        if (expired == null) return;
        foreach (var projectile in expired) {
            Console.WriteLine($"FortProjectileSystem: '{projectile.GetFName()}' expired - destroying it server-side.");
            InFlight.Remove(projectile);
            projectile.Destroy();
        }
    }
}
