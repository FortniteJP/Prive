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

    /// <summary>This world's share of FortProjectileSystem's state - see FWorldSubsystem.</summary>
    private sealed class FProjectileState : FWorldSubsystem {
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
        public float Speed =>
            float.TryParse(Options.Get("PROJECTILE_SPEED"), out var s) && s > 0 ? s : 4000f;

        /// <summary>
        ///     How long a projectile actor lives on the server before it is destroyed.
        ///
        ///     NOT a fuse - the client owns the fuse and the explosion, and this server sees neither. It
        ///     is only here so the actor and its channel do not leak for the rest of the match. Longer
        ///     than any real fuse on purpose: destroying one early would delete a grenade out from under
        ///     the client's own explosion.
        /// </summary>
        public float Lifetime =>
            float.TryParse(Options.Get("PROJECTILE_LIFETIME"), out var s) && s > 0 ? s : 15f;

        /// <summary>
        ///     GA_Athena_Grenade_WithTrajectory_C's PostThrowEndDelay, read from the pak: 0.4 seconds.
        ///
        ///     This is how long AFTER the projectile appears that the real ability ends. Its graph is
        ///     literally `Created -> AthenaProjectileSpawned -> WaitDelay(PostThrowEndDelay) ->
        ///     K2_AbilityCompleted`, and 0.4s is why a real player can throw grenades about twice a
        ///     second rather than once per fuse. PROJECTILE_END_DELAY overrides it.
        /// </summary>
        public float PostThrowEndDelay =>
            float.TryParse(Options.Get("PROJECTILE_END_DELAY"), out var s) && s > 0 ? s : 0.4f;

        /// <summary>
        ///     The projectile's gravity scale, as the ABILITY passes it to SpawnProjectileAndWait: 0.8.
        ///
        ///     NOT the 0.7 on B_Prj_Athena_Grenade_Base - that is the Blueprint's own default, and the
        ///     task overwrites it with the ability's value on every spawn. Both are real numbers in the
        ///     paks and picking the wrong one is a plausible-looking arc that is quietly 12% off.
        /// </summary>
        public float GravityScale =>
            float.TryParse(Options.Get("PROJECTILE_GRAVITY_SCALE"), out var s) && s > 0 ? s : 0.8f;

        /// <summary>
        ///     The explosion's radius in units, read from the ability's own effect container:
        ///     TargetSelection.List[0] is Shape=Sphere, TestType=Overlap, Range=500.
        /// </summary>
        public float ExplosionRadius =>
            float.TryParse(Options.Get("PROJECTILE_RADIUS"), out var s) && s > 0 ? s : 500f;

        /// <summary>
        ///     Damage to a PLAYER at any range inside the radius - the item's UtilityItemDamage row has
        ///     DmgPB, DmgMid, DmgLong and DmgMaxRange all equal to 100, i.e. no falloff at all.
        /// </summary>
        public float PlayerDamage =>
            float.TryParse(Options.Get("PROJECTILE_DAMAGE"), out var s) && s > 0 ? s : 100f;

        /// <summary>Damage to a BUILDING - the same row's EnvDmgPB/Mid/Long/MaxRange, all 375.</summary>
        public float EnvironmentDamage =>
            float.TryParse(Options.Get("PROJECTILE_ENV_DAMAGE"), out var s) && s > 0 ? s : 375f;

        /// <summary>
        ///     B_Prj_Athena_Grenade_Base's ProjectileComp0.Bounciness (0.3) and Friction (0.4) - the
        ///     coefficient of restitution and the tangential drag its bounces actually use. Read, not
        ///     tuned. PROJECTILE_BOUNCINESS / PROJECTILE_FRICTION override them.
        /// </summary>
        public float Bounciness =>
            float.TryParse(Options.Get("PROJECTILE_BOUNCINESS"), out var s) && s >= 0 ? s : 0.3f;

        public float BounceFriction =>
            float.TryParse(Options.Get("PROJECTILE_FRICTION"), out var s) && s >= 0 ? s : 0.4f;

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
        public float KillDelay =>
            float.TryParse(Options.Get("PROJECTILE_KILL_DELAY"), out var s) && s > 0 ? s : 1f;

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
        public float FuseTime =>
            float.TryParse(Options.Get("PROJECTILE_FUSE"), out var s) && s > 0 ? s : 2.75f;

        /// <summary>
        ///     Every projectile currently in flight, so the fuse has something to walk.
        ///
        ///     NOT world.PersistentLevel.Actors - and that mistake cost a whole live round. UWorld's
        ///     level actor array is not where SpawnActor puts a runtime actor; replication finds them
        ///     through UNetDriver.NetworkObjectList instead, which is why the grenade flew perfectly
        ///     (it was replicating) while the fuse never fired once (the tick walked an array it was
        ///     not in). A dedicated list depends on neither registry.
        /// </summary>
        public readonly List<AFortProjectileBase> InFlight = new();

        /// <summary>
        ///     FORTNITE'S GRAVITY IS -2800, not UE's -980. Read from `DefaultGravityZ` in the shipped
        ///     FortniteGame/Config/DefaultEngine.ini.
        ///
        ///     Assuming the engine default made the server's grenades fall at barely a third of the real
        ///     rate, so they sailed far past where the player's own grenade landed - which is exactly what
        ///     replicating the server's flight made visible the moment it could be seen at all. Worth
        ///     remembering beyond projectiles: anything here that integrates gravity wants this number.
        /// </summary>
        public float WorldGravity =>
            float.TryParse(Options.Get("WORLD_GRAVITY_Z"), out var s) && s > 0 ? s : 2800f;

        /// <summary>
        ///     PROJECTILE_REPLICATE_MOVEMENT=1 sends the server's own simulated position to the client, so
        ///     the grenade the player watches is the one the damage is computed from. Off by default: the
        ///     server has less collision than the client does, so turning it on trades a flight that looks
        ///     right for a flight that is HONEST about what the server believes. See where it is used.
        /// </summary>
        public bool ReplicateMovement =>
            Options.Get("PROJECTILE_REPLICATE_MOVEMENT") is "1";

        /// <summary>
        ///     Whether a measured surface may pull the simulation floor DOWN as well as up. Off, because
        ///     a walked cell records the surfaces people walked and says nothing about the ones they did
        ///     not - see the floor block in StepFlight.
        /// </summary>
        public bool MeasuredFloorLowers =>
            Options.Get("PROJECTILE_MEASURED_FLOOR_LOWERS") is "1";

        public readonly List<FPendingAbilityEnd> PendingEnds = new();

        /// <summary>
        ///     A server-initiated prediction key counter, exactly like the emote system's and for the
        ///     same reason - see FPredictionKey::CreateNewServerInitiatedKey. Separate because the two
        ///     are independent streams and sharing a counter would only couple them.
        /// </summary>
        public short _nextDancePredictionKey = 1;

        /// <summary>
        ///     CLEARING PushMomentum STOPS THE VICTIM DEAD IN MID-AIR, and that is not a side effect -
        ///     it is the whole of what the zero branch does.
        ///
        ///     `AFortPawn::OnRep_PushMomentum` (0x1419347C0) branches on the length: non-zero writes
        ///     Velocity.X/Y, and ZERO calls the movement component's vtable slot 0x400. For the real
        ///     PlayerPawn_Athena that slot is 0x140C5DB60, whose first three instructions are:
        ///
        ///         movsd  qword ptr [rcx+0xC4], xmm0     ; Velocity.X = Velocity.Y = 0
        ///         mov    dword ptr [rcx+0xCC], eax      ; Velocity.Z = 0
        ///
        ///     **All three components.** So the old fixed 0.5s clear was, half a second into every
        ///     throw, telling the client to freeze in the air - which is exactly the reported "it flies
        ///     a certain distance, then the impulse suddenly vanishes and it drops straight down".
        ///     (The engine's own UCharacterMovementComponent::StopActiveMovement only clears
        ///     Acceleration, which is what the vtable slot's NAME says and what this was reasoned from
        ///     the first time. Fortnite's override is the one that runs, and reading it was the only
        ///     way to know.)
        ///
        ///     Nothing else decays the throw. The client's own falling physics leaves lateral velocity
        ///     completely alone on this build: `BrakingDecelerationFalling` and `FallingLateralFriction`
        ///     are BOTH 0 in AFortPlayerPawnAthena's CDO, read out of the dump
        ///     (PriveDev/dumpwork/movedefaults.py), so ApplyVelocityBraking returns without touching
        ///     anything and a launched player keeps their speed until they hit something.
        ///
        ///     So the push is now taken off on LANDING instead of on a timer - where zeroing the
        ///     velocity is what landing means anyway - and the timeout below is only a backstop for a
        ///     landing this server never sees.
        /// </summary>
        public float PushMomentumMaxSeconds =>
            float.TryParse(Options.Get("PUSH_MOMENTUM_MAX_SECONDS"), out var seconds)
                ? seconds
                : 12.0f;

        /// <summary>
        ///     Deployed actors with an end time, and the reason there is one at all: the CLIENT already
        ///     removes these on its own - the shield dome's BeginPlay sets a timer off its LifespanTime
        ///     and the air strike's spawner has an InitialLifeSpan - so a server that keeps replicating
        ///     them is holding a channel open for something nobody can see any more.
        ///
        ///     Not a substitute for the client's timer and not trying to be: the durations here are read
        ///     from the same rows the Blueprint reads, so the two agree rather than one driving the other.
        /// </summary>
        public readonly List<(ABuildingActor Actor, float EndsAt)> Deployed = new();

        /// <summary>
        ///     The longest a low-gravity aura may stay on without the pawn being seen to land. Not a row
        ///     - the game ends it from the landing itself - but a backstop; see TickLowGravity for why
        ///     there has to be one. SHOCKWAVE_LOWGRAV_MAX_SECONDS overrides it.
        /// </summary>
        public float LowGravityMaxSeconds =>
            float.TryParse(Options.Get("SHOCKWAVE_LOWGRAV_MAX_SECONDS"), out var seconds)
                ? seconds
                : 12.0f;

        public readonly List<FThrownFlight> _flights = new();
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
        public float CapsuleHalfHeight =>
            float.TryParse(Options.Get("PAWN_CAPSULE_HALF_HEIGHT"), out var s) && s > 0 ? s : 96f;

        /// <summary>
        ///     PROJECTILE_REGRANT=0 turns the re-grant workaround off, leaving only the ClientEndAbility
        ///     handshake - which is the right thing to run when working out why that handshake does not
        ///     land on its own.
        /// </summary>
        public bool PROJECTILE_REGRANT => Options.Get("PROJECTILE_REGRANT") is not "0";
    }

    private static FProjectileState StateOf(UWorld world) => world.GetSubsystem<FProjectileState>();

    /// <summary>NumberOfBouncesTillExplode on B_Prj_Athena_Grenade_Base - a grenade that hits five surfaces goes off early.</summary>
    private const int BouncesTillExplode = 5;

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
        var state = StateOf(world);

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
        // THE ITEM'S OWN SPEED, not the frag grenade's. Nine abilities override the family's 4000
        // and the difference is visible: a firework mortar throws at 2500 and used to sail past
        // whatever it was aimed at, a clinger throws at 6000. See FortThrowSpeeds.
        var speed = FortThrowSpeeds.For(itemName, state.Speed);
        projectile.Velocity = new FVector { X = forward.X * speed, Y = forward.Y * speed, Z = forward.Z * speed };
        if (state.ReplicateMovement) projectile.ReplicatedMovement.LinearVelocity = projectile.Velocity;

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
        projectile.ThrowerGroundZ = pawn.GetActorLocation().Z - state.CapsuleHalfHeight;

        // SEED IT BEFORE THE CHANNEL OPENS. The open bunch carries whatever ReplicatedMovement holds,
        // and an unset one is Location (0,0,0) - so the client dutifully put every grenade at the world
        // origin the instant it was thrown and it simply vanished, while the server went on simulating
        // and damaging correctly. Filling it here means the first thing the client ever hears about
        // this projectile is where it actually is.
        if (state.ReplicateMovement) {
            projectile.bReplicateMovement = true;

            // A projectile does not override FRepMovement's quantization, so it uses the ENGINE
            // default rather than the pawn's RoundTwoDecimals. The level is not on the wire - both
            // ends read their own - so getting it wrong is silent and total: the client decodes the
            // position at the wrong scale and the grenade disappears.
            projectile.ReplicatedMovement.LocationQuantization =
                world.Options.Get("PROJECTILE_REP_SCALE") is "100"
                    ? (100u, 30u)   // the pawn's RoundTwoDecimals, if a projectile turns out to use it
                    : FRepMovement.RoundWholeNumber;
            projectile.ReplicatedMovement.Location = location;
            projectile.ReplicatedMovement.Rotation = direction;
        }
        projectile.ExplodesAtWorldTime = world.TimeSeconds + state.FuseTime;
        projectile.ExpiresAtWorldTime = world.TimeSeconds + state.Lifetime;
        projectile.SetRole(ENetRole.ROLE_Authority);
        projectile.SetReplicates(true);
        state.InFlight.Add(projectile);

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
                state.PendingEnds.Add(new FPendingAbilityEnd {
                    PlayerState = ps,
                    AbilitySystem = asc,
                    Handle = spec.Handle,
                    PredictionKey = key,
                    EndsAtWorldTime = world.TimeSeconds + state.PostThrowEndDelay,
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
                          $"(speed {state.Speed:F0}, fuse {state.FuseTime:F2}s, server-side lifetime {state.Lifetime:F0}s)");
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
        if (projectile.Velocity.X * projectile.Velocity.X + projectile.Velocity.Y * projectile.Velocity.Y > 1f)
            projectile.LastFlightYaw = MathF.Atan2(projectile.Velocity.Y, projectile.Velocity.X) * (180f / MathF.PI);

        var state = StateOf(world);

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
            Z = projectile.Velocity.Z - state.WorldGravity * state.GravityScale * dt
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

        if (measured is { } measuredZ && (known is not { } k || measuredZ > k || state.MeasuredFloorLowers)) {
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
            var rebound = -projectile.Velocity.Z * state.Bounciness;

            if (rebound < SlideSpeed) {
                var drag = MathF.Pow(1f - state.BounceFriction, dt);
                projectile.Velocity = new FVector {
                    X = projectile.Velocity.X * drag,
                    Y = projectile.Velocity.Y * drag,
                    Z = 0f
                };
            } else {
                projectile.BounceCount++;

                // ARMED FIRST, BOUNCED SECOND - and the order is the whole of it. This call used to
                // sit above the assignment below, so the deploying projectile's velocity was set to
                // zero and then immediately overwritten by the rebound: an impulse grenade stopped
                // and carried on bouncing anyway, which is exactly what was reported.
                projectile.Velocity = ArmOnHitDelay(world, projectile)
                    ? new FVector()
                    : new FVector {
                        X = projectile.Velocity.X * (1f - state.BounceFriction),
                        Y = projectile.Velocity.Y * (1f - state.BounceFriction),
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
        var buildHit = BuildingStructuralSupportSystem.Of(world).SweepToBuild(from, next);
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
            var sliding = reboundSpeed * state.Bounciness < SlideSpeed;
            var deploying = false;
            if (!sliding) {
                projectile.BounceCount++;

                // A FLOOR-ONLY DEPLOYABLE BOUNCES OFF ANYTHING STEEPER - see FDeployRule. The
                // ground-plane contact above never needs this test: its normal is straight up.
                var landsHere = FortDeployables.RuleFor(projectile.SourceItemName).MinFloorNormalZ is not { } minZ
                                || n.Z > minZ;
                deploying = landsHere && ArmOnHitDelay(world, projectile);
            }

            var tangentScale = sliding ? MathF.Pow(1f - state.BounceFriction, dt) : 1f - state.BounceFriction;
            var bounce = sliding ? 0f : state.Bounciness;

            // A piece that has landed to deploy does not bounce off whatever it landed on - same as
            // the ground case above.
            projectile.Velocity = deploying ? new FVector() : new FVector {
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
            PublishMovement(world, projectile);
            return;
        }

        projectile.SimulatedLocation = next;

        PublishMovement(world, projectile);
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
    private static void PublishMovement(UWorld world, AFortProjectileBase projectile) {
        var worldState = StateOf(world);

        // Settle FIRST, and unconditionally. This is flight state, not presentation - leaving it
        // behind the knob would make the projectile behave differently depending on whether anyone
        // was watching. A hop smaller than the threshold is the end of the flight, not a bounce.
        if (SpeedSquared(projectile.Velocity) < StopSpeed * StopSpeed) projectile.Velocity = new FVector();

        if (!worldState.ReplicateMovement) return;

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
    private static bool WorldLineBlocked(UWorld world, FVector from, FVector to) {
        if (world.Options.Get("BLAST_WORLD_LINE_OF_SIGHT") is "0") return false;

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
        var state = StateOf(world);

        var origin = projectile.SimulatedLocation;

        // THE ITEM'S OWN RADIUS. A shockwave grenade's is 500 (Default.KnockGrenade.Radius) and a
        // stink bomb's cloud is 512, where the frag's constant is what everything used to use. The
        // constant remains the fallback for an item with no row.
        var effect = FortGrenadeEffects.For(projectile.SourceItemName);
        var blastRadius = effect is { Radius: > 0f } ? effect.Value.Radius : state.ExplosionRadius;
        var radiusSquared = blastRadius * blastRadius;

        // THE ITEM'S OWN NUMBERS, not the frag grenade's. Everything thrown used to explode for
        // 100/375 because that is the one ability whose values had been read - so a shockwave
        // grenade (really 5), a stink bomb (really 5) and a Playset grenade (really 0) all killed
        // outright. See FortProjectileStats; the frag's numbers remain the fallback for an item the
        // table does not know, and the fallback SAYS SO rather than passing silently.
        var stats = FortProjectileStats.For(projectile.SourceItemName);
        var playerDamage = stats?.Player ?? state.PlayerDamage;
        var environmentDamage = stats?.Environment ?? state.EnvironmentDamage;

        // AND ZERO WHEN THE ITEM DOES NOT DAMAGE, which the stat row cannot tell you: a boogie
        // bomb's own WeaponStatHandle names the FRAG's row (100/375), so it exploded for 100 and
        // killed whoever it was meant to make dance. An impulse grenade, a shockwave, a smoke and a
        // stink bomb are all the same shape of wrong - the stink bomb damages through its CLOUD, not
        // its blast. See the generator for why this is spelled out rather than read.
        if (effect is { Damages: false }) {
            playerDamage = 0f;
            environmentDamage = 0f;
        }

        // A DEPLOYABLE DOES NOT EXPLODE, and the stat row cannot say so any more than it could for
        // the boogie bomb. `Athena_FireworksMortar`'s row is 10/40 - that is what one of the ROCKETS
        // does when it goes off, and the thing that lands is the mortar's HOLDER, which places an
        // emplacement and then fires them. Left alone, the holder detonated for the rocket's damage
        // the moment it touched the ground and the emplacement never got a chance to be the item.
        // The snowman and the shield bubble are already 0/0 in the table and are unaffected either
        // way; naming the rule here rather than per item is what keeps the next one right.
        if (FortDeployables.For(projectile.SourceItemName).Length > 0) {
            playerDamage = 0f;
            environmentDamage = 0f;
        }

        if (stats is null && projectile.SourceItemName is { Length: > 0 } unknown)
            Console.WriteLine($"FortProjectileSystem: '{unknown}' has no row in FortProjectileStats - " +
                              $"exploding for the frag grenade's {state.PlayerDamage:F0}/{state.EnvironmentDamage:F0}.");
        var instigator = projectile.GetInstigator();

        // Everyone the blast reached, for the NON-damage half. Collected rather than acted on
        // inline because the effects need the whole set (and because a boogie bomb's victims are the
        // people it damaged for zero).
        var caught = new List<APawn>();

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

            // NAMED, not just skipped. An actor with no usable position is a bug somewhere else, and
            // this loop is where it surfaced - as a NullReferenceException that took the server down
            // mid-match with a stack naming only DistSquared. See AActor.SetActorLocation.
            if (actor.GetActorLocation() is not { } actorLocation) {
                Console.WriteLine($"FortProjectileSystem: {actor.GetFName()} says it knows where it is but " +
                                  "has no location - skipped for this explosion.");
                continue;
            }

            if (FVector.DistSquared(origin, actorLocation) > radiusSquared) continue;

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
                && BuildingStructuralSupportSystem.Of(world).IsLineBlocked(origin, actor.GetActorLocation())) {
                blocked++;
                continue;
            }

            if (actor is not ABuildingActor && WorldLineBlocked(world, origin, actor.GetActorLocation())) {
                blockedByWorld++;
                continue;
            }

            switch (actor) {
                // AN ITEM THAT DOES NO ENVIRONMENTAL DAMAGE TOUCHES NOTHING, and the guard belongs
                // here rather than being left to ApplyDamage's own `amount <= 0` early-out. That
                // early-out does protect the hit points - a 0 never moved a building's health, which
                // was checked rather than assumed - but ApplyDamage answers "not destroyed" and the
                // caller then plays the DAMAGE REACTION anyway: EBA_Breaking and a damage cue with a
                // magnitude of zero. So an impulse or a shockwave landing among player builds made
                // every one of them flash as though hit, for nothing, which is not what either item
                // does (`Default.KnockGrenade`/`ShockwaveGrenade` do no environmental damage at all -
                // see FortGrenadeEffects.Damages).
                //
                // The counter is not incremented either: "hit 6 building(s) for 0" was a true
                // sentence about a thing that did not happen.
                case ABuildingActor when environmentDamage <= 0f:
                    break;

                case ABuildingActor building when !building.bDestroyed
                        && !BuildingStructuralSupportSystem.Of(world).IsLineBlocked(origin, building.GetActorLocation(), building)
                        && !WorldLineBlocked(world, origin, building.GetActorLocation()):
                    BuildingStructuralSupportSystem.Of(world).ApplyDamage(building, (int) environmentDamage);
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

                    // THE THROWER IS THE KILLER, and this used to leave that out. The instigator was
                    // already resolved above and used for logging, and ApplyDamage's fourth
                    // parameter is optional, so the omission was invisible: grenade kills reported
                    // `killer=none`, and everything downstream that needs a killer got nothing -
                    // DeathInfo.FinisherOrDowner, ClientOnPawnDied's killer references, and
                    // ClientReceiveKillNotification, whose whole content is (Killer, Killed).
                    //
                    // Found by trying to test the elimination feed: the user blew themselves up on
                    // purpose to produce a death WITH a killer, and it still logged killer=none.
                    // A self-elimination is a real killer, and Fortnite reports it as one.
                    var killer = instigator?.PlayerState ?? instigator?.Controller?.PlayerState;

                    FortDamageSystem.ApplyDamage(victim, playerDamage, EDeathCause.Grenade, killer);
                    caught.Add(pawn);
                    pawnsHit++;
                    if (pawn == instigator)
                        Console.WriteLine("FortProjectileSystem:   ...including the thrower - the real ability does not exclude them either.");
                    break;
            }
        }

        Console.WriteLine($"FortProjectileSystem: explosion of {projectile.SourceItemName ?? "?"} at {origin} " +
                          $"radius {blastRadius:F0} hit " +
                          $"{pawnsHit} pawn(s) for {playerDamage:F0} and {buildingsHit} building(s) for {environmentDamage:F0}" +
                          $"{(blocked > 0 ? $", {blocked} shielded by player builds" : "")}" +
                          $"{(blockedByWorld > 0 ? $", {blockedByWorld} shielded by the map" : "")}.");

        Deploy(world, projectile, origin);

        // The AIR STRIKE's damage is not this explosion - it is sixty rockets over the next eight
        // seconds. See FortAirstrike, and FortDeployables for the spawner that goes with it.
        if (FortAirstrike.ItemName.Equals(projectile.SourceItemName, StringComparison.OrdinalIgnoreCase))
            FortAirstrike.Begin(world, origin, projectile.GetInstigator());

        if (effect is { } grenade) ApplyGrenadeEffect(world, projectile, origin, grenade, caught);

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
                              $"(radius {blastRadius:F0}), inNetworkObjectList={inList}, " +
                              $"knownLocation={instigator.bHasKnownLocation}; " +
                              $"{BuildingStructuralSupportSystem.Of(world).PiecesWithin(origin, 1000f)} build(s) within 1000 " +
                              $"of the blast, {BuildingStructuralSupportSystem.Of(world).PiecesWithin(instigator.GetActorLocation(), 1000f)} " +
                              "near the thrower; baked landscape under " +
                              $"the blast = {(baked is { } b ? b.ToString("F0") : "NONE")}, " +
                              $"simulation floor = {projectile.ThrowerGroundZ:F0}, " +
                              $"bounces = {projectile.BounceCount}.");
        }
    }

    /// <summary>One throw waiting for its ability to be told it is over.</summary>
    private sealed class FPendingAbilityEnd {
        /// <summary>
        ///     The pawn whose emote montage this end should also stop - a boogie bomb's victim.
        ///     Null for a throw, which has no montage of its own to stop.
        /// </summary>
        public APawn? StopEmoteMontage;

        public required APlayerState PlayerState;
        public required UFortAbilitySystemComponent AbilitySystem;
        public required int Handle;
        public required FPredictionKey PredictionKey;
        public required float EndsAtWorldTime;
        public AFortWeapon? Weapon;
        public required UObject AbilityClass;
    }

    public static void Tick(UWorld world) {
        var state = StateOf(world);

        // The lingering half of the thrown items: a knockback that has to be taken back off, and a
        // stink bomb's cloud that keeps biting. See TickGrenadeEffects.
        TickGrenadeEffects(world, world.TimeSeconds);

        for (var i = state.PendingEnds.Count - 1; i >= 0; i--) {
            var pending = state.PendingEnds[i];
            if (world.TimeSeconds < pending.EndsAtWorldTime) continue;

            state.PendingEnds.RemoveAt(i);

            if (pending.StopEmoteMontage is { } dancer) {
                dancer.EmoteMontageIsStopped = true;
                dancer.EmoteMontage = null;
                world.NetDriver?.FlushActorProperties(dancer);

                // AND CLEAR THE SPEC, because ClientEndAbility on its own does not end anything on
                // this client - that is not a guess, it is what the comment on the re-grant below
                // has recorded since the throw handshake was worked out: the payload is right, the
                // client keeps running the ability anyway, and a FRESH SPEC is the only thing that
                // has ever freed one. The throw path clears and re-grants because it needs to throw
                // again; a boogie bomb's victim only needs the clearing half.
                //
                // This is why the first attempt at stopping the dance changed nothing: it sent the
                // end and stopped there.
                pending.AbilitySystem.ClearAbility(pending.Handle);
                world.NetDriver?.FlushAbilitySystemComponent(pending.PlayerState);

                Console.WriteLine($"FortProjectileSystem: {pending.PlayerState.GetFName()} stops dancing - " +
                                  $"spec {pending.Handle} cleared and the montage stopped.");
            }

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
            if (pending.Weapon is { } weapon && state.PROJECTILE_REGRANT) {
                pending.AbilitySystem.ClearAbility(pending.Handle);

                var regranted = pending.AbilitySystem.GrantAbility(
                    pending.AbilityClass, weapon, replicateInstance: true);
                weapon.GrantedAbilitySpecHandle = regranted.Handle;

                Console.WriteLine($"FortProjectileSystem: re-granted the throw ability as spec {regranted.Handle} " +
                                  $"(was {pending.Handle}) - a fresh spec is what makes the next throw possible today.");
            }

            Console.WriteLine($"FortProjectileSystem: told the client to end throw ability spec {pending.Handle} " +
                              $"after {state.PostThrowEndDelay:F2}s.");
        }

        if (state.InFlight.Count == 0) return;

        List<AFortProjectileBase>? expired = null;

        foreach (var projectile in state.InFlight) {
            if (!projectile.bHasExploded) StepFlight(world, projectile);

            // Not for a floor-only deployable: its wall bounces are the item working as designed, and
            // five of them would otherwise put a snowman in mid-air beside the wall it kept hitting.
            var bouncedOut = projectile.BounceCount >= BouncesTillExplode
                             && FortDeployables.RuleFor(projectile.SourceItemName).MinFloorNormalZ == null;
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
                projectile.ExpiresAtWorldTime = world.TimeSeconds + state.KillDelay;

                Explode(world, projectile);

                Console.WriteLine($"FortProjectileSystem: '{projectile.GetFName()}' exploded at " +
                                  $"{projectile.SimulatedLocation} " +
                                  $"({(bouncedOut ? $"{projectile.BounceCount} bounces" : "fuse")}) - " +
                                  $"destroying it in {state.KillDelay:F1}s.");
            }

            if (world.TimeSeconds >= projectile.ExpiresAtWorldTime)
                (expired ??= new List<AFortProjectileBase>()).Add(projectile);
        }

        if (expired == null) return;
        foreach (var projectile in expired) {
            Console.WriteLine($"FortProjectileSystem: '{projectile.GetFName()}' expired - destroying it server-side.");
            state.InFlight.Remove(projectile);
            projectile.Destroy();
        }
    }

    /// <summary>
    ///     The half of a thrown item that is not damage: the throw, the dance, the cloud.
    ///
    ///     WHY IT IS SEPARATE FROM THE DAMAGE LOOP. A boogie bomb damages for zero, so "who did this
    ///     affect" cannot be read off the damage; and the effects need a set rather than one victim
    ///     at a time (a gas cloud lingers over everyone who walks into it, not only who was standing
    ///     there when it landed). The damage loop collects, this applies.
    /// </summary>
    private static void ApplyGrenadeEffect(
        UWorld world, AFortProjectileBase projectile, FVector origin,
        (EGrenadeEffect Kind, float Radius, float LaunchVelocity, float AddToZ,
         float Duration, float Period, float HitDelay, float DestroyDistance,
         bool FriendlyFire, bool Damages, bool FallDamage) effect,
        List<APawn> caught) {

        switch (effect.Kind) {
            case EGrenadeEffect.Knockback:
            case EGrenadeEffect.Chill:
                foreach (var pawn in caught) {
                    var launch = FortGrenadeEffects.LaunchVelocityFor(origin, pawn.GetActorLocation(),
                                                                     effect.LaunchVelocity, effect.AddToZ);
                    // TWO CHANNELS, AND ONLY ONE OF THEM CAN LIFT ANYBODY.
                    //
                    // PushMomentum is the horizontal half and the one everyone else sees: the
                    // client's OnRep writes it straight into CharacterMovement->Velocity.X and .Y
                    // and feeds AddInputVector with its direction. It never touches Velocity.Z -
                    // that is not a guess, it is the disassembly of AFortPawn::OnRep_PushMomentum
                    // (see APawn.PendingLaunchVelocity) - so no Z sent this way has ever arrived,
                    // at any magnitude. "The upward impact is weak" was really "there is none".
                    //
                    // The launch is what the projectile Blueprint actually does: LaunchCharacter
                    // with the whole vector, which on a dedicated server reaches the owning client
                    // as a movement correction carrying NewVelocity and MOVE_Falling. The falling
                    // mode is half the point - an upward velocity given to a character that still
                    // thinks it is walking is projected onto the floor and lost.
                    pawn.SetPushMomentum(launch);
                    pawn.RequestLaunch(launch);
                    world.NetDriver?.FlushActorProperties(pawn);

                    // THE "LOW GRAVITY" HALF OF A SHOCKWAVE, which in the data is not gravity at all:
                    // `Default.ShockwaveGrenade.AllPlayersTakeFallDamage` is 0 and the impulse
                    // grenade's row leaves it at 1. So a shockwave throws you across the map and you
                    // land unhurt, while an impulse throw is an ordinary fall - which is the whole
                    // difference between the two in play.
                    if (!effect.FallDamage) {
                        pawn.GrantFallDamageImmunity(world.TimeSeconds);
                        SendLowGravityCues(world, pawn);
                    }

                    // AND IT HAS TO BE TAKEN BACK OFF WHEN THEY LAND, not on a timer. A value
                    // left standing is a pawn that never stops being shoved, and the next throw
                    // would be no change on the wire and send nothing at all - but clearing it in
                    // the AIR zeroes the client's whole velocity and drops them where they are.
                    // See PushMomentumMaxSeconds.
                    WatchFlight(world, pawn, clearPush: true);

                    SmashThroughBuildings(world, origin, pawn, effect.DestroyDistance);
                }

                Console.WriteLine($"FortProjectileSystem: {projectile.SourceItemName} threw {caught.Count} pawn(s) " +
                                  $"at {effect.LaunchVelocity:F0} uu/s (+{effect.AddToZ:F0} Z before normalising)" +
                                  (effect.FallDamage ? "" : " - and they land unhurt") +
                                  (effect.Kind == EGrenadeEffect.Chill
                                      ? " - the slippery-feet half is the client's own and is not sent."
                                      : "."));
                break;

            case EGrenadeEffect.Dance:
                foreach (var pawn in caught) BoogieBomb(world, pawn, effect.Duration);
                Console.WriteLine($"FortProjectileSystem: {projectile.SourceItemName} set {caught.Count} pawn(s) " +
                                  $"dancing for {effect.Duration:F0}s.");
                break;

            case EGrenadeEffect.Gas:
                _gasClouds.Add((origin, effect.Radius, world.TimeSeconds + effect.Duration,
                                world.TimeSeconds + effect.Period, effect.Period,
                                projectile.GetInstigator()));
                Console.WriteLine($"FortProjectileSystem: {projectile.SourceItemName} left a cloud at {origin} " +
                                  $"(radius {effect.Radius:F0}, {effect.Duration:F0}s, a tick every {effect.Period:F1}s).");
                break;
        }
    }

    /// <summary>
    ///     Makes one pawn dance, the way a boogie bomb does: by running the game's OWN stun ability on
    ///     them.
    ///
    ///     `GA_DanceGrenade_Stun_C` is ServerInitiated, which is the one property that lets this
    ///     server start an ability on a client at all - so the boogie bomb needs no new wire
    ///     mechanism, only the emote recipe pointed at a different ability
    ///     (FortEmoteSystem.PlayEmoteItem is the same three steps). The montage goes on the pawn
    ///     beside it so ONLOOKERS see the dance too; without it only the victim's own client would.
    ///
    ///     The five seconds are the ability's, not ours: it ends itself, and the server does not have
    ///     to time anything. Duration is logged so a mismatch with Default.DanceGrenade.Duration is
    ///     visible if the ability ever stops agreeing.
    /// </summary>
    private static void BoogieBomb(UWorld world, APawn pawn, float duration) {
        var state = StateOf(world);

        if (pawn.PlayerState is not { AbilitySystemComponent: { } abilitySystem } playerState) return;
        if (world.NetDriver is not { } netDriver) return;

        var spec = abilitySystem.GrantAbility(UAssetRegistry.GetOrCreate(FortGrenadeEffects.DanceStunAbilityPath));

        // What everybody else sees. Same handles the emote system drives, and the same ForcePlayBit
        // toggle: a repeat has to look like a change or no OnRep fires on an onlooker's client.
        pawn.EmoteMontage = UAssetRegistry.GetOrCreate(FortGrenadeEffects.DanceStunMontagePath);
        pawn.EmoteMontagePosition = 0f;
        pawn.EmoteMontagePlayRate = 1f;
        pawn.EmoteMontageBlendTime = 0.25f;
        pawn.EmoteMontageIsStopped = false;
        pawn.EmoteMontageSkipPositionCorrection = true;
        pawn.EmoteMontageForcePlayBit = !pawn.EmoteMontageForcePlayBit;

        netDriver.FlushAbilitySystemComponent(playerState);

        // THE SAME KEY HAS TO COME BACK ON THE END, and passing a blank one is why the first attempt
        // at stopping the dance did nothing: ClientEndAbility is matched against the activation, and
        // an end carrying a different prediction key is not an end to anything the client is running.
        // The throw path has always kept its key for exactly this reason; the boogie bomb did not.
        var danceKey = new FPredictionKey {
            bValidKeyForConnection = true,
            bIsServerInitiated = true,
            Current = state._nextDancePredictionKey++
        };

        netDriver.SendClientActivateAbilitySucceed(playerState, abilitySystem, spec.Handle, danceKey);

        // AND IT HAS TO BE ENDED, or the victim dances for the rest of the match. In the real game
        // the ability is held up by GE_DanceStun's five seconds and ends when that expires - this
        // server never applies the effect, so nothing was ever going to stop it. The same
        // ClientEndAbility handshake the throw itself uses does the job; the montage is stopped in
        // the same breath so onlookers see the dance finish too.
        state.PendingEnds.Add(new FPendingAbilityEnd {
            PlayerState = playerState,
            AbilitySystem = abilitySystem,
            Handle = spec.Handle,
            PredictionKey = danceKey,
            EndsAtWorldTime = world.TimeSeconds + duration,
            AbilityClass = spec.Ability,
            StopEmoteMontage = pawn
        });

        Console.WriteLine($"FortProjectileSystem: {playerState.GetFName()} is boogie-bombed for {duration:F0}s " +
                          $"(spec handle {spec.Handle}).");
    }

    /// <summary>A stink bomb's cloud: where, how big, until when, and when it next bites.</summary>
    private static readonly List<(FVector Origin, float Radius, float EndsAt, float NextTickAt,
                                  float Period, APawn? Instigator)> _gasClouds = new();

    /// <summary>
    ///     The lingering half of the thrown items - a knockback that has to be taken back off, and a
    ///     gas cloud that keeps damaging whoever is standing in it. Driven from the projectile tick,
    ///     which already runs every frame.
    /// </summary>
    private static void TickGrenadeEffects(UWorld world, float timeSeconds) {
        TickLowGravity(world, timeSeconds);
        FortAirstrike.Tick(world, timeSeconds);
        ReapDeployed(world, timeSeconds);

        for (var i = _gasClouds.Count - 1; i >= 0; i--) {
            var cloud = _gasClouds[i];

            if (timeSeconds >= cloud.EndsAt) { _gasClouds.RemoveAt(i); continue; }
            if (timeSeconds < cloud.NextTickAt) continue;

            _gasClouds[i] = cloud with { NextTickAt = timeSeconds + cloud.Period };

            var radiusSquared = cloud.Radius * cloud.Radius;
            var stats = FortProjectileStats.For("Athena_GasGrenade");
            var damage = stats?.Player ?? 5f;
            var killer = cloud.Instigator?.PlayerState ?? cloud.Instigator?.Controller?.PlayerState;

            foreach (var actor in world.NetDriver?.NetworkObjectList.ToArray() ?? Array.Empty<AActor>()) {
                if (actor is not APawn pawn || !actor.bHasKnownLocation) continue;
                if (FVector.DistSquared(cloud.Origin, pawn.GetActorLocation()) > radiusSquared) continue;
                if (pawn.PlayerState is not { } victim) continue;

                FortDamageSystem.ApplyDamage(victim, damage, EDeathCause.Grenade, killer);
            }
        }
    }

    /// <summary>
    ///     Starts the "goes off N seconds after it TOUCHES something" clock, for the items that have
    ///     one, the first time they touch anything.
    ///
    ///     A FUSE AND A HIT DELAY ARE DIFFERENT THINGS, and three of the throwables use the second.
    ///     A CLINGER does not count down from the throw at all - it sticks where it lands and goes
    ///     off 2.5 seconds later (`Default.StickyGrenade.OnHitExplodeDelay`), so one thrown across a
    ///     room and one dropped at your feet both give the same warning. A shockwave grenade's is
    ///     0.5 and a chiller's the same. Everything else keeps the frag's fuse.
    ///
    ///     Armed ONCE: the deadline is only ever brought FORWARD, so a grenade that lands, rolls and
    ///     bumps a wall does not keep resetting its own timer and never explode.
    /// </summary>
    private static bool ArmOnHitDelay(UWorld world, AFortProjectileBase projectile) {
        // A DEPLOYABLE ARMS ON CONTACT WITH NO DELAY AT ALL, and its Blueprint says so twice over.
        //
        // It stops rather than bounces: `Bounciness` is 0.1 on the sneaky snowman and 0.2 on the
        // firework mortar's holder, against the frag grenade's own value that this server was using
        // for everything - so what should have been a dead landing was several visible hops. And it
        // deploys on **OnStop**, not on a fuse: both projectiles' OnStop event is the one that jumps
        // into the ubergraph where the spawn happens (statement 2215 on the snowman, 2048 on the
        // shield bubble). With a bounciness of 0.1 that stop is a fraction of a second after first
        // contact, which is why arming here - stop dead where it first touched - is the faithful
        // reading and not a shortcut.
        //
        // What it replaces is worse than a bounce: with no row in FortGrenadeEffects these fell
        // through to the frag's 2.75-second FUSE, so a snowman thrown at your feet bounced away and
        // appeared somewhere else nearly three seconds later. The real item has no such timer.
        var deployable = FortDeployables.For(projectile.SourceItemName).Length > 0;

        var effect = FortGrenadeEffects.For(projectile.SourceItemName);

        if (effect == null && !deployable) return false;
        if (projectile.bLandedAndDeploying) return true;

        projectile.bLandedAndDeploying = true;

        // IT STOPS WHERE IT LANDS. Everything with an effect DEPLOYS rather than bounces: a boogie
        // bomb goes off where it hit, a stink bomb's cloud sits there, a clinger sticks. The frag
        // grenade keeps the bouncing model, because that IS the frag grenade.
        //
        // Zeroing the velocity is what makes the difference visible: without it a shockwave grenade
        // rolled on for half a second and threw people from somewhere they had already walked past,
        // and a firework mortar sailed off the far side of the island.
        projectile.Velocity = new FVector();

        var delay = deployable ? 0f : effect!.Value.HitDelay;
        var deadline = world.TimeSeconds + delay;
        if (deadline < projectile.ExplodesAtWorldTime) projectile.ExplodesAtWorldTime = deadline;

        Console.WriteLine($"FortProjectileSystem: {projectile.SourceItemName} landed and stopped - " +
                          $"{delay:F1}s until it deploys" +
                          (delay > 0f ? " (its own OnHitExplodeDelay, not the fuse)." : " (at once)."));
        return true;
    }

    /// <summary>
    ///     The visible half of a shockwave's low gravity - all three of the cues the game itself
    ///     puts on the player it throws, and now including the one that lasts.
    ///
    ///     Found by following what the projectile actually applies: `GE_Athena_ShockGrenade_FX`
    ///     grants `GA_Athena_ShockGrenade_RemoveFX`, whose entire content is a LOOPING cue and a
    ///     LANDING one, both named after the Low Gravity Rock - the shockwave reuses that item's
    ///     effects, which is exactly what being thrown by one looks like.
    ///
    ///     WHICH RPC A CUE NEEDS IS DECIDED BY ITS NOTIFY'S CLASS, and getting that wrong is what
    ///     "there is still no low gravity effect" was:
    ///
    ///         GCN_Athena_LowGravity_Liftoff_C  FortGameplayCueNotify_Simple    Executed
    ///         GCN_Athena_LowGravity_Land_C     FortGameplayCueNotify_Simple    Executed
    ///         GCN_Athena_LowGravity_C          FortGameplayCueNotify_Looping   Added / WhileActive / Removed
    ///
    ///     The two Simple ones are one-shots and go out as Executed. The Looping one is sent the way
    ///     real UE sends it (AbilitySystemComponent.cpp:1144) - the Added RPC for OnActive AND an
    ///     element in the ASC's ActiveGameplayCues array for WhileActive - because only the array
    ///     can take it off again. See TickLowGravity for when that happens, and FActiveGameplayCue
    ///     for why the array is the only removal channel there is.
    ///
    ///     SHOCKWAVE_FX=0 turns the whole thing off.
    /// </summary>
    private static void SendLowGravityCues(UWorld world, APawn pawn) {
        var state = StateOf(world);

        if (world.Options.Get("SHOCKWAVE_FX") is "0") return;

        SendCueToEveryone(world, pawn, FortGrenadeEffects.LowGravLiftoffCue);

        if (pawn.PlayerState?.AbilitySystemComponent is not { } asc) return;
        if (!asc.AddGameplayCue(FortGrenadeEffects.LowGravLoopingCue)) return;

        // OnActive for everyone who can see the pawn, then the array element that keeps it alive.
        // The two halves are independent on purpose: if the ASC block is delayed the cue still
        // starts from the RPC, and if the RPC is lost WhileActive still starts it.
        foreach (var connection in world.NetDriver?.ClientConnections ?? Enumerable.Empty<UNetConnection>()) {
            if (connection.FindActorChannel(pawn) is not { } channel) continue;
            channel.SendNetMulticastInvokeGameplayCueAddedWithParams(FortGrenadeEffects.LowGravLoopingCue, null);
        }

        world.NetDriver?.FlushAbilitySystemComponent(pawn.PlayerState);

        WatchFlight(world, pawn, endLowGravity: true);

        Console.WriteLine($"FortProjectileSystem: {pawn.GetFName()} is in low gravity - aura on until they land " +
                          $"(or {state.LowGravityMaxSeconds:F0}s, whichever comes first).");
    }

    /// <summary>
    ///     Ends a low-gravity flight WHEN THE PLAYER LANDS, which is the one thing a fixed timer
    ///     could never get right: a shockwave throw lasts as long as it lasts.
    ///
    ///     Landing is read off the movement mode the client sends with every move - falling is 3
    ///     (see APawn.TrackMoveFlags) - and only after the pawn has actually been seen falling, so
    ///     the throw's own first frames, where the client is still walking, cannot end it instantly.
    ///
    ///     THE CAP IS NOT A TIDINESS MEASURE. If the aura is left on, nothing else will ever take it
    ///     off: a looping cue has no timeout of its own, so a player whose landing frame is simply
    ///     lost would glow for the rest of the match. Ending late is recoverable; not ending is not.
    /// </summary>
    private static void TickLowGravity(UWorld world, float timeSeconds) {
        var state = StateOf(world);

        const byte falling = 3;

        for (var i = state._flights.Count - 1; i >= 0; i--) {
            var flight = state._flights[i];
            var mode = flight.Pawn.LastClientMovementMode;

            if (mode == falling) flight.SeenFalling = true;

            var landed = flight.SeenFalling && mode is { } current && current != falling;
            var expired = timeSeconds - flight.StartedAt >= FlightMaxSeconds(world, flight);
            if (!landed && !expired) continue;

            state._flights.RemoveAt(i);
            EndFlight(world, flight, landed ? "landed" : "timed out");
        }
    }

    /// <summary>
    ///     Starts watching one thrown player until they land. Both things that have to be undone
    ///     after a throw end at the same moment - the low-gravity aura and the PushMomentum - so
    ///     they share one watcher rather than racing two timers.
    ///
    ///     A pawn already being watched has the new job added to its existing entry instead of a
    ///     second entry: a shockwave caught by a second blast mid-flight would otherwise be landed
    ///     twice, and the first landing would clear the push the second one still needs.
    /// </summary>
    private static void WatchFlight(UWorld world, APawn pawn, bool clearPush = false, bool endLowGravity = false) {
        var state = StateOf(world);

        foreach (var existing in state._flights) {
            if (existing.Pawn != pawn) continue;

            existing.ClearPush |= clearPush;
            existing.EndLowGravity |= endLowGravity;
            existing.StartedAt = world.TimeSeconds;
            existing.SeenFalling = false;
            return;
        }

        state._flights.Add(new FThrownFlight {
            Pawn = pawn,
            StartedAt = world.TimeSeconds,
            ClearPush = clearPush,
            EndLowGravity = endLowGravity
        });
    }

    /// <summary>Whichever backstop is longer, since one entry can carry both jobs.</summary>
    private static float FlightMaxSeconds(UWorld world, FThrownFlight flight) =>
        flight.EndLowGravity ? MathF.Max(StateOf(world).LowGravityMaxSeconds, StateOf(world).PushMomentumMaxSeconds) : StateOf(world).PushMomentumMaxSeconds;

    /// <summary>
    ///     The end of a throw: the aura comes off, the landing thump plays, and the push is taken
    ///     back. See PushMomentumMaxSeconds for why taking the push back is a LANDING event and not
    ///     a timed one - doing it in the air freezes the victim where they are.
    /// </summary>
    private static void EndFlight(UWorld world, FThrownFlight flight, string why) {
        if (flight.EndLowGravity) EndLowGravity(world, flight.Pawn, why);

        if (flight.ClearPush) {
            flight.Pawn.SetPushMomentum(new FVector());
            world.NetDriver?.FlushActorProperties(flight.Pawn);
        }
    }

    /// <summary>
    ///     Takes the aura off and plays the landing thump. Removing the ActiveGameplayCues element
    ///     is the whole of the first half - there is no "cue removed" RPC to send instead. The
    ///     client runs PreReplicatedRemove, and therefore the notify's Removed event, purely because
    ///     the element stopped being there.
    /// </summary>
    private static void EndLowGravity(UWorld world, APawn pawn, string why) {
        if (pawn.PlayerState?.AbilitySystemComponent is { } asc &&
            asc.RemoveGameplayCue(FortGrenadeEffects.LowGravLoopingCue)) {
            world.NetDriver?.FlushAbilitySystemComponent(pawn.PlayerState);
        }

        SendCueToEveryone(world, pawn, FortGrenadeEffects.LowGravLandingCue);

        Console.WriteLine($"FortProjectileSystem: {pawn.GetFName()} is out of low gravity ({why}).");
    }

    /// <summary>
    ///     The shockwave's other half: whoever it throws SMASHES THROUGH what is in front of them.
    ///
    ///     Straight out of `B_Prj_Athena_ShockGrenade`'s graph, which runs this right after
    ///     LaunchCharacter - see FortGrenadeEffects.DestructionCapsuleRadius for the disassembled
    ///     shape of it. A person-sized capsule is swept from the victim, along the direction the
    ///     blast threw them, for DestructionDistance (1400 on the shockwave, and 0 - i.e. never - on
    ///     everything else, including the impulse grenade, whose Blueprint has no such property).
    ///
    ///     NOT A BLAST RADIUS, and that is the part worth keeping straight: nothing behind the
    ///     grenade is touched, and nothing beside the victim. The wall you were standing against
    ///     goes, and so does whatever is a storey or two further along the same line.
    ///
    ///     WHAT IT CAN AND CANNOT REACH. Player builds, chests, and any map scenery this server has
    ///     already built a stand-in for are all ABuildingActors in the net driver's list, so one loop
    ///     covers them. Map props nobody has touched yet are NOT there at all - a tree exists on the
    ///     client until some client names it by path (NativeRpcHandlers.DamageLevelActor), and this
    ///     server cannot address one it has never been told about. That is a limit of the whole
    ///     map-actor design here, not of this sweep; it is logged rather than hidden.
    /// </summary>
    private static void SmashThroughBuildings(UWorld world, FVector origin, APawn pawn, float destroyDistance) {
        if (destroyDistance <= 0f) return;
        if (world.Options.Get("SHOCKWAVE_DESTRUCTION") is "0") return;

        var start = pawn.GetActorLocation();

        // GetDirectionUnitVector(HitLocation, victim) - the blast toward the victim, which is the
        // launch direction BEFORE the Z floor is applied. Straight up when the grenade went off
        // under someone's feet, which is the case that lifts them through their own floor.
        var dx = start.X - origin.X;
        var dy = start.Y - origin.Y;
        var dz = start.Z - origin.Z;
        var length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length < 1e-3f) return;

        var end = new FVector {
            X = start.X + dx / length * destroyDistance,
            Y = start.Y + dy / length * destroyDistance,
            Z = start.Z + dz / length * destroyDistance
        };

        var smashed = 0;
        var candidates = 0;

        // WHAT WAS NEARLY HIT, for the case that matters more than the hits: "0 destroyed" has three
        // completely different causes - no ABuildingActor exists at all (a map prop this server has
        // never been told about), one exists but the swept capsule misses it, or one is in the way
        // and the box test is wrong - and the count alone cannot tell them apart. So the nearest few
        // are named with their distance FROM THE SEGMENT, which is the number the test actually
        // turns on.
        var nearest = new List<(ABuildingActor Building, float Distance)>();

        foreach (var actor in world.NetDriver?.NetworkObjectList.ToArray() ?? Array.Empty<AActor>()) {
            if (actor is not ABuildingActor building || building.bDestroyed) continue;

            // The same guard the explosion loop needs: an actor whose position this server never
            // learned reads as (0,0,0), and a sweep passing near the world origin would take out
            // every one of them at once. See AActor.bHasKnownLocation.
            if (!building.bHasKnownLocation) continue;

            candidates++;
            nearest.Add((building, DistanceToSegment(building.GetActorLocation(), start, end)));

            if (!BuildingStructuralSupportSystem.Of(world).SweptCapsuleTouches(
                    building, start, end,
                    FortGrenadeEffects.DestructionCapsuleRadius,
                    FortGrenadeEffects.DestructionCapsuleHalfHeight)) continue;

            BuildingStructuralSupportSystem.Of(world).ApplyDamage(building, (int) FortGrenadeEffects.DestructionDamage);
            smashed++;
        }

        var props = SmashThroughMapProps(world, pawn, start, end);

        Console.WriteLine($"FortProjectileSystem: {pawn.GetFName()} was thrown through {smashed} building(s) " +
                          $"and {props} map prop(s) - a {FortGrenadeEffects.DestructionCapsuleRadius:F0}-radius " +
                          $"capsule swept {destroyDistance:F0} units from {start} toward {end}, " +
                          $"{candidates} building actor(s) considered.");

        if (smashed > 0 || props > 0) return;

        // NAMED ONE BY ONE when nothing broke. A map prop nobody has damaged yet is not in the list
        // at all - it only becomes a server-side actor once some client names it by path (see
        // NativeRpcHandlers.DamageLevelActor) - so "0 considered" and "12 considered, nearest 900
        // units off the line" are different problems with different fixes, and this is the line that
        // says which.
        if (candidates == 0) {
            Console.WriteLine("FortProjectileSystem:   ...nothing to destroy: this server has no building " +
                              "ACTOR anywhere. Player builds and already-damaged map props would be here; " +
                              "an untouched tree or house is only ever a client-side actor.");
            return;
        }

        foreach (var (building, distance) in nearest.OrderBy(entry => entry.Distance).Take(3))
            Console.WriteLine($"FortProjectileSystem:   ...missed {building.GetFName()} " +
                              $"({building.ClassName}) at {building.GetActorLocation()}, its pivot " +
                              $"{distance:F0} units from the swept line.");
    }

    /// <summary>
    ///     The map's own scenery - trees, walls, furniture - along the same swept line.
    ///
    ///     SEPARATE FROM THE BUILDING LOOP ABOVE BECAUSE THE POPULATION IS SEPARATE, and that is the
    ///     whole difficulty of destroying map geometry from outside the game. A player build is an
    ///     actor this server spawned and can enumerate; a tree is an actor in a streaming sublevel
    ///     this server never loads, which exists here only once somebody has NAMED it - the client
    ///     supplies the path when it reports a hit (NativeRpcHandlers.DamageLevelActor). So the
    ///     server cannot ask what is nearby; it has to have been told in advance, which is what the
    ///     FortMapProps bake is.
    ///
    ///     Once a prop is picked, the rest is the path DamageLevelActor already walks: build a
    ///     stably-named stand-in for the path, give it the real hit points its class resolves to,
    ///     register it for replication, then destroy it. The client resolves the path to the actor it
    ///     already has and plays its own destruction.
    ///
    ///     WHAT IS DELIBERATELY NOT DONE: the stand-in is given no location. A level actor's position
    ///     belongs to the client's copy, and pushing one would be this server telling the client to
    ///     MOVE a tree - see DamageLevelActor, which sets none either. The consequence is that these
    ///     actors read as position-unknown to everything else (an ordinary explosion skips them), and
    ///     that is the safe direction to be wrong in.
    /// </summary>
    private static int SmashThroughMapProps(UWorld world, APawn pawn, FVector start, FVector end) {
        if (world.NetDriver is not { } netDriver) return 0;

        var margin = FortMapProps.PropMargin;
        var smashed = 0;
        var failed = 0;

        // WHOSE CLIENT'S NAME FOR THE LEVEL. A POI sublevel is streamed as an INSTANCE, so the
        // package the client holds carries a suffix the cooked path does not have, and only the
        // client can say what it is (see UNetConnection.ClientLevelInstances). The victim's own
        // connection is the one used: they are the player who must see the wall go.
        //
        // WITH SEVERAL CLIENTS THIS IS INCOMPLETE and says so rather than pretending: one stand-in
        // carries one path, and another client that streamed the same level under a different name
        // would not resolve it. Single-client is the case this server is exercised in; the honest
        // fix is per-connection relevancy, which this server has no shape for yet.
        var connection = world.NetDriver?.ClientConnections
            .FirstOrDefault(candidate => candidate.PlayerController?.Pawn == pawn)
                         ?? world.NetDriver?.ClientConnections.FirstOrDefault();

        if (connection == null) return 0;

        var picked = FortMapProps.Along(start, end, margin, out var nearMisses);
        var unresolved = 0;

        foreach (var prop in picked) {
            // NOT LOADED ON THIS CLIENT = NOT DESTROYABLE, and skipping it is the point rather than a
            // giving-up. Opening a channel for an actor the client cannot find is exactly the
            // "SerializeNewActor failed to find/spawn actor. Actor: None" / NMT_ActorChannelFailure
            // pair this check exists to stop, and a failed channel is worse than a wall left standing.
            if (prop.PathFor(connection) is not { } path) {
                unresolved++;
                continue;
            }

            ABuildingActor standIn;
            try {
                standIn = world.MapActors.GetOrCreate<ABuildingActor>(path);
            } catch (Exception ex) {
                Console.WriteLine($"FortProjectileSystem:   could not name '{path}' - {ex.Message}");
                continue;
            }

            // Already gone, or already something this server treats specially. A chest is not
            // scenery and neither is a llama - the same two exclusions DamageLevelActor makes, for
            // the same reason, and needed again here because the bake cannot tell what a path will
            // turn out to have been registered as.
            if (standIn.bDestroyed) continue;
            if (standIn is ABuildingContainer or AFortAthenaSupplyDropLlama) continue;

            standIn.MarkAsLevelActor();

            if (!netDriver.NetworkObjectList.Contains(standIn)) {
                    if (FortHarvestResources.MaxHealthFor(path) is { } maxHealth) {
                    standIn.InitializeLevelActorHitPoints(maxHealth);
                }

                standIn.SetReplicates(true);
                netDriver.AddNetworkActor(standIn);
            }

            // The same flat 10,000 the real GE carries, so this destroys whatever it touches rather
            // than chipping it - see FortGrenadeEffects.DestructionDamage.
            //
            // NAMED WHEN IT REFUSES, because "the prop was chosen and nothing happened" is a
            // different failure from "no prop was chosen" and the totals cannot separate them.
            if (!standIn.ApplyDamage((int) FortGrenadeEffects.DestructionDamage)) {
                Console.WriteLine($"FortProjectileSystem:   '{path}' survived 10000 damage " +
                                  $"({standIn.CurrentHitPoints}/{standIn.MaxHitPoints} HP) - not destroyed.");
                failed++;
                continue;
            }

            if (!standIn.MarkDestroyed()) {
                Console.WriteLine($"FortProjectileSystem:   '{path}' would not mark destroyed " +
                                  "(already flagged?).");
                failed++;
                continue;
            }

            Console.WriteLine($"FortProjectileSystem:   destroyed map prop '{path}' " +
                              $"({standIn.MaxHitPoints} HP) at {prop.Location}");
            smashed++;
        }

        if (smashed > 0) return smashed;

        if (FortMapProps.Count == 0) {
            Console.WriteLine("FortProjectileSystem:   ...and no map props, because the prop bake is not loaded " +
                              "(see FortMapProps).");
            return 0;
        }

        // THE MAP HALF'S OWN MISS REPORT. The bake holds 59,722 actors and their positions are
        // exact (every one of FortDoorPlacements' 19,284 entries matches it), so a miss is about
        // this SWEEP, not the data - and the number that decides it is the distance from the line.
        Console.WriteLine($"FortProjectileSystem:   ...{picked.Count} map prop(s) touched the line " +
                          $"(capsule margin {margin:F0}), {failed} refused, {unresolved} in a level this " +
                          $"client has not reported " +
                          $"(it knows {connection.ClientLevelInstances.Count} instanced and " +
                          $"{connection.ClientVisibleLevelNames.Count} plain level(s)). Nearest misses:");

        foreach (var (prop, distance) in nearMisses)
            Console.WriteLine($"FortProjectileSystem:     {distance,6:F0} away (radius {prop.RadiusXY:F0}, " +
                              $"Z {prop.Location.Z + prop.CenterZ - prop.HalfZ:F0}.." +
                              $"{prop.Location.Z + prop.CenterZ + prop.HalfZ:F0})  {prop.CookedPath}");

        return smashed;
    }

    /// <summary>Distance from a point to a segment - diagnostics only, so the miss can be quantified.</summary>
    private static float DistanceToSegment(FVector point, FVector from, FVector to) {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;

        var lengthSquared = dx * dx + dy * dy + dz * dz;
        if (lengthSquared < 1e-6f) return MathF.Sqrt(FVector.DistSquared(point, from));

        var t = ((point.X - from.X) * dx + (point.Y - from.Y) * dy + (point.Z - from.Z) * dz) / lengthSquared;
        t = MathF.Max(0f, MathF.Min(1f, t));

        var closest = new FVector { X = from.X + dx * t, Y = from.Y + dy * t, Z = from.Z + dz * t };
        return MathF.Sqrt(FVector.DistSquared(point, closest));
    }

    /// <summary>
    ///     The items that LEAVE SOMETHING BEHIND rather than exploding - a sneaky snowman, a shield
    ///     bubble, a firework mortar's emplacement.
    ///
    ///     All three projectiles do the same thing in their own Blueprint: spawn a class named by one
    ///     of their own properties at the point they stopped. See FortDeployables for the three rows
    ///     and for where each class name was read from.
    ///
    ///     THE CLASS COMES FROM THE PATH, NOT FROM THE C# TYPE - ABuildingActor.ClassForPath is what
    ///     tells the client what to build, and the C# type only picks the RepLayout this server
    ///     writes with. Those are two different decisions and conflating them is what
    ///     `SpawnActor&lt;ABuildingWall&gt;` threw an InvalidCastException over once already.
    ///
    ///     WHICH C# TYPE IS NOT COSMETIC. A BuildingGameplayActor is ABuildingSMActor's SIBLING, so
    ///     the ordinary building layout would name handles its class does not have and the client
    ///     would close the connection - see AFortDeployedActor. The table says which fork each class
    ///     is on, checked with `pakreader supers` rather than inferred from the name.
    /// </summary>
    private static void Deploy(UWorld world, AFortProjectileBase projectile, FVector origin) {
        var deployables = FortDeployables.For(projectile.SourceItemName);
        if (deployables.Length == 0) return;
        if (world.Options.Get("DEPLOYABLES") is "0") return;

        // THE FIRST ACTOR IS THE ANCHOR. Anything marked GoesWithFirst is bound to it, so it goes
        // when the anchor does - see AFortDeployedActor.GoesDownWith.
        ABuildingActor? anchor = null;

        foreach (var deployable in deployables) {
            var actor = DeployOne(world, projectile, origin, deployable);
            if (actor == null) continue;

            if (anchor == null) {
                anchor = actor;
                continue;
            }

            if (deployable.GoesWithFirst && anchor is AFortDeployedActor bindable)
                bindable.GoesDownWith.Add(actor);
        }
    }

    /// <summary>
    ///     Puts ONE of an item's actors down. Separate from Deploy because an item can leave several
    ///     - the shield bubble is a core AND a dome, and only the dome is the shield.
    /// </summary>
    private static ABuildingActor? DeployOne(UWorld world, AFortProjectileBase projectile, FVector origin,
                                             FortDeployables.FDeployed deployable) {
        var state = StateOf(world);

        var location = new FVector { X = origin.X, Y = origin.Y, Z = origin.Z + deployable.ZOffset };

        // THE UCLASS DECIDES THE C# TYPE, AND SpawnActor<T> IS ONLY A CAST. That combination is what
        // made every gameplay-actor deployable silently fail to exist: ABuildingActor.ClassForPath
        // bakes `typeof(ABuildingActor)` into the UClass, UClass.CreateDefaultObject does
        // Activator.CreateInstance on exactly that, and `SpawnActor<AFortDeployedActor>` then cast
        // an ABuildingActor to AFortDeployedActor and threw. The throw landed in the catch below and
        // became one "could not deploy" line, so the shield bubble played its deploy sound and left
        // nothing behind - which read for two rounds as a replication or visibility problem.
        //
        // The snowman was unaffected and that is why this survived: it is the only row with
        // GameplayActor false, so it is the only one that was ever really spawned.
        //
        // StaticClassForPath is keyed by (type, path), so asking for the same path as a different C#
        // type gives a separate UClass with the same NativePackagePath - the client still sees the
        // Blueprint path it expects, and the server gets the type whose RepLayout arm is correct.
        ABuildingActor? actor;
        try {
            actor = deployable.GameplayActor
                ? world.SpawnActor<AFortDeployedActor>(
                    GUClassArray.StaticClassForPath<AFortDeployedActor>(deployable.ClassPath),
                    new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient })
                : world.SpawnActor<ABuildingActor>(
                    ABuildingActor.ClassForPath(deployable.ClassPath),
                    new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient });
        } catch (Exception ex) {
            Console.WriteLine($"FortProjectileSystem: could not deploy '{deployable.ClassPath}' - {ex.Message}");
            return null;
        }

        if (actor == null) {
            Console.WriteLine($"FortProjectileSystem: SpawnActor returned null for '{deployable.ClassPath}'");
            return null;
        }

        actor.SetActorLocation(location);

        // Before the channel opens, so it rides the spawn header - see FDeployed.Scale.
        if (deployable.Scale != 1f)
            actor.SetActorScale3D(new FVector { X = deployable.Scale, Y = deployable.Scale, Z = deployable.Scale });

        if (actor is AFortDeployedActor deployed) {
            deployed.DeployedClassPath = deployable.ClassPath;
            deployed.Indestructible = deployable.Indestructible;
        }

        // FACING THE THROWER'S HEADING, not the projectile's. A grenade tumbles, and a snowman that
        // came to rest upside down is not something the item ever does; the yaw a player would expect
        // is the one they were facing. Yaw only - these all stand upright.
        //
        // UNLESS THE ITEM'S OWN SPAWN SAYS OTHERWISE - the snowman faces its flight direction turned
        // a quarter. See FDeployRule.FlightYawOffset.
        if (FortDeployables.RuleFor(projectile.SourceItemName).FlightYawOffset is { } flightOffset) {
            actor.SetActorRotation(new FRotator { Yaw = projectile.LastFlightYaw + flightOffset });
        } else if (projectile.GetInstigator()?.GetActorRotation().Yaw is { } yaw) {
            actor.SetActorRotation(new FRotator { Yaw = yaw });
        }

        actor.SetRole(ENetRole.ROLE_Authority);
        actor.SetReplicates(true);

        if (deployable.Lifespan > 0f) state.Deployed.Add((actor, world.TimeSeconds + deployable.Lifespan));

        Console.WriteLine($"FortProjectileSystem: {projectile.SourceItemName} deployed " +
                          $"'{deployable.ClassPath}' at {location} " +
                          $"({(deployable.GameplayActor ? "BuildingGameplayActor layout" : "building-piece layout")}" +
                          (deployable.Lifespan > 0f ? $", {deployable.Lifespan:F0}s" : "") +
                          (deployable.Indestructible ? ", indestructible" : "") + ").");
        return actor;
    }

    private static void ReapDeployed(UWorld world, float timeSeconds) {
        var state = StateOf(world);

        for (var i = state.Deployed.Count - 1; i >= 0; i--) {
            if (timeSeconds < state.Deployed[i].EndsAt) continue;

            var actor = state.Deployed[i].Actor;
            state.Deployed.RemoveAt(i);

            if (actor.IsPendingKillPending()) continue;

            Console.WriteLine($"FortProjectileSystem: deployed '{actor.GetFName()}' reached the end of its " +
                              "lifespan - taking it off the wire.");
            actor.Destroy();
        }
    }

    /// <summary>
    ///     One explosion at a point: everything in radius takes it, players and structures alike.
    ///
    ///     Extracted so the AIR STRIKE's sixty rockets are the same blast a grenade is rather than a
    ///     second implementation that drifts from it - the alternative was copying the loop, and a
    ///     copied damage loop is how "the shockwave stopped hurting buildings" would arrive later.
    ///
    ///     DELIBERATELY SIMPLER THAN Explode's OWN LOOP in one respect: no line-of-sight test. A
    ///     rocket falls from twelve thousand units up (`Default.AppleSauce.RocketHeight`), so the
    ///     ceiling between it and its target is the thing it just came through - tracing from the
    ///     blast to the victim would have the roof shield them from a rocket that hit the roof.
    /// </summary>
    public static void Blast(UWorld world, FVector origin, float radius,
                             float playerDamage, float environmentDamage, APawn? instigator) {
        var radiusSquared = radius * radius;
        var killer = instigator?.PlayerState ?? instigator?.Controller?.PlayerState;

        var pawns = 0;
        var buildings = 0;

        foreach (var actor in world.NetDriver?.NetworkObjectList.ToArray() ?? Array.Empty<AActor>()) {
            if (!actor.bHasKnownLocation) continue;
            if (actor.GetActorLocation() is not { } location) continue;
            if (FVector.DistSquared(origin, location) > radiusSquared) continue;

            switch (actor) {
                case ABuildingActor building when !building.bDestroyed && environmentDamage > 0f:
                    BuildingStructuralSupportSystem.Of(world).ApplyDamage(building, (int) environmentDamage);
                    buildings++;
                    break;

                case APawn pawn when playerDamage > 0f:
                    if (pawn.PlayerState is not { } victim) break;
                    FortDamageSystem.ApplyDamage(victim, playerDamage, EDeathCause.Grenade, killer);
                    pawns++;
                    break;
            }
        }

        if (pawns > 0 || buildings > 0) {
            Console.WriteLine($"FortProjectileSystem.Blast: {origin} r{radius:F0} hit {pawns} pawn(s) " +
                              $"for {playerDamage:F0} and {buildings} building(s) for {environmentDamage:F0}.");
        }
    }

    private static void SendCueToEveryone(UWorld world, APawn pawn, string cueTag) {
        foreach (var connection in world.NetDriver?.ClientConnections ?? Enumerable.Empty<UNetConnection>()) {
            if (connection.FindActorChannel(pawn) is not { } channel) continue;
            channel.SendNetMulticastInvokeGameplayCueExecutedWithParams(cueTag, null);
        }
    }

    /// <summary>One player mid-throw, and what has to be undone when they land.</summary>
    private sealed class FThrownFlight {
        public required APawn Pawn;
        public float StartedAt;

        /// <summary>
        ///     Set once the client has reported falling. Until then a landing cannot be detected,
        ///     because as far as this server knows the pawn never left the ground.
        /// </summary>
        public bool SeenFalling;

        /// <summary>Take PushMomentum back off - see PushMomentumMaxSeconds.</summary>
        public bool ClearPush;

        /// <summary>Remove the low-gravity aura and play the landing thump.</summary>
        public bool EndLowGravity;
    }

}
