namespace AFortOnlineBeacon.Net.Channels.Actor;

public class UActorChannel : UChannel {
    public AActor? Actor { get; private set; }
    public FNetworkGUID ActorNetGUID { get; set; } = new FNetworkGUID();

    public UActorChannel() {
        ChType = EChannelType.CHTYPE_Actor;
        ChName = EName.Actor;
        // bClearRecentActorRefs = true;
        // bHoldQueuedExportBunchesAndGUIDs = false;
        // QueuedCloseReason = EChannelCloseReason::Destroyed;
    }

    /// <summary>Binds this channel to the actor it will replicate. Simplified port of UActorChannel::SetChannelActor.</summary>
    public void SetChannelActor(AActor inActor) {
        Actor = inActor;

        var packageMap = (UPackageMapClient) Connection!.PackageMap!;
        ActorNetGUID = packageMap.GuidCache!.GetOrAssignNetGUID(inActor);

        Connection.ActorChannels[inActor] = this;
    }

    protected override void CleanUp(bool bForDestroy, EChannelCloseReason closeReason) {
        base.CleanUp(bForDestroy, closeReason);

        // Leave the map consistent, or ServerReplicateActors would keep believing this connection
        // still has a channel for the actor and never reopen one.
        if (Actor != null) Connection?.ActorChannels.Remove(Actor);
    }

    /// <summary>
    ///     Properties marked dirty for the one-shot initial replication below. Handle numbering
    ///     (NativeRepLayouts) was ground-truthed via live wire probes on 2026-08-24 - see that
    ///     file's doc comment for the full story (AttachmentReplication consumes SIX handles, not
    ///     one, which is what made every downstream handle wrong earlier this session). AGameState
    ///     needs bReplicatedHasBegunPlay, APlayerState needs bHasStartedPlaying, and
    ///     APlayerController needs both bHasServerFinishedLoading and the PlayerState object
    ///     reference itself (so the client can resolve PlayerController->PlayerState) - together,
    ///     per Project-Reboot-3.0's own disabled workaround comment, these are what gate a real
    ///     client's loading-screen dismissal.
    /// </summary>
    /// <summary>
    ///     REP_DISABLE - a comma-separated list of property names to drop from whatever changed-set
    ///     GetInitialReplicatedProperties would otherwise return, so a suspected group can be
    ///     switched off without editing code. Purely a bisecting aid: when adding several properties
    ///     at once changes client behaviour for the worse, this narrows down which group did it in
    ///     one run each instead of by reasoning.
    ///
    ///     An entry is either a bare property name, which disables it on every actor, or
    ///     "ActorTypeName:PropertyName", which disables it only on that actor. The qualified form
    ///     matters because the same name lives at different handles on different classes -
    ///     "PlayerState" is handle 16 on APlayerController (load-bearing: it gates the loading
    ///     screen) but handle 17 on APawn, so only "APawn:PlayerState" can turn the latter off.
    ///     A colon is used rather than a dot because property names themselves contain dots
    ///     (CharacterData.Parts[0]).
    /// </summary>
    /// <summary>One "skipping vehicle blocks" line per vehicle class, not per bunch.</summary>
    private static readonly HashSet<string> _warnedVehicleBlock = new();

    private static readonly HashSet<string> DisabledProperties =
        (Environment.GetEnvironmentVariable("REP_DISABLE") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();

    private static HashSet<string> GetInitialReplicatedProperties(AActor actor) {
        var changed = GetInitialReplicatedPropertiesCore(actor);
        if (DisabledProperties.Count == 0) return changed;

        var typeName = actor.GetType().Name;
        var dropped = changed.Where(name =>
            DisabledProperties.Contains(name) || DisabledProperties.Contains($"{typeName}:{name}")).ToArray();

        if (dropped.Length > 0) {
            changed.ExceptWith(dropped);
            Console.WriteLine($"GetInitialReplicatedProperties: REP_DISABLE dropped [{string.Join(", ", dropped)}] from {typeName}");
            File.AppendAllText("REP_DISABLE.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {typeName} dropped [{string.Join(", ", dropped)}]{Environment.NewLine}");
        }

        return changed;
    }

    /// <summary>
    ///     DoorDesiredRotOffset (handle 70) - how far the door turns. ON by default; DOOR_ROT_OFFSET=0
    ///     drops it, and dropping it makes the swing wrong again.
    ///
    ///     LIVE-CONFIRMED, after being wrongly ruled out. `ABuildingWall::OnRep_bDoorOpen`
    ///     (0x1413F9ED0 in the 10.40 client) only compares bDoorOpen against its predicted
    ///     bLocalDoorOpen, plays the open/close sound, and tail-calls a generic building notify - it
    ///     never touches 0xBD0, and no NATIVE code anywhere does. That looked conclusive and was not:
    ///     the doors are BLUEPRINTS (`Rural_House_Wall_9_C` -> `Parent_BuildingWall_C`), so the swing
    ///     is BYTECODE, which a native displacement search cannot see by construction. Sending this
    ///     makes the door open and close correctly and consistently; not sending it does not.
    ///
    ///     "No native code reads it" is not "nothing reads it" - remember that before ruling a
    ///     replicated property out on a Blueprint actor.
    /// </summary>
    /// <summary>
    ///     BUS_ATTACH_PAWN=1 - see the AttachmentReplication note in the pawn's set. Off by default
    ///     because the bus destroys the pawn rather than attaching it.
    /// </summary>
    private static bool EnvAttachPawn => Environment.GetEnvironmentVariable("BUS_ATTACH_PAWN") is "1";

    /// <summary>
    ///     Adds the six AttachmentReplication handles when BUS_ATTACH_PAWN=1, or - and this is the
    ///     case that matters now - once this pawn has actually been attached to something.
    ///
    ///     RIDING A VEHICLE IS AN ATTACHMENT, and unlike the battle bus (where attaching the pawn is
    ///     the wrong model outright) it is the right one: a passenger really is parented to the
    ///     vehicle. So the handles have to be available without BUS_ATTACH_PAWN - but only for a pawn
    ///     that is actually using them, because sending AttachParent=null to everyone caused a
    ///     spawn-time camera roll. AActor.bAttachmentEverSet is what keeps that narrow, and its being
    ///     STICKY is what lets the detach go out when the rider steps off.
    /// </summary>
    /// <summary>
    ///     Adds the seven ReplicatedBasedMovement handles (19-25) once this pawn is actually ON
    ///     something - riding a vehicle.
    ///
    ///     CONDITIONAL FOR THE SAME REASON THE ATTACHMENT SET IS: a property in this set is diffed and
    ///     sent for every pawn on every tick, and a walking player is based on nothing. Adding them
    ///     unconditionally would put seven more handles on the wire for every player for the whole
    ///     match to say "still null" - and, worse, would send handle 19 as a null object reference
    ///     from the very first bunch, which is exactly the kind of change that has killed connections
    ///     here before. They appear when there is something to say.
    /// </summary>
    private static HashSet<string> WithBasedMovement(HashSet<string> names, APawn pawn) {
        if (pawn.bMovementBaseEverSet)
            names.UnionWith(new[] {
                "ReplicatedBasedMovement.MovementBase", "ReplicatedBasedMovement.BoneName",
                "ReplicatedBasedMovement.Location", "ReplicatedBasedMovement.Rotation",
                "ReplicatedBasedMovement.bServerHasBaseComponent",
                "ReplicatedBasedMovement.bRelativeRotation", "ReplicatedBasedMovement.bServerHasVelocity"
            });

        // VehicleStateRep, 134-140 - and this is the half that reaches the DRIVER. The base above is
        // COND_SimulatedOnly, so it goes to everyone except the person sitting in the seat; this
        // carries no condition. SeatTransitionVector (139) is deliberately absent - see the layout.
        if (pawn.bVehicleStateEverSet)
            names.UnionWith(new[] {
                "VehicleStateRep.Vehicle", "VehicleStateRep.VehicleApexZ", "VehicleStateRep.SeatIndex",
                "VehicleStateRep.ExitSocketIndex", "VehicleStateRep.bOverrideVehicleExit",
                "VehicleStateRep.EntryTime"
            });

        return names;
    }

    private static HashSet<string> WithPawnAttachment(HashSet<string> names, AActor pawn) {
        if (!EnvAttachPawn && !pawn.bAttachmentEverSet) return names;

        names.UnionWith(new[] {
            "AttachmentReplication.AttachParent", "AttachmentReplication.LocationOffset",
            "AttachmentReplication.RelativeScale3D", "AttachmentReplication.RotationOffset",
            "AttachmentReplication.AttachSocket", "AttachmentReplication.AttachComponent"
        });

        return names;
    }

    /// <summary>
    ///     PlayerNamePrivate, ON by default. REPLICATE_PLAYER_NAME=0 turns it off.
    ///
    ///     IT WAS BRIEFLY TURNED OFF AND A LIVE TEST PUT IT BACK - the squad list went BLANK. That
    ///     settles something worth writing down: **the client reads its own name for the squad list
    ///     out of this replicated property**, and does NOT fall back to the name it sent itself in
    ///     the join URL. Sending it is correct.
    ///
    ///     The argument for switching it off was that a real server does not send it, and the
    ///     evidence for THAT is real but does not say what it seemed to: Project-Reboot-3.0's source
    ///     writes PlayerNamePrivate only in FortServerBotManagerAthena.cpp:56 (a BOT name override),
    ///     and a controlled scan of its capture finds "dev" in the C-&gt;S join URL but zero times in
    ///     7230 S-&gt;C packets at all eight bit alignments. What that rules out is PLAINTEXT and the
    ///     sixteen shifted variants of the (C - 3j) mod 8 model. It does not rule out the name being
    ///     sent in some other encoded form - and now that we know the client needs the property,
    ///     that is the likelier reading. PR3.0's own squad list may simply have been blank too;
    ///     nobody ever checked it.
    ///
    ///     So the mangling stands unexplained and AGameModeBase.PreCompensateName stays. What IS
    ///     settled: the wire is correct (this server's own payload was hand-decoded at the logged
    ///     bit offsets - 20 chars plus NUL, length 21, with HeroId following cleanly), so the
    ///     transform is applied inside the client, to a value that arrives intact.
    /// </summary>
    private static HashSet<string> WithPlayerName(HashSet<string> names) {
        if (Environment.GetEnvironmentVariable("REPLICATE_PLAYER_NAME") is not "0") names.Add("PlayerNamePrivate");

        return names;
    }

    private static HashSet<string> WithDoorRotation(HashSet<string> properties) {
        if (Environment.GetEnvironmentVariable("DOOR_ROT_OFFSET") is not "0") properties.Add("DoorDesiredRotOffset");

        return properties;
    }

    /// <summary>
    ///     Handles 2 and 6 on a dropped pickup, so the SERVER's toss is what the player watches.
    ///
    ///     THIS LIST USED TO SAY "NO bReplicateMovement / ReplicatedMovement", on evidence: two live
    ///     rounds where a pickup carrying replicated movement was one the client would not let
    ///     anyone pick up - not a single ServerHandlePickup in either. Both of those rounds also
    ///     had the item 95 units INSIDE the landscape for its whole flight (FortPickupToss's
    ///     RestClearance was 40 against a measured 135), which is reason enough on its own for the
    ///     client's interaction query to find nothing. The confound is gone, and the substep counts
    ///     in FortPickupToss.BeginStreamed say the server is the only thing that flies a dropped
    ///     item on a real 10.40 server, so this goes back in.
    ///
    ///     PICKUP_REPLICATE_MOVEMENT=0 takes it out again if the old symptom returns; the item then
    ///     simply waits at its launch point for the AtRest handoff.
    ///
    ///     Note these are only ever non-default WHILE A TOSS IS IN THE AIR - FortPickupToss.Tick
    ///     clears bReplicateMovement the moment the item lands - so a resting or generated pickup
    ///     costs nothing but two comparisons.
    /// </summary>
    private static HashSet<string> WithPickupMovement(AFortPickup pickup, HashSet<string> properties) {
        // ONLY WHILE THE TOSS IS IN THE AIR. Listing these unconditionally would put a zeroed
        // ReplicatedMovement in the OPEN bunch of every floor-loot and container pickup on the map -
        // 2895 of them - and a zeroed FRepMovement is a Location of (0, 0, 0). bReplicateMovement
        // bumps the property-set revision when it changes, so the channel rebuilds this at the
        // moment the toss starts and again at the moment it lands.
        if (pickup.bReplicateMovement && Environment.GetEnvironmentVariable("PICKUP_REPLICATE_MOVEMENT") is not "0") {
            properties.Add("bReplicateMovement");
            properties.Add("ReplicatedMovement");
        }

        return properties;
    }

    /// <summary>What a thrown projectile replicates - see the AFortProjectileBase arm below.</summary>
    private static readonly HashSet<string> ProjectileProperties =
        Environment.GetEnvironmentVariable("PROJECTILE_REPLICATE_MOVEMENT") is "1"
            ? new HashSet<string> { "RemoteRole", "Role", "Owner", "Instigator", "bHasExploded",
                                    "bIsBeingKilled", "bReplicateMovement", "ReplicatedMovement" }
            : new HashSet<string> { "RemoteRole", "Role", "Owner", "Instigator", "bHasExploded",
                                    "bIsBeingKilled" };

    /// <summary>
    ///     Adds the two per-spawn movement overrides, and ONLY for a projectile that actually has
    ///     them.
    ///
    ///     Listing them unconditionally would be worse than useless: ReplicatedMaxSpeed's "no
    ///     override" value is 0, and 0 is not an inert MaxSpeed - it is a real one, and telling the
    ///     client that every grenade's cap is zero is a good way to make thrown weapons stop
    ///     working. Every projectile except the air strike's rocket leaves both at zero, so this
    ///     costs two comparisons and changes nothing for them. See AFortProjectileBase for what the
    ///     pair is for and NativeRepLayouts.ProjectileProps for the handles.
    /// </summary>
    private static HashSet<string> WithSpeedOverride(AFortProjectileBase projectile,
                                                     HashSet<string> properties) {
        if (projectile.ReplicatedMaxSpeed > 0f) properties.Add("ReplicatedMaxSpeed");
        if (projectile.GravityScale != 0f) properties.Add("GravityScale");

        // AND ITS POSITION, WHEN THE SERVER IS THE ONE FLYING IT - read off the actor's own flag
        // rather than PROJECTILE_REPLICATE_MOVEMENT, so it is a per-projectile decision instead of
        // a global one. A thrown grenade leaves the flag alone and nothing changes for it; the air
        // strike's rocket sets it, because no client ability flies a rocket and a projectile that
        // holds still is, for this particular Blueprint, completely invisible (its actor is hidden
        // and its smoke trail is dragged to wherever the actor IS).
        //
        // The same trap FortPickupToss records applies: a zeroed FRepMovement is Location (0,0,0),
        // so this must only be listed for an actor whose ReplicatedMovement is actually being kept
        // up to date - UNetDriver's GatherCurrentMovement does that for anything flagged.
        if (projectile.bReplicateMovement) {
            properties.Add("bReplicateMovement");
            properties.Add("ReplicatedMovement");
        }

        return properties;
    }

    private static HashSet<string> GetInitialReplicatedPropertiesCore(AActor actor) => actor switch {
        // The Athena block (WarmupCountdown*/AircraftStartTime/TotalPlayers/PlayersLeft/
        // CurrentPlaylistId/GamePhase/bGameModeWillSkipAircraft) mirrors what raider3.5 sets in
        // Logic/Game.h::OnReadyToStartMatch on a real injected 10.40 server. Until this landed the
        // client's GameState looked like a match that had never left EAthenaGamePhase::None, with
        // no playlist id and a battle bus it was still expecting to be put on.
        AGameState => new HashSet<string> {
            "RemoteRole", "Role", "bReplicatedHasBegunPlay", "MatchState", "FortTimeOfDayManager",
            "WorldManager", "ReplicatedWorldTimeSeconds",
            "WarmupCountdownStartTime", "WarmupCountdownEndTime", "AircraftStartTime",
            "TotalPlayers", "PlayersLeft", "CurrentPlaylistId", "GamePhase",
            "CurrentPlaylistInfo.BasePlaylist", "bGameModeWillSkipAircraft",
            // Teams (29) and the battle bus (159/160). All three CHANGE during a match - the bus is
            // spawned after the GameState exists, and bAircraftIsLocked is the whole drop-window
            // control - so they have to be named here or the per-tick diff would never compare them.
            // See Round 60 in [[afortonlinebeacon-status]] for what forgetting this costs.
            "TeamCount", "Aircrafts", "bAircraftIsLocked",
            // The storm (112/149/157). SafeZoneIndicator is the reference the client's map hangs off,
            // and all three change during a match, so all three have to be named here.
            "SafeZonesStartTime", "SafeZoneIndicator", "SafeZonePhase",
            // The five management-actor references (32, 38, 105, 106, 185). Initial state - the
            // actors are spawned in InitGameState, before any client exists - but listed here
            // because that is also the per-tick diff list, and an actor that had to be respawned
            // would otherwise never be re-announced. See Net/Actors/FortManagementActors.cs.
            "PoiManager", "AnnouncementManager", "SpecialActorData", "ReplOverrideData",
            "VolumeManager"
        },
        // The six management actors themselves. Role and RemoteRole only, which is not a
        // simplification: it is byte-for-byte what the real server's 29-bit blocks for
        // FortSpecialActorReplicationInfo and FortClientAnnouncementManager contain. What they are
        // FOR is being pointed at, not what they carry.
        AFortManagementActor => new HashSet<string> { "RemoteRole", "Role" },
        // A playlist mutator. Role and RemoteRole only, and bMutatorActive (16) deliberately absent -
        // its CDO already carries the right value, so sending it would be MORE than the real server
        // does. See AFortGameplayMutator.
        AFortGameplayMutator => new HashSet<string> { "RemoteRole", "Role" },
        // A parked vehicle. Role and RemoteRole only - where it is and which way it faces travel in
        // the spawn header, and it has no driver, so bHasDriver (22) already matches its CDO. See
        // AFortAthenaVehicle for why this is not an APawn here.
        // A vehicle sends OWNER as well, and it is not cosmetic: the client resolves an actor's
        // "owning connection" by walking Owner up to a PlayerController, and refuses to send that
        // actor's server RPCs without one -
        //
        //     LogNet: Warning: UNetDriver::ProcessRemoteFunction: No owning connection for actor
        //     ShoppingCartVehicleSK_C_2147478650. Function ServerStartFire will not be processed.
        //
        // which is the client saying, out loud, that boarding half-worked: it seated the player and
        // then dropped everything they tried to do from the seat. The server sets the owner on
        // boarding; without the property here that assignment never left the machine.
        AFortAthenaVehicle => new HashSet<string> { "RemoteRole", "Role", "Owner", "bHasDriver" },
        // A thrown projectile. bHasExploded (16) is the ONLY property that ever changes, and it has
        // to be listed here or it never goes out: this set is not just the OPEN bunch, it also gates
        // the per-tick diff, so a property missing from it is invisible to CompareProperties forever.
        //
        // That is exactly how "the fuse fires and nothing explodes" happened - the server set
        // bHasExploded, said so in the log, and the channel then filtered it out with no complaint.
        // A property added to a layout is not replicated until it is added HERE too.
        //
        // Where the projectile IS never appears: the client flies it itself from the spawn header's
        // velocity, and the server does not simulate the arc, so sending ReplicatedMovement would
        // snap the grenade back to the muzzle every tick.
        // ReplicatedMovement (6) and its gate bReplicateMovement (2) are added only when
        // PROJECTILE_REPLICATE_MOVEMENT is set - see FortProjectileSystem.ReplicateMovement for what
        // that switch actually decides. Listing them unconditionally would send a Location that the
        // server only updates when the simulation runs, which is worse than sending none.
        AFortProjectileBase projectile =>
            WithSpeedOverride(projectile, new HashSet<string>(ProjectileProperties)),
        // The storm circle. Every one of these changes at each phase - the client interpolates from
        // Last to Next between the two shrink times - so the per-tick diff has to be walking them.
        AFortSafeZoneIndicator => new HashSet<string> {
            "RemoteRole", "Role",
            "LastRadius", "NextRadius", "NextNextRadius",
            "LastCenter", "NextCenter", "NextNextCenter",
            "SafeZoneStartShrinkTime", "SafeZoneFinishShrinkTime",
            "MegaStormDelayTimeBeforeDestruction", "Radius"
        },
        // The battle bus. Its whole flight plan is initial state - the client simulates the flight
        // itself from these, so nothing here needs to change again once sent - but JumpFlashCount
        // and the times are listed anyway so a re-plan would actually go out.
        AFortAthenaAircraft => new HashSet<string> {
            "RemoteRole", "Role",
            "FlightInfo.FlightStartLocation", "FlightInfo.FlightStartRotation", "FlightInfo.FlightSpeed",
            "FlightInfo.TimeTillFlightEnd", "FlightInfo.TimeTillDropStart", "FlightInfo.TimeTillDropEnd",
            "FlightStartTime", "FlightEndTime", "DropStartTime", "DropEndTime",
            "ReplicatedFlightTimestamp", "AircraftIndex"
        },
        APlayerState => WithPlayerName(new HashSet<string> { "RemoteRole", "Role", "UniqueId", "bHasFinishedLoading", "bHasStartedPlaying", "HeroId", "HeroType",
            // ALL SIX SLOTS, not the three the built-in default happened to fill. Head/Body/Backpack
            // was enough while every player was the same commando; a real locker outfit brings a HAT
            // (CID_028's Hat_F_Commando_08_V01) and some bring a face or a charm, and a slot that is
            // never named in a changed set is a slot that is never sent. A null in an unused slot is
            // the correct value for "this outfit has no hat" - WasPartReplicatedFlags is what says
            // which ones carry meaning. See FortLockerProfile.
            "CharacterData.WasPartReplicatedFlags",
            "CharacterData.Parts[0]", "CharacterData.Parts[1]", "CharacterData.Parts[2]",
            "CharacterData.Parts[3]", "CharacterData.Parts[4]", "CharacterData.Parts[5]",
            // The plain-float health mirror (216-219), the second of the two paths a client could be
            // drawing a health bar from - see NativeRepLayouts for why both are sent. These CHANGE
            // during a match, so they have to be listed here or the per-tick diff would never
            // compare them (the same reason AFortPickup.bPickedUp is listed).
            "CurrentHealth", "MaxHealth", "CurrentShield", "MaxShield",
            // FDeathInfo (258-262), all default until the player is killed - see
            // FortDamageSystem.Kill. DeathTags (263) is never sent; there is no tag-container
            // serializer here and an empty container is what an ordinary death carries anyway.
            "DeathInfo.FinisherOrDowner", "DeathInfo.bDBNO", "DeathInfo.DeathCause",
            "DeathInfo.Distance", "DeathInfo.bInitialized",
            // Handle 233 - where the player finished, which Battle Royale's death screen is built
            // around. Zero until they die, and it was Reserved(...) until 2026-09-07, so the client
            // has been told "#0" for every death this server ever reported.
            "Place",
            "TeamIndex", "SquadId",
            // Handle 69 - this player's team-private actor. See FortManagementActors.cs.
            "PlayerTeamPrivate",
            // Aboard the battle bus (252). Starts true only when the aircraft phase is on, and is
            // cleared by ServerAttemptAircraftJump, so it changes mid-match and has to be here.
            "bInAircraft" }),
        // AFortInventory's own InventoryType (handle 16). Its other Net property, Inventory
        // (FFortItemList), is a FastArraySerializer / Custom Delta property and cannot go through
        // FRepLayout at all - see NativeRepLayouts.InventoryProps.
        AFortInventory => new HashSet<string> { "RemoteRole", "Role", "Owner", "InventoryType" },
        // AFortPickup: the whole PrimaryPickupItemEntry struct (handles 17-36, one per member) plus
        // the two flags that say what kind of pickup it is and that it is already at rest. Every
        // member has to be listed - RepLayout gave each one its own handle, and the client's
        // OnRep_PrimaryPickupItemEntry sees whatever arrives, so a missing member is a zero.
        // AFortWeapon: what the weapon IS (WeaponData), which inventory row it belongs to
        // (ItemEntryGuid) and how loaded it is. Owner is the pawn - a real server sets it
        // (raider3.5 Inventory.h:294) and the client uses it to decide whose hands to put this in.
        // AFortWeap_BuildingTool::DefaultMetadata is wire handle 36 - but ONLY on that subclass.
        // AFortWeaponPickaxeAthena and AFortWeaponRanged are SIBLINGS of AFortWeap_BuildingTool
        // under plain AFortWeapon, not descendants of it: their real ClassReps stops at handle 33
        // (ReloadAbilitySpecHandle) or wherever their OWN extra properties end, and handle 36
        // simply does not exist for them. This project has one C# AFortWeapon type standing in for
        // that whole hierarchy, so the split has to happen on STATE (does this instance actually
        // have DefaultMetadata set?), not on C# type. Sending handle 36 unconditionally to every
        // weapon (as an earlier version of this code did) broke every weapon, not just building
        // tools: the client's HandleToCmdIndex.IsValidIndex(36) check fails for a class whose own
        // Cmds array ends before 36, ReceiveProperties_r logs "BunchIsError" and returns false, and
        // the connection dies a few ticks later with no server-side exception at all - reproduced
        // twice, disconnecting during the very first few ticks after spawn, well before any
        // building tool was ever equipped (the pickaxe alone was enough to trigger it). Same failure
        // shape as the historical ATOMIC_STRUCTS bug in [[rep_handle_derivation]].
        AFortWeapon w when w.DefaultMetadata != null => new HashSet<string> {
            "RemoteRole", "Role", "Owner", "Instigator", "WeaponData",
            "ItemEntryGuid.A", "ItemEntryGuid.B", "ItemEntryGuid.C", "ItemEntryGuid.D",
            "WeaponLevel", "AmmoCount",
            "PrimaryAbilitySpecHandle", "ReloadAbilitySpecHandle",
            "DefaultMetadata"
        },
        AFortWeapon => new HashSet<string> {
            // Instigator (handle 15) is what the client's own weapon code reads to find the pawn
            // holding this - without it AFortWeaponRanged::OwnerIsMoving errors every frame and the
            // weapon never becomes usable.
            "RemoteRole", "Role", "Owner", "Instigator", "WeaponData",
            "ItemEntryGuid.A", "ItemEntryGuid.B", "ItemEntryGuid.C", "ItemEntryGuid.D",
            "WeaponLevel", "AmmoCount",
            // Which granted abilities this weapon fires and reloads with - handles 31 and 33.
            "PrimaryAbilitySpecHandle", "ReloadAbilitySpecHandle"
        },
        AFortPickup pickupActor => WithPickupMovement(pickupActor, new HashSet<string> {
            "RemoteRole", "Role",
            "PrimaryPickupItemEntry.Count", "PrimaryPickupItemEntry.ItemDefinition",
            "PrimaryPickupItemEntry.OrderIndex", "PrimaryPickupItemEntry.Durability",
            "PrimaryPickupItemEntry.Level", "PrimaryPickupItemEntry.LoadedAmmo",
            "PrimaryPickupItemEntry.ItemGuid.A", "PrimaryPickupItemEntry.ItemGuid.B",
            "PrimaryPickupItemEntry.ItemGuid.C", "PrimaryPickupItemEntry.ItemGuid.D",
            "PrimaryPickupItemEntry.StateValues", "PrimaryPickupItemEntry.AlterationInstances",
            "PrimaryPickupItemEntry.GenericAttributeValues",
            "PickupLocationData.LootInitialPosition", "PickupLocationData.LootFinalPosition",
            "PickupLocationData.FinalTossRestLocation", "PickupLocationData.TossState",
            // THE FLIGHT TO THE PLAYER (38, 40, 43, 44, 47). Like bPickedUp below, these are only
            // ever meaningful after the initial burst - they say which pawn the item is flying to
            // and how, so the client can animate it into the player's hands instead of watching it
            // blink out. Listed here because this set doubles as the per-tick diff's walk list: a
            // property missing from it is never compared and can never start being sent.
            "PickupLocationData.PickupTarget", "PickupLocationData.ItemOwner",
            "PickupLocationData.FlyTime",
            "PickupLocationData.StartDirection", "PickupLocationData.bPlayPickupSound",
            "bTossedFromContainer", "bServerStoppedSimulation",
            // Not sent as true at spawn - it is the per-tick diff that carries it, once
            // ServerHandlePickup flips it. That makes this the first property in the project
            // whose whole purpose is to change after the initial burst.
            "bPickedUp"
        }),
        // WorldInventory maps to handle 34 as a plain ObjectRef - CONFIRMED live by the truncated
        // name probe, which reported "Property=WorldInventory, Parent=25, Cmd=53, ReadHandle=34"
        // with ReadLen=8, i.e. a packed NetGUID byte, matching PlayerState's own ObjectRef read at
        // handle 16 (and unlike the 1-bit bools that really occupy 49/50/51, where every earlier
        // attempt had wrongly placed this). See NativeRepLayouts.PlayerControllerProps.
        // APawn: everything APawn::PossessedBy sets - without these the client sees an unowned pawn
        // with no controller and no player state.
        APawn pawnActor => WithBasedMovement(WithPawnAttachment(new HashSet<string> {
            "RemoteRole", "Role", "Owner", "PlayerState", "Controller",
            // Handle 3, and only ever true for a corpse. Without it in this walk list the property
            // is never compared and can never start being sent, which is precisely how dead bodies
            // came to vanish on the instant - see NativeRepLayouts' entry for it.
            "bTearOff",
            // Handles 2 and 6 - where everyone ELSE sees this pawn. Without them a remote player is
            // frozen at the position their actor-spawn header carried; see Core.Math.FRepMovement.
            "bReplicateMovement", "ReplicatedMovement",
            // Handles 29 and 30 - what a remote player is DOING there. Position alone leaves them
            // upright and unanimated whatever they are actually up to; see APawn.
            "ReplicatedMovementMode", "bIsCrouched",
            // Handles 103, 104 and 106 - the descent. All three have their own OnRep, which is what
            // makes them the ones the animation is actually driven by.
            "bIsSkydiving", "bIsParachuteOpen", "bIsSkydivingFromBus",
            // Handle 145. Without it the client dereferences a null glider a second into the
            // skydive - see APawn.CosmeticGlider.
            "CosmeticLoadout.Glider",
            // Handles 7-12, and DELIBERATELY NOT SENT unless BUS_ATTACH_PAWN=1 asks for them.
            //
            // Sending them unconditionally was actively harmful. With nothing attached, AttachParent
            // goes out as NULL, and AActor::OnRep_AttachmentReplication's else branch (Actor.cpp:1677)
            // runs `DetachFromActor(KeepWorldTransform)` and then, if bReplicateMovement,
            // `OnRep_ReplicatedMovement()` - and this server never sends ReplicatedMovement, so the
            // client applies its DEFAULT, an all-zero location and rotation. The live symptom was a
            // spawn-time camera roll of 90 degrees that went from occasional to every single time
            // the moment these were added.
            //
            // The layout entries stay real and correctly typed (see NativeRepLayouts) - attachment
            // is how vehicles and ziplines will work - but the battle bus does not use it: a real
            // server destroys the pawn instead.
            // Null at spawn and set by ServerExecuteInventoryItem. Listed here because
            // ReplicatedProperties doubles as the set the per-tick diff walks - a property absent
            // from it is never compared, so it could never start being sent later either.
            "CurrentWeapon",
            // False until the player is killed - see FortDamageSystem.Kill. Listed for the same
            // reason CurrentWeapon is: it only ever changes after the initial burst.
            "bIsDying",
            // WHERE THIS PLAYER IS LOOKING VERTICALLY, and the only source anyone else has for it -
            // a capsule carries yaw alone, so without this every other player's head stays level.
            // It changes constantly, so listing it here is what puts it in the per-tick diff; the
            // condition table above already marks it SkipOwner, which is what stops the owner being
            // told its own aim. See APawn.RemoteViewPitch.
            "RemoteViewPitch",
            // The shockwave grenade's throw - see FortProjectileSystem's knockback and
            // NativeRepLayouts handle 65. Listed here because this set is also the per-tick diff's
            // walk list: a property missing from it is never compared, so it could never start
            // being sent later either.
            "PushMomentum",
            // Null until this player emotes, and back to null when they stop - the only part of an
            // emote that reaches anybody but the emoter. See APawn.LastReplicatedEmoteExecuted;
            // listed here for the same reason as the two above.
            "LastReplicatedEmoteExecuted",
            // RepAnimMontageInfo, handles 176-182 - the half of an emote that reaches ONLOOKERS.
            // Every one has to be listed or the per-tick diff never compares it: this set doubles
            // as the walk list, which is how "the server set it, said so in the log, and the channel
            // filtered it out" has happened before. See NativeRepLayouts' RepAnimMontageInfo block.
            "RepAnimMontageInfo.AnimMontage", "RepAnimMontageInfo.PlayRate",
            "RepAnimMontageInfo.Position", "RepAnimMontageInfo.BlendTime",
            "RepAnimMontageInfo.ForcePlayBit", "RepAnimMontageInfo.IsStopped",
            "RepAnimMontageInfo.SkipPositionCorrection",
            // Handles 95, 126 and 127 - the storm. All three CHANGE mid-match, so they have to be
            // listed here or the per-tick diff would never compare them. bIsInAnyStorm is the one
            // that actually lights up the screen effect - see APawn.bIsInAnyStorm.
            "bIsNearSafeZoneEdge", "bIsInAnyStorm", "bIsInsideSafeZone",
            // Handles 52, 53 and 71 - whether a dance MOVES. Listed here because they change
            // mid-match (they are set when an emote starts and cleared when it ends), so the
            // per-tick diff has to compare them. See FortEmoteAssets.Generated.cs.
            "bMovingEmote", "bMovingEmoteForwardOnly", "EmoteWalkSpeed"
        }, pawnActor), pawnActor),
        APlayerController => new HashSet<string> {
            "RemoteRole", "Role", "bHasInitiallySpawned", "bHasServerFinishedLoading",
            "PlayerState", "Pawn", "WorldInventory",
            // Without this the client's inventory capacity is zero and it refuses every pickup.
            "OverriddenBackpackSize",
            // Handle 75. False on an untold client, and a dead player may not jump or build.
            "bMarkedAlive",
            // Handle 80. Without this the client has no BroadcastRemoteClientInfo to call
            // ServerSetPlayerBuildableClass on - see AFortBroadcastRemoteClientInfo's doc comment.
            "BroadcastRemoteClientInfo"
        },
        // AFortBroadcastRemoteClientInfo: just enough that the client can resolve who owns this and
        // that it's live. RemoteBuildableClass is never sent from HERE - it starts unset and only
        // becomes non-default once ServerSetPlayerBuildableClass actually sets it (see
        // NativeRpcHandlers), same as AFortPickup.bPickedUp above.
        AFortBroadcastRemoteClientInfo => new HashSet<string> { "RemoteRole", "Role", "Owner", "bActive" },
        // A placed building piece. ReplicatedBuildingAttributeSet (handle 19) is what points the
        // client at the health values it draws a health bar from; bDestroyed (24) is what
        // distinguishes "this was destroyed" from "this went out of relevance" when the channel
        // later closes. Both start at their defaults and only ever change afterwards, which is
        // exactly why they have to be listed HERE too - this set doubles as the per-tick diff's
        // walk list, so a property missing from it is never compared and could never start being
        // sent later (same reason AFortPickup.bPickedUp and APawn.CurrentWeapon are listed).
        // A chest or ammo box. Everything a building piece sends, plus the two that ARE the open:
        // bAlreadySearched (71, whose OnRep swaps in the opened mesh) and the animation counter (76).
        // A MAP door - a stand-in for an actor in a streaming sublevel this server never loaded, so
        // there is no build animation, no health bar and no mirroring to send. bDoorOpen (73) is the
        // whole thing; the collision flag rides along so an open door is actually walkable.
        //
        // MATCHED ON bPlayerPlaced BEING FALSE, which is what MarkAsLevelActor sets. A door the
        // PLAYER built is also an ABuildingWall and must NOT take this arm: it is a real spawned
        // building piece and needs everything a building piece sends. It falls through to the
        // ABuildingActor arm below, which adds the door handles back through WithDoorProperties.
        ABuildingWall { bPlayerPlaced: false } => WithDoorRotation(new HashSet<string> {
            "RemoteRole", "Role", "bDestroyed", "bPlayerPlaced",
            "bDoorOpen", "bDoorCollisionDisabled"
        }),
        ABuildingContainer => new HashSet<string> {
            "RemoteRole", "Role", "bDestroyed", "bPlayerPlaced",
            "ReplicatedLootTier", "bAlreadySearched", "SearchBounceData.SearchAnimationCount"
        },
        // A SPRAY ON A WALL. Deliberately minimal, and above the ABuildingActor arm for the same
        // reason the llama is: that arm names ReplicatedDrawScale3D, BuildingAnimation, the
        // attribute set and MinimalReplicationProxy - a build-in animation, a health bar and a
        // damage proxy, none of which a decal has any business carrying. The one thing worth
        // sending is which spray it is. See AFortSprayDecalInstance.
        AFortSprayDecalInstance => new HashSet<string> { "RemoteRole", "Role", "SprayInfo.SprayAsset" },
        // A supply llama. MUST be above the ABuildingActor arm, and not only because a llama IS one:
        // that arm names ReplicatedDrawScale3D, BuildingAnimation and MinimalReplicationProxy.*,
        // which are ABuildingSMActor handles in the 45-67 range that a llama's class simply does not
        // have. Sending one is the BunchIsError-then-silent-disconnect failure - see
        // NativeRepLayouts.SupplyDropLlamaProps. Looted (39) is the whole opened-state visual.
        AFortAthenaSupplyDropLlama => new HashSet<string> { "RemoteRole", "Role", "Looted" },
        // A DEPLOYED SHIELD BUBBLE OR EMPLACEMENT, and it is above the ABuildingActor arm for
        // exactly the reason the llama is - it is the same fork. These are BuildingGameplayActors,
        // ABuildingSMActor's SIBLINGS, so ReplicatedDrawScale3D, BuildingAnimation and
        // MinimalReplicationProxy.* below name handles their class does not have.
        //
        // NOTHING BUT THE ROLES, and that is not a stub: where a placed wall needs health, a build-in
        // animation and a mirror flip, a deployable needs only to EXIST at a place. Its position
        // rides SerializeNewActor's own header rather than the property list (which is why the llama
        // works with this same pair), and everything it looks like and does afterwards belongs to
        // the Blueprint the client builds from the class path.
        AFortDeployedActor => new HashSet<string> { "RemoteRole", "Role" },
        ABuildingActor placedBuilding => WithDoorProperties(placedBuilding,
            WithBuildingAttributeSet(placedBuilding, new HashSet<string> {
            "RemoteRole", "Role", "HealthBarIndicatorDifficultyRating",
            "bDestroyed", "bPlayerPlaced",
            // MIRRORING (45 and 58). Both of these had a working getter in NativeRepLayouts and
            // were never named here, so neither had ever gone out on the wire - which is why three
            // rounds of increasingly correct mirror code changed nothing on screen. This whitelist
            // is the gate: a property absent from it is not in the initial burst AND is never
            // compared by the per-tick diff, so it can never start being sent later either.
            //
            // ReplicatedDrawScale3D is the one that actually draws the mirror: real
            // ABuildingSMActor::SetMirrored only forces the sign of RelativeScale3D.X, and this
            // property (with its own OnRep_ReplicatedDrawScale3D) is how that scale reaches a
            // client. bMirrored has no OnRep and changes nothing visual by itself, but a real
            // server replicates it, so it goes too.
            "bMirrored", "ReplicatedDrawScale3D",
            // The build-in and destruction animations (54/56/57) - without BuildingAnimation a piece
            // just pops in and pops out.
            "bUnderConstruction", "bIsInitiallyBuilding", "BuildingAnimation",
            // The compact health path (59/61/62). This, not the attribute set, is what carries a
            // LIVE health update: it is RepNotify on the actor itself, so the client is told health
            // changed instead of only finding out next time something re-reads the value.
            "MinimalReplicationProxy.BuildTime", "MinimalReplicationProxy.RepairTime",
            "MinimalReplicationProxy.Health", "MinimalReplicationProxy.MaxHealth",
            // The damage cue (67) - see ABuildingActor.OnDamaged.
            "ProxyGameplayCueDamagePhysical.ProxyGameplayCueDamagePhysicalMagnitude"
        })),
        _ => new HashSet<string> { "RemoteRole", "Role" }
    };

    /// <summary>
    ///     Adds the door handles (70/73/74) to a PLAYER-BUILT wall's property set.
    ///
    ///     A player-built door is not the map-door case at all: it is a real spawned piece, so it
    ///     needs the whole building set - health, build animation, mirroring, the damage cue - AND
    ///     the door flags on top. Its class really is an ABuildingWall subclass (PBWA_W1_DoorSide_C
    ///     and friends), so the handles exist on it and BuildingWallProps is the layout either way.
    ///
    ///     WITHOUT THIS a built door opens and never closes. The piece used to be spawned as a plain
    ///     ABuildingActor, so bDoorOpen was neither in its layout nor in this whitelist and the
    ///     server had nothing to say back; the client predicted the swing locally, was never
    ///     confirmed, and had no way to ask again.
    ///
    ///     Every wall piece gets them, not just the door variants: a plain wall's class has the
    ///     handles too (they are ABuildingWall's), and OnRep_bDoorOpen on a wall with no door in it
    ///     does nothing - which is exactly the reasoning FortMapWalls' name gate already documents.
    /// </summary>
    private static HashSet<string> WithDoorProperties(ABuildingActor building, HashSet<string> properties) {
        if (building is not ABuildingWall) return properties;

        properties.Add("bDoorOpen");
        properties.Add("bDoorCollisionDisabled");
        return WithDoorRotation(properties);
    }

    /// <summary>
    ///     Adds the attribute-set references (handles 19 and 20) unless this is a player-built piece
    ///     with the sub-object gated off - see ReplicateBuildingAttributeSet for the evidence and the
    ///     BUILDING_ATTR_SET switch.
    ///
    ///     THE TWO HAVE TO AGREE. Handle 19 is an ObjectRef NAMING the attribute set, so sending it
    ///     while the set itself is never replicated hands the client a reference to an object it will
    ///     never receive - and this project's standing hazard is exactly that: an ObjectRef whose
    ///     target does not resolve arrives null and is never reconsidered, because a property that
    ///     matches the shadow is not compared again. Gating one without the other would swap "two
    ///     sources of health" for "one dangling pointer", which is not obviously better.
    /// </summary>
    private static HashSet<string> WithBuildingAttributeSet(ABuildingActor building, HashSet<string> properties) {
        if (building.bPlayerPlaced && Environment.GetEnvironmentVariable("BUILDING_ATTR_SET") != "1") {
            return properties;
        }

        properties.Add("ReplicatedBuildingAttributeSet");

        // The component the attribute set has to be reachable through before its OnRep can
        // broadcast anything - see ABuildingActor.AbilitySystemComponent.
        properties.Add("ReplicatedAbilitySystemComponent");
        return properties;
    }

    /// <summary>
    ///     Default subobjects (CreateDefaultSubobject in the native constructor) to replicate via a
    ///     sub-object content block (see ReplicateSubobject) alongside this actor's own RepLayout
    ///     property push, mirroring real UE's AActor::ReplicateSubobjects override per class.
    ///     Currently unused - AFortPlayerController::WorldInventory was originally modeled this way,
    ///     but a live client rejected it ("Sub-object cannot be actor class"), proving
    ///     /Script/FortniteGame.FortInventory is really an actor class, not a UObject default
    ///     subobject - see AFortInventory/NativeRepLayouts.PlayerControllerProps for where it lives
    ///     now (a plain ObjectRef Cmd, like AController.PlayerState). Left in place - and
    ///     WriteContentBlockHeader/WriteContentBlockPayload/ReplicateSubobject below still real,
    ///     ported directly from UE 4.23's own DataChannel.cpp - for whatever future property turns
    ///     out to actually be a genuine UObject default subobject.
    /// </summary>
    private static IEnumerable<UObject> GetInitialReplicatedSubobjects(AActor actor) {
        yield break;
    }

    /// <summary>
    ///     One-shot version of UActorChannel::ReplicateActor - writes and sends the actor's spawn
    ///     header, followed by a property push via NativeRepLayouts/FRepLayout (this project's
    ///     minimal stand-in for FObjectReplicator::ReplicateProperties/FRepLayout::SendProperties).
    ///
    ///     TEMPORARY diagnostic, replacing the normal layout entirely: if REPLAYOUT_PROBE_HANDLE=N
    ///     is set, sends HandleProbe's truncated name probe for handle N on the actor named by
    ///     REPLAYOUT_PROBE_ACTOR (substring match on the runtime type name, default APlayerController).
    ///     It makes the client's read of that property overflow, so ReceiveProperties_r logs
    ///     "BunchIsError - Property=<real name>, Parent=<idx>, Cmd=<idx>" - the only condition under
    ///     which the client will name a handle at all. See HandleProbe.cs for how to read the result.
    /// </summary>
    /// <summary>
    ///     UActorChannel::SetChannelActorForDestroy (DataChannel.cpp:2194) - tell a client to destroy
    ///     an actor WITHOUT having a channel for it.
    ///
    ///     THIS IS THE PIECE DORMANCY WAS MISSING. Closing a channel is how this server removes an
    ///     actor from a client, and a dormant actor has no channel to close - so a dormant actor that
    ///     gets destroyed would live on the client forever. It is also the general hole: distance
    ///     culling means a client may never have had a channel for an actor at all, yet may still
    ///     know its NetGUID from an object reference somewhere else.
    ///
    ///     The wire form is deliberately tiny and is what the client is written to recognise: open a
    ///     fresh actor channel, and make its FIRST bunch a CLOSE bunch whose entire payload is the
    ///     destroyed actor's object reference. UPackageMapClient::SerializeNewActor
    ///     (PackageMapClient.cpp:339) reads the guid, sees `Ar.AtEnd() &amp;&amp; NetGUID.IsDynamic()`
    ///     on a closing channel, and returns "no actor spawned" with the comment
    ///     "This can happen when dormant actors that don't have channels get destroyed" - i.e. it is
    ///     documented as exactly this case. If the channel is NOT closing it logs an error and sets
    ///     the archive error instead, which is why the close flag matters as much as the payload.
    ///
    ///     Only DYNAMIC guids qualify. A static, path-named actor is destroyed by a different
    ///     mechanism in UE (the startup-actor list), and sending one here would fail that IsDynamic
    ///     test on the client and error the bunch rather than doing nothing.
    /// </summary>
    public void SendDestructionInfo(AActor destroyed) {
        if (Connection == null || Closing) return;

        var packageMap = (UPackageMapClient) Connection.PackageMap!;
        var netGuid = packageMap.GuidCache!.GetOrAssignNetGUID(destroyed);

        if (!netGuid.IsValid() || netGuid.IsDefault()) {
            Console.WriteLine($"UActorChannel.SendDestructionInfo: {destroyed.GetType().Name} " +
                              $"'{destroyed.GetFName()}' has no usable NetGUID - the client was never told " +
                              "about it, so there is nothing to destroy.");
            return;
        }

        using var closeBunch = new FOutBunch(this, true) {
            bReliable = true,
            CloseReason = EChannelCloseReason.Destroyed
        };

        if (closeBunch.IsError()) return;

        // The whole payload. Anything after it and the client's AtEnd() test fails, and it would try
        // to read a full actor spawn out of a bunch that does not contain one.
        packageMap.SerializeObject(closeBunch, destroyed);

        Console.WriteLine($"UActorChannel.SendDestructionInfo: ChIndex={ChIndex} destroys " +
                          $"{destroyed.GetType().Name} '{destroyed.GetFName()}' by guid {netGuid} " +
                          $"({closeBunch.GetNumBits()} bits, no channel was open for it)");

        SendBunch(closeBunch, false);
    }

    public unsafe void ReplicateActor() {
        if (Actor == null || Connection == null) return;

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        var packageMap = (UPackageMapClient) Connection.PackageMap!;

        // DataChannel.cpp:2825. The must-be-mapped list is per-connection and is supposed to be
        // drained by the SendBunch of whichever bunch referenced those objects. Anything still in it
        // here belongs to an earlier bunch that never flushed it, and would be prepended to THIS
        // actor's bunch instead - worse, if it ever leaked onto a control-channel bunch the client
        // would never strip the prefix, since only UActorChannel::ReceivedBunch reads it.
        if (packageMap.GetMustBeMappedGuidsInLastBunch().Count != 0) {
            Console.WriteLine("ReplicateActor: MustBeMappedGuidsInLastBunch is not empty at the start of " +
                              $"replication ({packageMap.GetMustBeMappedGuidsInLastBunch().Count} leftover) - " +
                              "some earlier bunch serialized objects without flushing them.");
        }

        packageMap.SerializeNewActor(bunch, this, Actor);
        Actor.OnSerializeNewActor(bunch);

        var payload = new FNetBitWriter(Connection.PackageMap, 64);
        // REPLAYOUT_PROBE_HANDLE - the truncated name probe (see HandleProbe.WriteTruncatedNameProbe).
        // Any handle is valid, including the already-confirmed 1-22, which are useful as calibration
        // targets; there is deliberately no guard that can throw here, since this runs deep inside
        // packet dispatch where an exception kills the server outright.
        var probeHandleEnv = Environment.GetEnvironmentVariable("REPLAYOUT_PROBE_HANDLE");
        // REPLAYOUT_PROBE_ACTOR picks which actor's channel carries the probe, matched against the
        // runtime type name (case-insensitive substring, e.g. "AFortInventory"). Defaults to
        // APlayerController, which is what every probe so far has targeted. Only one actor should
        // ever match: the probe deliberately corrupts that channel, and the client closes the
        // connection as soon as it fails.
        // The OPEN burst gets the per-connection role too, and so does the shadow seeded from it at
        // the end - see FScopedRoleDowngrade. Without this a non-owning client is told
        // ROLE_AutonomousProxy at spawn and only corrected a tick later, which is a whole tick of
        // believing it owns somebody else's pawn.
        using var openRoleDowngrade = new FScopedRoleDowngrade(Actor, IsNetOwner);

        var probeActor = Environment.GetEnvironmentVariable("REPLAYOUT_PROBE_ACTOR") ?? nameof(APlayerController);
        if (Actor.GetType().Name.Contains(probeActor, StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(probeHandleEnv, out var probeHandle) && probeHandle > 0) {
            Console.WriteLine($"ReplicateActor: REPLAYOUT_PROBE_HANDLE={probeHandle} on {Actor.GetType().Name} - sending TRUNCATED name probe (RemoteRole anchor + bare handle, no value bits, no terminator) instead of the normal layout");
            HandleProbe.WriteTruncatedNameProbe(payload, Actor, probeHandle);
        } else {
            NativeRepLayouts.Get(Actor).WriteChangedProperties(payload, Actor, InitiallySentProperties);
            WriteCustomDeltaProperties(payload);
        }

        bunch.WriteBit(true); // bHasRepLayout
        bunch.WriteBit(true); // bIsActor

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        Console.WriteLine($"ReplicateActor: {Actor.GetFName()} ChIndex={ChIndex} RemoteRole={Actor.RemoteRole} " +
                          $"Role={Actor.Role} numPayloadBits={numPayloadBits} " +
                          $"payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        foreach (var subobject in GetInitialReplicatedSubobjects(Actor)) ReplicateSubobject(subobject, bunch);

        CommitFastArrayBaseStates(SendBunch(bunch, false));

        // Everything the burst just wrote is now the client's view of this actor, so record it -
        // otherwise the first ReplicateActorUpdate would resend all of it as "changed".
        NativeRepLayouts.Get(Actor).SeedShadowState(Actor, AllReplicatedProperties, _shadowState);

        // THE EARLY ABILITY PUSH IS OFF NOW, AND THE FORMAT FIX IS WHY.
        //
        // A client's FFastArraySerializer decides ONCE, the first time that array object is
        // serialised, whether it speaks the plain wire format or the delta-STRUCT one - and then
        // LATCHES it for the object's lifetime (NetSerialization.h:1232-1239; the early-out at 1070
        // honours the latch on every later call). The deciding bit comes off OUR wire, but the
        // client's own DEMO recorder serialises these same arrays when it records a newly created
        // actor, with the bit true, and whoever touches the array first wins.
        //
        // This used to be a RACE fix: push the component on the opening tick so our read got there
        // first. It fixed the abilities and it broke JUMPING, at every size tried - the full bunch,
        // the bunch without its must-be-mapped GUIDs, the fast array alone (1579 bits), and the fast
        // array held until possession completed. The symptom names the subsystem: the client stops
        // asking to activate spec handle 1 (Jump) while handle 2 (Sprint) still works, so it is
        // CHARACTER MOVEMENT refusing, not the ability system - "a pawn it has possessed but not
        // finished", which is the possession-window rule again ("it is not volume, it is WHAT lands
        // in the possession window"). One run with ASC_ON_OPEN=0 brought jumping straight back, and
        // that is what pinned it on this push rather than on anything else changed the same day.
        //
        // The answer was the FORMAT, not the timing. This server now writes
        // FastArrayDeltaSerialize_DeltaSerializeStructs and claims it, so both sides latch the same
        // way whichever of them gets there first and there is no race left to win - see
        // FFastArraySerializerWriter's class doc and [[fastarray-delta-latch]].
        //
        // ASC_ON_OPEN=1 puts the early push back, for bisecting only. It is expected to break
        // jumping; that is the finding, not a regression.
        if (Environment.GetEnvironmentVariable("ASC_ON_OPEN") is "1" && PossessionComplete) {
            ReplicateAbilitySystemComponent(fastArraysOnly: true);
        } else {
            _ascOpenPushPending = Environment.GetEnvironmentVariable("ASC_ON_OPEN") is "1";
        }
    }

    /// <summary>
    ///     The candidate property set for this channel's actor, resolved once. Doubles as the
    ///     initial burst's changed set and as the set the per-tick diff walks - a property the
    ///     server would never send at join is not one it should start sending later either.
    ///
    ///     Cached because GetInitialReplicatedProperties has side effects (the REP_DISABLE report
    ///     writes to the console and to a file), and the diff pass runs every tick.
    /// </summary>
    private HashSet<string>? _replicatedProperties;
    private HashSet<string>? _replicatedPropertiesForOwner;

    /// <summary>
    ///     COND_SimulatedOnly - properties a connection must NOT be told about its OWN pawn.
    ///
    ///     This is the first replication CONDITION this project has needed, and it is not a nicety:
    ///     sending these to the owner actively breaks the game. The owning client predicts its own
    ///     movement and owns the truth about it; a server value arriving for one of these runs the
    ///     property's OnRep and overwrites that truth with a slightly older, coarser copy. The live
    ///     symptom was a player deploying their glider and then standing bolt upright and floating -
    ///     their own bIsParachuteOpen was being switched back off by this server a tick later.
    ///
    ///     The three engine ones are quoted, not guessed - ACharacter::GetLifetimeReplicatedProps
    ///     (Character.cpp:1489):
    ///
    ///         DOREPLIFETIME_CONDITION(ACharacter, ReplicatedMovementMode, COND_SimulatedOnly);
    ///         DOREPLIFETIME_CONDITION(ACharacter, bIsCrouched,            COND_SimulatedOnly);
    ///
    ///     and AActor's ReplicatedMovement is COND_SimulatedOrPhysics, which is the same thing here
    ///     because nothing on this server simulates physics.
    ///
    ///     bIsSkydiving and bIsParachuteOpen are Fortnite's own and their conditions cannot be read
    ///     from any source available here - but they are derived FROM the owner's own move in the
    ///     first place (see APawn.TrackMoveFlags), so echoing them back at it can only ever be stale.
    ///
    ///     bIsSkydivingFromBus is deliberately NOT in this list. It is the one piece of descent state
    ///     the owner cannot know - a bus jump and a launch pad are the same custom movement mode, and
    ///     only the server can tell them apart.
    /// </summary>
    private static readonly HashSet<string> SimulatedOnlyProperties = new() {
        "ReplicatedMovement", "ReplicatedMovementMode", "bIsCrouched",
        "bIsSkydiving", "bIsParachuteOpen"
    };

    /// <summary>
    ///     UE's replication CONDITIONS (ELifetimeCondition), reduced to the four distinctions a
    ///     server with one connection per player can actually make.
    /// </summary>
    private enum ERepCondition {
        /// <summary>COND_None - every connection, every update.</summary>
        None,

        /// <summary>COND_OwnerOnly / COND_AutonomousOnly - only the connection that owns the actor.</summary>
        OwnerOnly,

        /// <summary>COND_SkipOwner - everybody except the owner.</summary>
        SkipOwner,

        /// <summary>
        ///     COND_SimulatedOnly, COND_SimulatedOrPhysics, COND_SimulatedOnlyNoReplay - only where
        ///     the actor is a SIMULATED proxy, which for this server means "not the owner", since
        ///     nothing here simulates physics and the owner's own pawn is an autonomous proxy.
        /// </summary>
        SimulatedOnly,

        /// <summary>COND_InitialOnly - in the open bunch and never again.</summary>
        InitialOnly,

        /// <summary>COND_ReplayOnly - never, here. There are no replay connections.</summary>
        ReplayOnly
    }

    /// <summary>
    ///     Replication conditions, QUOTED FROM THE ENGINE rather than derived. Every entry below is
    ///     a DOREPLIFETIME_CONDITION line in UE 4.23's own GetLifetimeReplicatedProps:
    ///
    ///         ActorReplication.cpp:400  AActor      ReplicatedMovement          COND_SimulatedOrPhysics
    ///         Pawn.cpp                  APawn       RemoteViewPitch             COND_SkipOwner
    ///         Character.cpp:1489+       ACharacter  RepRootMotion               COND_SimulatedOnly
    ///                                               ReplicatedBasedMovement     COND_SimulatedOnly
    ///                                               ReplicatedMovementMode      COND_SimulatedOnly
    ///                                               bIsCrouched                 COND_SimulatedOnly
    ///                                               bProxyIsJumpForceApplied    COND_SimulatedOnly
    ///                                               AnimRootMotionTranslationScale COND_SimulatedOnly
    ///                                               ReplicatedServerLastTransformUpdateTimeStamp COND_SimulatedOnlyNoReplay
    ///                                               ReplayLastTransformUpdateTimeStamp COND_ReplayOnly
    ///         PlayerController.cpp      APlayerController TargetViewRotation    COND_OwnerOnly
    ///                                                     SpawnLocation         COND_OwnerOnly
    ///         PlayerState.cpp           APlayerState Ping                       COND_SkipOwner
    ///                                                PlayerId / bIsABot /
    ///                                                bIsInactive / UniqueId     COND_InitialOnly
    ///         GameStateBase.cpp         AGameStateBase GameModeClass            COND_InitialOnly
    ///         GameState.cpp             AGameState     ElapsedTime              COND_InitialOnly
    ///
    ///     WHY THIS IS WORTH DOING AS A TABLE rather than one property at a time: getting a condition
    ///     wrong does not under-replicate, it ACTIVELY BREAKS THE GAME, and this project has already
    ///     paid for that twice. The glider bug was exactly this - the owner's own bIsParachuteOpen
    ///     being echoed back at it a tick later and switching itself off, leaving the player standing
    ///     upright and floating. That was found by playing; the engine had stated the rule all along.
    ///
    ///     A key matches a property and EVERYTHING UNDER IT: "RepRootMotion" covers
    ///     RepRootMotion.Location and its eleven siblings, because a condition applies to the whole
    ///     struct, not to the leaves FRepLayout flattens it into.
    ///
    ///     PlayerID, not PlayerId - that is this project's spelling (see NativeRepLayouts). The two
    ///     Fortnite entries at the end are NOT quoted: their conditions cannot be read from anything
    ///     available here, and the reasoning for them is in SimulatedOnlyProperties' own comment.
    /// </summary>
    private static readonly Dictionary<string, ERepCondition> LifetimeConditions = new() {
        ["ReplicatedMovement"] = ERepCondition.SimulatedOnly,
        ["RemoteViewPitch"] = ERepCondition.SkipOwner,
        ["RepRootMotion"] = ERepCondition.SimulatedOnly,
        ["ReplicatedBasedMovement"] = ERepCondition.SimulatedOnly,
        ["ReplicatedMovementMode"] = ERepCondition.SimulatedOnly,
        ["bIsCrouched"] = ERepCondition.SimulatedOnly,
        ["bProxyIsJumpForceApplied"] = ERepCondition.SimulatedOnly,
        ["AnimRootMotionTranslationScale"] = ERepCondition.SimulatedOnly,
        ["ReplicatedServerLastTransformUpdateTimeStamp"] = ERepCondition.SimulatedOnly,
        ["ReplayLastTransformUpdateTimeStamp"] = ERepCondition.ReplayOnly,
        ["TargetViewRotation"] = ERepCondition.OwnerOnly,
        ["SpawnLocation"] = ERepCondition.OwnerOnly,
        ["Ping"] = ERepCondition.SkipOwner,
        ["PlayerID"] = ERepCondition.InitialOnly,
        ["bIsABot"] = ERepCondition.InitialOnly,
        ["bIsInactive"] = ERepCondition.InitialOnly,
        ["UniqueId"] = ERepCondition.InitialOnly,
        ["GameModeClass"] = ERepCondition.InitialOnly,
        ["ElapsedTime"] = ERepCondition.InitialOnly,

        // Fortnite's own, derived rather than quoted - see SimulatedOnlyProperties.
        ["bIsSkydiving"] = ERepCondition.SimulatedOnly,
        ["bIsParachuteOpen"] = ERepCondition.SimulatedOnly
    };

    /// <summary>
    ///     Checks every key in <see cref="LifetimeConditions"/> against the property names the
    ///     layouts actually contain, and says so if one matches nothing.
    ///
    ///     A NAME-KEYED TABLE FAILS SILENTLY, which is the whole reason this exists: a misspelled or
    ///     since-renamed key simply never matches, the condition is never applied, and the property
    ///     goes to a connection that should not have it - which is not a missing feature but an
    ///     actively broken one (the glider bug). `PlayerId` vs `PlayerID` was exactly one such
    ///     near-miss, caught while writing the table only because it was checked by hand.
    ///
    ///     Same shape as NativeClassNetCache's FieldNetIndex check, and called from the same place.
    /// </summary>
    public static void VerifyLifetimeConditions() {
        var known = NativeRepLayouts.AllPropertyNames.ToHashSet();

        var unmatched = LifetimeConditions.Keys
            .Where(key => !known.Contains(key) && !known.Any(name => name.StartsWith(key + ".", StringComparison.Ordinal)))
            .ToArray();

        if (unmatched.Length == 0) {
            Console.WriteLine($"UActorChannel: all {LifetimeConditions.Count} replication conditions match a real property.");
            return;
        }

        Console.WriteLine("UActorChannel: REPLICATION CONDITION KEYS MATCH NOTHING and are therefore doing " +
                          "nothing - the properties they name will go to connections that should not get them: " +
                          string.Join(", ", unmatched));
    }

    /// <summary>The condition on a property, following a dotted leaf back to the struct it belongs to.</summary>
    private static ERepCondition ConditionFor(string propertyName) {
        if (LifetimeConditions.TryGetValue(propertyName, out var exact)) return exact;

        var dot = propertyName.IndexOf('.');
        return dot > 0 && LifetimeConditions.TryGetValue(propertyName[..dot], out var onStruct)
            ? onStruct
            : ERepCondition.None;
    }

    /// <summary>Whether a property may go out in the per-tick DIFF to this connection.</summary>
    private bool AllowedInUpdate(string propertyName) => ConditionFor(propertyName) switch {
        ERepCondition.None => true,
        ERepCondition.OwnerOnly => IsNetOwner,
        ERepCondition.SkipOwner => !IsNetOwner,
        // COND_SimulatedOnly is about the ROLE THIS CONNECTION SEES, not about ownership. UE sets
        // FReplicationFlags::bNetSimulated from the remote role, and the only actors whose remote role
        // is AutonomousProxy for their owner are that player's own pawn and controller - everything
        // else is a simulated proxy to EVERYONE, including whoever owns it.
        //
        // Reading it as plain !IsNetOwner cost a live round: a thrown projectile is owned by the
        // thrower, so its ReplicatedMovement was withheld from the one player watching it, and the
        // server's simulated flight was invisible to exactly the person who asked to see it. For a
        // pawn the two readings agree, which is why nothing noticed until an owned SIMULATED actor
        // existed.
        ERepCondition.SimulatedOnly => !(IsNetOwner && Actor?.RemoteRole == ENetRole.ROLE_AutonomousProxy),
        // Sent once in the open bunch and never compared again - that is what "initial" means.
        ERepCondition.InitialOnly => false,
        ERepCondition.ReplayOnly => false,
        _ => true
    };

    /// <summary>Whether a property may go out in the OPEN bunch to this connection.</summary>
    private bool AllowedAtOpen(string propertyName) => ConditionFor(propertyName) switch {
        ERepCondition.None => true,
        ERepCondition.OwnerOnly => IsNetOwner,
        ERepCondition.SkipOwner => !IsNetOwner,
        ERepCondition.SimulatedOnly => !IsNetOwner,
        ERepCondition.InitialOnly => true,
        ERepCondition.ReplayOnly => false,
        _ => true
    };

    /// <summary>
    ///     The actor's <see cref="AActor.ReplicatedPropertySetRevision"/> when this cache was built.
    ///
    ///     THE CACHE HAD NO INVALIDATION AND THAT SILENTLY DEFEATED A FEATURE. The set is computed
    ///     once, at channel open; the vehicle-riding work then made the pawn's set depend on
    ///     AActor.bAttachmentEverSet, which by definition turns true LATER - so the six
    ///     AttachmentReplication handles were still absent when the player got on, the diff never
    ///     compared them, and nothing at all went out. The server logged a successful attach and the
    ///     client heard nothing, which is exactly the failure mode that is hardest to read.
    ///
    ///     An actor bumps its revision when something that changes its property SET changes, and this
    ///     rebuilds. Not a per-tick recompute: GetInitialReplicatedProperties has side effects (the
    ///     REP_DISABLE report writes to the console and a file) and the diff pass runs every tick.
    /// </summary>
    private int _replicatedPropertiesRevision = -1;

    private HashSet<string> ReplicatedProperties {
        get {
            if (_replicatedProperties == null || _replicatedPropertiesRevision != Actor!.ReplicatedPropertySetRevision) {
                _replicatedPropertiesRevision = Actor!.ReplicatedPropertySetRevision;
                _replicatedProperties = GetInitialReplicatedProperties(Actor);
                _replicatedPropertiesForOwner = null;
                _initiallySentProperties = null;
            }

            return _replicatedPropertiesForOwner ??=
                _replicatedProperties.Where(AllowedInUpdate).ToHashSet();
        }
    }

    /// <summary>
    ///     Everything this channel replicates at all, BEFORE conditions. The shadow is seeded from
    ///     this rather than from the filtered set, so a property that is only ever sent at open (a
    ///     COND_InitialOnly one) is still recorded as "the client has this".
    /// </summary>
    private HashSet<string> AllReplicatedProperties {
        get {
            _ = ReplicatedProperties;
            return _replicatedProperties!;
        }
    }

    /// <summary>
    ///     Of those, the ones the OPEN bunch actually writes - everything except the properties
    ///     below, whose default already matches what a freshly constructed client-side actor has.
    ///
    ///     Real UE does the same thing for a better reason than caution: FRepLayout compares against
    ///     the archetype at open and only sends what differs, so a default-valued property costs no
    ///     bits. Here it is also a deliberate narrowing of risk. The join path works and is the most
    ///     expensive thing in this project to break; a property that can only ever matter later has
    ///     no business widening the one bunch every session depends on.
    ///
    ///     The shadow is still seeded from the FULL set (see the SeedShadowState call), so an
    ///     unsent default is recorded as "the client has this" and the first tick does not resend it
    ///     - which is true, because it is the value the client built the actor with.
    /// </summary>
    private HashSet<string> InitiallySentProperties =>
        _initiallySentProperties ??= AllReplicatedProperties
            .Where(AllowedAtOpen)
            .Except(NeverSentAtOpen)
            .ToHashSet();

    private HashSet<string>? _initiallySentProperties;

    /// <summary>
    ///     Properties whose default IS the client's default, and which only ever become interesting
    ///     once something happens in the match - death, so far. Listed here rather than left out of
    ///     ReplicatedProperties entirely, because the per-tick diff still has to walk them.
    /// </summary>
    private static readonly HashSet<string> NeverSentAtOpen = new() {
        "bIsDying", "LastReplicatedEmoteExecuted",
        // NOT THE MONTAGE STRUCT. It was here on the reasoning that it "is meaningless until somebody
        // emotes" - and that reasoning cost the same bug twice.
        //
        // NeverSentAtOpen does not stop the shadow being SEEDED with the current value; it only stops
        // the value going out. So a member that already holds its final value when the channel opens
        // is recorded as sent, never changes again, and is never transmitted at all. First it was
        // PlayRate defaulting to 1.0 against the client's 0; then, with the defaults matched, it was
        // a player who emoted BEFORE an onlooker's channel for their pawn opened - PlayRate was
        // already 1.0 at open, so that onlooker never received it and saw the emote frozen on its
        // first frame.
        //
        // Six small values in the opening burst is the cheap side of that trade, and it is also
        // CORRECT: a player who is mid-emote when you arrive should be mid-emote when you see them.
        "DeathInfo.FinisherOrDowner", "DeathInfo.bDBNO", "DeathInfo.DeathCause",
        "DeathInfo.Distance", "DeathInfo.bInitialized",
        // Zero until the player is eliminated, and zero IS the client's default - so it belongs
        // here rather than in the opening burst, and the per-tick diff still walks it.
        "Place"
    };

    /// <summary>
    ///     What this channel has already put on the wire, per property name. Stands in for real UE's
    ///     shadow buffer (FRepState::StaticBuffer) - see FRepLayout.GetComparableValue.
    /// </summary>
    private readonly Dictionary<string, object?> _shadowState = new();

    /// <summary>
    ///     Time, on the driver's clock, at which this channel is next allowed to consider the actor
    ///     for replication. Real UE keeps the equivalent on the actor as
    ///     AActor::NetUpdateTime/NetUpdateFrequency; it lives here because this project has one
    ///     channel per actor per connection and no ServerReplicateActors prioritisation pass.
    /// </summary>
    private float _nextUpdateTime;

    /// <summary>
    ///     The ongoing half of UActorChannel::ReplicateActor - the branch real UE takes when
    ///     OpenPacketId is already set (DataChannel.cpp:227-155): no spawn header, no bNetInitial,
    ///     and an UNRELIABLE bunch. Unreliable matters here more than in real UE: this project's
    ///     reliable queue is 256 entries deep and closes the connection when it overflows, so
    ///     property updates at gameplay rate must never take that path. A dropped update is
    ///     harmless - the property still differs from the shadow next tick, so it is simply sent
    ///     again.
    ///
    ///     Sends nothing at all when nothing changed, which is the normal case.
    /// </summary>
    /// <summary>
    ///     FScopedRoleDowngrade (DataChannel.cpp:2692) - THE THING THAT MAKES A REMOTE PLAYER MOVE.
    ///
    ///     A player pawn is set ROLE_AutonomousProxy so its own client can predict its movement. That
    ///     role is correct for exactly one connection. Sent to ANY OTHER client it is a lie with
    ///     teeth: that client also believes it owns the pawn, runs its own prediction on it, and
    ///     therefore IGNORES the replicated position - which is a pawn frozen wherever it was
    ///     created, however correctly ReplicatedMovement is being sent.
    ///
    ///     Real UE mutates the actor for the duration of the replication and puts it back afterwards,
    ///     which is exactly what this does. It has to be done here rather than in the layout's
    ///     getter, because the answer is per CONNECTION and a getter only sees the object.
    ///
    ///     Applied around the diff as well as the write: the shadow state is per channel, so the
    ///     comparison has to see the same downgraded value it is going to send, or every tick would
    ///     find RemoteRole "changed" and resend it forever.
    /// </summary>
    private readonly struct FScopedRoleDowngrade : IDisposable {
        private readonly AActor _actor;
        private readonly ENetRole _actualRemoteRole;

        public FScopedRoleDowngrade(AActor actor, bool bNetOwner) {
            _actor = actor;
            _actualRemoteRole = actor.RemoteRole;

            if (_actualRemoteRole == ENetRole.ROLE_AutonomousProxy && !bNetOwner) {
                actor.SetAutonomousProxy(false);
            }
        }

        public void Dispose() {
            if (_actor.RemoteRole != _actualRemoteRole && _actualRemoteRole == ENetRole.ROLE_AutonomousProxy) {
                _actor.SetAutonomousProxy(true);
            }
        }
    }

    /// <summary>
    ///     FReplicationFlags::bNetOwner - does the connection this channel belongs to own the actor?
    ///     The owner chain ends at the PlayerController, which is what a connection has.
    /// </summary>
    /// <summary>
    ///     Who this channel talks to, for the log.
    ///
    ///     ADDED AFTER A TWO-PLAYER SESSION COULD NOT BE READ: every ability line printed
    ///     `Actor=FortPlayerStateAthena` and a ChIndex, and ChIndex is per CONNECTION - so two lines
    ///     about two different players were indistinguishable from two lines about one player on two
    ///     connections. A log that cannot tell two players apart is no use in the one situation that
    ///     needs it.
    /// </summary>
    /// <summary>
    ///     This channel's actor, named the way the CLIENT's log names it - by NetGUID - so the two
    ///     logs can be laid side by side. NOT GetUniqueID(): that returns UObjectBase._InternalIndex,
    ///     which nothing in this project ever assigns (the compiler says so, CS0649), so it is always
    ///     zero and every actor looks like every other one.
    /// </summary>
    private string ActorName {
        get {
            if (Actor == null) return "no actor";

            var guid = (Connection?.PackageMap as UPackageMapClient)?.GuidCache?.GetNetGUID(Actor);
            return $"{Actor.GetFName()}<{guid?.Value.ToString() ?? "?"}>";
        }
    }

    private string ConnectionName =>
        Connection?.PlayerController is { } pc
            ? $"{pc.GetFName()}{(IsNetOwner ? " (OWNER)" : "")}"
            : Connection?.RemoteAddr?.ToString() ?? "no connection";

    private bool IsNetOwner =>
        Connection?.PlayerController is { } owner && Actor != null &&
        (Actor.IsOwnedBy(owner) || Actor == owner || (Actor as APawn)?.Controller == owner);

    /// <summary>
    ///     <paramref name="force" /> skips the NetUpdateFrequency gate only - never the open/ack
    ///     ones, which exist because an update that overtakes its own channel-open is dropped
    ///     silently and never retried. Used when a property has to reach the client as its OWN
    ///     change rather than folded into the next scheduled diff; see FortEmoteSystem.
    /// </summary>
    /// <summary>
    ///     The same three signals UNetDriver.OpenChannelsForNewlyRelevantActors uses: the client has
    ///     no pawn to take, has said it finished loading one, or has acknowledged the one it has.
    /// </summary>
    private bool PossessionComplete =>
        Connection?.PlayerController is not { } viewer
        || viewer.Pawn == null || viewer.bClientPawnLoaded || viewer.AcknowledgedPawn == viewer.Pawn;

    /// <summary>
    ///     The ability array still owes this connection its early push - the channel opened during
    ///     the possession window, so it could not go out there. Sent the moment possession finishes,
    ///     which is still long before the client's demo recorder touches the array.
    /// </summary>
    private bool _ascOpenPushPending;

    public unsafe bool ReplicateActorUpdate(bool force = false) {
        if (Actor == null || Connection == null || Closing || Broken) return false;

        if (_ascOpenPushPending && PossessionComplete) {
            _ascOpenPushPending = false;
            ReplicateAbilitySystemComponent(fastArraysOnly: true);
        }

        // Not yet opened on the wire - the initial burst has not run, and there is nothing to diff
        // against. ReplicateActor is what opens it.
        if (OpenPacketId.First == UnrealConstants.IndexNone) return false;

        // Wait for the open to be ACKED, not merely sent. These updates are unreliable and UDP is
        // unordered, so one could otherwise overtake the reliable open bunch and reach a client that
        // has no channel for it yet - UActorChannel::ProcessBunch drops those silently ("New actor
        // channel received non-open packet"). Silently is the problem: the packet WAS delivered, so
        // no NAK ever arrives to re-dirty the shadow, and the property would stay stale forever.
        if (!OpenAcked) return false;

        var driverTime = Connection.Driver!.GetElapsedTime();
        if (!force && driverTime < _nextUpdateTime) return false;

        var frequency = Actor.NetUpdateFrequency > 0.0f ? Actor.NetUpdateFrequency : 1.0f;
        _nextUpdateTime = driverTime + 1.0f / frequency;

        var layout = NativeRepLayouts.Get(Actor);

        // Everything from here to the end of the write sees the per-connection role - see
        // FScopedRoleDowngrade.
        using var roleDowngrade = new FScopedRoleDowngrade(Actor, IsNetOwner);

        var changed = layout.CompareProperties(Actor, ReplicatedProperties, _shadowState);

        // Custom deltas go in their own bunch, so an actor with no changed RepLayout property can
        // still have a changed fast array. The same is true one level down, for a component's.
        var wroteSomething = ReplicateCustomDeltaUpdate();
        wroteSomething |= ReplicateEquippedWeapon();
        wroteSomething |= ReplicateAbilitySystemComponent();
        wroteSomething |= ReplicateVehicleSeats();

        wroteSomething |= ReplicateMovementSet();
        wroteSomething |= ReplicatePlayerAttrSet();
        wroteSomething |= ReplicateHealthSet();
        wroteSomething |= ReplicateBuildingAttributeSet();

        if (changed.Count == 0) return wroteSomething;

        var changedNames = changed.Select(entry => entry.Name).ToHashSet();

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = false;

        using var payload = new FNetBitWriter(Connection.PackageMap, 64);
        layout.WriteChangedProperties(payload, Actor, changedNames);

        bunch.WriteBit(true); // bHasRepLayout
        bunch.WriteBit(true); // bIsActor

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        Console.WriteLine($"ReplicateActorUpdate: {Actor.GetType().Name} ChIndex={ChIndex} " +
                          $"changed=[{string.Join(", ", changedNames)}] numPayloadBits={numPayloadBits} " +
                          $"payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        var packetRange = SendBunch(bunch, false);

        // Only now, after the bunch is away - a shadow updated ahead of a send that never happened
        // would make the property look unchanged forever.
        FRepLayout.CommitShadowState(changed, _shadowState);

        // ...but "away" is not "arrived". These bunches are unreliable, so remember what rode in
        // which packet; a NAK has to undo the shadow update or the property is stale forever.
        if (packetRange.First != UnrealConstants.IndexNone) _unackedUpdates[packetRange.First] = changed;

        // Anything at or below the last delivered packet id has been resolved one way or the other -
        // ack and nak are consumed strictly in order, so this watermark only moves forward.
        if (_unackedUpdates.Count > 1) {
            var acked = Connection.OutAckPacketId;
            foreach (var packetId in _unackedUpdates.Keys.Where(id => id <= acked).ToArray()) {
                _unackedUpdates.Remove(packetId);
            }
        }

        return true;
    }

    /// <summary>
    ///     Sends the actor's changed fast arrays as a content block of their own - bHasRepLayout
    ///     false, which real UE also produces whenever RepLayout wrote nothing
    ///     (DataReplication.cpp:1588); the client then skips ReceiveProperties and goes straight to
    ///     the field loop.
    ///
    ///     Deliberately a SEPARATE, RELIABLE bunch rather than riding along in the unreliable
    ///     property update. Real UE puts both in one content block and absorbs loss through
    ///     FObjectReplicator::ReceivedNak rolling the custom delta base state back; this port has no
    ///     such rollback, and a lost fast-array delta is unrecoverable - the base state has already
    ///     advanced, so the change is never reconsidered and the client's inventory is permanently
    ///     wrong. Reliability is affordable here in a way it is not for per-tick properties: fast
    ///     arrays change on events (an item picked up, ammo spent), not every frame.
    /// </summary>
    private unsafe bool ReplicateCustomDeltaUpdate() {
        using var payload = new FNetBitWriter(Connection!.PackageMap, 256);

        if (!WriteCustomDeltaProperties(payload)) return false;

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        bunch.WriteBit(false); // bHasRepLayout - no property stream in this block
        bunch.WriteBit(true);  // bIsActor

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        Console.WriteLine($"ReplicateCustomDeltaUpdate: {Actor!.GetType().Name} ChIndex={ChIndex} " +
                          $"numPayloadBits={numPayloadBits} payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        CommitFastArrayBaseStates(SendBunch(bunch, false));

        return true;
    }

    /// <summary>Property updates sent in an unreliable bunch, keyed by the packet that carried them.</summary>
    private readonly Dictionary<int, List<(string Name, object? Value)>> _unackedUpdates = new();

    /// <summary>
    ///     Real UE re-dirties unreliable properties through FObjectReplicator::ReceivedNak /
    ///     FRepChangedPropertyTracker. This is the same idea with this port's much simpler shadow:
    ///     forget what the lost packet claimed to have delivered, so the next compare sees those
    ///     properties as changed again and resends them.
    ///
    ///     Without this, a single dropped packet leaves a property permanently stale - it matches
    ///     the shadow forever and is never reconsidered. Reliable bunches do not need it; the base
    ///     implementation resends those outright.
    /// </summary>
    public override void ReceivedNak(int nakPacketId) {
        base.ReceivedNak(nakPacketId);

        if (!_unackedUpdates.Remove(nakPacketId, out var lost)) return;

        foreach (var (name, _) in lost) _shadowState.Remove(name);

        Console.WriteLine($"UActorChannel.ReceivedNak: ChIndex={ChIndex} packet {nakPacketId} was lost, " +
                          $"re-dirtying [{string.Join(", ", lost.Select(entry => entry.Name))}]");
    }

    /// <summary>
    ///     Forget what the shadow says about one property, so the next ReplicateActorUpdate sees it
    ///     as changed and sends it again. Same mechanism ReceivedNak uses, exposed for the one case
    ///     that is not a lost packet: a reference that was WRITTEN correctly but could not be
    ///     RESOLVED by the client, because the actor it names had no channel yet.
    ///
    ///     UPackageMap writes such a reference as a bare NetGUID; if the client has never seen that
    ///     GUID it reads null and, in this project's experience, never comes back to it - real UE's
    ///     FObjectReplicator::UpdateUnmappedObjects would retry, but nothing here makes the server
    ///     send it a second time, and a property that matches the shadow is never reconsidered.
    ///     Re-dirtying once, after the referenced actor's channel is open, is the whole fix.
    /// </summary>
    public void MarkPropertyDirty(string propertyName) => _shadowState.Remove(propertyName);

    /// <summary>
    ///     Port of UActorChannel::WriteContentBlockHeader (DataChannel.cpp). Every replicated object
    ///     in a bunch - the actor itself, or one of its subobjects - is preceded by this small header:
    ///     a bHasRepLayout bit, a bIsActor bit (1 lets the reader skip straight to the payload, since
    ///     it already knows which actor this channel is for), and, for anything else, the object's own
    ///     NetGUID reference (the exact same SerializeObject/InternalWriteObject/ExportNetGUID pipeline
    ///     already used for SerializeNewActor's Archetype reference and AController.PlayerState) plus
    ///     a "stably named" bit. Real UE gates the stably-named-vs-class-fallback choice on
    ///     Connection->Driver->IsServer(); that's always true in this codebase (server-only), so it's
    ///     not checked here. NET_CHECKSUM(Bunch) right after the object reference in real UE is a
    ///     complete no-op outside non-shipping/non-test builds (see CoreNet.h's
    ///     `#if !(UE_BUILD_SHIPPING || UE_BUILD_TEST)` gate on the NET_CHECKSUM macro) - this project's
    ///     already-proven-working SerializeNewActor/InternalWriteObject paths likewise never emit
    ///     anything for it, so it's omitted here too for consistency with a real (shipping/test) client.
    /// </summary>
    public void WriteContentBlockHeader(UObject obj, FOutBunch bunch, bool hasRepLayout) {
        bunch.WriteBit(hasRepLayout);

        var isActor = obj == Actor;
        bunch.WriteBit(isActor);
        if (isActor) return;

        var packageMap = (UPackageMapClient) Connection!.PackageMap!;
        packageMap.SerializeObject(bunch, obj);

        // A SUB-OBJECT THAT COULD NOT BE GIVEN A NetGUID, said out loud. FNetGUIDCache refuses one
        // to anything whose IsSupportedForNetworking() is false, and the default rule for that walks
        // the OUTER chain - so a component of a runtime-spawned actor is refused unless its class
        // overrides it, exactly as UActorComponent does. The reference then goes out as the invalid
        // guid 0 and the SERVER NOTICES NOTHING: the block is written, the bunch is sent, the log
        // line says it worked. The only symptom is on the client, one layer removed from the cause:
        //
        //     LogNet: Warning: UActorChannel::ProcessBunch: ReadContentBlockPayload failed to
        //             find/create object. RepObj: NULL, Channel: 18
        //
        // Once per class, because the answer is a property of the class and repeating it per tick
        // would bury it. See UFortVehicleSeatComponent, which is the case that cost a live test.
        if (!packageMap.GuidCache!.GetNetGUID(obj).IsValid() &&
            _warnedUnsupportedSubObjects.Add(obj.GetClass().GetFName().ToString())) {
            Console.WriteLine($"UActorChannel.WriteContentBlockHeader: {obj.GetFName()} " +
                              $"({obj.GetClass().GetFName()}) has NO NetGUID - it is being sent as the " +
                              "invalid guid 0 and the client will resolve it to NULL. Its class almost " +
                              "certainly needs `public override bool IsSupportedForNetworking() => true;` " +
                              "(the default rule refuses anything whose outer chain is not name-stable).");
        }

        if (obj.IsNameStableForNetworking()) {
            bunch.WriteBit(true);
        } else {
            bunch.WriteBit(false);

            // UClass.CreateDefaultObject() is what actually gives a UClass instance its own
            // resolvable (package, name) identity (see its own comment) - it's normally only called
            // lazily via GetDefaultObject()/GetArchetype() when spawning an actor of that class.
            // A class referenced ONLY here (as a subobject's class fallback, never as some actor's
            // archetype) would otherwise still have a blank FName/null Outer at export time -
            // confirmed live 2026-08-24 ("FullNetGUIDPath: [15]EMPTY"). Calling GetDefaultObject()
            // first (result unused) forces that lazy initialization before we export the class itself.
            var classObj = obj.GetClass();
            classObj.GetDefaultObject();
            packageMap.SerializeObject(bunch, classObj);
        }
    }

    /// <summary>
    ///     Port of UActorChannel::WriteContentBlockPayload (DataChannel.cpp): a content block header
    ///     followed by a packed bit count and that many payload bits.
    /// </summary>
    public unsafe void WriteContentBlockPayload(UObject obj, FOutBunch bunch, bool hasRepLayout, FNetBitWriter payload) {
        WriteContentBlockHeader(obj, bunch, hasRepLayout);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());
    }

    /// <summary>
    ///     Simplified port of UActorChannel::ReplicateSubobject (DataChannel.cpp) - writes a
    ///     sub-object content block for a non-actor UObject owned by this channel's Actor (e.g.
    ///     AFortPlayerController::WorldInventory, a default subobject created via
    ///     CreateDefaultSubobject in the native constructor). Real UE first tries
    ///     FObjectReplicator::ReplicateProperties (the subobject's own FRepLayout push) and only
    ///     falls back to an empty-payload content block if that had nothing to send; this project
    ///     doesn't implement per-subobject FRepLayout property replication, so it always takes that
    ///     fallback (`WriteContentBlockPayload(Obj, Bunch, false, EmptyPayload)`), which is also
    ///     exactly what real UE does the very first time a subobject with no changed properties of
    ///     its own is replicated - the sole purpose is letting the client resolve/create the
    ///     subobject's NetGUID.
    ///
    ///     Real UE's version also has a defensive pre-step
    ///     (`if (!GuidCache->SupportsObject(Obj)) GuidCache->AssignNewNetGUID_Server(Obj)`) before
    ///     calling SerializeObject, needed because FNetGUIDCache::SupportsObject would otherwise
    ///     reject a default subobject whose Outer (the dynamically-spawned actor) isn't itself
    ///     name-stable. That's not needed here: UFortWorldItem already overrides
    ///     IsSupportedForNetworking() to return true (see its own doc comment), so
    ///     FNetGUIDCache.SupportsObject/GetOrAssignNetGUID (called inside SerializeObject, inside
    ///     WriteContentBlockHeader) already assigns a NetGUID correctly on the first reference without
    ///     a separate bootstrap step.
    /// </summary>
    public void ReplicateSubobject(UObject obj, FOutBunch bunch) {
        if (Connection == null) return;

        var emptyPayload = new FNetBitWriter(Connection.PackageMap!, 8);
        Console.WriteLine($"ReplicateSubobject: obj={obj.GetFName()} class={obj.GetClass().GetFName()} owner={Actor?.GetFName()}");
        WriteContentBlockPayload(obj, bunch, false, emptyPayload);
    }

    /// <summary>
    ///     Sends AController::ClientRestart(APawn* NewPawn) on this (already-open) PlayerController
    ///     channel - the server->client RPC real UE fires from Possess()/RestartPlayer() to tell the
    ///     client-side PlayerController it now controls a pawn. A real PR3.0 client capture
    ///     (2026-08-24) showed this is what the client is actually waiting on: without it, the
    ///     client kept calling ServerSetSpectatorLocation forever (still thinks it's a spectator);
    ///     with it, the client immediately replies with ServerAcknowledgePossession and switches to
    ///     ServerMoveNoBase. Only a single object-reference parameter, so this is hand-written rather
    ///     than routed through a generic RPC-writer abstraction (see FRpcReader for the read-side
    ///     equivalent, which this project only needed for client->server calls until now).
    /// </summary>
    /// <summary>
    ///     Stand-in for FObjectReplicator::ReplicateCustomDeltaProperties (DataReplication.cpp:1421),
    ///     called straight after the RepLayout property push so both land in the same content-block
    ///     payload - the same single-Writer arrangement real UE uses in ReplicateProperties.
    ///
    ///     Only AFortInventory::Inventory is sent, and only ever as an empty delta. That property is
    ///     an FFortItemList, which derives from FFastArraySerializer and is therefore a Custom Delta
    ///     property: it is excluded from FRepLayout's handle stream entirely and has to travel as
    ///     its own RepIndex-addressed field. See FFastArraySerializerWriter for the wire format and
    ///     why an empty delta is still a real message rather than a no-op.
    ///
    ///     Why send an empty inventory at all: the client's ClientRestart_Implementation currently
    ///     stops with "Quickbars are invalid, waiting to finish restarting". Quickbars are not
    ///     replicated on this build - AFortQuickBars derives from AFortClientOnlyActor and
    ///     AFortPlayerController::ClientQuickBars carries no Net flag, so the client builds them
    ///     itself - and the inventory is the most plausible thing it is waiting on, since quickbars
    ///     are a view onto it and AFortInventory has both an OnRep on Inventory and its own
    ///     HandleInventoryLocalUpdate. Unconfirmed; if this does not move the client on, the field
    ///     framing here is still the prerequisite for any real item replication later.
    /// </summary>
    /// <summary>
    ///     What this connection was last sent for each of the actor's fast arrays, keyed by
    ///     ClassNetCache field name. See FNetFastTArrayBaseState for why an accurate base key is
    ///     load-bearing rather than cosmetic.
    /// </summary>
    private readonly Dictionary<string, FNetFastTArrayBaseState> _fastArrayBaseStates = new();

    /// <summary>
    ///     Replicates this actor's AbilitySystemComponent as a SUB-OBJECT content block - the first
    ///     time this project sends one, and the only shape a component can travel in (a component is
    ///     not an actor, so it never gets a channel of its own).
    ///
    ///     The framing is the ordinary content block with bIsActor=0, which makes the header carry
    ///     the component's own NetGUID plus - because a component of a runtime-spawned actor is
    ///     never name-stable - its class, so the client can construct it. That matches a real
    ///     Project-Reboot-3.0 capture exactly: the player's ASC there is a dynamic guid with no path
    ///     export, and every one of its 96 blocks rides its owner's channel this way.
    ///
    ///     Sent in its own bunch rather than appended to the actor's, purely so a failure here
    ///     cannot corrupt the actor's own property stream while this path is new.
    /// </summary>
    /// <param name="fastArraysOnly">
    ///     Send the custom-delta fields and nothing else - no RepLayout properties, no ability
    ///     instances, no must-be-mapped announcement. Used on the channel's opening tick, where the
    ///     ability array has to arrive early but everything around it must not. See the caller.
    /// </param>
    private unsafe bool ReplicateAbilitySystemComponent(bool fastArraysOnly = false) {
        if (Connection == null) return false;

        // A building carries its own ASC for exactly one reason - to make its attribute set's
        // OnRep able to broadcast (see ABuildingActor.AbilitySystemComponent) - but it rides the
        // wire through the identical sub-object block a PlayerState's does, so the two share this.
        var asc = Actor switch {
            APlayerState playerState => playerState.AbilitySystemComponent,
            ABuildingActor building => building.AbilitySystemComponent,
            _ => null
        };

        if (asc == null) return false;

        // BEFORE the spec that names them. A spec's ReplicatedInstances is an ObjectRef array, and
        // this project's standing hazard is an ObjectRef whose target the client has not built yet:
        // it resolves to null on arrival and a property matching the shadow is never reconsidered.
        // Sending the instance's own content block first means the client has constructed the object
        // by the time the spec points at it.
        if (!fastArraysOnly) ReplicateAbilityInstances(asc);

        using var payload = new FNetBitWriter(Connection.PackageMap, 256);

        // The component's own properties come first, in the same handle stream an actor uses - the
        // ONLY thing that makes this a component rather than an actor is the content block header.
        // OwnerActor/AvatarActor are what let the client run InitAbilityActorInfo and therefore
        // apply movement attributes to the pawn; see NativeRepLayouts.AbilitySystemComponentProps.
        var layout = NativeRepLayouts.AbilitySystemComponent;
        var changed = fastArraysOnly
            ? new List<(string Name, object? Value)>()
            : layout.CompareProperties(asc, AbilitySystemProperties, _ascShadowState);
        var changedNames = changed.Select(entry => entry.Name).ToHashSet();

        if (changedNames.Count > 0) layout.WriteChangedProperties(payload, asc, changedNames);

        var wroteDelta = WriteCustomDeltaField(payload, NativeClassNetCache.FortAbilitySystemComponentCache,
            "ActivatableAbilities", fieldPayload =>
                FFastArraySerializerWriter.WriteDelta(fieldPayload, asc.ActivatableAbilities,
                    BaseStateFor("ActivatableAbilities"), FFastArraySerializerWriter.WriteAbilitySpec,
                    FFastArraySerializerWriter.WriteAbilitySpecDeltaStruct));

        // The second fast array on this component. Only sent once something is actually in it -
        // an empty one has nothing to say, and a bare header would make the client run its whole
        // PostReceiveCleanup for no reason (see WriteCustomDeltaField's own note).
        if (asc.ActiveGameplayEffects.Count > 0) {
            wroteDelta |= WriteCustomDeltaField(payload, NativeClassNetCache.FortAbilitySystemComponentCache,
                "ActiveGameplayEffects", fieldPayload =>
                    FFastArraySerializerWriter.WriteDelta(fieldPayload, asc.ActiveGameplayEffects,
                        BaseStateFor("ActiveGameplayEffects"), FFastArraySerializerWriter.WriteActiveGameplayEffect,
                        FFastArraySerializerWriter.WriteActiveGameplayEffectDeltaStruct));
        }

        // The third one - and its guard is NOT `Count > 0`, which is the mistake this shape invites.
        // A cue's whole point is that it also ends: the delta that REMOVES the last element is sent
        // from an array that is by then empty, so the field has to keep being offered for as long as
        // this connection still believes something is in it. BaseStateFor's record of what it was
        // last sent is exactly that memory.
        var cueBaseState = BaseStateFor("ActiveGameplayCues");
        if (asc.ActiveGameplayCues.Count > 0 || cueBaseState.IdToKey.Count > 0) {
            wroteDelta |= WriteCustomDeltaField(payload, NativeClassNetCache.FortAbilitySystemComponentCache,
                "ActiveGameplayCues", fieldPayload =>
                    FFastArraySerializerWriter.WriteDelta(fieldPayload, asc.ActiveGameplayCues,
                        cueBaseState, FFastArraySerializerWriter.WriteActiveGameplayCue,
                        FFastArraySerializerWriter.WriteActiveGameplayCueDeltaStruct));
        }

        if (changedNames.Count == 0 && !wroteDelta) return false;

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        // bHasRepLayout says whether a handle stream leads the payload. WriteContentBlockHeader
        // writes that bit, the bIsActor=0 bit, and the object reference.
        WriteContentBlockHeader(asc, bunch, hasRepLayout: changedNames.Count > 0);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        var ascGuid = ((UPackageMapClient) Connection.PackageMap!).GuidCache!.GetNetGUID(asc);
        Console.WriteLine($"ReplicateAbilitySystemComponent: ChIndex={ChIndex} to={ConnectionName} " +
                          $"Actor={ActorName} " +
                          $"ascNetGuid={ascGuid} stablyNamed={asc.IsNameStableForNetworking()} " +
                          $"changed=[{string.Join(", ", changedNames)}] " +
                          $"abilities={asc.ActivatableAbilities.Count} numPayloadBits={numPayloadBits} " +
                          $"payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        // THE ONE TARGETED VERSION OF THE NET_ASYNC_LOAD TEST. Announcing must-be-mapped GUIDs makes
        // the client HOLD every later bunch on this channel until they resolve - which is what we
        // want at join time (turning it off wholesale with NET_ASYNC_LOAD=0 hangs the loading
        // screen, so the queueing is load-bearing there) and is also the only known mechanism that
        // can stall one connection's ability deltas forever while the server sees nothing wrong.
        //
        // ASC_MUST_BE_MAPPED=0 drops the announcement for THIS bunch only. The ability specs it
        // carries name an emote asset and its ability class by GUID; if those are what a stalled
        // channel is waiting on, this is the switch that says so - and it leaves every other
        // channel's queueing, including the join burst's, exactly as it was.
        if (Environment.GetEnvironmentVariable("ASC_MUST_BE_MAPPED") is "0"
            && Connection.PackageMap is UPackageMapClient packageMap) {
            var pending = packageMap.GetMustBeMappedGuidsInLastBunch();
            if (pending.Count > 0) {
                Console.WriteLine($"ReplicateAbilitySystemComponent: ASC_MUST_BE_MAPPED=0 - dropping " +
                                  $"[{string.Join(", ", pending.Select(g => g.Value))}] from this bunch so it " +
                                  "cannot queue behind an unresolved reference.");
                pending.Clear();
            }
        }

        // The opening tick announces nothing: a must-be-mapped GUID is what makes the client stall
        // this channel to async-load a Blueprint class, and that is the possession-window hazard.
        if (fastArraysOnly && Connection.PackageMap is UPackageMapClient openPackageMap) {
            openPackageMap.GetMustBeMappedGuidsInLastBunch().Clear();
        }

        var ascSent = SendBunch(bunch, false);
        CommitFastArrayBaseStates(ascSent);

        // Only after the bunch is away, for the same reason ReplicateActorUpdate commits late.
        if (ascSent.First != UnrealConstants.IndexNone) FRepLayout.CommitShadowState(changed, _ascShadowState);

        // AVATARACTOR HAS TO BE RE-SENT UNTIL THIS CONNECTION CAN ACTUALLY RESOLVE IT, and that is
        // the whole of "an onlooker never sees an emote".
        //
        // AFortPawn::OnRep_ReplicatedAnimMontage (static 0x141973660) begins:
        //
        //      rbx = [this + 0xD00]        ; AFortPawn::AbilitySystemComponent
        //      if (rbx == 0) return        ; <- nothing happens, ever
        //      ... copy RepAnimMontageInfo into the ASC and call ITS OnRep (vtable +0x7B8)
        //
        // That pointer is NOT replicated (it is absent from the pawn's handle list); the client
        // builds it from the ASC, which needs the ASC bound to this pawn - AvatarActor.
        //
        // A PlayerState's channel opens on an onlooker's connection BEFORE that player's pawn does.
        // AvatarActor is written then as a NetGUID for an actor the client has not been told to
        // spawn, so it resolves NULL - and it never changes afterwards, so the diff never sends it
        // again. This project's standing hazard, in the one place where it costs a whole feature.
        //
        // Re-dirtying it while the target has no channel here costs one ObjectRef per pass and stops
        // the moment the pawn's channel exists.
        if (asc.AvatarActor is { } avatar && Connection.FindActorChannel(avatar) == null) {
            _ascShadowState.Remove("AvatarActor");
            _ascShadowState.Remove("OwnerActor");
        }

        // Same repair as handle 19's: a building's own initial push named this component before it
        // had a NetGUID, so the client read it null and would never reconsider.
        if (Actor is ABuildingActor && !_sentBuildingAbilitySystem) {
            _sentBuildingAbilitySystem = true;
            MarkPropertyDirty("ReplicatedAbilitySystemComponent");
        }

        return true;
    }

    private bool _sentBuildingAbilitySystem;

    /// <summary>
    ///     Sends one sub-object content block per replicated ability instance - the port of
    ///     UAbilitySystemComponent::ReplicateSubobjects' AllReplicatedInstancedAbilities loop
    ///     (AbilitySystemComponent.cpp:1490).
    ///
    ///     The block carries NO PAYLOAD, and that is the whole point: everything the client needs is
    ///     in the header, because the object's name is not stable and WriteContentBlockHeader
    ///     therefore writes its CLASS alongside its NetGUID, which is exactly enough for the client
    ///     to construct a GA_*_C of the right type. The ability's own state is GAS's to rebuild.
    ///
    ///     ONCE EACH. An ability instance is created at grant time and never changes afterwards, so
    ///     there is nothing to diff and re-sending would be pure noise. _sentAbilityInstances is per
    ///     CHANNEL rather than per component for the same reason every other send here is: a second
    ///     connection has its own channel and its own copy of this bookkeeping.
    /// </summary>
    private unsafe void ReplicateAbilityInstances(UFortAbilitySystemComponent asc) {
        if (Connection == null) return;

        foreach (var instance in asc.AllReplicatedInstancedAbilities) {
            if (!_sentAbilityInstances.Add(instance)) continue;

            using var bunch = new FOutBunch(this, false);
            bunch.bReliable = true;

            WriteContentBlockHeader(instance, bunch, hasRepLayout: false);

            uint numPayloadBits = 0;
            bunch.SerializeIntPacked(&numPayloadBits);

            var guid = ((UPackageMapClient) Connection.PackageMap!).GuidCache!.GetNetGUID(instance);
            Console.WriteLine($"ReplicateAbilityInstances: ChIndex={ChIndex} Actor={Actor?.GetFName()} " +
                              $"sent ability instance {instance.GetFName()} of {instance.GetClass().NativePackagePath} " +
                              $"as guid {guid} (header only, no payload)");

            SendBunch(bunch, false);
        }
    }

    private readonly HashSet<UObject> _sentAbilityInstances = new();

    /// <summary>Sub-object classes already reported as un-networkable - see WriteContentBlockHeader.</summary>
    private readonly HashSet<string> _warnedUnsupportedSubObjects = new();

    /// <summary>
    ///     Runs the ability-system push out of band, outside the per-tick replication pass. The one
    ///     caller is <see cref="FortEmoteSystem"/>: an emote grants a spec and then immediately tells
    ///     the client to activate it, and putting the grant on the wire first spares that a tick.
    ///
    ///     Safe to call at any time - it is the same idempotent compare-and-send ReplicateActorUpdate
    ///     runs, and it sends nothing when nothing changed.
    /// </summary>
    public bool FlushAbilitySystemComponent() => ReplicateAbilitySystemComponent();

    /// <summary>What this channel last sent for the vehicle's seat component.</summary>
    private readonly Dictionary<string, object?> _seatShadowState = new();

    /// <summary>
    ///     Sends the vehicle's seat array as a sub-object content block - the same framing the
    ///     AbilitySystemComponent uses, and the piece that tells a client it is genuinely SEATED.
    ///
    ///     Only `Player`, and only on the seats that changed hands. The array's other thirty members
    ///     per seat stay exactly as the client's Blueprint configured them, because the element count
    ///     on the wire matches the count it already has and `PrepReceivedArray` then resizes nothing
    ///     - see ERepPropertyKind.StructArray for why that is the whole trick, and
    ///     UFortVehicleSeatComponent for why the seats have to be baked rather than invented.
    ///
    ///     RELIABLE, unlike the actor property update it rides beside. Seating changes on an event
    ///     and never repeats: a lost "you are in seat 0" is not corrected by the next tick, it leaves
    ///     a player who is driving a vehicle their own client thinks is empty - which is precisely
    ///     the state that made exiting impossible in the first place.
    ///
    ///     VEHICLE_SEATS=0 turns it off. The escape hatch is here because this is the first array of
    ///     structs this project has ever written, and a wrong handle inside one is not a wrong value:
    ///     the client's ReceiveProperties fails and the CONNECTION closes. Driving already works
    ///     without this, so it must stay possible to get back to that.
    /// </summary>
    private unsafe bool ReplicateVehicleSeats() {
        if (Connection == null || Actor is not AFortAthenaVehicle vehicle) return false;
        if (Environment.GetEnvironmentVariable("VEHICLE_SEATS") is "0") return false;

        // Nothing to say until someone has actually been seated: creating the component early would
        // export a NetGUID for it and change nothing on the client.
        if (vehicle.SeatComponent is not { } seats) return false;

        var layout = NativeRepLayouts.VehicleSeatComponent;
        var changed = layout.CompareProperties(seats, SeatProperties, _seatShadowState);
        if (changed.Count == 0) return false;

        var changedNames = changed.Select(entry => entry.Name).ToHashSet();

        using var payload = new FNetBitWriter(Connection.PackageMap, 256);
        layout.WriteChangedProperties(payload, seats, changedNames);

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        WriteContentBlockHeader(seats, bunch, hasRepLayout: true);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        var guid = ((UPackageMapClient) Connection.PackageMap!).GuidCache!.GetNetGUID(seats);
        Console.WriteLine($"ReplicateVehicleSeats: ChIndex={ChIndex} Actor={vehicle.GetFName()} " +
                          $"seatGuid={guid} slots=[{string.Join(", ", seats.PlayerSlots.Select(
                              (slot, index) => $"{index}:{slot.Player?.GetFName().ToString() ?? "-"}"))}] " +
                          $"numPayloadBits={numPayloadBits} " +
                          $"payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        SendBunch(bunch, false);

        FRepLayout.CommitShadowState(changed, _seatShadowState);
        return true;
    }

    /// <summary>The one property of the seat component this server ever sends.</summary>
    private static readonly HashSet<string> SeatProperties = new() { "PlayerSlots" };

    /// <summary>
    ///     Sends the PlayerState's MovementSet attribute values as a sub-object content block - the
    ///     same framing the AbilitySystemComponent uses, one sibling over.
    ///
    ///     Only SpeedMultiplier is sent. Every other attribute already has a healthy value on the
    ///     client (its log prints WalkSpeed 200, RunSpeed 410), so re-sending them would be noise;
    ///     SpeedMultiplier is the one that never appears there at all.
    /// </summary>
    /// <summary>
    ///     Sends a placed building's health attribute set as a sub-object content block - the same
    ///     framing the AbilitySystemComponent and MovementSet use, and the thing a client needs
    ///     before it can put a health bar over a piece.
    ///
    ///     ORDERING MATTERS HERE. The building's own handle 19
    ///     (ReplicatedBuildingAttributeSet) is an ObjectRef naming this set, and this project's
    ///     known hazard is that an ObjectRef written before its target has a NetGUID arrives null
    ///     and STAYS null, because a property matching the shadow is never reconsidered. Two things
    ///     keep that from happening: this runs from ReplicateActorUpdate BEFORE the actor's own
    ///     bunch is written, so WriteContentBlockHeader has already exported the set's GUID by the
    ///     time handle 19 is serialised; and on the first send it re-dirties handle 19 outright, to
    ///     cover the case where the channel's initial open push already sent it as null.
    /// </summary>
    private unsafe bool ReplicateBuildingAttributeSet() {
        if (Connection == null || Actor is not ABuildingActor { BuildingAttributeSet: { } attributeSet } buildingActor) return false;

        // NOT ON A PLAYER-BUILT PIECE, by default, and that default comes from the reference capture
        // rather than from taste.
        //
        // A real server carries a building's health on the building's OWN channel, as
        // MinimalReplicationProxy.Health/MaxHealth - the client's RepLayout log names them as
        // Int16Property, which is exactly what NativeRepLayouts declares at handles 61/62. The whole
        // PR3.0 capture contains exactly ONE BuildingAttributeSet sub-object, and it belongs to
        // `BGA_Athena_Ostrich_Drop_C`, a level building gameplay actor. **Not one PBWA_* has one.**
        //
        // So on a player-built piece this is a SECOND source of health that the reference server
        // does not send, arriving as floats beside the Int16 the client is already reading - and a
        // client given two sources for one number is a good candidate for health that "looks off".
        //
        // Kept behind a switch rather than deleted, because the capture cannot actually prove the
        // negative: every PBWA in it was replaced within milliseconds while the player edited
        // stairs, so NONE of them was ever damaged, and "no health traffic" there is as consistent
        // with "nothing to send" as with "never sent". BUILDING_ATTR_SET=1 restores the old
        // behaviour for a side-by-side, which is the only way to settle it.
        if (buildingActor.bPlayerPlaced && Environment.GetEnvironmentVariable("BUILDING_ATTR_SET") != "1") {
            return false;
        }

        var layout = NativeRepLayouts.BuildingActorSet;
        var changed = layout.CompareProperties(attributeSet, BuildingAttributeSetProperties, _buildingAttrSetShadowState);
        if (changed.Count == 0) return false;

        var changedNames = changed.Select(entry => entry.Name).ToHashSet();

        using var payload = new FNetBitWriter(Connection.PackageMap, 128);
        layout.WriteChangedProperties(payload, attributeSet, changedNames);

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        WriteContentBlockHeader(attributeSet, bunch, hasRepLayout: true);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        Console.WriteLine($"ReplicateBuildingAttributeSet: ChIndex={ChIndex} Actor={Actor.GetFName()} " +
                          $"Health={attributeSet.Health}/{attributeSet.MaxHealth} " +
                          $"changed=[{string.Join(", ", changedNames)}] numPayloadBits={numPayloadBits}");

        SendBunch(bunch, false);
        FRepLayout.CommitShadowState(changed, _buildingAttrSetShadowState);

        if (!_sentBuildingAttributeSet) {
            _sentBuildingAttributeSet = true;
            MarkPropertyDirty("ReplicatedBuildingAttributeSet");
        }

        return true;
    }

    /// <summary>
    ///     Health and MaxHealth, four leaves each - the value pair plus the unclamped pair, matched
    ///     to the player's health set after a live client showed the unclamped half staying at 0.
    ///     The other five leaves of each attribute are clamp configuration the client owns.
    /// </summary>
    private static readonly HashSet<string> BuildingAttributeSetProperties = new() {
        "Health.BaseValue", "Health.CurrentValue",
        "Health.UnclampedBaseValue", "Health.UnclampedCurrentValue",
        "MaxHealth.BaseValue", "MaxHealth.CurrentValue",
        "MaxHealth.UnclampedBaseValue", "MaxHealth.UnclampedCurrentValue"
    };

    private readonly Dictionary<string, object?> _buildingAttrSetShadowState = new();
    private bool _sentBuildingAttributeSet;

    private unsafe bool ReplicateMovementSet() {
        if (Connection == null || Actor is not APlayerState { MovementSet: { } movementSet }) return false;

        var layout = NativeRepLayouts.MovementSet;
        var changed = layout.CompareProperties(movementSet, MovementSetProperties, _movementSetShadowState);
        if (changed.Count == 0) return false;

        var changedNames = changed.Select(entry => entry.Name).ToHashSet();

        using var payload = new FNetBitWriter(Connection.PackageMap, 128);
        layout.WriteChangedProperties(payload, movementSet, changedNames);

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        WriteContentBlockHeader(movementSet, bunch, hasRepLayout: true);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        Console.WriteLine($"ReplicateMovementSet: ChIndex={ChIndex} RunSpeed={movementSet.RunSpeed} " +
                          $"changed=[{string.Join(", ", changedNames)}] numPayloadBits={numPayloadBits} " +
                          $"payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        SendBunch(bunch, false);
        FRepLayout.CommitShadowState(changed, _movementSetShadowState);
        return true;
    }

    /// <summary>
    ///     The movement attributes this server sends. MaxWalkSpeed reads 0 on the client
    ///     (confirmed with `GetAll FortMovementComp_CharacterAthena MaxWalkSpeed`), and Fortnite
    ///     drives it from these - so with nothing feeding them the character has no speed at all.
    ///     Base and Current always travel together.
    /// </summary>
    private static readonly HashSet<string> MovementSetProperties = new() {
        "WalkSpeed.BaseValue", "WalkSpeed.CurrentValue",
        "RunSpeed.BaseValue", "RunSpeed.CurrentValue",
        "SprintSpeed.BaseValue", "SprintSpeed.CurrentValue",
        "CrouchedRunSpeed.BaseValue", "CrouchedRunSpeed.CurrentValue",
        "CrouchedSprintSpeed.BaseValue", "CrouchedSprintSpeed.CurrentValue",
        "BackwardSpeedMultiplier.BaseValue", "BackwardSpeedMultiplier.CurrentValue",
        "SpeedMultiplier.BaseValue", "SpeedMultiplier.CurrentValue"
    };

    private readonly Dictionary<string, object?> _movementSetShadowState = new();

    /// <summary>
    ///     The stamina set, sent exactly like the movement set one sibling over.
    ///
    ///     Stamina is not cosmetic: a Fortnite jump spends it, so a client reading zero refuses
    ///     to jump at all - locally, before any RPC, which is why the server saw a client that
    ///     crouched and fired and harvested but never once set the jump flag in a move.
    /// </summary>
    private unsafe bool ReplicatePlayerAttrSet() {
        if (Connection == null || Actor is not APlayerState { PlayerAttrSet: { } attrSet }) return false;

        var layout = NativeRepLayouts.PlayerAttrSet;
        var changed = layout.CompareProperties(attrSet, PlayerAttrSetProperties, _playerAttrSetShadowState);
        if (changed.Count == 0) return false;

        var changedNames = changed.Select(entry => entry.Name).ToHashSet();

        using var payload = new FNetBitWriter(Connection.PackageMap, 128);
        layout.WriteChangedProperties(payload, attrSet, changedNames);

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        WriteContentBlockHeader(attrSet, bunch, hasRepLayout: true);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        Console.WriteLine($"ReplicatePlayerAttrSet: ChIndex={ChIndex} Stamina={attrSet.Stamina}/{attrSet.MaxStamina} " +
                          $"changed=[{string.Join(", ", changedNames)}] numPayloadBits={numPayloadBits} " +
                          $"payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        SendBunch(bunch, false);
        FRepLayout.CommitShadowState(changed, _playerAttrSetShadowState);
        return true;
    }

    /// <summary>Stamina and its regen, plus the cap the client clamps against.</summary>
    private static readonly HashSet<string> PlayerAttrSetProperties = new() {
        "Stamina.BaseValue", "Stamina.CurrentValue",
        "StaminaRegenRate.BaseValue", "StaminaRegenRate.CurrentValue",
        "StaminaRegenDelay.BaseValue", "StaminaRegenDelay.CurrentValue",
        "MaxStamina.BaseValue", "MaxStamina.CurrentValue"
    };

    private readonly Dictionary<string, object?> _playerAttrSetShadowState = new();

    /// <summary>
    ///     The player's health set, sent exactly like the two sets above it.
    ///
    ///     This is the first attribute set on the PlayerState whose value CHANGES during a match -
    ///     stamina and the speeds are pushed once and never move - which makes it the first real
    ///     test of whether a GAS attribute arriving over the wire drives anything on the HUD. That
    ///     question is the open one from the building health-bar work: a building's set is this same
    ///     class at these same handles, and its bar never updates.
    /// </summary>
    private unsafe bool ReplicateHealthSet() {
        if (Connection == null || Actor is not APlayerState { HealthSet: { } healthSet }) return false;

        var layout = NativeRepLayouts.HealthSet;
        var changed = layout.CompareProperties(healthSet, HealthSetProperties, _healthSetShadowState);
        if (changed.Count == 0) return false;

        var changedNames = changed.Select(entry => entry.Name).ToHashSet();

        using var payload = new FNetBitWriter(Connection.PackageMap, 128);
        layout.WriteChangedProperties(payload, healthSet, changedNames);

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        WriteContentBlockHeader(healthSet, bunch, hasRepLayout: true);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        Console.WriteLine($"ReplicateHealthSet: ChIndex={ChIndex} Health={healthSet.Health}/{healthSet.MaxHealth} " +
                          $"Shield={healthSet.CurrentShield}/{healthSet.Shield} " +
                          $"changed=[{string.Join(", ", changedNames)}] numPayloadBits={numPayloadBits}");

        SendBunch(bunch, false);
        FRepLayout.CommitShadowState(changed, _healthSetShadowState);
        return true;
    }

    /// <summary>
    ///     Health and shield, current and max - four leaves each, not two. The unclamped pair is
    ///     sent because a live client showed it staying at 0 while BaseValue/CurrentValue carried
    ///     the real damage, which no correctly-built Fortnite attribute ever looks like; see
    ///     NativeRepLayouts.BuildHealthSetProps for the GetAll output that measured it. The
    ///     remaining five leaves of each attribute are clamp configuration the client owns.
    /// </summary>
    private static readonly HashSet<string> HealthSetProperties = new() {
        "Health.BaseValue", "Health.CurrentValue",
        "Health.UnclampedBaseValue", "Health.UnclampedCurrentValue",
        "MaxHealth.BaseValue", "MaxHealth.CurrentValue",
        "MaxHealth.UnclampedBaseValue", "MaxHealth.UnclampedCurrentValue",
        "CurrentShield.BaseValue", "CurrentShield.CurrentValue",
        "CurrentShield.UnclampedBaseValue", "CurrentShield.UnclampedCurrentValue",
        "Shield.BaseValue", "Shield.CurrentValue",
        "Shield.UnclampedBaseValue", "Shield.UnclampedCurrentValue"
    };

    private readonly Dictionary<string, object?> _healthSetShadowState = new();
    /// <summary>
    ///     The AbilitySystemComponent properties this server sends. Deliberately just the two links
    ///     that make the component usable - everything else on it is either server bookkeeping or a
    ///     subsystem this project does not have.
    /// </summary>
    private static readonly HashSet<string> AbilitySystemProperties = new() {
        "SpawnedAttributes", "OwnerActor", "AvatarActor"
    };

    /// <summary>The component's own shadow buffer, kept apart from the actor's.</summary>
    private readonly Dictionary<string, object?> _ascShadowState = new();

    /// <summary>
    ///     Turns a written fast-array delta into a SENT one - or throws it away so the next pass
    ///     writes it again.
    ///
    ///     A fast array has no redundancy and no resync point: the next delta is computed against
    ///     what this connection is believed to hold, so a delta that was serialised but never
    ///     delivered leaves the connection stuck at that version for the rest of the match. Both of
    ///     SendBunch's failure paths return First == INDEX_NONE (a bunch too large to construct, and
    ///     a reliable-buffer overflow, which also closes the connection), so this is the whole test.
    ///
    ///     See FNetFastTArrayBaseState.StagePending for the bug this exists to prevent.
    /// </summary>
    private void CommitFastArrayBaseStates(FPacketIdRange sent) {
        var delivered = sent.First != UnrealConstants.IndexNone;

        foreach (var state in _fastArrayBaseStates.Values) {
            if (delivered) state.Commit();
            else state.Discard();
        }

        if (!delivered) {
            Console.WriteLine($"UActorChannel: a custom-delta bunch for {Actor?.GetFName()} on ChIndex={ChIndex} " +
                              "was NOT sent - the fast-array base states were rolled back so the next pass resends them.");
        }
    }

    private FNetFastTArrayBaseState BaseStateFor(string fieldName) {
        if (!_fastArrayBaseStates.TryGetValue(fieldName, out var state)) {
            state = new FNetFastTArrayBaseState();
            _fastArrayBaseStates[fieldName] = state;
        }

        return state;
    }

    /// <summary>
    ///     Writes every custom delta field whose array has moved since this connection last saw it.
    ///     Called both from the initial burst (where every base state is empty, so everything is
    ///     "changed") and from the per-tick pass - the delta computation is identical, which is the
    ///     point: the first send is just a delta against nothing.
    ///
    ///     Returns true when at least one field was written.
    /// </summary>
    private unsafe bool WriteCustomDeltaProperties(FNetBitWriter payload) {
        if (Connection == null) return false;

        var wroteSomething = false;

        switch (Actor) {
            case AFortInventory inventory:
                wroteSomething |= WriteCustomDeltaField(payload, "Inventory", fieldPayload =>
                    FFastArraySerializerWriter.WriteDelta(fieldPayload, inventory.Inventory, BaseStateFor("Inventory"),
                        FFastArraySerializerWriter.WriteItemEntry,
                        FFastArraySerializerWriter.WriteItemEntryDeltaStruct));
                break;

            // AFortGameStateAthena::GameMemberInfoArray - the team/squad roster the client looks up
            // by unique id. See FFastArraySerializerWriter.WriteGameMemberInfo.
            //
            // PLAIN FORMAT, deliberately, and it is the one array here that must stay that way.
            // FGameMemberInfoArray never calls SetDeltaSerializationEnabled, so the client's copy
            // has no HasDeltaBeenRequested flag and cannot take the delta-struct path no matter what
            // bit we write (NetSerialization.h:1235). A 10.40 client's own log settles it: every
            // struct that ever takes that path names itself in a
            // "FastArrayDeltaSerialize_DeltaSerializeStruct for <Struct>" line, and GameMemberInfo
            // appears only in the plain "FastArrayDeltaSerialize for GameMemberInfo" one - 6619
            // times, with zero struct-path lines. Claiming the struct format here would have it read
            // as plain and corrupt the roster.
            case AGameState gameState when gameState.GameMemberInfoArray.Count > 0:
                wroteSomething |= WriteCustomDeltaField(payload, "GameMemberInfoArray", fieldPayload =>
                    FFastArraySerializerWriter.WriteDelta(fieldPayload, gameState.GameMemberInfoArray, BaseStateFor("GameMemberInfoArray"),
                        FFastArraySerializerWriter.WriteGameMemberInfo));
                break;

            // NOTE: AFortGameStateAthena::CurrentPlaylistInfo is deliberately NOT sent as a custom
            // delta field. Handle 155 in the ordinary property stream already delivers BasePlaylist
            // (live-confirmed: the client logs OnRep_CurrentPlaylistInfo -> LoadCurrentPlaylistData
            // -> OnPlaylistDataLoadCompleted for Playlist_DefaultSolo), and a bare
            // FFastArraySerializer header on the same property makes the client log
            // "NetDeltaSerialize - Mismatch read" and permanently mark the field bIncompatible for
            // the connection (DataReplication.cpp:1058-1068). Whatever Fortnite's hand-written
            // FPlaylistPropertyArray::NetDeltaSerialize reads, it is not the stock 4-int32 header -
            // three live runs pinned the reader to consuming fewer bits than that. See
            // FFastArraySerializerWriter.WritePlaylistPropertyArrayDelta for the measurements.
        }

        return wroteSomething;
    }

    /// <summary>
    ///     Writes one Custom Delta property as a ClassNetCache-indexed field. Identical framing to
    ///     SendRpc's below, which is the point: real UE routes custom delta properties and RPCs
    ///     through the very same UActorChannel::WriteFieldHeaderAndPayload.
    /// </summary>
    /// <summary>
    ///     Wraps one custom delta payload in its ClassNetCache field header. <paramref name="writeDelta"/>
    ///     returns false when the array has not moved for this connection, in which case NOTHING is
    ///     written - an unchanged fast array must be absent from the bunch, not present as an empty
    ///     header, or the client re-runs its whole PostReceiveCleanup and RepNotify for no reason.
    /// </summary>
    private bool WriteCustomDeltaField(FNetBitWriter payload, string fieldName, Func<FNetBitWriter, bool> writeDelta) =>
        WriteCustomDeltaField(payload, NativeClassNetCache.Get(Actor!), fieldName, writeDelta);

    private unsafe bool WriteCustomDeltaField(FNetBitWriter payload, FClassNetCache classCache, string fieldName, Func<FNetBitWriter, bool> writeDelta) {
        var field = classCache.GetFromName(fieldName);
        if (field == null) {
            Console.WriteLine($"WriteCustomDeltaProperties: '{fieldName}' not found in {Actor!.GetType().Name}'s ClassNetCache, not sending");
            return false;
        }

        using var fieldPayload = new FNetBitWriter(Connection!.PackageMap, 256);
        if (!writeDelta(fieldPayload)) return false;

        var fieldIndex = (uint) field.FieldNetIndex;
        payload.SerializeInt(&fieldIndex, (uint) (classCache.GetMaxIndex() + 1));
        var fieldBits = (uint) fieldPayload.GetNumBits();
        payload.SerializeIntPacked(&fieldBits);
        var fieldData = fieldPayload.GetData();
        fixed (byte* p = fieldData) payload.SerializeBits(p, fieldPayload.GetNumBits());

        Console.WriteLine($"WriteCustomDeltaProperties: {fieldName} fieldIndex={fieldIndex} maxIndex={classCache.GetMaxIndex()} fieldBits={fieldBits}");

        return true;
    }

    /// <summary>Last weapon this CONNECTION was told the pawn is holding - see ReplicateEquippedWeapon.</summary>
    private AFortWeapon? _equipNotifiedWeapon;

    private bool _hasEquipNotified;

    /// <summary>
    ///     AFortPawn::ClientInternalEquipWeapon(AFortWeapon*), sent whenever this pawn's CurrentWeapon
    ///     CHANGES - which is what it always should have been keyed on.
    ///
    ///     IT USED TO BE SENT FROM ONE PLACE ONLY: the moment that weapon's own actor channel opened
    ///     (UNetDriver.OpenChannelsForNewlyRelevantActors). That works for a weapon being equipped for
    ///     the first time and does nothing at all for a RE-equip, because the channel is already open
    ///     and never opens again. So switching back to a weapon the client had already seen sent
    ///     NOTHING - and the code's own note from 2026-08-29 says exactly what that costs:
    ///
    ///         "leaving a building tool for the pickaxe/a gun left the client stuck showing the ghost
    ///          AND the build-mode arm pose forever ... skipping it for a normal weapon left the
    ///          client with no signal to tear down the OLD equip's state, only to raise the new one."
    ///
    ///     That is the build ghost at spawn. The client picks a building tool by itself on joining,
    ///     the server equips it, ClientActivateSlot then sends the client back to the pickaxe - and
    ///     the pickaxe's channel had opened long before, so nothing told the client to tear the
    ///     building tool's state down. Ghost on screen, pickaxe in hand.
    ///
    ///     Per CONNECTION, not per weapon, which the old flag could not be: a flag on the weapon is
    ///     consumed by whichever connection reaches it first, so a second client would never be told.
    ///
    ///     Waits for the weapon's own channel: the parameter is an object reference, and sending it
    ///     before that actor has a NetGUID makes the client log "Unable to resolve RPC parameter ...
    ///     Parameter Weap" and drop the call. Returning without recording anything means the next
    ///     tick simply tries again.
    /// </summary>
    private bool ReplicateEquippedWeapon() {
        if (Actor is not APawn pawn || Connection == null) return false;

        var weapon = pawn.CurrentWeapon;
        if (_hasEquipNotified && ReferenceEquals(_equipNotifiedWeapon, weapon)) return false;

        // Nothing held: there is no ClientInternalEquipWeapon(null) to send, so just remember it so
        // the next real equip counts as a change.
        if (weapon == null) {
            _equipNotifiedWeapon = null;
            _hasEquipNotified = true;
            return false;
        }

        if (Connection.FindActorChannel(weapon) == null) return false;

        _equipNotifiedWeapon = weapon;
        _hasEquipNotified = true;

        // SENT FOR EVERY EQUIP, INCLUDING ONES THE CLIENT ASKED FOR ITSELF.
        //
        // It was briefly suppressed for client-initiated equips, on a misreading of a log: a run of
        // wall -> pickaxe -> wall -> pickaxe was taken for the server and client fighting over the
        // focused quickbar slot, when the tail of that same log (assault rifle, then the edit tool)
        // shows it was a person cycling their quickbar by hand. Suppressing it removed the join-time
        // ghost and ALSO removed building entirely - because this RPC is what RAISES the build
        // preview when a building tool is equipped, not just what tears it down when one is put
        // away. Both directions need it.
        SendObjectRpc("ClientInternalEquipWeapon", weapon);
        Console.WriteLine($"UActorChannel.ReplicateEquippedWeapon: sent ClientInternalEquipWeapon({weapon.GetFName()}) " +
                          $"on ChIndex={ChIndex}");

        return true;
    }

    /// <summary>Which RPC names RPC_DUMP has already dumped - one sample each is the point.</summary>
    private static readonly HashSet<string> DumpedRpcPayloads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many raw dumps each RPC has already produced - see DumpRawRpcPayload's cap.</summary>
    private static readonly Dictionary<string, int> _rpcDumpCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     RPC_DUMP=&lt;Name&gt;[,&lt;Name&gt;...] - print one raw sample of a named incoming RPC's payload as
    ///     hex, before any declared parameter layout touches it, and leave the read position exactly
    ///     where it found it.
    ///
    ///     The point is to stop guessing. A parameter layout derived from an SDK header is a
    ///     hypothesis, and a wrong one about a length-prefixed field does not produce a wrong value,
    ///     it produces an overrun - which says "wrong" but not "wrong how". A hex sample says how.
    ///
    ///     Bits, not just bytes: the payload starts at an arbitrary bit offset inside the bunch, so
    ///     the dump is taken by re-reading bits from fieldStart and packing them LSB-first, which is
    ///     the order UE writes them in. A dump that has been byte-aligned by accident is a dump of
    ///     something else - see [[afortonlinebeacon-status]] Round 141, where exactly that mistake
    ///     made a whole packet scan come back empty.
    /// </summary>
    private void DumpRawRpcPayload(FInBunch bunch, string fieldName, long fieldStart, long fieldEnd,
                                   string? reason = null) {
        // Asked for by name, OR taken automatically because the declared layout just failed on it.
        // The automatic case is the important one: a dump is never more wanted than at the moment a
        // decode goes wrong, and making that cost a second run with an env var set is a wasted round
        // trip - the evidence should be captured the first time the problem happens.
        if (reason == null) {
            if (Environment.GetEnvironmentVariable("RPC_DUMP") is not { Length: > 0 } wanted) return;
            if (!wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Contains(fieldName, StringComparer.OrdinalIgnoreCase)) return;
        }

        // Normally once each - a second identical dump teaches nothing. The exception is an RPC
        // whose layout is still unknown: one sample cannot distinguish a FIXED-width encoding from a
        // packed one that happened to land on the same size, and it is the second sample, taken
        // somewhere else, that tells them apart. Four is enough for that and still bounded.
        _rpcDumpCounts.TryGetValue(fieldName, out var already);
        if (already >= (reason == null ? 1 : 4)) return;
        _rpcDumpCounts[fieldName] = already + 1;
        DumpedRpcPayloads.Add(fieldName);

        var numBits = (int) Math.Max(0, fieldEnd - fieldStart);
        var resumeAt = bunch.Pos;

        var bytes = new byte[(numBits + 7) / 8];
        bunch.Pos = fieldStart;

        for (var i = 0; i < numBits; i++) {
            if (bunch.ReadBit()) bytes[i / 8] |= (byte) (1 << (i % 8));
        }

        bunch.Pos = resumeAt;

        Console.WriteLine($"UActorChannel.DumpRawRpcPayload: {fieldName} on ChIndex={ChIndex} - {numBits} bits" +
                          (reason == null ? "" : $" ({reason})") +
                          $"{Environment.NewLine}    LSB-first hex={Convert.ToHexString(bytes)}");

        foreach (var text in FindPrintableRuns(bytes, numBits)) Console.WriteLine($"    {text}");
    }

    /// <summary>
    ///     Every readable ASCII run in a raw payload, WITH THE BIT OFFSET IT STARTS AT, searched at
    ///     all eight bit alignments.
    ///
    ///     A plain byte-aligned ASCII column is close to useless on a bit-packed payload and this is
    ///     not a hypothetical complaint: the first level-visibility dump rendered as
    ///     "....BU...r..V..D.V..." and looked like noise, when in fact it contained two clean paths
    ///     starting at bits 36 and 557 - neither a multiple of eight. Finding them by hand took a
    ///     separate script. Doing the shift search here means the next payload gives up its strings
    ///     in the log line itself.
    ///
    ///     The OFFSET is the valuable half of the output. Knowing a string starts at bit 36 is what
    ///     lets you work out what the 36 bits in front of it are, which is the actual question.
    /// </summary>
    private static IEnumerable<string> FindPrintableRuns(byte[] bytes, int numBits, int minLength = 6) {
        bool Bit(int i) => (bytes[i / 8] & (1 << (i % 8))) != 0;

        int ByteAt(int bitPos) {
            var value = 0;
            for (var k = 0; k < 8; k++)
                if (Bit(bitPos + k)) value |= 1 << k;
            return value;
        }

        var run = new System.Text.StringBuilder();
        var runStart = 0;

        for (var start = 0; start + 8 <= numBits; start++) {
            var c = ByteAt(start);

            if (c is >= 0x20 and < 0x7F) {
                if (run.Length == 0) runStart = start;
                run.Append((char) c);
                // A printable byte found at bit N is also findable at N+8, N+16 ... so only advance
                // one byte at a time WITHIN a run; the outer loop still tries every alignment.
                start += 7;
                continue;
            }

            if (run.Length >= minLength) yield return $"bit {runStart,4}: \"{run}\"";
            run.Clear();
        }

        if (run.Length >= minLength) yield return $"bit {runStart,4}: \"{run}\"";
    }

    public void SendClientRestart(APawn pawn) => SendPawnRpc("ClientRestart", pawn);

    /// <summary>
    ///     AFortPlayerController::ClientActivateSlot(EFortQuickBars InQuickBar, int32 Slot,
    ///     float ActivateDelay, bool bUpdatePreviousFocusedSlot, bool bForceExecution) - tells the
    ///     client WHICH QUICKBAR SLOT to hold.
    ///
    ///     WHY IT IS NEEDED. Items go out with OrderIndex = -1 ("no opinion, place it yourself"),
    ///     which is what a real server sends - so the client builds its own quickbars and then picks
    ///     a slot to start on. Left to itself this client picks a BUILDING TOOL: the server log shows
    ///     it asking, unprompted, moments after joining -
    ///
    ///         ServerExecuteInventoryItem ItemGuid=b347030d... ('BuildingItemData_Wall'),
    ///                                    currently holding 69d18c0c... (the pickaxe)
    ///
    ///     - and the server duly equips it, which is why a building GHOST is on screen at spawn while
    ///     the player is holding, and can swing, the pickaxe. The server was not doing anything
    ///     wrong; nothing had ever told the client which slot to be on.
    ///
    ///     ALL FIVE PARAMETERS GO OUT AT THEIR DEFAULTS, and that is not laziness - it is what the
    ///     real server sends. The PR3.0 capture's ClientActivateSlot (field 109 on the
    ///     PlayerController, packet #8050) is FIVE BITS of payload, which is exactly one bit per
    ///     parameter with every one of them zero. That encoding is the same one
    ///     SendClientReportDamagedResourceBuilding uses and had verified: a presence bit before each
    ///     non-bool parameter, and for a bool the single bit IS the value. Defaults mean
    ///     EFortQuickBars 0 (Primary) and Slot 0 - the pickaxe.
    /// </summary>
    public void SendClientActivateSlot() =>
        SendRpc("ClientActivateSlot", writer => {
            writer.WriteBit(false); // InQuickBar absent -> EFortQuickBars::Primary
            writer.WriteBit(false); // Slot absent -> 0
            writer.WriteBit(false); // ActivateDelay absent -> 0.0f
            writer.WriteBit(false); // bUpdatePreviousFocusedSlot
            writer.WriteBit(false); // bForceExecution
        });

    /// <summary>
    ///     APlayerController::ClientRetryClientRestart - the server's half of the possession retry
    ///     handshake, and the answer to a question the client had been asking us over and over with
    ///     no reply.
    ///
    ///     Real UE (PlayerController.cpp:2684-2701, 672-706): when the client's
    ///     ClientRestart_Implementation cannot finish - on this build it bails with
    ///     "ClientRestart_Implementation failed because Quickbars are invalid, waiting to finish
    ///     restarting" - it never acknowledges the pawn, and instead calls
    ///     ServerCheckClientPossessionReliable. The server answers:
    ///
    ///         void APlayerController::ServerCheckClientPossession_Implementation() {
    ///             if (AcknowledgedPawn != GetPawn()) {
    ///                 LastRetryPlayerTime = ForceRetryClientRestartTime;   // bypass the throttle
    ///                 SafeRetryClientRestart();                            // ClientRetryClientRestart(GetPawn())
    ///             }
    ///         }
    ///
    ///     and ClientRetryClientRestart_Implementation does strictly MORE than re-issue the call -
    ///     it relinks pawn and controller client-side first:
    ///
    ///         SetPawn(NewPawn); NewPawn->Controller = this; NewPawn->OnRep_Controller();
    ///         ClientRestart(GetPawn());
    ///
    ///     So this is not merely a retry: OnRep_Controller is client-side state we had no other way
    ///     to trigger. Until now this project sent ClientRestart exactly once and ignored every
    ///     ServerCheckClientPossession(Reliable) that came back, so the loop the engine designed to
    ///     converge here could never run.
    ///
    ///     Same single-APawn-parameter shape as ClientRestart, so it shares SendPawnRpc.
    /// </summary>
    public void SendClientRetryClientRestart(APawn pawn) => SendPawnRpc("ClientRetryClientRestart", pawn);

    /// <summary>
    ///     Server-side half of the possession handshake, driven by name rather than through
    ///     NativeRpcHandlers because every branch needs the channel to reply on. Called for every
    ///     field, whether or not it decoded into a registered RPC - the field's own declared bit
    ///     count resyncs the reader either way, so the parameters do not have to be understood.
    ///
    ///     The important one is ServerSetSpectatorLocation. It looks like pure spectator noise, but
    ///     APlayerController::ServerSetSpectatorLocation_Implementation (PlayerController.cpp:2745)
    ///     is where a real server keeps retrying possession:
    ///
    ///         else if (World->TimeSeconds != LastSpectatorStateSynchTime) {
    ///             if (AcknowledgedPawn != GetPawn()) SafeRetryClientRestart();
    ///             else { ClientGotoState(GetStateName()); ClientSetViewTarget(GetViewTarget()); }
    ///         }
    ///
    ///     That matters because ClientRestart can legitimately fail the first few times and the
    ///     client then goes quiet - it only sends ServerCheckClientPossession while it still thinks
    ///     it has a pawn. Observed here: the client tried twice, both times before its character
    ///     customization had finished loading, and nothing ever retried afterwards even though the
    ///     loader completed milliseconds later. The client keeps sending ServerSetSpectatorLocation
    ///     the whole time, so that is the heartbeat a server is meant to retry on.
    ///
    ///     (The else branch - ClientGotoState/ClientSetViewTarget - is not implemented yet; it is
    ///     what pulls the client out of spectator state once it HAS acknowledged.)
    /// </summary>
    private void HandlePossessionRpc(string fieldName) {
        if (Actor is not APlayerController pc) return;

        switch (fieldName) {
            case "ServerAcknowledgePossession":
                // The APawn* parameter is deliberately not decoded - the only pawn this controller
                // can be acknowledging is its own.
                pc.AcknowledgedPawn = pc.Pawn;
                Console.WriteLine($"HandlePossessionRpc: ServerAcknowledgePossession - client acknowledged {pc.Pawn?.GetFName()}");
                break;

            case "ServerCheckClientPossession":
            case "ServerCheckClientPossessionReliable":
            case "ServerSetSpectatorLocation":
                if (pc.AcknowledgedPawn != pc.Pawn) SafeRetryClientRestart();
                break;
        }
    }

    /// <summary>APlayerController::RetryClientRestartThrottleTime.</summary>
    private const float RetryClientRestartThrottleTime = 0.5f;

    private float _LastRetryPlayerTime = float.NegativeInfinity;

    /// <summary>
    ///     Answers ServerCheckClientPossession / ...Reliable, mirroring
    ///     APlayerController::SafeRetryClientRestart - throttle included.
    ///
    ///     This was first written WITHOUT the throttle, reasoning that real UE deliberately bypasses
    ///     it here (ServerCheckClientPossession_Implementation stamps LastRetryPlayerTime with
    ///     ForceRetryClientRestartTime first, commenting "Client already throttles their call to
    ///     this function, so respond immediately"). That reasoning was wrong in this context and the
    ///     result was a self-amplifying loop: the client's ClientRestart fails, it immediately calls
    ///     ServerCheckClientPossessionReliable from the failure path (not the throttled
    ///     SafeServerCheckClientPossession path), we immediately answer with another RELIABLE
    ///     ClientRetryClientRestart, and round it goes at frame rate. NumOutRec on this channel then
    ///     climbs about 60/second until it hits UNetConnection.ReliableBuffer (256), at which point
    ///     UChannel.SendBunch closes the connection - roughly four seconds in.
    ///
    ///     UE never sees this because on a healthy server the loop converges after one or two
    ///     rounds. The client's own throttle cannot save us, because it is the FAILURE path doing
    ///     the calling. So the brake has to live here; 0.5s is UE's own constant.
    /// </summary>
    public void SafeRetryClientRestart() {
        if (Actor is not APlayerController pc) return;
        if (pc.Pawn == null) {
            Console.WriteLine("SafeRetryClientRestart: PlayerController has no Pawn, not retrying");
            return;
        }

        // UWorld.TimeSeconds only became a real clock when UWorld.Tick started advancing it; if
        // GetWorld() is ever null here the throttle would jam at 0 and this would fire exactly once,
        // so fall back to a source that always moves.
        var now = Actor.GetWorld()?.TimeSeconds ?? (Environment.TickCount64 / 1000f);
        if (now - _LastRetryPlayerTime <= RetryClientRestartThrottleTime) return;
        _LastRetryPlayerTime = now;

        SendClientRetryClientRestart(pc.Pawn);
    }

    /// <summary>UCharacterMovementComponent::NetworkMinTimeBetweenClientAckGoodMoves.</summary>
    private const float NetworkMinTimeBetweenClientAckGoodMoves = 0.10f;

    private bool _LoggedFirstGoodMoveAck;

    /// <summary>
    ///     Port of the bAckGoodMove branch of UCharacterMovementComponent::SendClientAdjustment.
    ///     Real UE drives this once per ServerReplicateActors pass ("we do this here so that we send
    ///     a maximum of one per packet to that client"); this project has no such pass, so it runs
    ///     off the receive path instead - with the same 0.10s throttle, which dominates either way.
    ///
    ///     ACharacter::ClientAckGoodMove is declared UFUNCTION(unreliable, client), hence reliable:
    ///     false. One ack frees every client saved move up to its timestamp, so 10Hz is plenty for a
    ///     client moving at 60Hz.
    /// </summary>
    private void SendClientAckGoodMove() {
        if (Actor is not APawn pawn) return;
        if (pawn.PendingAckGoodMoveTimeStamp <= 0f) return;

        // A PENDING CORRECTION SUPPRESSES THE ACK, because in real UE they are the same message.
        // FNetworkPredictionData_Server_Character has ONE PendingAdjustment with a `bAckGoodMove`
        // flag, and SendClientAdjustment (CharacterMovementComponent.cpp:9002) is a straight
        // if/else on it: a correction REPLACES the ack, it does not accompany it.
        //
        // Sending both independently - which this did - is a race the correction loses about half
        // the time. ClientAckGoodMove frees every client saved move up to its timestamp, and
        // ClientAdjustPosition_Implementation opens by looking that same timestamp UP in that same
        // list (`GetSavedMoveIndex`, line 9165) and returns if it is gone. So the ack going out
        // first quietly deletes the move the correction was about to name, the correction is
        // discarded, and the player is left in whatever movement mode they were stuck in - which is
        // exactly the intermittent "sometimes cannot move after getting out" this produced.
        if (pawn.PendingMovementModeCorrection != null) return;

        var now = Actor.GetWorld()?.TimeSeconds ?? (Environment.TickCount64 / 1000f);
        if (now - pawn.ServerLastClientGoodMoveAckTime <= NetworkMinTimeBetweenClientAckGoodMoves) return;
        pawn.ServerLastClientGoodMoveAckTime = now;

        var timeStamp = pawn.PendingAckGoodMoveTimeStamp;
        pawn.PendingAckGoodMoveTimeStamp = 0f;

        if (!_LoggedFirstGoodMoveAck) {
            _LoggedFirstGoodMoveAck = true;
            Console.WriteLine($"UActorChannel.SendClientAckGoodMove: ChIndex={ChIndex} acknowledging the client's first move (TimeStamp={timeStamp}). " +
                "Client saved moves should stop piling up now - watch for 'Hit limit of 96 saved moves' to disappear.");
        }

        SendRpc("ClientAckGoodMove", writer => {
            writer.WriteBit(true); // TimeStamp is present (non-bool RPC param protocol - see FRpcReader)
            writer.WriteFloat(timeStamp);
        }, reliable: false);
    }

    /// <summary>
    ///     ACharacter::ClientAdjustPosition (Character.h:296) - the server telling an autonomous
    ///     proxy to trust it about where it is and, crucially, WHAT MOVEMENT MODE IT IS IN.
    ///
    ///     WHY THIS HAD TO EXIST. A player who got out of a vehicle could not move: their client
    ///     stayed in EFortCustomMovement::Driving (movementMode 17 = custom 1 + the 16 threshold)
    ///     with nothing left to drive, and no amount of clearing VehicleStateRep or the seat array
    ///     changed it. The mode is not a replicated property for its owner - ACharacter's
    ///     ReplicatedMovementMode is COND_SimulatedOnly, so it reaches every client EXCEPT the one
    ///     whose pawn it is. `ApplyNetworkMovementMode(ServerMovementMode)` inside this RPC, under
    ///     the comment "Trust the server's movement mode" (CharacterMovementComponent.cpp:9198), is
    ///     the only channel there is.
    ///
    ///     A CORRECTION ALWAYS MOVES THE PLAYER, and that is the hard part rather than the mode
    ///     byte. There is no mode-only variant: the implementation does
    ///     `SetWorldLocation(WorldShiftedNewLocation, ETeleportType::TeleportPhysics)`
    ///     unconditionally, with no collision sweep. So a position that is wrong by a couple of
    ///     metres does not get corrected by the client - it drops the player through the floor,
    ///     which is exactly what the first version of this did.
    ///
    ///     THE FIRST VERSION SENT THE CLIENT'S OWN RELATIVE POSITION BACK, reasoning that the client
    ///     could turn it into a world position and so nobody had to know one. That is wrong, and
    ///     the engine says so in a single line - the reconstruction is
    ///
    ///         WorldShiftedNewLocation = NewLocation + BaseLocation;   // line 9184
    ///
    ///     which adds the base's LOCATION and throws its ROTATION away, while the relative location
    ///     the client computed (ACharacter::SaveRelativeBasedMovement) is in the base's rotated
    ///     local space. For a shopping cart parked at any yaw but zero, the seat offset comes back
    ///     rotated wrongly - metres off, downwards as often as not, and TeleportPhysics puts the
    ///     player there regardless of what is in the way. (The `// TODO: error handling` sitting
    ///     next to that line is a fair warning about how much this path was ever exercised.)
    ///
    ///     SO THE POSITION IS DERIVED FROM WHAT THIS SERVER ACTUALLY KNOWS, in world space:
    ///
    ///       * APlayerController::ServerUpdateCamera keeps arriving throughout a ride and carries a
    ///         WORLD-space camera location. It is not the pawn - a third-person boom measured 254
    ///         units on this build (see the RPC's own comment) - but it is real, current, and above
    ///         the player rather than below them.
    ///       * The baked map then supplies the ground under that XY, which is what
    ///         TerrainHeightMap exists for and is trusted for elsewhere (building support, projectile
    ///         floors). Dropping the player onto measured ground is the one thing that reliably does
    ///         not put them inside it.
    ///
    ///     Where the bake has no ground - the warmup island, for one - the camera position itself
    ///     is used instead, and that is safe for the same reason the ground was: UE's third-person
    ///     camera collision-sweeps to avoid penetrating geometry, so the camera is always standing
    ///     in free space. Only a client that has never sent a camera at all gets no correction.
    /// </summary>
    private void SendMovementCorrection() {
        if (Actor is not APawn pawn) return;
        if (pawn.TakeMovementModeCorrection() is not { } packedMode) return;

        // 1. THE TIMESTAMP MUST NAME A LIVE SAVED MOVE. ClientAdjustPosition_Implementation opens
        //    with `GetSavedMoveIndex(TimeStamp)` and returns outright if that misses (line 9165),
        //    logging at a verbosity nothing prints. LastClientMoveTimeStamp is the newest move
        //    actually received and is never cleared, unlike PendingAckGoodMoveTimeStamp.
        if (pawn.LastClientMoveTimeStamp <= 0f) {
            Console.WriteLine($"UActorChannel.SendMovementCorrection: {pawn.GetFName()} has never sent a move, " +
                              "so there is no timestamp a correction could name - not sending.");
            return;
        }

        // A LAUNCH IS THE OTHER REASON TO CORRECT, and it wants a different position and a real
        // velocity. The vehicle-exit case has no idea where the player is (a based move carries no
        // world position) and has to reconstruct one from the camera; a launched player has been
        // sending unbased moves all along, so their own last reported position is exactly right -
        // and naming it makes the correction a pure velocity+mode change with no teleport at all.
        var launch = pawn.TakeLaunchVelocity();

        var newLocation = launch != null && pawn.HasFreshUnbasedLocation
            ? pawn.GetActorLocation()
            : ResolveCorrectionLocation(pawn);

        if (newLocation is not { } correctedLocation) return;

        SendRpc("ClientAdjustPosition", writer => {
            writer.WriteBit(true);
            writer.WriteFloat(pawn.LastClientMoveTimeStamp);          // TimeStamp

            writer.WriteBit(true);
            correctedLocation.NetSerializeWrite(writer);              // NewLoc, absolute

            // NewVel. Zero for a vehicle exit - a server that is not simulating this pawn has no
            // honest velocity to give, and the client is about to land on the ground anyway - but a
            // LAUNCH is the one moment this server does know better than the client, because the
            // grenade is its own. This is the whole delivery mechanism for a shockwave: the
            // projectile Blueprint calls LaunchCharacter on the server, and this line is what that
            // becomes on the wire.
            writer.WriteBit(true);
            (launch ?? new FVector()).NetSerializeWrite(writer);

            // NewBase / NewBaseBoneName - both absent. Sending the position ABSOLUTE is what makes
            // that safe: a relative correction whose base fails to resolve on the client is thrown
            // away entirely ("could not resolve the new relative movement base actor, ignoring
            // server correction!", line 9152), and an absolute one has no such dependency.
            writer.WriteBit(false);
            writer.WriteBit(false);

            // bHasBase / bBaseRelativePosition - BOOLS, so no presence bit of their own. That is
            // FRepLayout::SendPropertiesForRPC's rule and it is not cosmetic: writing a presence bit
            // here would shift every bit after it.
            writer.WriteBit(false);
            writer.WriteBit(false);

            writer.WriteBit(true);
            writer.WriteByte(packedMode);                             // ServerMovementMode
        }, reliable: true);

        // THE CORRECTION IS ALSO THE ACK, so the pending one is dropped rather than sent after it.
        // ClientAdjustPosition_Implementation calls `ClientData->AckMove(MoveIndex, *this)` on the
        // way through (CharacterMovementComponent.cpp:9172), which frees the client's saved moves
        // exactly as ClientAckGoodMove would - real UE never sends both because they are two
        // branches of one function. Leaving this set would put a redundant ack on the wire naming a
        // move the client has just retired.
        pawn.PendingAckGoodMoveTimeStamp = 0f;

        // AND THE SERVER'S OWN COPY, which has been stale since the player boarded (a based
        // ServerMove carries no world position - see the move handler). Leaving it behind would
        // make relevancy, fall damage and every distance check run off the boarding spot. For a
        // launch it is the pawn's own position already, so this is a no-op there.
        pawn.SetActorLocation(correctedLocation);

        Console.WriteLine($"UActorChannel.SendMovementCorrection: ChIndex={ChIndex} {pawn.GetFName()} -> packed " +
                          $"movement mode {packedMode} " +
                          $"({(packedMode >= 16 ? $"custom {packedMode - 16}" : "not custom")}) " +
                          $"at {correctedLocation}, TimeStamp={pawn.LastClientMoveTimeStamp}" +
                          (launch is { } thrown
                              ? $", LAUNCHED at ({thrown.X:F0}, {thrown.Y:F0}, {thrown.Z:F0})."
                              : ".") +
                          " If the client ignores this, its log says why (LogNetPlayerMovement).");
    }

    /// <summary>
    ///     Where to put the pawn in a correction - the camera's world XY, dropped onto the baked
    ///     ground. Null when either half is unavailable, which means no correction is sent at all.
    /// </summary>
    private static FVector? ResolveCorrectionLocation(APawn pawn) {
        if ((pawn.Controller as APlayerController)?.LastClientCameraLocation is not { } camera) {
            Console.WriteLine($"UActorChannel.SendMovementCorrection: {pawn.GetFName()} needs a movement-mode " +
                              "correction but this client has never sent ServerUpdateCamera, so there is no " +
                              "world position to put it at - not sending (a guess would teleport it into the map).");
            return null;
        }

        // HOW OLD THAT CAMERA IS, reported every time. A correction placed from a stale camera is a
        // teleport back to wherever the player was when it went stale, and the failure looks exactly
        // like a correction that was never sent - so the age is part of the answer, not a detail.
        var controller = (APlayerController) pawn.Controller!;
        var age = (pawn.GetWorld()?.TimeSeconds ?? 0f) - controller.LastClientCameraTime;

        // WALK BACK ALONG THE BOOM FIRST, so everything below is about the PLAYER's position rather
        // than the camera's. The camera hangs behind and above; its own forward vector points at the
        // player, and LastObservedCameraBoom is how far along that ray they were the last time both
        // ends were known good.
        //
        // SAFE BECAUSE OF WHAT THE CLIENT ALREADY DID: UE's third-person camera sweeps from the pawn
        // out to the camera and stops at the first obstruction, so the whole segment between them is
        // clear. A MEASURED boom lands on that segment. A guessed one could overshoot past the
        // player into whatever is in front of them, which is why this uses no default - without a
        // measurement it stays at the camera, two metres back and definitely clear.
        var aimed = camera;
        var boomNote = "no boom measured yet, so this is the camera position itself";

        if (pawn.LastObservedCameraBoom is { } boom && controller.LastClientCameraRotation is { } look) {
            var forward = look.GetForwardVector();
            aimed = new FVector {
                X = camera.X + forward.X * boom,
                Y = camera.Y + forward.Y * boom,
                Z = camera.Z + forward.Z * boom
            };

            boomNote = $"walked {boom:F0}uu along the camera's forward vector to reach the player";
        }

        // ...AND ONLY THEN ASK FOR THE GROUND, at the point the player is actually going to be put.
        // Querying under the CAMERA and then placing somewhere else was wrong by a boom's length,
        // which over a cliff edge or a roof line is the difference between standing and falling.
        // Searching downward is the right direction either way: the camera sits above the player.
        var ground = TerrainHeightMap.GetSurfaceUnder(aimed.X, aimed.Y, PawnGroundProbeRadius, camera.Z);

        if (ground is not { } groundZ) {
            // NO BAKED GROUND HERE - and refusing to send was the wrong answer, measured: the very
            // first live attempt failed at (-116795, -117859, 4504), which is the WARMUP ISLAND. It
            // is a separate sublevel placed off the map proper and the terrain bake does not cover
            // it, so a rule of "ground or nothing" means no correction anywhere a tester actually
            // stands before a match starts.
            //
            // THE POINT ON THE BOOM IS THE FALLBACK, and it is a much better one than it looks:
            // UE's third-person camera sweeps from the pawn out to the camera and stops at the first
            // obstruction, so every point on that segment is FREE SPACE by construction - which is
            // the exact property the ground lookup was there to guarantee. With a measured boom that
            // point IS the player; with none yet it is the camera, a couple of metres back and
            // equally clear, and the client simply drops.
            //
            // The baked ground is still preferred for Z where it exists, because a ray along the
            // boom says where the player is standing and not what they are standing on.
            Console.WriteLine($"UActorChannel.SendMovementCorrection: camera at ({camera.X:F0}, " +
                              $"{camera.Y:F0}, {camera.Z:F0}) [{age:F1}s old], {boomNote} - no baked ground " +
                              $"there, so placing at ({aimed.X:F0}, {aimed.Y:F0}, {aimed.Z:F0}) as-is. " +
                              "(Warmup island and anywhere else outside the terrain bake - see " +
                              "map-collision-bake.)");

            return aimed;
        }

        // GROUND WINS ON Z, the boom on XY. The bake knows the floor better than a camera ray does,
        // and the boom knows where along the ground the player was standing - taking one from each
        // is better than either alone.
        Console.WriteLine($"UActorChannel.SendMovementCorrection: camera at ({camera.X:F0}, {camera.Y:F0}, " +
                          $"{camera.Z:F0}) [{age:F1}s old], {boomNote}; baked ground {groundZ:F0}.");

        return new FVector { X = aimed.X, Y = aimed.Y, Z = groundZ + PawnCapsuleHalfHeight };
    }

    /// <summary>
    ///     Half of a Fortnite player capsule, which is what separates the actor's ORIGIN (its
    ///     centre) from the ground its feet are on. Placing an actor at the ground height itself
    ///     buries it to the waist and the client's floor check then resolves it downwards.
    /// </summary>
    private const float PawnCapsuleHalfHeight = 96f;

    /// <summary>
    ///     How wide a footprint the ground query may consider, matching the capsule's radius. Zero
    ///     would ask about a single point and miss whenever the camera happens to hang over an edge.
    /// </summary>
    private const float PawnGroundProbeRadius = 48f;

    private void SendPawnRpc(string fieldName, APawn pawn) => SendObjectRpc(fieldName, pawn);

    /// <summary>
    ///     A server-&gt;client RPC taking exactly one object reference. Covers ClientRestart /
    ///     ClientRetryClientRestart (an APawn*) and ClientSetHUD (a TSubclassOf&lt;AHUD&gt;, which is
    ///     just an object reference to a UClass on the wire, so a path-exported UAssetRegistry entry
    ///     serves as well as a spawned actor does).
    /// </summary>
    public void SendObjectRpc(string fieldName, UObject obj) =>
        SendRpc(fieldName, writer => {
            writer.WriteBit(true); // the parameter is present (non-bool RPC param protocol - see FRpcReader)
            ((UPackageMapClient) writer.PackageMap!).SerializeObject(writer, obj);
        });

    /// <summary>
    ///     A server-&gt;client RPC whose UFunction takes no parameters, so its field payload is simply
    ///     zero bits long. WriteFieldHeaderAndPayload still writes the RepIndex and a packed length
    ///     of 0, which is all the client's ReadFieldHeaderAndPayload needs to dispatch the call.
    /// </summary>
    public void SendParameterlessRpc(string fieldName) => SendRpc(fieldName, static _ => {});

    /// <summary>
    ///     AFortPlayerController::ClientReportDamagedResourceBuilding(ABuildingSMActor*,
    ///     TEnumAsByte&lt;EFortResourceType&gt;, int32, bool, bool) - how a real server tells the player
    ///     who just hit a building that they hit it. A working injected server (the Nebula
    ///     reconstruction's AttemptSpawnResources) calls exactly this on every building hit, computing
    ///     bDestroyed as `(GetHealth() - ActualDamageDealt) &lt;= 0`.
    ///
    ///     Sent to the INSTIGATOR only, which is the point: the damage number and the health bar over
    ///     a build are local HUD, not replicated actor state. This project spent three rounds pushing
    ///     replicated health at the problem - the piece's health value does reach the client (the
    ///     breaking animation on the very same push proves the transport works), it simply is not what
    ///     the HUD listens to.
    ///
    ///     Parameter encoding is FRepLayout::SendPropertiesForRPC: every non-bool parameter is
    ///     preceded by a "was it sent" bit, bools are the bare value bit with no such prefix. The
    ///     resource type is a TEnumAsByte, so CeilLogTwo(EFortResourceType_MAX=5) = 3 bits.
    /// </summary>
    public unsafe void SendClientReportDamagedResourceBuilding(UObject building, byte resourceType,
                                                               int resourceCount, bool bDestroyed,
                                                               bool bJustHitWeakspot) =>
        SendRpc("ClientReportDamagedResourceBuilding", writer => {
            writer.WriteBit(true);
            ((UPackageMapClient) writer.PackageMap!).SerializeObject(writer, building);

            writer.WriteBit(true);
            var resourceBits = resourceType;
            writer.SerializeBits(&resourceBits, 3);

            writer.WriteBit(true);
            writer.WriteInt32(resourceCount);

            writer.WriteBit(bDestroyed);
            writer.WriteBit(bJustHitWeakspot);
        });

    /// <summary>
    ///     A hardcoded-EName FName parameter, as UPackageMap::StaticSerializeName writes it:
    ///     one bit bHardcoded, then SerializeIntPacked of the EName index (CoreNet.cpp:274, and
    ///     MAX_NETWORKED_HARDCODED_NAME = 410 in UnrealNames.h). A name above that limit goes as a
    ///     STRING instead, which is what makes the observed sizes below so informative.
    /// </summary>
    private static unsafe void WriteHardcodedName(FNetBitWriter writer, uint nameIndex) {
        writer.WriteBit(true);                  // bHardcoded
        writer.SerializeIntPacked(&nameIndex);
    }

    /// <summary>EName indices from UnrealNames.inl - the only three this server needs.</summary>
    private const uint NameDefault = 204;
    private const uint NameSpectating = 322;

    /// <summary>
    ///     APlayerController::ClientGotoState(FName NewState) and ClientSetCameraMode(FName) - the
    ///     other two thirds of boarding the battle bus. See SendClientSetViewTarget for the capture.
    ///
    ///     THE STATE IS Spectating, and the capture proves it by its TIMING rather than its content.
    ///     The PR3.0 session sends ClientGotoState at 19:59:14.555, .587, then 19:59:16.652,
    ///     19:59:18.685, 19:59:20.752 - i.e. every ~2.03-2.07 seconds. That is the fingerprint of
    ///     APlayerController::ServerSetSpectatorLocation_Implementation (PlayerController.cpp:2739),
    ///     which re-sends ClientGotoState(GetStateName()) only
    ///     `if (IsInState(NAME_Spectating))` and only once
    ///     `World->TimeSeconds - LastSpectatorStateSynchTime > 2.f`. Its other branch is
    ///     ClientGotoState followed immediately by ClientSetViewTarget - exactly the pair the
    ///     transition sends. A player on the bus is a SPECTATOR with the aircraft as view target.
    ///
    ///     THE CAMERA MODE IS Default, by elimination rather than by timing. Both RPCs are 18 bits,
    ///     which is far too small for a string, so both names must be hardcoded ENames. UE's
    ///     camera-mode names are Default, ThirdPerson, FirstPerson and FreeCam - and only `Default`
    ///     (204) is in UnrealNames.inl at all; the other three are not hardcoded and could not fit.
    ///     It is also what APlayerController::ResetCameraMode sends (PlayerController.cpp:1579).
    ///
    ///     18 bits is the check on the encoding, and it lands exactly: 1 presence bit (the rule
    ///     SendNetMulticastAthenaBatchedDamageCues documents - one per non-bool parameter) + 1 bit
    ///     bHardcoded + 16 bits for SerializeIntPacked of a two-byte index (both 204 and 322 need
    ///     two) = 18.
    ///
    ///     Both are overridable (BUS_GOTO_STATE_NAME / BUS_CAMERA_MODE_NAME, EName indices) so an
    ///     alternative can be tried against a live client without a rebuild.
    /// </summary>
    public void SendClientGotoState(uint nameIndex) =>
        SendRpc("ClientGotoState", writer => {
            writer.WriteBit(true);              // the parameter's presence bit
            WriteHardcodedName(writer, nameIndex);
        });

    /// <summary>See SendClientGotoState.</summary>
    public void SendClientSetCameraMode(uint nameIndex) =>
        SendRpc("ClientSetCameraMode", writer => {
            writer.WriteBit(true);
            WriteHardcodedName(writer, nameIndex);
        });

    /// <summary>
    ///     APlayerController::ClientOnPawnSpawned() - no parameters, and the capture sends it as
    ///     part of the jump response. See AGameModeBase.TickBoarding for the full sequence.
    /// </summary>
    public void SendClientOnPawnSpawned() => SendRpc("ClientOnPawnSpawned", _ => { });

    /// <summary>
    ///     APlayerController::ClientSetRotation(FRotator NewRotation, bool bResetCamera) - the
    ///     server telling the client which way the player is facing.
    ///
    ///     This server never sent it, and a real one does: the PR3.0 capture has it at login
    ///     (decoded_new.txt #356, `field[11] = ClientSetRotation (21 bits)`) and again around the
    ///     jump (#8505). Without it nothing on the client's side is ever told what the initial
    ///     control rotation should be.
    ///
    ///     The 21 bits are the check on the encoding and they land exactly: 1 presence bit for the
    ///     rotator parameter (the one-per-non-bool-parameter rule
    ///     SendNetMulticastAthenaBatchedDamageCues documents) + FRotator::SerializeCompressedShort's
    ///     three per-axis presence bits + 16 for the single non-zero axis + 1 for bResetCamera,
    ///     which is a bool and so gets no presence bit of its own = 21. The capture's other size,
    ///     37, is the same thing with two axes present.
    /// </summary>
    public void SendClientSetRotation(FRotator rotation, bool bResetCamera) =>
        SendRpc("ClientSetRotation", writer => {
            writer.WriteBit(true);
            rotation.NetSerializeWrite(writer);
            writer.WriteBit(bResetCamera);
        });

    /// <summary>
    ///     AFortPlayerController::ClientSetSpectatorCamera(FVector CameraLocation,
    ///     FRotator CameraRotation) - where the camera sits between joining and boarding the bus.
    ///
    ///     GROUND TRUTH from the PR3.0 capture (decoded_new.txt #215): the real server sends this at
    ///     LOGIN, in the same bunch as ClientCapBandwidth and immediately BEFORE the first
    ///     ClientGotoState. That ordering is the whole point - ClientGotoState(Spectating) puts the
    ///     client in a spectator state, and this is what that state's camera is aimed at. This server
    ///     sent the GotoState and never the camera.
    ///
    ///     The capture's 117 bits are the check on the encoding and they land exactly:
    ///
    ///         1  presence bit for CameraLocation (one per non-bool PARAMETER)
    ///        96  the FVector's three floats - a PLAIN FVector has no NetSerialize in 4.23 (only the
    ///            FVector_NetQuantize family does), so FRepLayout flattens it to three float leaves
    ///            and leaves are NOT individually presence-bitted
    ///         1  presence bit for CameraRotation
    ///         3  FRotator::SerializeCompressedShort's per-axis presence bits
    ///        16  the one non-zero axis
    ///       ---
    ///       117
    ///
    ///     - which also says the capture's rotation was yaw-only, i.e. level. This sends whatever it
    ///     is given and lets FRotator.NetSerializeWrite decide how many axes are non-zero, so a
    ///     pitched camera simply costs 16 bits more.
    ///
    ///     UNTESTED against a live client.
    /// </summary>
    public void SendClientSetSpectatorCamera(FVector cameraLocation, FRotator cameraRotation) =>
        SendRpc("ClientSetSpectatorCamera", writer => {
            writer.WriteBit(true);              // CameraLocation is present
            writer.WriteFloat(cameraLocation.X);
            writer.WriteFloat(cameraLocation.Y);
            writer.WriteFloat(cameraLocation.Z);

            writer.WriteBit(true);              // CameraRotation is present
            cameraRotation.NetSerializeWrite(writer);
        });

    /// <summary>
    ///     APlayerController::ClientSetViewTarget(AActor* A, FViewTargetTransitionParams Params) -
    ///     what actually puts the camera ON the battle bus.
    ///
    ///     Setting AFortPlayerStateAthena::bInAircraft alone gets the HUD into its aircraft state,
    ///     which is enough to make the phase LOOK like it worked - the reported symptom was "the view
    ///     feels like the bus but I am still in third person and never board". The camera does not
    ///     move until the view target does, and only this RPC moves it.
    ///
    ///     GROUND TRUTH, not derivation. The PR3.0 capture shows exactly three RPCs on the player
    ///     controller's channel at the Aircraft transition, in this order
    ///     (PriveDev/PacketProxy/decoded_new.txt, packet #8050):
    ///
    ///         field[45] = ClientSetCameraMode   (18 bits)
    ///         field[24] = ClientGotoState       (18 bits)
    ///         field[50] = ClientSetViewTarget   (86 bits)
    ///
    ///     and the server log confirms the same pair by name at 19:59:14.555.
    ///
    ///     The 86 bits ARE the check on the encoding below. Following the rule
    ///     SendNetMulticastAthenaBatchedDamageCues documents - one presence bit per non-bool
    ///     PARAMETER, then that parameter's flattened leaves - this writes
    ///     1 + objectRef + 1 + 32 (BlendTime) + 3 (BlendFunction) + 32 (BlendExp) + 1 (bLockOutgoing)
    ///     = 70 + objectRef, so the capture's 86 pins the object reference at 16 bits, which is what
    ///     a mid-range NetGUID costs. A layout that did not add up would have shown here.
    ///
    ///     FViewTargetTransitionParams and EViewTargetBlendFunction are read from the real 4.23
    ///     source (Engine/Classes/Camera/PlayerCameraManager.h): BlendTime, BlendFunction, BlendExp,
    ///     bLockOutgoing in that order, VTBlend_MAX = 5 so the enum is SerializeInt(_, 6) = 3 bits.
    ///     The values sent are the struct's own constructor defaults - a cut, not a blend, which is
    ///     what boarding a bus should look like.
    /// </summary>
    public unsafe void SendClientSetViewTarget(UObject target) =>
        SendRpc("ClientSetViewTarget", writer => {
            writer.WriteBit(true);
            ((UPackageMapClient) writer.PackageMap!).SerializeObject(writer, target);

            writer.WriteBit(true);
            writer.WriteFloat(0f);                  // BlendTime - 0 means no blend at all
            var blendFunction = 1u;                 // VTBlend_Cubic, the struct's default
            writer.SerializeInt(&blendFunction, 6); // EViewTargetBlendFunction, VTBlend_MAX = 5
            writer.WriteFloat(2f);                  // BlendExp, the struct's default
            writer.WriteBit(false);                 // bLockOutgoing
        });

    /// <summary>
    ///     AFortPlayerController::ClientOnPawnDied(FFortPlayerDeathReport) - the RPC that makes a
    ///     death a DEATH on the client.
    ///
    ///     Why this and not the replicated flags. The server was already setting AFortPawn::bIsDying
    ///     (handle 47), FDeathInfo on the PlayerState and bMarkedAlive=false, and the live symptom was
    ///     a player at 0 HP who could not shoot or build but was otherwise alive and walking around.
    ///     `AFortPawn::bIsDying` HAS NO OnRep - the SDK header lists OnRep_IsDBNO, OnRep_IsKnockedBack
    ///     and a dozen others next to it, and nothing for bIsDying - so it replicates perfectly and
    ///     runs nothing. (Same trap as bIsInsideSafeZone vs bIsInAnyStorm one round earlier.) The
    ///     death path a real server actually drives is this RPC: Project-Reboot-3.0 HOOKS
    ///     ClientOnPawnDied rather than calling anything else, which is only possible because the
    ///     native death already goes through it, and FortniteUI's own
    ///     `HandleLocalPawnDied(FFortPlayerDeathReport)` is what puts the elimination screen up.
    ///
    ///     THE STRUCT, verified twice over. raider3.5's Chapter 1 SDK gives FFortPlayerDeathReport as
    ///     0x50 bytes laid out ServerTimeForRespawn 0x00, ServerTimeForResurrect 0x04, LethalDamage
    ///     0x08, KillerPlayerState 0x10, KillerPawn 0x18, DamageCauser 0x20, bDroppedBackpack and
    ///     bNotifyUI as bits of 0x28, Tags 0x30 - and 10.40's own SDK agrees on the 0x50 size. The
    ///     dump confirms the order independently: execClientOnPawnDied (0x141F36840) reads three
    ///     floats, then three qwords, then the bitfield byte, then the tag container, and copies them
    ///     to struct+0, +4, +8, +0x10, +0x18, +0x20, +0x28, +0x30 before calling vtable[0x1FF8].
    ///
    ///     So the wire is this project's usual RPC rule - one presence bit for the single struct
    ///     PARAMETER, then its members flattened in offset order with no further bits - with two
    ///     details worth naming:
    ///
    ///       * the two bools are bits IN PLACE, no presence bit, same as everywhere else here;
    ///       * FGameplayTagContainer has a native NetSerializer, so it is NOT flattened. Its
    ///         serializer writes the tag count in UGameplayTagsManager::NumBitsForContainerSize bits
    ///         first, and that is 6 by default and is NOT overridden in FortniteGame's DefaultGame.ini
    ///         (checked with Tools/PakReader) - so an empty container is exactly six zero bits.
    ///
    ///     UNTESTED against a live client.
    /// </summary>
    public unsafe void SendClientOnPawnDied(UObject? killerPlayerState, UObject? killerPawn,
                                            UObject? damageCauser, float lethalDamage,
                                            bool notifyUI = true) =>
        SendRpc("ClientOnPawnDied", writer => {
            var packageMap = (UPackageMapClient) writer.PackageMap!;

            writer.WriteBit(true);          // the DeathReport parameter is present

            writer.WriteFloat(0f);          // 0x00 ServerTimeForRespawn  - no respawn in Battle Royale
            writer.WriteFloat(0f);          // 0x04 ServerTimeForResurrect
            writer.WriteFloat(lethalDamage); // 0x08 LethalDamage

            packageMap.SerializeObject(writer, killerPlayerState); // 0x10
            packageMap.SerializeObject(writer, killerPawn);        // 0x18
            packageMap.SerializeObject(writer, damageCauser);      // 0x20

            writer.WriteBit(false);         // 0x28 bit 0 bDroppedBackpack
            writer.WriteBit(notifyUI);      // 0x28 bit 1 bNotifyUI

            // 0x30 Tags - an empty FGameplayTagContainer, which is ONE BIT SET, not six zeroes.
            //
            // THIS WAS WRONG, and wrong in the way that hurts: `FGameplayTagContainer::NetSerialize`
            // (GameplayTagContainer.cpp:968) opens with "1st bit to indicate empty tag container or
            // not (empty tag containers are frequently replicated). Early out if empty." Writing six
            // zeroes told the client the container was NOT empty and then handed it a count field,
            // so it read 1 + NumBitsForContainerSize = 7 bits where six were written and ran off the
            // end of the RPC. Found while reading the same function for FGameplayEventData - see
            // Core/FGameplayTypes.
            FGameplayTypes.WriteEmptyTagContainer(writer);
        });

    /// <summary>
    ///     AFortPlayerController::ClientSendMessage(FText Message, USoundBase* StartSound) - an
    ///     arbitrary string to one client, with an optional sound.
    ///
    ///     THE FIRST FText THIS PROJECT HAS EVER WRITTEN. See Core/FText for the format and where it
    ///     is read from; the short version is that an FText parameter has no NetSerializeItem, so it
    ///     takes the ordinary archive path, and a literal built the way a server builds one is
    ///     culture-invariant and therefore skips all the FTextHistory machinery.
    ///
    ///     WHAT IT IS FOR, and what it is NOT. It is not the elimination feed: that feed's text is
    ///     composed CLIENT-SIDE from the two PlayerStates' names, and
    ///     `AFortGameStateAthena::KillFeedEntry` - a TArray&lt;FText&gt; that looks like exactly the right
    ///     hook - is not a replicated property at all (rep_handles has the GameState going from
    ///     WinningPlayerState at 0x1358 straight past it). This is the channel that does carry a
    ///     server-chosen string.
    ///
    ///     The sound is sent as a null object reference. A real SoundBase would have to be a
    ///     name-stable asset the client can resolve, which is a separate piece of work and not what
    ///     the message is for.
    ///
    ///     UNTESTED against a live client.
    /// </summary>
    /// <summary>
    ///     APlayerController::ClientTeamMessage(APlayerState* SenderPlayerState, FString S, FName Type,
    ///     float MsgLifeTime) - UE's own chat/message path, and the OTHER half of an experiment.
    ///
    ///     WHY BOTH THIS AND ClientSendMessage. The FText one went out cleanly - the log shows
    ///     `ClientSendMessage fieldIndex=139 numPayloadBits=196` and the session carried on normally,
    ///     which a corrupt FString length inside it would very likely not have survived - and yet
    ///     nothing appeared on screen. Two explanations remain and they need separating:
    ///
    ///       * the FText encoding is subtly wrong, or
    ///       * ClientSendMessage has no in-match UI bound to it at all. Its neighbours in the field
    ///         list are ClientSendConfirmationMessage and ClientRequestReadyCheck, which are frontend
    ///         and party things, so this is quite likely.
    ///
    ///     This RPC carries its text as an **FString**, which this project has been writing correctly
    ///     for a long time (FUniqueNetIdRepl and NMT_BeaconJoin both depend on it). So: if this one
    ///     shows text and the FText one does not, the encoding is exonerated and the answer is which
    ///     UI is listening. If NEITHER shows anything, the FText writer is not the suspect either and
    ///     the question becomes which channel Fortnite's HUD actually draws.
    ///
    ///     `Type` is sent as the hardcoded EName `None` (index 0), which is what
    ///     APlayerController::ClientMessage itself defaults to and what the HUD then substitutes its
    ///     own default for. `Say` is NOT a hardcoded EName in 4.23 - UnrealNames.inl has no entry for
    ///     it - so sending it would mean the string form of an FName, which is a separate encoding
    ///     and not worth mixing into an experiment about text. MsgLifeTime 0 means "HUD default".
    ///
    ///     UNTESTED against a live client.
    /// </summary>
    public void SendClientTeamMessage(UObject? sender, string message, uint typeNameIndex, float lifeTime = 0f) =>
        SendRpc("ClientTeamMessage", writer => {
            writer.WriteBit(true);
            ((UPackageMapClient) writer.PackageMap!).SerializeObject(writer, sender);

            writer.WriteBit(true);
            writer.WriteString(message);

            writer.WriteBit(true);
            WriteHardcodedName(writer, typeNameIndex);

            writer.WriteBit(true);
            writer.WriteFloat(lifeTime);
        });

    public void SendClientSendMessage(string message) =>
        SendRpc("ClientSendMessage", writer => {
            writer.WriteBit(true);                       // Message is present
            Core.FText.Serialize(writer, message);

            writer.WriteBit(true);                       // StartSound is present...
            ((UPackageMapClient) writer.PackageMap!).SerializeObject(writer, null);   // ...as null
        });

    /// <summary>
    ///     AFortPlayerControllerPvP::ClientReceiveKillNotification(AFortPlayerStateZone* Killer,
    ///     AFortPlayerStateZone* Killed) - the elimination feed entry.
    ///
    ///     THE SIGNATURE IS READ, NOT INFERRED FROM THE SIZE. The 0906 capture shows this RPC going
    ///     out 1 ms after ClientOnPawnDied at 5.6 bytes, and a byte count is not a signature - it was
    ///     left unimplemented for exactly that reason until the SDK could be asked
    ///     (`FN_FortniteGame_classes.hpp:8522`). Two object references, and the parameter rule this
    ///     project already proved with ClientReportDamagedResourceBuilding applies: one presence bit
    ///     each, then the value.
    ///
    ///     A SOLO DEATH HAS NO KILLER, and null is the honest thing to send - the storm and a fall
    ///     eliminate you without anyone doing it. Whether the client draws a feed line for a null
    ///     killer is its business; a fabricated killer would be a lie that shows up in the feed.
    ///
    ///     UNTESTED against a live client.
    /// </summary>
    public void SendClientReceiveKillNotification(UObject? killer, UObject? killed) =>
        SendRpc("ClientReceiveKillNotification", writer => {
            var packageMap = (UPackageMapClient) writer.PackageMap!;

            writer.WriteBit(true);
            packageMap.SerializeObject(writer, killer);

            writer.WriteBit(true);
            packageMap.SerializeObject(writer, killed);
        });

    /// <summary>
    ///     AFortPlayerController::ClientSpawnWeakSpotOnBuildingActor(const FBuildingWeakSpotData&amp;) -
    ///     the RPC that actually puts the weak-spot marker on screen (bJustHitWeakspot above only
    ///     reports that an EXISTING one was hit; nothing shows without this one firing first). See
    ///     NativeRpcHandlers.DamageLevelActor for when this is called - a few seconds after the
    ///     piece's first recorded hit, an approximation of Fortnite's own timed reveal - and for why
    ///     the position/normal are the triggering hit's own impact point rather than a real point
    ///     picked on the mesh surface (this project has no collision geometry to pick one from).
    ///
    ///     FBuildingWeakSpotData (raider3.5's SDK dump, ScriptStruct FortniteGame.BuildingWeakSpotData,
    ///     0x38 bytes) is a plain reflected struct, not a NetSerialize-native one: ParentBuilding
    ///     (TWeakObjectPtr&lt;ABuildingSMActor&gt;, offset 0x0), Normal (FVector_NetQuantizeNormal, 0x8),
    ///     Position (FVector_NetQuantize10, 0x14), then 0x18 bytes of engine bookkeeping with no
    ///     reflected properties (not serialized) - so the wire shape is the same "one presence bit per
    ///     parameter, then its members in offset order" rule BatchedDamageCues above already proved,
    ///     with the one struct parameter's three real members following its single presence bit.
    ///
    ///     UNTESTED against a live client - this RPC has never been sent by this project before.
    /// </summary>
    public void SendClientSpawnWeakSpotOnBuildingActor(UObject parentBuilding, FVector normal, FVector position) =>
        SendRpc("ClientSpawnWeakSpotOnBuildingActor", writer => {
            var packageMap = (UPackageMapClient) writer.PackageMap!;

            writer.WriteBit(true); // ReplicatedWeakSpotData parameter present
            packageMap.SerializeObject(writer, parentBuilding);
            normal.NetSerializeWriteFixed(writer, 1, 16);        // FVector_NetQuantizeNormal
            position.NetSerializeWriteQuantized(writer, 10, 24); // FVector_NetQuantize10
        });

    /// <summary>
    ///     AFortPawn::NetMulticast_InvokeGameplayCueExecuted_FromSpec - the RPC a real 10.40 server
    ///     sends in the SAME PACKET as the attribute update whenever a player's health changes.
    ///
    ///     Ground truth, one storm-damage tick from the reference capture (client log 20:01:17.440):
    ///
    ///         Channel 12 (the PAWN):       Received RPC: NetMulticast_InvokeGameplayCueExecuted_FromSpec [93.0 bytes]
    ///                                      resolving Default__GE_OutsideSafeZoneDamage_C, the PlayerState and the pawn
    ///         Channel 4 (the PLAYERSTATE): Unreliable Bunch, Size 3.6+50.1   &lt;- the health set update
    ///
    ///     This server sent the second half and not the first, which is the whole of why its health
    ///     bar never redrew: the value arrived (a live `GetAll` proved it matches to the last digit)
    ///     but nothing told the HUD a gameplay effect had happened, and the bar's only inputs take a
    ///     modification REASON.
    ///
    ///     Encoding is the same rule the working BatchedDamageCues RPC established - one presence
    ///     bit per non-bool PARAMETER, then that parameter's flattened leaves. See
    ///     FGameplayEffectSpecForRPC for the spec's own layout and for why the effect context goes
    ///     out as invalid.
    ///
    ///     PredictionKey's presence bit is 0 on purpose: a server-originated cue has no client
    ///     prediction to reconcile, so the parameter is identical to its default and a real server
    ///     would not send it either (FRepLayout::SendPropertiesForRPC only sets the bit when the
    ///     value differs from the default).
    /// </summary>
    public unsafe void SendNetMulticastInvokeGameplayCueExecutedFromSpec(FGameplayEffectSpecForRPC spec) =>
        SendRpc("NetMulticast_InvokeGameplayCueExecuted_FromSpec", writer => {
            var packageMap = (UPackageMapClient) writer.PackageMap!;

            writer.WriteBit(true);                       // the Spec parameter is present
            packageMap.SerializeObject(writer, spec.Def);

            // A dynamic array inside an RPC parameter is a raw uint16 count followed by each
            // element's own leaves - SerializeProperties_DynamicArray_r, RepLayout.cpp:5240. Not a
            // packed int, and no per-element header.
            var count = (ushort) spec.ModifiedAttributes.Count;
            writer.WriteUInt16(count);
            foreach (var modified in spec.ModifiedAttributes) {
                writer.WriteString(modified.AttributeName);
                packageMap.SerializeObject(writer, modified.Attribute);
                packageMap.SerializeObject(writer, modified.AttributeOwner);
                writer.WriteFloat(modified.TotalMagnitude);
            }

            writer.WriteBit(false);   // FGameplayEffectContextHandle: ValidData = 0
            writer.WriteBit(true);    // AggregatedSourceTags: IsEmpty = 1
            writer.WriteBit(true);    // AggregatedTargetTags: IsEmpty = 1
            writer.WriteFloat(spec.Level);
            writer.WriteFloat(spec.AbilityLevel);

            writer.WriteBit(false);   // PredictionKey: absent, i.e. left at its default
        });

    /// <summary>
    ///     AFortPawn::NetMulticast_InvokeGameplayCueExecuted_WithParams - a GameplayCue fired at
    ///     everyone, with a payload the cue's own Blueprint reads.
    ///
    ///     THIS IS HOW THE EMOJI SPRITE GETS ON SCREEN, and it took a whole-pak search to find. The
    ///     emoji's visual is not in the montage and not in the ability: it is
    ///     `GCNS_GM_OnDisplayEmoji`, a FortGameplayCueNotify_Simple bound to
    ///     `GameplayCue.Abilities.Emotes.DisplayEmoji`, whose OnStartParticleSystemSpawned casts the
    ///     cue's SOURCE OBJECT to UAthenaEmojiItemDefinition and calls ConfigureParticleSystem on it
    ///     to put that emoji's texture on the P_Emote_Show_Emoji particle. Exactly three packages in
    ///     the whole install mention ConfigureParticleSystem and all three are those cue notifies
    ///     (`pakreader nameref`), and exactly two mention the tag - the notify and the tag table - so
    ///     NO Blueprint executes it. The client's C++ does, on the server side, which is why this
    ///     server has to send it by hand.
    ///
    ///     ENCODING. Three parameters, each a struct with its own NetSerialize, so the usual rule
    ///     applies - one presence bit per parameter, then the struct's own bits:
    ///
    ///       * the TAG as a flat 14-bit net index (see FortGameplayTags; index 0 is a real tag, so
    ///         an unknown name has to send InvalidNetIndex rather than nothing);
    ///       * the PREDICTION KEY absent, i.e. left at its default - a server-originated cue has no
    ///         client prediction to reconcile, and the receiving side only checks
    ///         `PredictionKey.IsLocalClientKey() == false` before running the cue;
    ///       * the PARAMETERS: 12 RepFlag bits saying which members follow, then both tag containers
    ///         (one bit each, empty), then those members in enum order
    ///         (FGameplayCueParameters::NetSerialize, GameplayEffectTypes.cpp:788).
    ///
    ///     Sent on the PAWN, not the ability system component, because that is where the reference
    ///     capture sends the sibling _FromSpec RPC from - AFortPawn implements
    ///     IAbilitySystemReplicationProxyInterface and forwards to its own ASC, and the pawn's
    ///     channel is always relevant to everyone who can see the emote.
    /// </summary>
    public unsafe void SendNetMulticastInvokeGameplayCueExecutedWithParams(string cueTagName, UObject? sourceObject) =>
        SendRpc("NetMulticast_InvokeGameplayCueExecuted_WithParams", writer => {
            var packageMap = (UPackageMapClient) writer.PackageMap!;

            writer.WriteBit(true);                                  // the GameplayCueTag parameter
            FGameplayTypes.WriteTag(writer, cueTagName);

            writer.WriteBit(false);                                 // PredictionKey: left at default

            writer.WriteBit(true);                                  // the GameplayCueParameters parameter
            FGameplayTypes.WriteCueParameters(writer, packageMap, sourceObject);

            // Sent RELIABLY even though the engine declares the function unreliable, exactly as the
            // sibling _FromSpec sender does and for the same reason: an emote's cue fires once, and
            // a dropped one is a player pressing an emoji and seeing nothing.
        });

    /// <summary>
    ///     AFortPawn::NetMulticast_InvokeGameplayCueAdded_WithParams - the OnActive half of a cue
    ///     that STAYS ON, and the sibling of the Executed sender above in every respect but its
    ///     meaning to the client.
    ///
    ///     Identical parameters, identical encoding (Tag, PredictionKey, Parameters), and it is sent
    ///     on the PAWN for the same reason - AFortPawn is the ASC's replication proxy, which is what
    ///     `IAbilitySystemReplicationProxyInterface::Call_InvokeGameplayCueAdded_WithParams` resolves
    ///     to on this build.
    ///
    ///     ON ITS OWN THIS IS A ONE-SHOT TOO, and that is the trap worth naming: it makes the notify
    ///     run OnActive and nothing more. The cue only persists - and can only later be REMOVED -
    ///     because an element also lands in the ASC's ActiveGameplayCues array, which is what makes
    ///     the client run WhileActive now and Removed later. The pair is what real UE sends together
    ///     (AbilitySystemComponent.cpp:1144), and a PR3.0 capture shows exactly that pair around a
    ///     shield potion: this RPC on the pawn's channel, then one new ActiveGameplayCues element on
    ///     the PlayerState's. Send the RPC without the array element and the effect never stops.
    /// </summary>
    public void SendNetMulticastInvokeGameplayCueAddedWithParams(string cueTagName, UObject? sourceObject,
                                                                 FVector? location = null) =>
        SendRpc("NetMulticast_InvokeGameplayCueAdded_WithParams", writer => {
            var packageMap = (UPackageMapClient) writer.PackageMap!;

            writer.WriteBit(true);                                  // the GameplayCueTag parameter
            FGameplayTypes.WriteTag(writer, cueTagName);

            writer.WriteBit(false);                                 // PredictionKey: left at default

            writer.WriteBit(true);                                  // the GameplayCueParameters parameter
            FGameplayTypes.WriteCueParameters(writer, packageMap, sourceObject, location);
        });

    /// <summary>
    ///     AFortPawn::NetMulticast_Athena_BatchedDamageCues - Athena's damage cue, and the only
    ///     damage-notification RPC in the whole net cache. This is what puts a damage number over a
    ///     hit, flashes the screen, and tells the client a hit was fatal or landed on shield.
    ///
    ///     GROUND TRUTH, not derivation: a real Project-Reboot-3.0 session sends this 78 times
    ///     (`PriveDev/PacketProxy/FortniteGame_PR3.0Client.log`, "Received RPC:
    ///     NetMulticast_Athena_BatchedDamageCues", 54.9 bytes each, always on a PlayerPawn_Athena_C).
    ///     Note what that same capture does NOT show: no such RPC during the 30 storm-damage ticks
    ///     on the local player, so this is the weapon-hit cue, not the health-bar's update channel.
    ///
    ///     Parameter encoding follows the rule proven by ClientReportDamagedResourceBuilding - one
    ///     presence bit per non-bool PARAMETER, then that parameter's flattened leaves with no
    ///     further bits (FRepLayout::SendPropertiesForRPC calls SerializeProperties_r over the
    ///     parameter's whole Cmd range; RepLayout.cpp:5658). Both parameters here are structs, so
    ///     each gets ONE presence bit followed by its members in offset order, with the RepSkip ones
    ///     (NonPlayerLocation/NonPlayerNormal/NonPlayerMagnitude/bIsValid) absent entirely.
    /// </summary>
    public void SendNetMulticastAthenaBatchedDamageCues(FVector location, FVector normal, float magnitude,
                                                        bool bIsFatal, bool bIsShield, bool bIsShieldDestroyed,
                                                        bool bIsBallistic, UObject? hitActor) =>
        SendRpc("NetMulticast_Athena_BatchedDamageCues", writer => {
            // FAthenaBatchedDamageGameplayCues_Shared
            writer.WriteBit(true);
            location.NetSerializeWriteQuantized(writer, 10, 24);  // FVector_NetQuantize10
            normal.NetSerializeWriteFixed(writer, 1, 16);         // FVector_NetQuantizeNormal
            writer.WriteFloat(magnitude);
            writer.WriteBit(false);                // bWeaponActivate - the weapon's own cue, not ours
            writer.WriteBit(bIsFatal);
            writer.WriteBit(false);                // bIsCritical - no headshot model here
            writer.WriteBit(bIsShield);
            writer.WriteBit(bIsShieldDestroyed);
            writer.WriteBit(false);                // bIsShieldApplied - that is healing, not damage
            writer.WriteBit(bIsBallistic);
            writer.WriteBit(false);                // NonPlayerbIsFatal    | the "non-player" half is
            writer.WriteBit(false);                // NonPlayerbIsCritical | for the same batch's
                                                   //                        scenery hit, which this
                                                   //                        server never batches in

            // FAthenaBatchedDamageGameplayCues_NonShared
            writer.WriteBit(true);
            var packageMap = (UPackageMapClient) writer.PackageMap!;
            packageMap.SerializeObject(writer, hitActor);
            packageMap.SerializeObject(writer, null);  // NonPlayerHitActor
        });

    /// <summary>
    ///     A server-&gt;client RPC taking one int32 - APlayerController::ClientCapBandwidth(int32 Cap),
    ///     which AGameModeBase::PostLogin sends right after GenericPlayerInitialization.
    /// </summary>
    public void SendIntRpc(string fieldName, int value) =>
        SendRpc(fieldName, writer => {
            writer.WriteBit(true); // the parameter is present (non-bool RPC param protocol - see FRpcReader)
            writer.WriteInt32(value);
        });

    /// <summary>
    ///     A server-&gt;client RPC taking one bool. Bools carry NO presence bit - the value bit is the
    ///     whole encoding (FRepLayout::SendPropertiesForRPC, mirrored by FRpcReader on the way in).
    /// </summary>
    public void SendBoolRpc(string fieldName, bool value) =>
        SendRpc(fieldName, writer => writer.WriteBit(value));

    /// <summary>
    ///     A server-&gt;client RPC on a replicated COMPONENT. Identical to SendRpc below except the
    ///     content block names the sub-object instead of the actor, and the field index is bounded by
    ///     the COMPONENT's ClassNetCache rather than the actor's - a component has its own index
    ///     space entirely (the ASC's GetMaxIndex is 53, so 6 bits).
    /// </summary>
    private unsafe void SendSubObjectRpc(UObject subObject, FClassNetCache classCache, string fieldName,
                                         Action<FNetBitWriter> writeParams, bool reliable = true) {
        if (Actor == null || Connection == null) return;

        var field = classCache.GetFromName(fieldName);
        if (field == null) {
            Console.WriteLine($"SendSubObjectRpc: '{fieldName}' not found in the sub-object's ClassNetCache, not sending");
            return;
        }

        using var payload = new FNetBitWriter(Connection.PackageMap!, 64);
        payload.WriteBit(false); // bDoChecksum
        uint terminator = 0;
        payload.SerializeIntPacked(&terminator); // empty RepLayout property section

        using var fieldPayload = new FNetBitWriter(Connection.PackageMap!, 64);
        writeParams(fieldPayload);

        var fieldIndex = (uint) field.FieldNetIndex;
        payload.SerializeInt(&fieldIndex, (uint) (classCache.GetMaxIndex() + 1));
        var fieldBits = (uint) fieldPayload.GetNumBits();
        payload.SerializeIntPacked(&fieldBits);
        var fieldData = fieldPayload.GetData();
        fixed (byte* p = fieldData) payload.SerializeBits(p, fieldPayload.GetNumBits());

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = reliable;

        WriteContentBlockHeader(subObject, bunch, hasRepLayout: true);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);
        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        Console.WriteLine($"SendSubObjectRpc: {fieldName} on {subObject.GetFName()} ChIndex={ChIndex} " +
                          $"to={ConnectionName} actor={ActorName} fieldIndex={fieldIndex} " +
                          $"numPayloadBits={numPayloadBits} payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        SendBunch(bunch, false);
    }

    /// <summary>
    ///     UAbilitySystemComponent::ClientActivateAbilitySucceed (field index 9) - the server's
    ///     "yes, that activation stands" for a client that has already predicted it.
    ///
    ///     Without this the client is left holding an unresolved prediction key forever, which is
    ///     why only the FIRST ServerTryActivateAbility ever arrived: it predicts, waits, and does
    ///     not ask again.
    /// </summary>
    public void SendClientActivateAbilitySucceed(UObject abilitySystem, int abilityHandle, FPredictionKey predictionKey) =>
        SendSubObjectRpc(abilitySystem, NativeClassNetCache.FortAbilitySystemComponentCache,
            "ClientActivateAbilitySucceed", writer => {
                writer.WriteBit(true);              // AbilityToActivate is present (non-bool param)
                writer.WriteInt32(abilityHandle);   // FGameplayAbilitySpecHandle's single int32

                writer.WriteBit(true);              // PredictionKey is present
                FPredictionKey.Write(writer, predictionKey);
            });

    /// <summary>
    ///     UAbilitySystemComponent::ClientActivateAbilitySucceedWithEventData(FGameplayAbilitySpecHandle,
    ///     FPredictionKey, FGameplayEventData) - the same activation, carrying the payload an ability
    ///     authored with "Activate Ability From Event" cannot run without.
    ///
    ///     THE CLIENT ASKED FOR THIS BY NAME. Activating GA_DefaultPlayer_Death with the plain
    ///     variant above produced, in its own log, in the same millisecond:
    ///
    ///         GA_DefaultPlayer_Death_C_2147464760 Activated
    ///         Warning: Ability ... expects event data but none is being supplied.
    ///         GA_DefaultPlayer_Death_C_2147464760 EndAbility
    ///
    ///     which is why the capture uses this variant [147.9 bytes] and not the other one. See
    ///     Core/FGameplayTypes for every sub-format in the payload and where each was read from.
    ///
    ///     UNTESTED against a live client.
    /// </summary>
    public void SendClientActivateAbilitySucceedWithEventData(UObject abilitySystem, int abilityHandle,
                                                              FPredictionKey predictionKey,
                                                              AActor? instigator, AActor? target,
                                                              float magnitude = 0f) =>
        SendSubObjectRpc(abilitySystem, NativeClassNetCache.FortAbilitySystemComponentCache,
            "ClientActivateAbilitySucceedWithEventData", writer => {
                writer.WriteBit(true);              // AbilityToActivate
                writer.WriteInt32(abilityHandle);

                writer.WriteBit(true);              // PredictionKey
                FPredictionKey.Write(writer, predictionKey);

                writer.WriteBit(true);              // TriggerEventData
                Core.FGameplayTypes.WriteEventData(writer, (UPackageMapClient) writer.PackageMap!,
                                                   instigator, target, magnitude);
            });

    /// <summary>
    ///     UAbilitySystemComponent::ClientEndAbility - the server telling a client that an ability it
    ///     is running has finished. THE OTHER HALF of the activation handshake, and the one this
    ///     project was missing entirely.
    ///
    ///     Why a server has to send it at all: an ability's own graph runs on both sides, but the
    ///     parts that need authority do not. A throw's SpawnProjectileAndWait is a SpawnActor-style
    ///     task, and those only spawn under IsNetAuthority() - so on the client the task's Created
    ///     delegate never fires, the WaitDelay(PostThrowEndDelay) after it never starts, and
    ///     K2_AbilityCompleted is never reached. The client's ability therefore CANNOT end by itself,
    ///     and until it does, Spec->IsActive() stays true and every further activation is refused
    ///     with "Can't activate instanced per actor ability ... already a currently active instance".
    ///     A real server ends its own copy and sends this; ReplicateEndOrCancelAbility's authority
    ///     branch is exactly that call.
    ///
    ///     The PREDICTION KEY MUST MATCH the one the client activated with. RemoteEndOrCancelAbility
    ///     walks the spec's instances and ends only the instance whose activation key equals the one
    ///     in this ActivationInfo; a fresh key ends nothing and fails silently.
    ///
    ///     FGameplayAbilityActivationInfo is an ordinary struct with no NetSerialize, so RepLayout
    ///     flattens it into its three replicated members in declaration order: ActivationMode (a
    ///     byte), the bCanBeEndedByOtherInstance bit, and PredictionKeyWhenActivated. Confirmed = 3
    ///     is the mode a client-predicted, server-acknowledged activation is in by this point.
    /// </summary>
    public void SendClientEndAbility(UObject abilitySystem, int abilityHandle, FPredictionKey predictionKey) =>
        SendSubObjectRpc(abilitySystem, NativeClassNetCache.FortAbilitySystemComponentCache,
            "ClientEndAbility", writer => {
                writer.WriteBit(true);              // AbilityToEnd present
                writer.WriteInt32(abilityHandle);

                writer.WriteBit(true);              // ActivationInfo present
                writer.WriteByte(3);                // EGameplayAbilityActivationMode::Confirmed
                writer.WriteBit(false);             // bCanBeEndedByOtherInstance
                FPredictionKey.Write(writer, predictionKey);
            });

    /// <param name="reliable">
    ///     Must match the UFUNCTION's own declaration. Getting this wrong is not cosmetic: an
    ///     unreliable-in-UE RPC sent reliably at gameplay frequency (ClientAckGoodMove fires at the
    ///     client's move rate) fills the 256-entry reliable buffer and kills the connection.
    /// </param>
    private unsafe void SendRpc(string fieldName, Action<FNetBitWriter> writeParams, bool reliable = true) {
        if (Actor == null || Connection == null) return;

        var classCache = NativeClassNetCache.Get(Actor);
        var field = classCache.GetFromName(fieldName);
        if (field == null) {
            Console.WriteLine($"SendRpc: '{fieldName}' not found in {Actor.GetFName()}'s ClassNetCache, not sending");
            return;
        }

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = reliable;

        var payload = new FNetBitWriter(Connection.PackageMap, 64);
        payload.WriteBit(false); // bDoChecksum
        uint terminator = 0;
        payload.SerializeIntPacked(&terminator); // empty RepLayout property section

        var fieldPayload = new FNetBitWriter(Connection.PackageMap!, 64);
        writeParams(fieldPayload);

        var fieldIndex = (uint) field.FieldNetIndex;
        payload.SerializeInt(&fieldIndex, (uint) (classCache.GetMaxIndex() + 1));
        var fieldBits = (uint) fieldPayload.GetNumBits();
        payload.SerializeIntPacked(&fieldBits);
        var fieldData = fieldPayload.GetData();
        fixed (byte* p = fieldData) payload.SerializeBits(p, fieldPayload.GetNumBits());

        bunch.WriteBit(true); // bHasRepLayout
        bunch.WriteBit(true); // bIsActor

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);
        var payloadData = payload.GetData();
        // Reliable RPCs here are one-shot handshake steps worth always seeing; unreliable ones
        // (ClientAckGoodMove) repeat at gameplay rate and would bury everything else.
        if (reliable || NetDebugLog.VerboseEnabled) Console.WriteLine($"SendRpc: {fieldName} fieldIndex={fieldIndex} numPayloadBits={numPayloadBits} payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        SendBunch(bunch, false);
    }

    public override void Tick() {
        base.Tick();
        // TODO: ProcessQueuedBunches
    }

    public override bool CanStopTicking() {
        return base.CanStopTicking() /* PendingGuidResolves / QueuedBunches */;
    }

    /// <summary>
    ///     Simplified port of UActorChannel::ReceivedBunch. A bunch can contain several back-to-back
    ///     "content blocks" (one per replicated object). Each one starts with a 2-bit header
    ///     (bHasRepLayout, bIsActor) followed by a packed bit count and that many payload bits.
    ///     A content block's payload can itself contain several fields (property updates / RPC
    ///     calls) back-to-back - see ReadContentBlockFields.
    /// </summary>
    protected override unsafe void ReceivedBunch(FInBunch bunch) {
        while (!bunch.AtEnd() && !bunch.IsError()) {
            var bHasRepLayout = bunch.ReadBit();
            if (bunch.IsError()) break;

            var bIsActor = bunch.ReadBit();
            if (bunch.IsError()) break;

            // UActorChannel::ReadContentBlockHeader (DataChannel.cpp:3293). bIsActor=1 means "this
            // block is for the channel's own actor", and the reader can go straight to the payload.
            // Anything else is a SUB-OBJECT - a replicated component - and carries its own object
            // reference first. Every GAS RPC arrives this way (UAbilitySystemComponent is a
            // component, not an actor), so until this was read rather than bailed on, pulling the
            // trigger threw away the rest of the bunch without saying what was in it.
            UObject? subObject = null;
            var subObjectPath = string.Empty;
            string? subObjectHeader = null;

            if (!bIsActor) {
                subObject = ((UPackageMapClient) Connection!.PackageMap!)
                    .SerializeObjectRead(bunch, out var subObjectGuid, out subObjectPath);

                if (bunch.IsError()) {
                    Console.WriteLine($"UActorChannel.ReceivedBunch: content block header decode failed on ChIndex={ChIndex} " +
                                      $"Actor={Actor?.GetFName()} - dropping the rest of the bunch");
                    return;
                }

                // The client can name a sub-object by PATH when this server never assigned it an id -
                // which is exactly how every interaction arrives, since InteractionComp is a default
                // sub-object of the controller and so is stably named. SerializeObjectRead refuses to
                // resolve a path (real UE refuses to CREATE from one, and rightly), but resolving it
                // against the channel actor's OWN sub-objects is a different thing entirely: nothing
                // is invented, the leaf name is only matched against components this server already
                // built. Without this the whole content block is skipped and chests, ammo boxes and
                // doors do nothing at all.
                if (subObject == null && subObjectPath.Length > 0 && Actor is APlayerController pc) {
                    var leaf = subObjectPath[(subObjectPath.LastIndexOf('.') + 1)..];
                    subObject = pc.ResolveNamedSubObject(leaf);
                }

                subObjectHeader = $"UActorChannel.ReceivedBunch: SUB-OBJECT content block ChIndex={ChIndex} " +
                                  $"Actor={Actor?.GetFName()} guid={subObjectGuid} " +
                                  $"path='{(subObjectPath.Length > 0 ? subObjectPath : "(none - referenced by id)")}' " +
                                  $"resolved={(subObject != null ? subObject.GetFName().ToString() : "NULL")}";
            }

            uint numPayloadBits = 0;
            bunch.SerializeIntPacked(&numPayloadBits);
            if (bunch.IsError()) break;

            // Logged only now, with the payload size attached. A sub-object block carrying ZERO bits
            // is UE merely naming the component, not calling anything on it - and without this number
            // an empty block and a real RPC look identical in the log, which cost a round: the header
            // said the component resolved, nothing followed, and there was no way to tell "decoded
            // nothing" from "there was nothing to decode".
            if (subObjectHeader != null) {
                // bitsLeft as well as the declared count. A block whose payload is LARGER than what
                // remains in the bunch is truncated - payloadEnd clamps to the end, the field loop
                // never runs, and the result looks exactly like an empty block. That ambiguity is what
                // made a real 2937-bit ServerAttemptInteract read as "nothing to decode".
                var bitsLeft = bunch.GetBitsLeft();
                var truncatedNote = numPayloadBits > bitsLeft ? "  *** TRUNCATED - payload exceeds the bunch ***" : "";
                Console.WriteLine($"{subObjectHeader} numPayloadBits={numPayloadBits} bitsLeft={bitsLeft}{truncatedNote}");
            }

            var payloadStart = bunch.Pos;
            var payloadEnd = payloadStart + Math.Min((long) numPayloadBits, bunch.GetBitsLeft());

            var rawHex = HexDumpBits(bunch, payloadStart, Math.Min(payloadEnd, payloadStart + 512));
            var truncated = payloadEnd - payloadStart > 512 ? "..." : "";
            if (NetDebugLog.VerboseEnabled) Console.WriteLine($"UActorChannel.ReceivedBunch: content block ChIndex={ChIndex} Actor={Actor?.GetFName()} bHasRepLayout={bHasRepLayout} numPayloadBits={numPayloadBits} raw={rawHex}{truncated}");

            // A sub-object this server cannot resolve has no ClassNetCache either, so its field
            // indices cannot be decoded - but NumPayloadBits below still resyncs the bunch, so the
            // NEXT block (and every later one) survives. That is the whole difference from before:
            // one unreadable component block used to cost the entire remainder of the bunch.
            if (!bIsActor) {
                // A component we could not resolve has no ClassNetCache either, so its field indices
                // cannot be decoded - but NumPayloadBits below still resyncs the bunch, so the rest
                // survives either way.
                var subObjectCache = subObject switch {
                    UFortAbilitySystemComponent => NativeClassNetCache.FortAbilitySystemComponentCache,
                    UFortControllerComponent_Interaction => NativeClassNetCache.FortControllerComponentInteractionCache,
                    UGameplayAbilityInstance => NativeClassNetCache.ThrownAbilityCache,
                    _ => null
                };

                if (subObjectCache != null) {
                    try {
                        ReadContentBlockFields(bunch, subObjectCache, payloadEnd, subObject);
                    } catch (Exception ex) {
                        Console.WriteLine($"UActorChannel.ReceivedBunch: sub-object field decode threw on ChIndex={ChIndex}: {ex}");
                    }
                } else if (subObject != null) {
                    // RESOLVED, but this server has no ClassNetCache for its class, so the field
                    // index (a bounded int whose width is GetMaxIndex()+1) cannot even be read - let
                    // alone named. Kept distinct from the unresolved case below because they call
                    // for opposite fixes and the old message claimed "unresolved" for both.
                    //
                    // This is the line to watch when a new ability starts talking back: a
                    // UGameplayAbilityInstance turning up here means the ReplicateYes chain worked
                    // and the client is now sending that ability's own Server_* RPCs (for a grenade,
                    // Server_SpawnProjectile). The bit count is the first real evidence about its
                    // parameter layout - a lone Location+Direction pair should be two FVectors.
                    Console.WriteLine($"UActorChannel.ReceivedBunch: skipping {numPayloadBits} bits on sub-object " +
                                      $"'{subObject.GetFName()}' of class '{subObject.GetClass().NativePackagePath ?? subObject.GetClass().GetFName().ToString()}' " +
                                      "- it RESOLVED, but there is no ClassNetCache for that class so the field index " +
                                      "cannot be decoded. The rest of the bunch is still read.");
                } else {
                    Console.WriteLine($"UActorChannel.ReceivedBunch: skipping {numPayloadBits} bits from unresolved " +
                                      $"sub-object '{subObjectPath}' - the rest of the bunch is still read");
                }
            } else if (Actor is AFortAthenaVehicle vehicleActor && !FortVehicleNetCaches.DecodeEnabled) {
                // LEFT ALONE ON PURPOSE. A vehicle's field numbering is derived rather than verified
                // (see FortVehicleNetCaches.DecodeEnabled), and a wrong numbering does not mislabel a
                // field - it reads the index at the wrong width and turns the rest of the block into
                // noise. Nothing needs these RPCs yet, and the block carries its own size, so skipping
                // it costs nothing and keeps the bunch intact.
                if (_warnedVehicleBlock.Add(vehicleActor.GetType().Name)) {
                    Console.WriteLine($"UActorChannel.ReceivedBunch: skipping content blocks on " +
                                      $"{vehicleActor.GetFName()} - vehicle field numbering is unverified, " +
                                      "VEHICLE_RPC_DECODE=1 to attempt it anyway.");

                    // ...but measure it on the way past. One block is usually enough to pin the number
                    // the client used, which is the one piece of information the SDK headers cannot
                    // supply.
                    CalibrateFieldIndexBound(bunch, bunch.Pos, payloadEnd, vehicleActor.GetFName().ToString());

                    // AND KEEP THE BYTES. The calibration came back with nine candidate bounds
                    // (18-26), which is not nine possibilities - it is one: every bound in (16, 32]
                    // reads the index with the same five bits, so the client's cache has between 17
                    // and 32 net fields. That is FAR smaller than the vehicle chain this server
                    // derived, and a number that small says the block may not belong to the vehicle
                    // class at all. Only the raw bits can settle which, so they are kept rather than
                    // reasoned about.
                    DumpRawRpcPayload(bunch, $"{vehicleActor.GetFName()}#block", bunch.Pos, payloadEnd,
                                      "vehicle content block, field numbering unverified");
                }

                bunch.Pos = payloadEnd;
            } else if (Actor != null) {
                try {
                    ReadContentBlockFields(bunch, NativeClassNetCache.Get(Actor), payloadEnd);
                } catch (Exception ex) {
                    // A ClassNetCache gap (an incompletely ground-truthed class - see
                    // NativeClassNetCache's own comments for which ones still are) desyncs the
                    // bounded-int field-index read and can throw (e.g. FBitReader::SerializeInt
                    // overflowing). The outer NumPayloadBits resync right below is ground truth
                    // regardless, so this is recoverable - better to lose this one content block
                    // than crash the whole process.
                    Console.WriteLine($"UActorChannel.ReceivedBunch: ReadContentBlockFields threw on ChIndex={ChIndex} Actor={Actor.GetFName()}: {ex}");
                }
            }

            // The outer NumPayloadBits is ground truth regardless of how the inner field decode
            // above went (it relies on a best-effort, unverified ClassNetCache guess - see
            // NativeClassNetCache) - always resync here so a bad guess can never desync the bunch.
            bunch.Pos = payloadEnd;
        }
    }

    /// <summary>
    ///     Walks the individual fields (property updates / RPC calls) packed into one actor content
    ///     block's payload, per UActorChannel::ReadFieldHeaderAndPayload: each field is a bounded-int
    ///     RepIndex (range = ClassNetCache->GetMaxIndex()+1, real UE's FBitReader::ReadInt encoding,
    ///     NOT SerializeIntPacked), a packed bit count, then that many payload bits. A field that
    ///     names a registered RPC (see NativeRpcHandlers) gets its parameters decoded and its
    ///     handler invoked; anything else is just logged and skipped, same as before - the field's
    ///     own declared bit count always resyncs bunch.Pos at the end of the loop body regardless of
    ///     what a handler actually consumed, so a handler bug can't desync the rest of the bunch.
    /// </summary>
    /// <summary>
    ///     Works out, FROM THE WIRE, what field-index bound a content block was written with - and so
    ///     how many net fields the client's copy of this class really has.
    ///
    ///     WHY MEASURE INSTEAD OF DERIVE. A field index is a BOUNDED int: its width depends on the
    ///     class's total net field count, so a server whose count is off by one decodes the index at
    ///     the wrong width and turns the rest of the block into noise. Deriving that count from the
    ///     Dumper-7 SDK headers works for properties and cannot work for functions - the headers carry
    ///     no FUNC_Net flag, so "is this an RPC" ends up inferred from the name. That guess produced
    ///     field sizes of three billion bits.
    ///
    ///     The wire settles it. A correctly-read block consumes EXACTLY its payload: index, packed
    ///     size, that many bits, repeat, ending on the last bit. Wrong bounds almost never do. So try
    ///     the plausible ones and keep those that parse cleanly - one survivor is the answer, several
    ///     means the block was too short to distinguish them and nothing is claimed.
    ///
    ///     This is a DIAGNOSTIC, not a decoder: it reports the number so the generated table can be
    ///     corrected. Guessing a field's identity from a calibrated index would be the same mistake
    ///     one level down.
    /// </summary>
    private unsafe void CalibrateFieldIndexBound(FInBunch bunch, long blockStart, long payloadEnd, string what) {
        var survivors = new List<int>();

        // Around the derived count, generously: a handful of missed or invented RPCs is exactly the
        // error being hunted, and the cost of a wider sweep is a few hundred arithmetic operations.
        for (var candidate = 2; candidate <= 512; candidate++) {
            bunch.Pos = blockStart;
            if (!ParsesCleanly(bunch, (uint) candidate, payloadEnd)) continue;

            survivors.Add(candidate);
            if (survivors.Count > 8) break;
        }

        bunch.Pos = blockStart;

        Console.WriteLine(survivors.Count switch {
            0 => $"UActorChannel.Calibrate: no field-index bound between 2 and 512 parses this {what} block " +
                 "cleanly - it may not be a content block at all.",
            1 => $"UActorChannel.Calibrate: {what} was written with a field-index bound of {survivors[0]} " +
                 $"(this server's cache says {NativeClassNetCache.Get(Actor!).GetMaxIndex() + 1}). " +
                 "Correct the generated field list to that many net fields.",
            _ => $"UActorChannel.Calibrate: {what} block is consistent with bounds " +
                 $"[{string.Join(", ", survivors)}] - too short to tell them apart."
        });
    }

    /// <summary>Whether a block parses to exactly its end when field indices are read with this bound.</summary>
    private static unsafe bool ParsesCleanly(FInBunch bunch, uint bound, long payloadEnd) {
        while (bunch.Pos < payloadEnd) {
            var index = bunch.ReadInt(bound);
            if (bunch.IsError() || index >= bound) return false;

            uint size = 0;
            bunch.SerializeIntPacked(&size);
            if (bunch.IsError()) return false;

            var end = bunch.Pos + (long) size;
            if (end > payloadEnd) return false;

            bunch.Pos = end;
        }

        return bunch.Pos == payloadEnd && !bunch.IsError();
    }

    private unsafe void ReadContentBlockFields(FInBunch bunch, FClassNetCache classCache, long payloadEnd,
                                               UObject? subObject = null) {
        var maxIndex = classCache.GetMaxIndex();
        var target = subObject ?? Actor;
        var rpcTable = subObject != null
            ? NativeRpcHandlers.GetForSubObject(subObject)
            : Actor != null ? NativeRpcHandlers.Get(Actor) : null;

        // Not gated on verbose, and not silent on failure. Every OTHER path out of this loop logs
        // something, so when a sub-object block resolved and then produced no output at all the only
        // candidates left were the two bare `break`s below - and being unable to tell "read nothing"
        // from "never entered the loop" cost a round of guessing.
        if (subObject != null) {
            Console.WriteLine($"UActorChannel.ReadContentBlockFields: {subObject.GetFName()} " +
                              $"maxIndex={maxIndex} span={payloadEnd - bunch.Pos} bits " +
                              $"rpcTable={(rpcTable == null ? "NONE" : rpcTable.Count.ToString())} " +
                              $"isError={bunch.IsError()} pos={bunch.Pos} payloadEnd={payloadEnd}");
        }

        while (bunch.Pos < payloadEnd && !bunch.IsError()) {
            var repIndex = (int) bunch.ReadInt((uint) (maxIndex + 1));
            if (bunch.IsError()) {
                Console.WriteLine($"UActorChannel.ReadContentBlockFields: field-index read FAILED on " +
                                  $"{target?.GetFName()} (maxIndex={maxIndex}) - abandoning this block");
                break;
            }

            uint fieldNumBits = 0;
            bunch.SerializeIntPacked(&fieldNumBits);
            if (bunch.IsError()) {
                Console.WriteLine($"UActorChannel.ReadContentBlockFields: field-size read FAILED on " +
                                  $"{target?.GetFName()} after index {repIndex} - abandoning this block");
                break;
            }

            var fieldStart = bunch.Pos;

            // A FIELD CANNOT BE BIGGER THAN THE BLOCK IT IS IN, and when it claims to be, the field
            // INDEX was read at the wrong bit width - which means everything after it is noise. The
            // vehicle work produced exactly this, three lines in a row:
            //
            //     field[49]=bWeaponActivated ... (1826968 payload bits)
            //     field[49]=bWeaponActivated ... (173695128 payload bits)
            //     field[49]=bWeaponActivated ... (3489014768 payload bits)
            //
            // Clamping quietly (which is what Math.Min alone did) hides that: the numbers scroll past
            // as a curiosity while the block is silently abandoned anyway. Saying it outright turns an
            // impossible size into what it actually is - proof that this class's ClassNetCache does
            // not match the client's.
            if (fieldNumBits > (ulong) (payloadEnd - fieldStart)) {
                Console.WriteLine($"UActorChannel.ReadContentBlockFields: field[{repIndex}] on " +
                                  $"{target?.GetFName()} claims {fieldNumBits} bits but only " +
                                  $"{payloadEnd - fieldStart} remain in the block - this class's field " +
                                  $"numbering does not match the client's (maxIndex={maxIndex}). " +
                                  "Abandoning the block.");
                break;
            }

            var fieldEnd = Math.Min(fieldStart + (long) fieldNumBits, payloadEnd);
            var fieldName = classCache.GetFromIndex(repIndex)?.Name ?? "?";

            // RPC_DUMP=<Name>[,<Name>...] - print the RAW payload of a named incoming RPC, once each,
            // as hex, before anything tries to interpret it.
            //
            // The counterpart to REPLAYOUT_PROBE_HANDLE, and it exists for the same reason: when a
            // parameter layout is a guess, the only way to stop guessing is to look at the bytes.
            // ServerUpdateLevelVisibility is what prompted it - its declared layout (two FNames and
            // a bit) read a string length of nonsense and walked off the end of the bunch, and no
            // amount of re-reasoning about UPackageMap::StaticSerializeName was going to settle
            // which of the several plausible framings 10.40 actually uses.
            DumpRawRpcPayload(bunch, fieldName, fieldStart, fieldEnd);

            if (rpcTable != null && rpcTable.TryGetValue(fieldName, out var rpcDef)) {
                // An RPC with no declared layout yet - see FRpcDef.DumpRawAlways. Passing a reason
                // bypasses the RPC_DUMP gate, which is the point: this is the one case where the
                // bytes are the entire content of the message as far as this server is concerned.
                if (rpcDef.DumpRawAlways)
                    DumpRawRpcPayload(bunch, fieldName, fieldStart, fieldEnd,
                                      "its parameter layout is still being derived");

                try {
                    var values = FRpcReader.ReadParams(bunch, rpcDef.Params);

                    // The one check available on an RPC layout that was derived rather than probed.
                    // A field carries no internal framing, so a decode that is off by a few bits
                    // yields plausible values and no error at all; the only thing that gives it away
                    // is finishing somewhere other than the field's own declared end.
                    if (rpcDef.ExpectsFullDecode && !bunch.IsError()) {
                        var leftover = fieldEnd - bunch.Pos;
                        if (leftover != 0) {
                            Console.WriteLine($"UActorChannel.ReceivedBunch: {fieldName} decoded {bunch.Pos - fieldStart} " +
                                              $"of {fieldNumBits} bits - {leftover} left over. The declared parameter " +
                                              "layout does not match the wire; treat the decoded values as suspect.");
                            DumpRawRpcPayload(bunch, fieldName, fieldStart, fieldEnd,
                                              $"{leftover} bits left over after the declared layout");
                        }
                    }

                    rpcDef.Invoke(target as AActor ?? Actor!, values);
                } catch (Exception ex) {
                    Console.WriteLine($"UActorChannel.ReceivedBunch: RPC {fieldName} failed to decode on ChIndex={ChIndex} Actor={Actor?.GetFName()}: {ex.GetType().Name}: {ex.Message}");
                    DumpRawRpcPayload(bunch, fieldName, fieldStart, fieldEnd, "the declared layout threw while reading it");
                }
            } else if (subObject != null) {
                // Not gated on verbose: a component field arrives only when the player actually does
                // something, and each unrecognised one names the next thing worth implementing.
                Console.WriteLine($"UActorChannel.ReceivedBunch:   SUB-OBJECT field[{repIndex}]={fieldName} " +
                                  $"on {subObject.GetFName()} ({fieldNumBits} payload bits) - no handler");
            } else {
                // NOT gated on verbose, for the same reason the sub-object case above isn't: a
                // top-level field the client sent that names no known RPC is exactly as informative
                // as an unrecognised sub-object one - each names the next thing worth implementing,
                // and staying silent here is how a genuine building-placement attempt (e.g.
                // ServerCreateBuildingActor, which ClassNetCache can name but this project has never
                // had a handler for) could arrive and leave no trace at all. Previously gated on
                // NET_VERBOSE, which meant every ordinary test run - including every past attempt to
                // confirm "the client never even tries to build" - could not actually tell an
                // unattempted RPC apart from an attempted-but-silently-dropped one.
                // ServerUpdateCamera specifically: sent every tick regardless of what the player is
                // doing, so it never names a new gate the way the rest of this branch's discoveries
                // did (ServerCreateBuildingActor, ServerSetPlayerBuildableClass, ...) - pure log
                // volume with nothing left to learn from it. Everything else stays unconditional.
                if (fieldName != "ServerUpdateCamera") {
                    // "no NativeRpcHandlers entry", NOT "nothing happens" - and the difference has
                    // already misled a reading of this log once. HandlePossessionRpc runs for EVERY
                    // field by name, whether or not the table knows it, which is how
                    // ServerAcknowledgePossession is handled while still printing this line.
                    Console.WriteLine($"UActorChannel.ReceivedBunch:   field[{repIndex}]={fieldName} on ChIndex={ChIndex} Actor={Actor?.GetFName()} ({fieldNumBits} payload bits) - no NativeRpcHandlers entry (may still be handled by name)");
                }
            }

            // Both of these are about the channel's ACTOR; a component's fields never mean either.
            if (subObject == null) {
                HandlePossessionRpc(fieldName);

                // Any accepted client move leaves the server owing the client EITHER an ack or a
                // correction, and this is where real UE decides which: SendClientAdjustment runs off
                // the move it just processed and is a straight if/else on bAckGoodMove
                // (CharacterMovementComponent.cpp:9002). Both live here for the same reason.
                //
                // ORDER MATTERS AND IS NOT COSMETIC. A correction names the timestamp of a move that
                // must still be in the client's SavedMoves, and an ack retires every move up to ITS
                // timestamp - so an ack sent between the request and the correction deletes the very
                // move the correction is about to name (GetSavedMoveIndex, line 10256, returns
                // INDEX_NONE for anything at or below LastAckedMove). Running the correction FIRST,
                // on the same received move, is what closes that window to nothing.
                //
                // Leaving the correction to the next replication pass - which is where it used to
                // run - left a window of one update tick, and the faster the client was moving the
                // more traffic there was to lose the race to. That is exactly the reported symptom:
                // getting out at speed sometimes left the player stuck in Driving.
                if (fieldName.StartsWith("ServerMove", StringComparison.Ordinal)) {
                    SendMovementCorrection();
                    SendClientAckGoodMove();
                }
            }

            bunch.Pos = fieldEnd;
        }
    }

    /// <summary>Side-effect-free hex dump of [startBit, endBit) - does not touch bunch.Pos.</summary>
    private static string HexDumpBits(FInBunch bunch, long startBit, long endBit) {
        var numBits = endBit - startBit;
        var bytes = new byte[(numBits + 7) / 8];
        for (long i = 0; i < numBits; i++) {
            if (bunch.BufferBits[(int) (startBit + i)]) bytes[i >> 3] |= (byte) (1 << (int) (i & 7));
        }

        return Convert.ToHexString(bytes);
    }
}