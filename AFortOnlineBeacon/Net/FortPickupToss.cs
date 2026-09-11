using AFortOnlineBeacon.Runtime;
using AFortOnlineBeacon.Core.Math;
using AFortOnlineBeacon.Net.Actors;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     Where a dropped item actually comes to rest, by simulating the toss the real server simulates.
///
///     THE GAP THIS FILLS. `SpawnDroppedPickup` put every dropped item at a fixed offset in front of
///     the player - `origin + forward * TossDistance`, at `origin.Z + TossHeight` - and left it
///     hanging there. Its own comment said so: "Real UE tosses the item along an arc; with no toss to
///     simulate, the least this server can do is not bury the pickup in the pawn's own capsule."
///     Standing on a ramp, at the edge of a build, or on any slope, the item floated or sank into
///     geometry, and a pickup the client's interaction query cannot reach is a pickup that is gone.
///
///     THE REAL SERVER SIMULATES THESE, and it is not a footnote - it is nine tenths of all the
///     physics it does. Of the 0906 PR3.0 capture's `LogProjectileMovement` lines, 279,874 are
///     `FortPickupAthena`: dropped items falling, bouncing and coming to rest, substep by substep,
///     through the same UProjectileMovementComponent that carries a grenade.
///
///     EVERY CONSTANT BELOW WAS MEASURED FROM THAT CAPTURE by Tools/ProjectileReplay, not chosen.
///     See the tool's own docstrings for how each is recovered and how well it fits.
///
///     TWO WAYS TO SPEND THE SAME SIMULATION, and both run the identical `Step`:
///
///       * SETTLED (default) solves the whole trajectory at the moment of the drop and publishes
///         only where it stopped. The item appears at its resting place instead of being watched
///         into it - a bargain, and worth naming as one.
///       * STREAMED (`PICKUP_TOSS_STREAM=1`) advances one substep per tick and replicates the
///         position as it goes, which is what the real server does. Opt-in because it puts
///         ReplicatedMovement on an actor that has never carried it, and an item that lands in the
///         wrong place is worse than one nobody watched fall.
///
///     They cannot drift apart: `Settle` and `Tick` both call `Step`, so the streamed arc ends
///     exactly where the settled one would have put it.
/// </summary>
internal sealed class FortPickupToss : FWorldSubsystem {
    /// <summary>This world's instance - see FWorldSubsystem.</summary>
    public static FortPickupToss Of(UWorld world) => world.GetSubsystem<FortPickupToss>();

    /// <summary>
    ///     Fortnite's world gravity times FortPickupAthena's ProjectileGravityScale, in uu/s^2.
    ///
    ///     MEASURED at 2800.0 (IQR 16.8 over 67 free-flight runs). The same tool reads 2800.0 off
    ///     `CBGA_GreenGlop_WithGrav_C` (IQR 4.2, n=877), 2243.2 off the frag grenade, 839.8 off the
    ///     grenade launcher and 560.0 off the ostrich drop - **all multiples of 280**. Against UE's
    ///     default 980 those are 2.857, 2.289, 0.857 and 0.571; against 1400 they are 2.0, 1.6, 0.6
    ///     and 0.4. So the world gravity is -1400 and these are round scales, which also confirms
    ///     FortProjectileSystem's own 2800 x 0.8 = 2240 for the grenade against an independent
    ///     measurement of 2243.2.
    /// </summary>
    private float Gravity =>
        float.TryParse(Options.Get("PICKUP_GRAVITY"), out var g) && g > 0 ? g : 2800f;

    /// <summary>
    ///     The MaxSpeed clamp, in uu/s. MEASURED at 503.7 (IQR 12.2 over 332 clamped substeps) -
    ///     recognised as steps where the velocity's DIRECTION turned while its LENGTH did not, which
    ///     only a clamp does. A dropped item is a slow, short toss, not a throw.
    /// </summary>
    private float MaxSpeed =>
        float.TryParse(Options.Get("PICKUP_MAX_SPEED"), out var s) && s > 0 ? s : 500f;

    /// <summary>
    ///     Restitution. MEASURED at 0.534 (IQR 0.071) across 22 bounces off WALLS - the ones whose
    ///     surface normal is horizontal, where gravity plays no part and so neither does any error in
    ///     correcting for it. The all-surfaces median is a wider 0.600, and floor bounces are the
    ///     reason: their answer depends on adding back exactly the right amount of gravity over the
    ///     post-bounce sub-step. The wall figure is the one to believe.
    /// </summary>
    private float Bounciness =>
        float.TryParse(Options.Get("PICKUP_BOUNCINESS"), out var b) && b >= 0 ? b : 0.534f;

    /// <summary>
    ///     Tangential loss per bounce, UE's Friction. THE LEAST CERTAIN NUMBER HERE, and worth saying
    ///     so: the fit is bimodal (median 0.113 with an IQR of 0.500), because 45,182 of the capture's
    ///     pickup bounces are on surfaces that match no axis and are refused rather than forced. An
    ///     earlier, looser pass over the same data gave 0.544 with an IQR of 0.002. 0.5 sits between
    ///     them and is the one constant to reach for first if settled items slide too far or too
    ///     little.
    /// </summary>
    private float BounceFriction =>
        float.TryParse(Options.Get("PICKUP_FRICTION"), out var f) && f >= 0 ? f : 0.5f;

    /// <summary>
    ///     The substep, in seconds. The capture's every projectile line reads `step 0.033`, which is
    ///     a 30 Hz fixed substep - the same one this has to use, because a bounce's outcome depends
    ///     on where in the step it happened.
    /// </summary>
    private const float SubStep = 1f / 30f;

    /// <summary>
    ///     Below this speed the item is at rest. UE calls it BounceVelocityStopSimulatingThreshold;
    ///     FortProjectileSystem has the same idea and the same reason - without it a pickup on a
    ///     floor jitters against it forever and the loop below never terminates early.
    /// </summary>
    private const float StopSpeed = 20f;

    /// <summary>
    ///     A ceiling on the simulation, in substeps. Ten seconds. A toss settles in well under one,
    ///     so reaching this means the item found somewhere to fall for ever - off the map, or through
    ///     a hole in the baked collision - and the answer is to stop and use where it got to rather
    ///     than to spin. See the note about degrading in [[feedback-guards-degrade-dont-refuse]].
    /// </summary>
    private const int MaxSubSteps = 300;

    /// <summary>
    ///     How far above the simulated contact point the ACTOR is drawn, in units.
    ///
    ///     An actor's location is its CENTRE while the simulation tracks the point that touches the
    ///     ground, so without this the item rests with its middle at ground level - half buried,
    ///     which is exactly what was reported ("visible, but sunk into the ground").
    ///
    ///     40 is this project's own answer to the identical question: FLOOR_LOOT_Z_OFFSET lifts
    ///     generated floor loot above its spawner with the comment "so pickups are not buried in the
    ///     floor". Reusing it means a dropped item and a spawned one sit at the same height rather
    ///     than at two heights nobody chose.
    ///
    ///     APPLIED TO THE ACTOR, NEVER TO THE PHYSICS. Adding it to the contact point instead makes
    ///     the item bounce forever - see the note in Step.
    /// </summary>
    /// <summary>
    ///     How far above the simulated contact point the ACTOR is drawn, in units - a VISUAL offset,
    ///     and NOT the same question as "how high does it have to be to clear the ground".
    ///
    ///     TWO NUMBERS THAT LOOKED LIKE ONE, and conflating them put every dropped item at rest in
    ///     mid-air:
    ///
    ///       * **135** is FortPickupAthena's collision capsule - what its ORIGIN needs above a
    ///         surface for the capsule to be free of it. Measured (see below). It matters exactly
    ///         once, at the SPAWN point, because that is where the client's own projectile movement
    ///         takes its one swept substep; starting inside geometry there is a depenetration
    ///         instead of a fall. NativeRpcHandlers.DropLaunchHeight is what has to satisfy it, and
    ///         does, with room to spare.
    ///       * **10** is where the item LOOKS right on the ground - roughly its own half-thickness,
    ///         because the mesh is drawn at the actor's ORIGIN rather than at the capsule's bottom.
    ///         That is exactly why 135 hung the item 1.35 m in the air.
    ///
    ///     40 was tried first, borrowed from FLOOR_LOOT_Z_OFFSET, and reported as "it does not quite
    ///     fall to the ground" - 0.4 m of daylight is visible. THE REFERENCE CANNOT SETTLE THIS: the
    ///     0906 server log has 3461 simulated arcs but only 17 that come to rest on flat open terrain
    ///     the height bake covers, and those spread from 60 to 175 (median 105) with an obvious
    ///     upward bias from items resting on props. So this is set by eye, and PICKUP_REST_CLEARANCE
    ///     is the knob - raise it if items start sinking into the ground instead.
    ///
    ///     A resting pickup is allowed to overlap the ground - nothing sweeps it, so nothing
    ///     depenetrates it - which is why 40 is safe here and 135 is not merely conservative but
    ///     wrong: at 135 the item comes to rest 1.35 m up and hangs in the air.
    ///
    ///     THE 135 MEASUREMENT, kept because it is what DropLaunchHeight is sized against.
    ///     `ResolvePenetration`'s depth obeys `depth = C - (Z - groundZ)`; against the baked
    ///     landscape grid, five drops of ours and one of the REFERENCE server's - 116,000 units away
    ///     across the map and 2,000 units lower - agree:
    ///
    ///         ours   Z=3922.8  depth  90.143  ground 3883  ->  C = 130.9
    ///         ours   Z=3914.9  depth  95.585  ground 3876  ->  C = 134.5
    ///         ours   Z=3906.8  depth 103.626  ground 3871  ->  C = 139.4
    ///         ours   Z=3885.5  depth 113.136  ground 3864  ->  C = 134.6
    ///         PR3.0  Z=2015.2  depth  34.998  ground 1915  ->  C = 135.2
    ///
    ///     So C is a property of the actor, not of our arithmetic or of the height bake.
    /// </summary>
    private float RestClearance =>
        float.TryParse(Options.Get("PICKUP_REST_CLEARANCE"), out var c) && c >= 0 ? c : 10f;

    /// <summary>The simulated point, raised to where the actor's origin belongs.</summary>
    private FVector Lift(FVector point) =>
        new() { X = point.X, Y = point.Y, Z = point.Z + RestClearance };

    /// <summary>
    ///     The inverse of <see cref="Lift" />: an ACTOR position, lowered to the point the simulation
    ///     tracks.
    ///
    ///     Needed because Step sweeps the point that TOUCHES surfaces while a caller only ever knows
    ///     where it wants the actor. Feeding an actor position straight into Settle made the item
    ///     fall a whole clearance further than it should - invisible at 40, and impossible to ignore
    ///     at the measured 135.
    /// </summary>
    private FVector Drop(FVector actorPosition) =>
        new() { X = actorPosition.X, Y = actorPosition.Y, Z = actorPosition.Z - RestClearance };

    /// <summary>
    ///     Whether a dropped item is thrown for the client to watch, or simply arrives where it
    ///     landed. ON by default as of Round 158, and the reversal is the point.
    ///
    ///     THE ADVICE HERE USED TO BE "LEAVE IT OFF", after five live attempts that each produced a
    ///     drop with no animation and two that produced something worse. The reasoning was sound and
    ///     the conclusion was wrong, because every one of those attempts spawned the actor at the
    ///     LANDING point with the old 40-unit clearance - which is 95 units inside the ground - so
    ///     the client's first physics substep was a depenetration and its projectile movement
    ///     stopped there. The client was never declining to animate; it was being handed a position
    ///     it could not fall from. See RestClearance for the measurement and BeginStreamed for the
    ///     one word that had to change.
    ///
    ///     PICKUP_TOSS_STREAM=0 restores the settled behaviour - the item is simply on the ground,
    ///     in the right place, immediately - which is still the right fallback if a drop ever lands
    ///     somewhere a player cannot reach.
    /// </summary>
    private bool Streaming => Options.Get("PICKUP_TOSS_STREAM") is not "0";

    /// <summary>
    ///     The simulated state of each toss in the air.
    ///
    ///     POSITION IS HELD HERE, NOT READ BACK OFF THE ACTOR, and that distinction is not fussiness.
    ///     The tick used to do `Step(pickup.GetActorLocation(), ...)` and then write `Lift(position)`
    ///     back - so it read the LIFTED value as the physics state and lifted it again, adding
    ///     RestClearance every single substep. Items flew upward at 40 units a step: launched at
    ///     Z=1309 and "RAN OUT of substeps" at Z=8772, while the up-front Settle of the very same
    ///     toss landed correctly at Z=1152 in the same log, three lines apart.
    ///
    ///     The actor's location is an OUTPUT of this simulation. Feeding an output back in as state
    ///     is how a fixed offset becomes an accelerating one.
    /// </summary>
    /// <summary>
    ///     A floor the toss may always fall back on: the height of the DROPPER'S FEET.
    ///
    ///     WHY IT IS NEEDED AT ALL. The three collision sources plus the landscape grid still leave
    ///     holes - the warmup island is the big one, a sublevel the persistent terrain grid does not
    ///     cover (the same gap SendMovementCorrection hit with "the baked map has no ground under the
    ///     camera"). A drop there met nothing, fell for ten seconds, and the toss had to be refused.
    ///     Every drop on the island therefore lost its animation.
    ///
    ///     WHY THE FEET ARE A HONEST ANSWER: the player is STANDING on something. Whatever the bake
    ///     knows or does not know, there is a surface at their feet, and an item they drop belongs on
    ///     it. FortProjectileSystem reaches for the same fact under the name ThrowerGroundZ, with the
    ///     same stated cost - "a grenade thrown off a cliff stops level with the cliff-top". Bounded,
    ///     and far better than an item with no arc or one that leaves the map.
    ///
    ///     Zero means "no fallback", which is what a caller that does not know a floor should pass.
    ///
    ///     Held per in-flight toss and set immediately before each Step - see Tick. A single shared
    ///     value would hand every item of a dropped STACK the last dropper's feet.
    /// </summary>
    private float _floorZ;

    /// <summary>
    ///     Starts a toss the player can WATCH, when PICKUP_TOSS_STREAM is on. Returns true when it
    ///     took the pickup; false means the caller should use <see cref="Settle" /> as before.
    ///
    ///     The pickup is put at the launch point and told to replicate its movement; Tick advances
    ///     it. Everything else about it - the entry, the toss state, the channel - is unchanged.
    /// </summary>
    /// <summary>
    ///     RETIRED. Always returns false, and the reason is worth more than the code was.
    ///
    ///     Five attempts were made to give a dropped item a visible toss. The server half is
    ///     CORRECT - the log proves it: `launched from (27772, -120509, 10141) ... should reach
    ///     (27783, -120631, 10005) in 0.80s` followed by `streamed toss settled at (27783, -120631,
    ///     10005) after 24 substep(s)`, with no drift warning. The trajectory, the landing point and
    ///     the timing all agree.
    ///
    ///     What none of the five produced is an animation, and two of them produced something worse:
    ///
    ///       * SERVER STREAMS THE POSITION (twice, before and after the simulation was fixed) - no
    ///         arc either time, and the item became UNPICKABLE. That is 2 for 2 on
    ///         `bReplicateMovement`: a pickup carrying replicated movement is one the client will not
    ///         let anyone touch. It never sent a single ServerHandlePickup for one.
    ///       * SERVER PUBLISHES THE ARC - the client reads it, parks the item at LootInitialPosition
    ///         for FlyTime, then snaps. Worse than not trying.
    ///       * SPAWN-HEADER VELOCITY ALONE - correct and harmless: the client builds its projectile
    ///         movement component, simulates with our exact numbers and plays the drop sound itself.
    ///         But it simulates ONE FRAME, and so does the reference client (its four substeps for
    ///         FortPickupAthena_2147467127 share a millisecond), so it moves the item hardly at all.
    ///
    ///     The settled default - solve the arc, place the item where it lands - is the best of them:
    ///     the item is on the ground, in the right place, immediately, and can be picked up. That is
    ///     what runs, and `Step`, the landscape lookup, the feet-as-floor fallback, the rest
    ///     clearance and the micro-bounce rule are all still doing their work inside it.
    ///
    ///     WHAT WOULD SETTLE IT is the thing that was proposed and not done: disassemble the native
    ///     `AFortPickup::TossPickup` out of the memory dump. It is native C++, so there is no
    ///     bytecode, but the .text is decrypted in a full dump ([[fortnite-exe-text-encrypted]]) and
    ///     [[cppstructops-vtable-route]] is the worked procedure. Five experiments cost a live round
    ///     each and produced three exclusions; one disassembly would say what the function sets.
    /// </summary>
    /// <summary>
    ///     Publishes the arc for the CLIENT to fly, and returns true when it took the pickup.
    ///
    ///     THE MECHANISM IS NOW READ, NOT GUESSED. `AFortPickup::SetupForMovementCompToss` was
    ///     disassembled out of the memory dump (see [[cppstructops-vtable-route]] for the route).
    ///     Its InProgress branch is, in full:
    ///
    ///         speed    = |LootFinalPosition - LootInitialPosition| * (1.0 / FlyTime)
    ///         velocity = solver(LootInitialPosition, LootFinalPosition, speed, 0.0)
    ///         MovementComponent->Velocity      = velocity          ; +0xC4 / +0xCC
    ///         MovementComponent->InitialSpeed  = |velocity|        ; +0xF4
    ///
    ///     The 1.0 was read straight out of the dump at 0x1444C4FB8. So the client needs FOUR things
    ///     and nothing else: the two positions, a non-zero FlyTime, and TossState = InProgress. It
    ///     needs no replicated movement, and no velocity from us - our spawn-header velocity is
    ///     irrelevant to this path.
    ///
    ///     **AND THE CLIENT NEVER RUNS ANY OF IT.** The paragraph that used to stand here worked out
    ///     that FlyTime is a divisor and that "the client needs FOUR things and nothing else". Both
    ///     halves are true of the code quoted above and both are irrelevant, because the branch is
    ///     entered only when `AActor::Role == ROLE_Authority` - see the instruction listing inside
    ///     BeginStreamed. On a client a replicated pickup is a simulated proxy, so this is server
    ///     code, and the properties are sent for the server's own bookkeeping and for the AtRest
    ///     handoff at the end of the flight, not to drive an animation.
    ///
    ///     WHAT DOES DRIVE IT is the client's own UProjectileMovementComponent, started from the
    ///     velocity in the spawn header - the ordinary way any simulated proxy moves, and the way
    ///     this project's frag grenades have always moved. All it ever needed was to be spawned
    ///     somewhere it could fall FROM.
    /// </summary>
    public bool BeginStreamed(AFortPickup pickup, FVector from, FVector velocity, float floorZ) {
        if (!Streaming) return false;

        // DROPPED INTO CONTACT SPACE FIRST - `from` is where the ACTOR is launched, and Step tracks
        // the point that touches surfaces. See Drop.
        var (landing, seconds, rested) = SettleTimed(Drop(from), velocity, floorZ);

        // NO GROUND, NO TOSS - see SettleActorLocation. An arc that ends in a hole is worse than none.
        if (!rested) {
            Console.WriteLine($"FortPickupToss: no ground under {Lift(from)} - leaving the item where it " +
                              "was dropped rather than flying it into a hole");
            return false;
        }

        var start = from;            // already an actor position: the launch point
        var end = Lift(landing);     // the simulated contact, raised to where the actor's origin belongs
        var distance = MathF.Sqrt(FVector.DistSquared(start, end));

        // FLYTIME IS THE FLIGHT, NOT A DIVISOR, and the correction is worth recording because the
        // opposite was written here with a disassembly to back it up.
        //
        // `AFortPickup::SetupForMovementCompToss` does compute a launch velocity from these three
        // properties - `speed = |LootFinalPosition - LootInitialPosition| / FlyTime`, fed to a
        // solver - and that is what the comment here used to describe. What the first reading of it
        // MISSED is the instruction immediately before:
        //
        //      0x14170D543   movzx eax, byte ptr [rbx + 0x3EC]   ; PickupLocationData.TossState
        //      0x14170D54A   cmp   al, 1                         ; InProgress?
        //      0x14170D54C   jne   0x14170D6BE                   ; no -> AtRest, or warn
        //      0x14170D552   cmp   byte ptr [rbx + 0xF0], 3      ; AActor::Role == ROLE_Authority?
        //      0x14170D559   jne   0x14170D721                   ; NO -> do nothing at all
        //
        // The arc solver is AUTHORITY-ONLY. On a client a replicated pickup has Role = 1
        // (ROLE_SimulatedProxy - the client's own UActorChannel::CleanUp line says so verbatim:
        // "Role: 1, RemoteRole: 3"), so that branch never runs there and no amount of care with
        // these three properties can make a client fly anything. The client's motion comes from its
        // OWN UProjectileMovementComponent, ticking on the spawn-header velocity - which is exactly
        // what the reference does, and what our own frag grenade already does for hundreds of
        // substeps.
        //
        // So FlyTime carries the honest simulated duration: it is what Tick uses to decide when the
        // arc is over, and nothing on the client divides by it.
        var flyTime = MathF.Max(seconds, 0.05f);

        // THE ACTOR STARTS WHERE THE TOSS STARTS. This line said `end` for five attempts, and that
        // one word is the whole missing animation: the spawn header carried the LANDING point, so
        // the client built its projectile movement component at a position already resting on the
        // ground - or, with the old 40-unit clearance, 95 units inside it - and the very first
        // substep was a depenetration rather than a fall. Tick moves the actor to `end` when the
        // flight is over, which is when the server needs it there.
        pickup.SetActorLocation(start);

        // AND THE SPAWN-HEADER VELOCITY TOO, which the rewrite dropped. The two are independent
        // routes into the same movement component and each is separately PROVEN to arrive: the
        // client's log showed it simulating with our exact spawn velocity ("Vel X=231.500 Y=118.300
        // Z=420.000", our numbers to the quantization), and the disassembly shows
        // SetupForMovementCompToss writing a solved one. Sending only the arc data left the client
        // with no launch velocity at all if the solver declines - and "it falls straight down with no
        // arc" is exactly what a movement component with gravity and no launch velocity does.
        //
        // bSerializeVelocity is only written when the velocity is non-zero (see
        // UPackageMapClient.SerializeNewActor), so this costs nothing on a pickup that is not tossed.
        pickup.Velocity = velocity;
        pickup.TossStartLocation = start;  // handle 41 - LootInitialPosition
        pickup.RestLocation = end;         // handles 42 and 45
        pickup.FlyTime = flyTime;          // handle 43 - the simulated flight
        pickup.StartDirection = Normalize(velocity);   // handle 44
        pickup.bServerStoppedSimulation = false;
        pickup.TossState = EFortPickupTossState.InProgress;
        pickup.SetNetDormancy(ENetDormancy.Awake);

        // THE SERVER FLIES IT, because the evidence says nothing else will. Counting substeps in the
        // 0906 capture settles who simulates a dropped item:
        //
        //     PR3.0 SERVER log     279,874 FortPickupAthena substeps, `Role: 3`
        //     PR3.0 CLIENT log         ~3 per pickup, `Role: 1`, then "is stuck inside ..."
        //     our CLIENT log            1 per pickup
        //
        // The reference client's projectile movement component dies within a frame exactly like
        // ours; it is the SERVER that runs the arc, one example being 40 substeps carrying
        // FortPickupAthena_2147461186 from Z=1374 down to Z=1212 over 1.1 s. So the item has to be
        // moved here and its position replicated - see UActorChannel's pickup arm and
        // PICKUP_REPLICATE_MOVEMENT.
        pickup.bReplicateMovement = ReplicateMovement;
        pickup.bUseExplicitVelocity = true;

        // ROUND ONE DECIMAL - READ OUT OF THE CDO, not guessed, and it is a THIRD value that neither
        // guess would have found.
        //
        // FRepMovement's quantization levels are NOT on the wire: each end reads its own copy, so a
        // mismatch is silent and total. Sent at the pawn's RoundTwoDecimals, a pickup at
        // (-117000, -121000, 4000) decodes as (-11700000, ...) - far past HALF_WORLD_MAX - and the
        // client says so:
        //
        //     LogProjectileMovement: Warning: FortPickupAthena_2147476721 is outside the world bounds!
        //
        // UMovementComponent::CheckStillInWorld then does `SetActorEnableCollision(false)` AND
        // `StopMovementImmediately()`, which is the whole of "it stopped falling and I cannot pick it
        // up" from one line. THAT is what the two old rounds blamed on bReplicateMovement.
        //
        // The level was then read straight out of Default__FortPickupAthena in the memory dump
        // (AActor+0x60 FRepMovement, LocationQuantizationLevel at +0x31, so AActor+0x91), after
        // checking the method against two independently confirmed CDOs:
        //
        //     Default__FortPlayerPawnAthena   RoundTwoDecimals    (the client console printed this)
        //     Default__FortProjectileBase     RoundWholeNumber    (live-confirmed by the grenade)
        //     Default__BuildingSMActor        RoundWholeNumber
        //     Default__FortPickupAthena       RoundOneDecimal     <- 10 / 27 bits
        //
        // All four read VelocityQuantizationLevel = RoundWholeNumber and RotationQuantizationLevel =
        // ByteComponents (the enum's 0), which is what NetSerializeWrite already does.
        // PICKUP_REP_SCALE overrides it: 1 or 100.
        pickup.ReplicatedMovement.LocationQuantization =
            Options.Get("PICKUP_REP_SCALE") switch {
                "1" => FRepMovement.RoundWholeNumber,
                "100" => (100u, 30u),
                _ => (10u, 27u)   // RoundOneDecimal - Default__FortPickupAthena's own level
            };
        pickup.ReplicatedMovement.Location = start;
        pickup.ReplicatedMovement.Rotation = pickup.GetActorRotation();
        pickup.ReplicatedMovement.LinearVelocity = velocity;

        InFlight.Add((pickup, end, flyTime, 0f, Drop(start), Clamp(velocity), 0));

        Console.WriteLine($"FortPickupToss: arc {start} -> {end}, {distance:F0} units in {flyTime:0.00}s, " +
                          $"launch velocity {velocity} ({Speed(velocity):F0} uu/s) - the client falls it, " +
                          "and AtRest hands it back at the end");
        return true;
    }

    /// <summary>
    ///     One toss in the air. Position and Velocity are the LIVE simulation state, held here and
    ///     never read back off the actor - see the note about feeding an output back in as state.
    ///     Position is in CONTACT space (see Drop/Lift); the actor is drawn at Lift(Position).
    /// </summary>
    private readonly List<(AFortPickup Pickup, FVector Landing, float Seconds, float StartedAt,
                                  FVector Position, FVector Velocity, int Steps)> InFlight = new();

    /// <summary>
    ///     Whether the flying item's position goes on the wire. On by default; PICKUP_REPLICATE_MOVEMENT=0
    ///     turns it off and leaves the item parked at its launch point until AtRest.
    ///
    ///     THIS IS THE SWITCH THAT WAS BLAMED FOR AN OLD BUG. Two live rounds concluded that "a
    ///     pickup carrying replicated movement is one the client will not let anyone pick up" - not
    ///     one ServerHandlePickup in either. Both of those rounds also placed the item 95 units
    ///     INSIDE the ground for the whole flight (RestClearance was 40 against a measured 135), and
    ///     an item buried in the landscape is unreachable by the client's interaction query for that
    ///     reason alone. The confound is now removed, so this gets one more honest test.
    /// </summary>
    private bool ReplicateMovement =>
        Options.Get("PICKUP_REPLICATE_MOVEMENT") is not "0";

    /// <summary>
    ///     The horizontal speed a toss needs to travel <paramref name="distance" /> before it lands.
    ///
    ///     Lives here rather than in the caller because it is arithmetic in THIS file's constants -
    ///     the measured gravity and the rest clearance - and a copy in NativeRpcHandlers would be
    ///     one more pair of numbers to keep in step. A container fans its loot out to chosen
    ///     DISTANCES; this turns each one into the velocity that gets there.
    ///
    ///     Plain ballistics, no collision: the real landing still comes from Settle, which may find a
    ///     wall or a slope first. That is the right order - aim, then simulate - and it is why an
    ///     authored fan point is now a target rather than a placement.
    /// </summary>
    public float SpeedForDistance(float launchZ, float floorZ, float upSpeed, float distance) {
        // The launch is an ACTOR position; the physics point that touches the ground is lower.
        var height = MathF.Max(launchZ - RestClearance - floorZ, 1f);
        var seconds = (upSpeed + MathF.Sqrt(upSpeed * upSpeed + 2f * Gravity * height)) / Gravity;

        return MathF.Min(distance / seconds, MaxSpeed);
    }

    private FVector Normalize(FVector v) {
        var length = Speed(v);
        return length <= 0f ? new FVector { Z = 1f } : new FVector { X = v.X / length, Y = v.Y / length, Z = v.Z / length };
    }

    /// <summary>
    ///     Advances every toss in the air by ONE SUBSTEP and retires the ones that have landed.
    ///
    ///     ONE SUBSTEP PER TICK, not one per frame's worth of time: the substep is a fixed 1/30 and
    ///     the world ticks at about that, so stepping once keeps the simulation at the rate the
    ///     constants were measured at. A toss lasts well under a second either way.
    ///
    ///     THE SERVER OWNS THE MOVEMENT. That is not a preference, it is what the reference does -
    ///     see the substep counts quoted in BeginStreamed. Both clients' projectile movement
    ///     components stop within a frame, so an item nobody moves is an item that hangs in the air
    ///     until AtRest snaps it down, which is the "floats, then jumps" this has produced twice.
    ///
    ///     Position is held in the InFlight tuple and NEVER read back off the actor: the tick used
    ///     to do Step(pickup.GetActorLocation(), ...) and write Lift(position) back, so it read the
    ///     LIFTED value as the physics state and lifted it again every substep. Items climbed 40
    ///     units a step and left the map. The actor's location is an OUTPUT of this simulation.
    /// </summary>
    public void Tick(float now) {
        var world = World;

        if (InFlight.Count == 0) return;

        for (var i = InFlight.Count - 1; i >= 0; i--) {
            var (pickup, landing, seconds, startedAt, position, velocity, steps) = InFlight[i];

            if (pickup.IsPendingKillPending()) { InFlight.RemoveAt(i); continue; }

            // THE CLOCK STARTS WHEN SOMEBODY CAN SEE IT, not when the item is dropped.
            //
            // A pickup's channel does not open on the spawning tick - ServerReplicateActors opens
            // newly relevant actors on a later pass, throttled by NEWLY_RELEVANT_PER_TICK - so the
            // client hears about the item some way into its flight. An arc nobody was there to see
            // is not an arc, and worse, the client's first sight of the item would be a position
            // partway down with no explanation of how it got there.
            if (startedAt == 0f) {
                var visible = world.NetDriver?.ClientConnections.Any(c => c.FindActorChannel(pickup) != null) ?? false;
                if (visible) InFlight[i] = (pickup, landing, seconds, now, position, velocity, steps);
                continue;
            }

            // ONE SUBSTEP, then the actor is moved to where it now is. `_floorZ` is per-toss and has
            // to be restored before every Step - a single shared value would hand every item of a
            // dropped STACK the last dropper's feet.
            _floorZ = landing.Z - RestClearance;
            var (next, bounced, rested) = Step(position, velocity);
            steps++;

            pickup.SetActorLocation(Lift(next));
            pickup.ReplicatedMovement.LinearVelocity = bounced;

            if (!rested && steps < MaxSubSteps) {
                InFlight[i] = (pickup, landing, seconds, startedAt, next, bounced, steps);
                continue;
            }

            // THE SERVER TAKES THE ITEM BACK at the end of the flight. AtRest plus
            // FinalTossRestLocation is the handoff, and it is exact: the client's AtRest branch is
            // NOT authority-gated - it stops the movement component and teleports the actor to this
            // point:
            //
            //      0x14170D6BE   cmp  al, 2                      ; AtRest?
            //      0x14170D6C2   call 0x1416F4BD0                ; stop simulating
            //      0x14170D6CA   lea  rdx, [rbx + 0x3E0]         ; FinalTossRestLocation
            //      0x14170D6DF   call 0x142E46FA0                ; SetActorLocation
            //
            // Which is also why AtRest must NOT be sent while the item is still meant to be falling:
            // it is a teleport, and sending it early is what "it snaps to the ground" was.
            var restedAt = Lift(next);

            pickup.SetActorLocation(restedAt);
            pickup.TossState = EFortPickupTossState.AtRest;
            pickup.bServerStoppedSimulation = true;
            pickup.TossStartLocation = restedAt;
            pickup.RestLocation = restedAt;

            // MOVEMENT COMES OFF THE WIRE AGAIN once the item is down. A resting pickup that keeps
            // carrying ReplicatedMovement is one more actor for the diff to walk for the rest of the
            // match, and it is also the state the two "unpickable" rounds were in.
            pickup.bReplicateMovement = false;
            pickup.bUseExplicitVelocity = false;

            pickup.FlushNetDormancy();
            pickup.SetNetDormancy(ENetDormancy.DormantAll);
            InFlight.RemoveAt(i);

            Console.WriteLine($"FortPickupToss: landed at {restedAt} after {steps} substep(s), " +
                              $"{now - startedAt:0.00}s watched (solved {seconds:0.00}s to {landing}) - AtRest");
        }
    }

    /// <summary>
    ///     One substep: gravity, the swept move, and a bounce if it hit something. Shared by both the
    ///     synchronous solve and the streamed one, so the two cannot drift apart - which is the whole
    ///     reason PICKUP_TOSS_STREAM is a fair comparison rather than two implementations.
    /// </summary>
    private (FVector Position, FVector Velocity, bool Rested) Step(FVector position, FVector velocity) {
        var v = Clamp(new FVector { X = velocity.X, Y = velocity.Y, Z = velocity.Z - Gravity * SubStep });

        var next = new FVector {
            X = position.X + v.X * SubStep,
            Y = position.Y + v.Y * SubStep,
            Z = position.Z + v.Z * SubStep
        };

        var contact = FirstContact(position, next);
        if (contact == null) return (next, v, false);

        var (point, normal) = contact.Value;

        // Back off ALONG THE NORMAL by a hair so the next step starts outside the surface rather
        // than exactly on it, where floating point puts it back inside.
        //
        // A HAIR, NOT THE ITEM'S SIZE. Making this the 40-unit clearance instead was tried and is
        // wrong in a way worth recording: the simulated point would be lifted 40 above the surface
        // after every contact, then free-fall those 40 units before contacting again, and the item
        // would bounce down the whole flight without ever resting. The clearance is about where the
        // ACTOR's origin is drawn, not about where the physics touches - see Lift.
        var landed = new FVector {
            X = point.X + normal.X * 0.5f,
            Y = point.Y + normal.Y * 0.5f,
            Z = point.Z + normal.Z * 0.5f
        };

        var approach = v.X * normal.X + v.Y * normal.Y + v.Z * normal.Z;
        var tangentScale = 1f - BounceFriction;

        var bounced = new FVector {
            X = (v.X - normal.X * approach) * tangentScale - normal.X * approach * Bounciness,
            Y = (v.Y - normal.Y * approach) * tangentScale - normal.Y * approach * Bounciness,
            Z = (v.Z - normal.Z * approach) * tangentScale - normal.Z * approach * Bounciness
        };

        // A BOUNCE THAT CANNOT CLEAR THE NEXT STEP'S GRAVITY IS NOT A BOUNCE - it is an item lying on
        // a surface, and without this it never stops.
        //
        // The arithmetic is the whole story. Gravity adds 2800/30 = 93.3 uu/s downward every substep;
        // hitting the ground with that returns 93.3 x 0.534 = 49.8 upward, which is above StopSpeed
        // and so counts as still moving. Next substep: the same 93.3, the same 49.8. An item resting
        // on the ground therefore micro-bounced forever, and every drop hit the 300-substep ceiling -
        // the live log gave it away by settling EXACTLY at the cap every single time, and by moving
        // barely a hundred units while doing it.
        //
        // Removing the normal component when it is that small leaves the tangential slide, which
        // friction halves at every contact and which therefore dies in a handful of steps. UE has the
        // same idea in BounceVelocityStopSimulatingThreshold; what it does NOT have is this server's
        // fixed substep, which is what makes the threshold a function of gravity rather than a
        // constant.
        var normalSpeed = bounced.X * normal.X + bounced.Y * normal.Y + bounced.Z * normal.Z;
        if (normalSpeed > 0f && normalSpeed < Gravity * SubStep) {
            bounced = new FVector {
                X = bounced.X - normal.X * normalSpeed,
                Y = bounced.Y - normal.Y * normalSpeed,
                Z = bounced.Z - normal.Z * normalSpeed
            };
        }

        return (landed, bounced, Speed(bounced) < StopSpeed);
    }

    /// <summary>
    ///     Simulates the toss and returns where the item settles.
    ///
    ///     Synchronous on purpose - see the class comment. The three collision sources are the same
    ///     three FortProjectileSystem sweeps against, in the same order and for the same reasons:
    ///     player builds, the game's own convex hulls, and the baked walls for meshes that ship no
    ///     hulls.
    /// </summary>
    /// <summary>
    ///     <see cref="Settle" />, raised to where the actor's origin belongs - what a caller placing
    ///     the actor wants. See <see cref="RestClearance" />.
    /// </summary>
    public FVector SettleActorLocation(FVector actorFrom, FVector velocity, float floorZ) {
        // Contact space in, actor space out - see Drop and Lift.
        var landing = Settle(Drop(actorFrom), velocity, floorZ);

        // NOWHERE TO LAND IS NOT A PLACE TO LAND. When the sweep finds nothing the loop runs out of
        // substeps having fallen ten seconds, and using that point puts the item thousands of units
        // underground - where nobody can pick it up. That is not hypothetical: the warmup island is
        // a sublevel the height grid does not cover (the same gap SendMovementCorrection hit with
        // "the baked map has no ground under the camera"), and a drop there produced
        // `gave up after 300 substeps at (-117196, -120866, -832)` from a launch at Z=4065.
        //
        // So an unresolved toss leaves the item WHERE IT WAS DROPPED, which is the behaviour that
        // worked before any of this existed. Degrade to the old answer, never to a hole in the map.
        return _lastSettleRested ? Lift(landing) : actorFrom;
    }

    /// <summary>
    ///     <see cref="Settle" />, and HOW LONG it took. The duration is what the client needs for
    ///     PickupLocationData.FlyTime - an arc with no time on it is one it cannot pace.
    /// </summary>
    public (FVector Landing, float Seconds, bool Rested) SettleTimed(FVector from, FVector velocity, float floorZ) {
        var landing = Settle(from, velocity, floorZ);
        return (landing, _lastSettleSteps * SubStep, _lastSettleRested);
    }

    private int _lastSettleSteps;

    /// <summary>Whether the last Settle came to rest, or merely ran out of substeps.</summary>
    private bool _lastSettleRested;

    public FVector Settle(FVector from, FVector velocity, float floorZ = 0f) {
        _floorZ = floorZ;

        var position = from;
        var v = Clamp(velocity);

        for (var step = 0; step < MaxSubSteps; step++) {
            var (next, bounced, rested) = Step(position, v);
            position = next;
            v = bounced;

            if (!rested) continue;

            _lastSettleSteps = step + 1;
            _lastSettleRested = true;
            Console.WriteLine($"FortPickupToss: settled at {position} after {step + 1} substep(s)");
            return position;
        }

        _lastSettleSteps = MaxSubSteps;
        _lastSettleRested = false;
        Console.WriteLine($"FortPickupToss: gave up after {MaxSubSteps} substeps at {position} - the item " +
                          "never came to rest, so this is where it got to rather than where it belongs");
        return position;
    }

    private (FVector Point, FVector Normal)? FirstContact(FVector from, FVector to) {
        var buildHit = BuildingStructuralSupportSystem.Of(World).SweepToBuild(from, to);
        var hullHit = WorldCollision.Sweep(from, to);
        var wallHit = TerrainWalls.Sweep(from, to);

        (FVector Point, FVector Normal)? best = null;
        var bestDistance = float.MaxValue;

        // THE LANDSCAPE, which none of the three sweeps above knows anything about. Leaving it out is
        // why the first streamed drop VANISHED: the log shows an item launched at Z=1661 and still
        // falling at Z=-3302 three hundred substeps later, having met nothing at all. The convex
        // hulls are placed MESHES and the baked walls are meshes without hulls; open terrain is
        // neither, and a player standing on grass is standing on exactly that.
        //
        // Same source FortProjectileSystem uses for the same reason, and the same "AT OR BELOW"
        // question - `GetSurfaceUnder` rather than `GetGroundHeight`, so an item dropped on a POI's
        // first storey is not told about the terrain twenty metres underneath it.
        //
        // The normal is straight up. The height grid stores a height, not a slope, so a genuinely
        // steep hillside will bounce an item as if it were flat; the alternative is the item falling
        // out of the world, which is what it did.
        if (to.Z < from.Z && TerrainHeightMap.GetSurfaceUnder(to.X, to.Y, 0f, from.Z) is { } groundZ
            && to.Z <= groundZ) {
            Consider(new FVector { X = to.X, Y = to.Y, Z = groundZ }, new FVector { Z = 1f });
        }

        void Consider(FVector point, FVector normal) {
            var dx = point.X - from.X;
            var dy = point.Y - from.Y;
            var dz = point.Z - from.Z;
            var distance = dx * dx + dy * dy + dz * dz;
            if (distance >= bestDistance) return;

            bestDistance = distance;
            best = (point, normal);
        }

        // The dropper's feet, LAST - a real surface always wins over the fallback plane.
        if (to.Z < from.Z && _floorZ != 0f && to.Z <= _floorZ) {
            Consider(new FVector { X = to.X, Y = to.Y, Z = _floorZ }, new FVector { Z = 1f });
        }

        if (buildHit is { } b) Consider(b.Point, AxisNormal(b.Axis, to, from));
        if (hullHit is { } h) Consider(h.Point, h.Normal);
        if (wallHit is { } w) Consider(w.Point, AxisNormal(w.Axis, to, from));

        return best;
    }

    /// <summary>
    ///     A box sweep reports which AXIS it entered through, not a normal. The normal is that axis
    ///     pointing back the way the item came - the same conversion FortProjectileSystem does so
    ///     that one bounce rule can serve all three collision sources.
    /// </summary>
    private FVector AxisNormal(int axis, FVector to, FVector from) {
        var sign = axis switch {
            0 => to.X > from.X ? -1f : 1f,
            1 => to.Y > from.Y ? -1f : 1f,
            _ => to.Z > from.Z ? -1f : 1f
        };

        return axis switch {
            0 => new FVector { X = sign },
            1 => new FVector { Y = sign },
            _ => new FVector { Z = sign }
        };
    }

    private float Speed(FVector v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    private FVector Clamp(FVector v) {
        var speed = Speed(v);
        if (speed <= MaxSpeed || speed <= 0f) return v;

        var k = MaxSpeed / speed;
        return new FVector { X = v.X * k, Y = v.Y * k, Z = v.Z * k };
    }
}
