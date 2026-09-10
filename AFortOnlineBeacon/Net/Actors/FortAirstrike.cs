namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     The AIR STRIKE's bombardment - `Athena_AppleSauce`, whose name gives nothing away.
///
///     Its projectile does not explode. `B_Prj_Athena_AppleSauce_Grenade` spawns a marker zone
///     (`BGA_AppleSauce_Zone_C`) and a rocket spawner (`B_BGA_AppleSauce_RocketSpawner_C`) and
///     destroys itself; what hurts arrives over the following seconds as rockets falling on a grid.
///     Until now this server exploded it once for the FRAG GRENADE's 100/375, because
///     `Athena_AppleSauce` has no row of its own in the damage table and fell back - which is exactly
///     the "it does the same damage as a grenade" that was reported.
///
///     EVERY NUMBER BELOW IS A ROW, read from AthenaGameData through the projectile's own
///     ScalableFloats - nothing here is chosen:
///
///         Default.AppleSauce.Damage                  75     per rocket, to players
///         Default.AppleSauce.EnvDamage              200     per rocket, to structures
///         Default.AppleSauce.ExplosionRange         400     each rocket's blast radius
///         Default.AppleSauce.RocketAmount            20     rockets in a group
///         Default.AppleSauce.GroupAmount              3     groups
///         Default.AppleSauce.RowSize                  3     so the grid is 3x3 cells
///         Default.AppleSauce.DistanceBetweenRockets 750     cell pitch
///         Default.AppleSauce.TimeBetweenRockets     0.14
///         Default.AppleSauce.TimeBetweenGroups       0.1
///         Default.AppleSauce.Delay                    0     before the first one
///         Default.AppleSauce.ZoneRadius             900     the marked area
///         Default.AppleSauce.RandomStrength         2.8     scatter, in units of the cell pitch
///
///     Sixty rockets at 0.14 s apart is about eight and a half seconds of bombardment, which is what
///     the item is: you have time to run out of the circle.
///
///     THE SCATTER IS THE ONE THING NOT PINNED EXACTLY. The Blueprint walks a 3x3 grid and offsets
///     each shot by RandomStrength through its own RandomFloatInRange; the sequence is the client's
///     and cannot be reproduced, so this server rolls its own inside the same grid and the same
///     radius. The AREA and the COUNT are exact; which square metre each individual rocket lands on
///     is not, and could not be without replicating every rocket.
/// </summary>
internal static class FortAirstrike {
    public const string ItemName = "Athena_AppleSauce";

    /// <summary>Per-rocket damage to players (`Default.AppleSauce.Damage`).</summary>
    private const float RocketDamage = 75f;

    /// <summary>Per-rocket damage to structures (`Default.AppleSauce.EnvDamage`).</summary>
    private const float RocketEnvironmentDamage = 200f;

    /// <summary>`Default.AppleSauce.ExplosionRange`.</summary>
    private const float RocketRadius = 400f;

    /// <summary>`Default.AppleSauce.RocketAmount` x `Default.AppleSauce.GroupAmount`.</summary>
    private const int RocketsPerGroup = 20;

    private const int Groups = 3;

    /// <summary>`Default.AppleSauce.RowSize` - the grid is this square.</summary>
    private const int RowSize = 3;

    /// <summary>`Default.AppleSauce.DistanceBetweenRockets`.</summary>
    private const float CellPitch = 750f;

    /// <summary>`Default.AppleSauce.TimeBetweenRockets` and `...TimeBetweenGroups`.</summary>
    private const float TimeBetweenRockets = 0.14f;

    private const float TimeBetweenGroups = 0.1f;

    /// <summary>`Default.AppleSauce.Delay` - before the first rocket is FIRED.</summary>
    private const float InitialDelay = 0f;

    /// <summary>
    ///     `Default.AppleSauce.RocketHeight` - how far up a rocket is fired from. With RocketSpeed
    ///     and the rocket's own `ProjectileGravityScale` of 0, that is a straight line down.
    /// </summary>
    private const float RocketHeight = 12288f;

    /// <summary>
    ///     `Default.AppleSauce.RocketSpeed`, and it is FASTER THAN THE ROCKET'S OWN ASSET, which is
    ///     the whole reason LaunchRocket has to send a speed override.
    ///
    ///     `B_Prj_AppleSauce_Rocket_Athena_C` authors InitialSpeed 2000 and MaxSpeed 2250 (the
    ///     AthenaProjectiles rows `Rocket_InitialSpeed_Athena` / `Rocket_MaxSpeed_Athena`). A client
    ///     told nothing else clamps the rocket to 2250 in
    ///     UProjectileMovementComponent::LimitVelocity, covers 3,900 of these 12,288 units in the
    ///     flight time, and is still a hundred metres up when the server retires it - so the smoke
    ///     trail that IS the rocket on screen never comes down where anyone is looking. See
    ///     AFortProjectileBase.ReplicatedMaxSpeed.
    /// </summary>
    private const float RocketSpeed = 7000f;

    /// <summary>
    ///     HOW LONG A ROCKET IS IN THE AIR, and leaving it out is what made the strike lethal at the
    ///     instant it landed.
    ///
    ///     `Default.AppleSauce.Delay` is 0, so the first rocket is FIRED immediately - but it is
    ///     fired from `Default.AppleSauce.RocketHeight` (12,288) at `Default.AppleSauce.RocketSpeed`
    ///     (7,000), which is a second and three quarters before it arrives. That flight is the
    ///     WARNING: the zone marker is up, nothing has hit yet, and a player has time to leave. The
    ///     first version scheduled impacts at the FIRING cadence, so a player who threw one at their
    ///     own feet was dead before the marker could mean anything.
    ///
    ///     The 0.14s between rockets is the gap between FIRINGS. Each of them still takes the same
    ///     flight, so the impacts keep that spacing - the whole pattern is shifted, not stretched.
    ///
    ///     DERIVED FROM THE TWO ROWS RATHER THAN WRITTEN OUT, because the server's schedule and the
    ///     client's simulation have to agree to the frame: the client flies the rocket itself, and
    ///     if this number and the speed on the wire ever disagree the damage lands somewhere the
    ///     rocket is not.
    /// </summary>
    private const float RocketFlightTime = RocketHeight / RocketSpeed;

    /// <summary>
    ///     `Default.AppleSauce.RandomStrength`, and the units are the Blueprint's, not a guess.
    ///     CalcSpawnLocOfNextRocket rolls `RandomFloatInRange` between the components of two literal
    ///     vectors, (-100,-100,0) and (100,100,0), and multiplies by this - so the scatter is +/-280
    ///     units around the cell, not a fraction of the 750 cell pitch. The first version read it as
    ///     the latter and spread the strike two and a half times too wide.
    /// </summary>
    private const float RandomStrength = 2.8f;

    /// <summary>The +/-100 CalcSpawnLocOfNextRocket rolls between, before RandomStrength scales it.</summary>
    private const float RandomBase = 100f;

    /// <summary>`Default.AppleSauce.ZoneRadius` - nothing lands outside this.</summary>
    private const float ZoneRadius = 900f;

    /// <summary>Rolled here rather than seeded per strike: the scatter is cosmetic, not a fixture.</summary>
    private static readonly Random Rng = new();

    /// <summary>
    ///     The two cues that ARE the air strike on screen - the marker on the ground and the rockets
    ///     raining onto it. Without them the server was killing people with nothing to see, which is
    ///     exactly what the first live test looked like.
    ///
    ///     BOTH ARE `FortGameplayCueNotify_Loop` (checked with `pakreader supers`, not assumed from
    ///     the name), so they answer to Added/WhileActive and an Executed would be a silent no-op -
    ///     the same rule the low-gravity aura was built on. That means they also have to be REMOVED,
    ///     which is why they ride the ASC's replicated cue list rather than a one-shot RPC.
    ///
    ///     THEY CARRY A LOCATION, and that is the whole reason FGameplayCueParameters::Location had
    ///     to go on the wire: the cue rides the THROWER's ability system, because that is what has a
    ///     replicated cue list, but it belongs to a patch of ground the thrower may be nowhere near.
    ///     Without the location the entire strike would draw itself on the player.
    /// </summary>
    private const string WarningCue = "GameplayCue.Athena.AppleSauce.Warning";

    /// <summary>See <see cref="WarningCue" />.</summary>
    private const string StrikeCue = "GameplayCue.Athena.AppleSauce";

    /// <summary>One strike's cues, and who is carrying them, until the last rocket has landed.</summary>
    private sealed record FCueHolder(UFortAbilitySystemComponent AbilitySystem, APlayerState Owner,
                                     APawn Pawn, float EndsAt);

    private static readonly List<FCueHolder> Holders = new();

    /// <summary>
    ///     `B_Prj_AppleSauce_Rocket_Athena_C`, which the spawner fires. A **FortProjectileBase**
    ///     (`pakreader supers`), so the C# type this server already uses for a grenade is the right
    ///     one and the layout is the one thrown weapons proved.
    /// </summary>
    private const string RocketClassPath =
        "/Game/Athena/Items/Consumables/AirStrike/B_Prj_AppleSauce_Rocket_Athena.B_Prj_AppleSauce_Rocket_Athena_C";

    /// <summary>One rocket still to land: where, when, and who is answerable for it.</summary>
    private sealed record FPendingRocket(FVector Where, float At, APawn? Instigator);

    private static readonly List<FPendingRocket> Pending = new();

    /// <summary>One rocket still to be FIRED - see LaunchRocket for why the two phases are separate.</summary>
    private sealed record FPendingLaunch(FVector Target, float At, APawn? Instigator);

    private static readonly List<FPendingLaunch> Launches = new();

    /// <summary>Rockets in the air: where each is headed, and when it gets there.</summary>
    private static readonly List<(AFortProjectileBase Rocket, FVector Target, float ArrivesAt)> InAir = new();

    /// <summary>
    ///     How long an arrived rocket is left on the wire after it goes off, so bHasExploded and
    ///     bIsBeingKilled actually REACH the client before its channel closes.
    ///
    ///     Same reasoning and same value as FortProjectileSystem's PROJECTILE_KILL_DELAY: destroying
    ///     the actor in the same breath as setting the flags sends neither, and the rocket then
    ///     simply vanishes with no impact at all.
    /// </summary>
    private const float ExplodeToRemoveDelay = 1f;

    /// <summary>Rockets that have gone off, waiting only to be taken off the wire.</summary>
    private static readonly List<(AFortProjectileBase Rocket, float RemoveAt)> Spent = new();

    /// <summary>
    ///     Whether this strike has already reported its first arrival - see the line it prints, which
    ///     is there to make "the rockets are not visible" answerable in ONE round instead of by
    ///     hypothesis. It says how many clients actually held a channel for the rocket at the moment
    ///     it landed: zero means the client never had it and the problem is relevancy or the channel
    ///     budget; non-zero means it did, and the problem is on the far side of the wire.
    ///
    ///     Once per strike rather than once per rocket, because there are sixty of them.
    /// </summary>
    private static bool _reportedArrival;

    /// <summary>
    ///     Schedules the whole bombardment at the moment the projectile lands. Every rocket's landing
    ///     point and time is decided here rather than as they go, so the strike is fixed once thrown -
    ///     the same as the real one, where the spawner is handed its grid up front.
    /// </summary>
    public static void Begin(UWorld world, FVector centre, APawn? instigator) {
        if (Environment.GetEnvironmentVariable("AIRSTRIKE") is "0") return;

        // The first IMPACT, not the first firing - see RocketFlightTime.
        var at = world.TimeSeconds + InitialDelay + RocketFlightTime;
        var scheduled = 0;

        for (var group = 0; group < Groups; group++) {
            for (var rocket = 0; rocket < RocketsPerGroup; rocket++) {
                // Walk the 3x3 grid in order, wrapping - the Blueprint's own GridFire - so the
                // sixty rockets are spread evenly over the cells rather than piled in one.
                var cell = (group * RocketsPerGroup + rocket) % (RowSize * RowSize);
                var cellX = cell % RowSize - (RowSize - 1) / 2f;
                var cellY = cell / RowSize - (RowSize - 1) / 2f;

                var scatter = RandomStrength * RandomBase;
                var x = centre.X + cellX * CellPitch + (float) (Rng.NextDouble() * 2.0 - 1.0) * scatter;
                var y = centre.Y + cellY * CellPitch + (float) (Rng.NextDouble() * 2.0 - 1.0) * scatter;

                // Nothing lands outside the marked circle - the zone is what a player reads to know
                // whether they are clear, so a rocket beyond it would be a lie about the warning.
                var dx = x - centre.X;
                var dy = y - centre.Y;
                var distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance > ZoneRadius) {
                    x = centre.X + dx / distance * ZoneRadius;
                    y = centre.Y + dy / distance * ZoneRadius;
                }

                var target = new FVector { X = x, Y = y, Z = centre.Z };

                Pending.Add(new FPendingRocket(target, at, instigator));

                // FIRED a flight-time earlier, so it ARRIVES when the damage does. The two are
                // scheduled separately rather than the impact being derived from the spawn, because
                // the client flies the rocket on its own clock and this server does not: what has to
                // agree is the moment it lands, and that is the one both are pinned to.
                Launches.Add(new FPendingLaunch(target, at - RocketFlightTime, instigator));

                at += TimeBetweenRockets;
                scheduled++;
            }

            at += TimeBetweenGroups;
        }

        _reportedArrival = false;

        ShowCues(world, centre, instigator, at);

        Console.WriteLine($"FortAirstrike: {scheduled} rocket(s) inbound at {centre} - first impact in " +
                          $"{InitialDelay + RocketFlightTime:F2}s, last in " +
                          $"{at - world.TimeSeconds:F1}s. {RocketDamage:F0}/{RocketEnvironmentDamage:F0} " +
                          $"each within {RocketRadius:F0}, zone radius {ZoneRadius:F0}, scatter " +
                          $"+/-{RandomStrength * RandomBase:F0}.");
    }

    /// <summary>Lands whatever is due. Driven from the projectile tick, which already runs every frame.</summary>
    public static void Tick(UWorld world, float timeSeconds) {
        for (var i = Launches.Count - 1; i >= 0; i--) {
            if (timeSeconds < Launches[i].At) continue;

            var launch = Launches[i];
            Launches.RemoveAt(i);

            LaunchRocket(world, launch.Target, launch.Instigator, timeSeconds);
        }

        // FLY THEM. A straight line at RocketSpeed - the rocket's own ProjectileGravityScale is 0 -
        // so the height left is simply the time left times the speed, which also means the position
        // and the scheduled impact can never drift apart: they are the same number read two ways.
        //
        // bUseExplicitVelocity keeps GatherCurrentMovement from deriving a velocity from successive
        // positions. It would get the right answer here, but the derived one lags a tick and the
        // client integrates it forward between updates.
        foreach (var (rocket, target, arrivesAt) in InAir) {
            var falling = MathF.Max(0f, arrivesAt - timeSeconds) * RocketSpeed;

            rocket.SetActorLocation(new FVector { X = target.X, Y = target.Y, Z = target.Z + falling });
        }

        // ARRIVAL IS AN EXPLOSION, NOT A DELETION. bHasExploded is what raises the rocket's own
        // OnExploded - its P_Impact burst, its ground decal and its AppleSauce_Explosion_Cue are all
        // in that Blueprint event and in nothing else - so a rocket that is merely destroyed on
        // touchdown lands in silence. Same explode -> kill -> remove sequence a grenade uses, and
        // for the same reason: both flags have to reach the client before the channel closes.
        for (var i = InAir.Count - 1; i >= 0; i--) {
            if (timeSeconds < InAir[i].ArrivesAt) continue;

            var arrived = InAir[i].Rocket;
            InAir.RemoveAt(i);

            arrived.bHasExploded = true;
            arrived.bIsBeingKilled = true;

            Spent.Add((arrived, timeSeconds + ExplodeToRemoveDelay));

            if (!_reportedArrival) {
                _reportedArrival = true;

                var watching = world.NetDriver?.ClientConnections
                                    .Count(c => c.FindActorChannel(arrived) != null) ?? 0;

                Console.WriteLine($"FortAirstrike: first rocket '{arrived.GetFName()}' arrived at " +
                                  $"{arrived.GetActorLocation()} - {watching} client(s) held a channel " +
                                  $"for it. Zero means it never reached anyone; non-zero means it did " +
                                  $"and the rest is client-side.");
            }
        }

        for (var i = Spent.Count - 1; i >= 0; i--) {
            if (timeSeconds < Spent[i].RemoveAt) continue;

            Spent[i].Rocket.Destroy();
            Spent.RemoveAt(i);
        }

        for (var i = Pending.Count - 1; i >= 0; i--) {
            if (timeSeconds < Pending[i].At) continue;

            var rocket = Pending[i];
            Pending.RemoveAt(i);

            FortProjectileSystem.Blast(world, rocket.Where, RocketRadius,
                                       RocketDamage, RocketEnvironmentDamage, rocket.Instigator);
        }

        for (var i = Holders.Count - 1; i >= 0; i--) {
            if (timeSeconds < Holders[i].EndsAt) continue;

            var holder = Holders[i];
            Holders.RemoveAt(i);

            var removed = holder.AbilitySystem.RemoveGameplayCue(StrikeCue)
                        | holder.AbilitySystem.RemoveGameplayCue(WarningCue);

            if (removed) world.NetDriver?.FlushAbilitySystemComponent(holder.Owner);

            Console.WriteLine($"FortAirstrike: the strike on {holder.Pawn.GetFName()}'s throw is over - " +
                              "marker and rockets removed.");
        }
    }

    /// <summary>
    ///     Fires one rocket: a real replicated actor, high above its target and pointing down.
    ///
    ///     WHY AN ACTOR AND NOT A CUE. The zone marker is a cue and works; the rockets are not. They
    ///     are `B_Prj_AppleSauce_Rocket_Athena_C`, spawned by the strike's own spawner, and a client
    ///     that is never sent one has nothing to draw - which is what "the area effect shows but the
    ///     falling rockets do not" was.
    ///
    ///     THE ACTOR IS NEVER DRAWN, AND THAT IS CORRECT. Its ReceiveBeginPlay ends in
    ///     SetActorHiddenInGame(true), and nothing in the whole Blueprint ever unhides it. What a
    ///     player sees is `RocketTrailPS`, a free-standing P_RocketTrail_01_Athena emitter that
    ///     BeginPlay spawns and ReceiveTick drags to the Mesh's world location every frame, behind
    ///     an IsDedicatedServer guard. So the rocket is visible exactly insofar as it MOVES: an
    ///     actor that arrives and then holds still is a smoke trail hanging motionless in the sky,
    ///     which is worth remembering before ever concluding "the client did not get it".
    ///
    ///     THE CLIENT FLIES IT, exactly as it flies a thrown grenade: the spawn header carries the
    ///     location, the rotation and the velocity, and its own UFortProjectileMovementComponent does
    ///     the rest. What a rocket needs on top of a grenade is the SPEED OVERRIDE - see RocketSpeed
    ///     and AFortProjectileBase.ReplicatedMaxSpeed - because 7,000 is faster than its own asset
    ///     allows and a client left to the asset clamps it to 2,250 and never gets down here.
    ///
    ///     The rocket's own `ProjectileGravityScale` is 0, so it is a straight line down at
    ///     RocketSpeed - no arc to model and nothing to keep in step.
    /// </summary>
    private static void LaunchRocket(UWorld world, FVector target, APawn? instigator, float timeSeconds) {
        var from = new FVector { X = target.X, Y = target.Y, Z = target.Z + RocketHeight };

        // Straight down. Pitch -90 is nose-down, and the rocket's own bRotationFollowsVelocity keeps
        // it that way on the client.
        var facing = new FRotator { Pitch = -90f };

        AFortProjectileBase? rocket;
        try {
            rocket = world.SpawnActor<AFortProjectileBase>(
                GUClassArray.StaticClassForPath<AFortProjectileBase>(RocketClassPath),
                new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient });
        } catch (Exception ex) {
            Console.WriteLine($"FortAirstrike: could not spawn a rocket - {ex.Message}");
            return;
        }

        if (rocket == null) return;

        rocket.SourceItemName = ItemName;
        rocket.SetActorLocation(from);
        rocket.SetActorRotation(facing);
        rocket.Velocity = new FVector { Z = -RocketSpeed };
        rocket.SimulatedLocation = from;

        // THE SPEED OVERRIDE, and without it the rocket never arrives - see RocketSpeed. Real
        // Fortnite sets this by passing InitialSpeed into UFortKismetLibrary::SpawnProjectile
        // (FireAirStrikeRocket's bytecode); ReplicatedMaxSpeed is how the client is told.
        //
        // GravityScale stays 0, which is both "no override" and the rocket's own authored value.
        rocket.ReplicatedMaxSpeed = RocketSpeed;

        // THE SERVER FLIES THIS ONE, and that is the difference from a grenade rather than an
        // embellishment on it. A grenade is flown by the CLIENT out of its own ability, so the
        // server's copy can stand still and PROJECTILE_REPLICATE_MOVEMENT is off; nothing flies a
        // rocket. Handing the client a spawn velocity and hoping its
        // UFortProjectileMovementComponent reproduces the same flight makes the whole thing depend
        // on client-side evaluation order that cannot be checked from here - and this Blueprint
        // punishes any error totally rather than partially, because its actor is hidden and the
        // smoke trail that IS the rocket is dragged to wherever the actor actually is. So the
        // position goes on the wire and Tick keeps it honest.
        //
        // Seeded BEFORE the channel opens for the reason the grenade path records: an unset
        // FRepMovement is Location (0,0,0), and the client believes it.
        rocket.bReplicateMovement = true;
        rocket.bUseExplicitVelocity = true;
        rocket.ReplicatedMovement.LocationQuantization = FRepMovement.RoundWholeNumber;
        rocket.ReplicatedMovement.Location = from;
        rocket.ReplicatedMovement.Rotation = facing;
        rocket.ReplicatedMovement.LinearVelocity = rocket.Velocity;

        if (instigator != null) {
            rocket.SetInstigator(instigator);
            rocket.SetOwner(instigator);
        }

        // NOT handed to FortProjectileSystem's own in-flight list. That list is the fuse-and-explode
        // machinery for a thrown item, and these have neither: their damage is scheduled, and their
        // end is the moment they arrive.
        rocket.SetRole(ENetRole.ROLE_Authority);
        rocket.SetReplicates(true);

        InAir.Add((rocket, target, timeSeconds + RocketFlightTime));
    }

    /// <summary>
    ///     Puts the marker and the rocket rain on screen, at the STRIKE's location rather than the
    ///     thrower's.
    ///
    ///     The pair - an element in the ASC's ActiveGameplayCues for WhileActive, and the Added RPC
    ///     on the pawn for OnActive - is what real UE sends together for a looping cue; see
    ///     FActiveGameplayCue. Removal, when the last rocket has landed, is the array element going
    ///     away, because there is no RPC that can say it.
    /// </summary>
    private static void ShowCues(UWorld world, FVector centre, APawn? instigator, float endsAt) {
        if (instigator?.PlayerState is not { } owner) return;
        if (owner.AbilitySystemComponent is not { } abilitySystem) return;

        var added = abilitySystem.AddGameplayCue(WarningCue, location: centre)
                  | abilitySystem.AddGameplayCue(StrikeCue, location: centre);

        if (!added) return;

        foreach (var connection in world.NetDriver?.ClientConnections ?? Enumerable.Empty<UNetConnection>()) {
            if (connection.FindActorChannel(instigator) is not { } channel) continue;

            channel.SendNetMulticastInvokeGameplayCueAddedWithParams(WarningCue, null, centre);
            channel.SendNetMulticastInvokeGameplayCueAddedWithParams(StrikeCue, null, centre);
        }

        world.NetDriver?.FlushAbilitySystemComponent(owner);

        Holders.Add(new FCueHolder(abilitySystem, owner, instigator, endsAt));
    }
}
