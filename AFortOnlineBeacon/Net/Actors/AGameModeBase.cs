namespace AFortOnlineBeacon.Net.Actors;

public class AGameModeBase : AInfo {
    public AGameModeBase() => OptionsString = string.Empty;
    
    /// <summary>
    ///     Save options string and parse it when needed
    /// </summary>
    public string OptionsString { get; set; }
    
    public AGameSession? GameSession { get; set; }
    public AGameState? GameState { get; set; }

    public virtual void InitGame(string mapName, string options, out string errorMessage) {
        // Default error.
        errorMessage = string.Empty;

        // Find world.
        var world = GetWorld();

        // Save Options for future use
        OptionsString = options;

        var spawnInfo = new FActorSpawnParameters {
            Instigator = GetInstigator(),
            ObjectFlags = EObjectFlags.RF_Transient
        };

        GameSession = world!.SpawnActor<AGameSession>(GUClassArray.StaticClass<AGameSession>(), spawnInfo);

        InitGameState();
    }

    /// <summary>
    ///     Simplified stand-in for AGameModeBase::InitGameState. Real UE only flips
    ///     GameState->bReplicatedHasBegunPlay true, and only advances MatchState past
    ///     EnteringMap, once AGameModeBase::StartMatch actually runs (normally gated behind
    ///     ReadyToStartMatch/a warmup countdown) - this project has no warmup flow yet, so the
    ///     match is declared fully started immediately on spawn. A real PR3.0 client capture
    ///     (2026-08-24) showed MatchState reaching InProgress a few seconds BEFORE the loading
    ///     screen actually dismissed - bReplicatedHasBegunPlay alone was confirmed NOT sufficient,
    ///     see AGameState.cs.
    /// </summary>
    public virtual void InitGameState() {
        var world = GetWorld();
        if (world == null) return;

        var spawnInfo = new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient };
        GameState = world.SpawnActor<AGameState>(GUClassArray.StaticClass<AGameState>(), spawnInfo);

        if (GameState != null) {
            GameState.SetRole(ENetRole.ROLE_Authority);
            GameState.SetReplicates(true);
            GameState.bReplicatedHasBegunPlay = true;
            // The one working 10.40 capture we have flips the client's UI to InGame_BR while
            // MatchState is still WaitingToStart, and only moves to InProgress 85ms LATER:
            //   16:58:21.201  MatchState changed previous=EnteringMap current=WaitingToStart
            //   16:58:21.927  [NativeTick] change state to InGame_BR
            //   16:58:22.012  MatchState changed previous=WaitingToStart current=InProgress
            // This server used to jump straight to InProgress, which makes AGameState::OnRep_MatchState
            // run HandleMatchIsWaitingToStart AND HandleMatchHasStarted back to back (both
            // HealthSnapshot lines show up in our client log, so it really does take both paths) -
            // i.e. the client is told the match already started while GamePhase is still Warmup.
            // MATCH_STATE overrides it without a rebuild.
            GameState.MatchState = new FName(
                Environment.GetEnvironmentVariable("MATCH_STATE") is { Length: > 0 } ms ? ms : "WaitingToStart");

            // AFortGameStateAthena's own match configuration. The defaults on AGameState already
            // match raider3.5's OnReadyToStartMatch (Warmup + no battle bus); GAME_PHASE is here
            // so a phase can be tried against a live client without a rebuild, since which phase
            // the client's UI wants is still being narrowed down.
            var gamePhase = Environment.GetEnvironmentVariable("GAME_PHASE");
            if (!string.IsNullOrWhiteSpace(gamePhase) && Enum.TryParse<EAthenaGamePhase>(gamePhase, true, out var parsedPhase)) {
                GameState.GamePhase = parsedPhase;
            }

            // AFortGameStateAthena::CurrentPlaylistInfo.BasePlaylist. A working 10.40 capture shows
            // the client's UI state going to InGame_BR within a second of OnRep_CurrentPlaylistInfo
            // resolving this asset, and staying Invalid (which blocks the loading screen) without it.
            // The map's own FortWorldManager, found with Tools/MapActorDump: class FortWorldManager,
            // name DO_NOT_DELETE_FortWorldManager, in Athena_Terrain's persistent level.
            var worldManagerPath = Environment.GetEnvironmentVariable("WORLD_MANAGER_ACTOR")
                                   ?? "/Game/Athena/Maps/Athena_Terrain.Athena_Terrain:PersistentLevel.DO_NOT_DELETE_FortWorldManager";
            GameState.WorldManager = UAssetRegistry.GetOrCreateSubObject(worldManagerPath);
            Console.WriteLine($"AGameModeBase.InitGameState: WorldManager='{worldManagerPath}' (handle 39)");

            var playlistPath = Environment.GetEnvironmentVariable("PLAYLIST_ASSET")
                               ?? "/Game/Athena/Playlists/Playlist_DefaultSolo.Playlist_DefaultSolo";
            GameState.BasePlaylist = UAssetRegistry.GetOrCreate(playlistPath);

            Console.WriteLine($"AGameModeBase.InitGameState: BasePlaylist='{playlistPath}' (handle 155), " +
                              $"bGameModeWillSkipAircraft={GameState.bGameModeWillSkipAircraft}, " +
                              $"CurrentPlaylistId={GameState.CurrentPlaylistId}");

            // Athena's loading screen blocks on this - see AFortTimeOfDayManager for the evidence.
            // Real UE reaches it through AFortGameStateBase::SetTimeOfDayManager, which the client
            // binary confirms is authority-only, so the server has to spawn one and let the
            // GameState's replicated FortTimeOfDayManager (handle 22) carry the reference across.
            var timeOfDayManager = world.SpawnActor<AFortTimeOfDayManager>(
                GUClassArray.StaticClass<AFortTimeOfDayManager>(), spawnInfo);

            if (timeOfDayManager != null) {
                timeOfDayManager.SetRole(ENetRole.ROLE_Authority);
                timeOfDayManager.SetReplicates(true);
                GameState.FortTimeOfDayManager = timeOfDayManager;
            }

            // The bus does NOT launch here any more. It used to, which meant GamePhase went
            // straight to Aircraft at match start - fine while everyone spawned on the main island
            // and the phase was cosmetic, wrong now that there is a warmup island to wait on.
            // See StartWarmupClock / TickPhases.
        }
    }

    /// <summary>
    ///     How long the warmup lasts before the bus, in seconds. 0 skips it and launches the bus (if
    ///     it is enabled at all) as soon as the first player joins, which is what this used to do.
    ///
    ///     60 rather than a figure taken from a capture: the one number that WAS taken from a capture
    ///     turned out to be the length of a human pressing keys on a modded server, not the game's
    ///     own pacing. A round number that is obviously a choice beats a wrong number that looks
    ///     researched.
    /// </summary>
    private static float WarmupSeconds => EnvFloat("WARMUP_SECONDS", 60f);

    private bool _warmupStarted;
    private bool _aircraftLaunched;
    private float _warmupEndTime;

    /// <summary>
    ///     Start the warmup countdown, on the FIRST player to join rather than at server start.
    ///
    ///     Deliberate: this server is started and then joined by hand, often a minute or two later,
    ///     so a countdown anchored to server start would already have expired by the time anyone was
    ///     on the island. Anchoring it to the first join is what makes the warmup actually last
    ///     WARMUP_SECONDS from the player's point of view.
    /// </summary>
    public void StartWarmupClock(float now) {
        if (_warmupStarted || GameState == null) return;
        _warmupStarted = true;
        _warmupEndTime = now + WarmupSeconds;

        GameState.GamePhase = EAthenaGamePhase.Warmup;
        GameState.WarmupCountdownStartTime = now;
        GameState.WarmupCountdownEndTime = _warmupEndTime;
        GameState.AircraftStartTime = _warmupEndTime;

        Console.WriteLine($"AGameModeBase: warmup started at {now:F1}, ends at {_warmupEndTime:F1} " +
                          $"(WARMUP_SECONDS={WarmupSeconds:F0}), GamePhase={GameState.GamePhase}");
    }

    /// <summary>The bus, once launched, until every client has been shown it.</summary>
    private AFortAthenaAircraft? _pendingBoarding;

    private readonly HashSet<UNetConnection> _boardedConnections = new();

    /// <summary>Pawns already handed back to their client - see TickBoarding's second half.</summary>
    private readonly HashSet<APawn> _restartedPawns = new();

    /// <summary>
    ///     Put each client's CAMERA on the bus, once the bus actually exists on that client.
    ///
    ///     THE ORDERING IS THE WHOLE POINT, and getting it wrong is why the first attempt changed
    ///     nothing. SpawnAircraft used to send the three camera RPCs inline, which put them on the
    ///     wire BEFORE the aircraft had a channel at all:
    ///
    ///         SendRpc: ClientSetViewTarget ...
    ///         AGameModeBase.SpawnAircraft: battle bus at ...
    ///         ServerReplicateActors: opening ChIndex=289 for newly relevant AFortAthenaAircraft
    ///
    ///     ClientSetViewTarget's parameter is an OBJECT REFERENCE, so the client was handed a
    ///     NetGUID for an actor it had never been told about and quietly ignored the whole call.
    ///     The give-away that everything else was fine: ServerAttemptAircraftJump still arrived, so
    ///     bInAircraft had replicated and the HUD believed it was aboard - only the camera did not
    ///     move. The bit-level encoding was never the problem (18/18/86, matching the capture
    ///     exactly); the actor simply did not exist yet on the receiving end.
    ///
    ///     So this waits for the aircraft's own channel to appear on a connection, which only
    ///     happens after ServerReplicateActors has run, and sends on a LATER tick - the same
    ///     "waiting for ServerReplicateActors to open its channel" beat SpawnDroppedPickup already
    ///     works to.
    /// </summary>
    private void TickBoarding(Runtime.UWorld world) {
        if (_pendingBoarding is not { } aircraft || world.NetDriver is not { } netDriver) return;

        foreach (var connection in netDriver.ClientConnections) {
            if (connection.PlayerController is not { } pc) continue;
            if (_boardedConnections.Contains(connection)) continue;

            // Not replicated to THIS client yet - try again next tick.
            if (connection.FindActorChannel(aircraft) is null) continue;
            if (connection.FindActorChannel(pc) is not { } pcChannel) continue;

            _boardedConnections.Add(connection);

            // RIDE the bus. Set HERE rather than in SpawnAircraft for exactly the reason the camera
            // RPCs are sent here: AttachParent is an object reference, so the aircraft has to exist
            // on this client first. Offset zero - sitting at the aircraft's origin - since no seat
            // sockets are replicated to aim at.
            //
            // SCALE MUST BE (1,1,1), NOT ZERO. AActor::OnRep_AttachmentReplication assigns it
            // straight through - `RootComponent->RelativeScale3D = AttachmentReplication.
            // RelativeScale3D` (Actor.cpp:1658) - so a zero here scales the pawn to nothing and its
            // transform goes degenerate. The engine's FRepAttachment constructor does ForceInit that
            // member, but that is the DETACHED default; the value a real server sends comes from
            // AActor::GatherCurrentMovement copying the component's actual scale, which is (1,1,1).
            // Sending the constructor default was this round's bug.
            //
            // OFF BY DEFAULT, because attaching the pawn is THE WRONG MODEL. Two independent
            // pieces of evidence, both gathered after this was written:
            //
            //   * The dump: AFortPlayerControllerAthena::EnterAircraft (0x141238640, found from its
            //     own "EnterAircraft: %s" log string) only sets bInAircraft on the PlayerState
            //     (`or byte ptr [rbx+0xf28], 2`, and 0xf28 bit 1 IS bInAircraft) and swaps in
            //     AircraftInputComponent (+0x618). It never touches the pawn.
            //   * The capture: the local pawn's IDENTITY CHANGES across the bus.
            //         19:57:49  SerializeNewActor: PlayerPawn_Athena_C_2147476394   (warmup)
            //         19:59:14  Aircraft phase - a pile of channels close, reason=Destroyed
            //         19:59:21  SerializeNewActor: PlayerPawn_Athena_C_2147474071   (on jump)
            //
            // So a real server DESTROYS the warmup pawn when the bus phase starts, leaves the player
            // a pure spectator whose view target is the aircraft, and spawns a NEW pawn when they
            // jump. Kept behind a flag rather than deleted only so the two models can be compared
            // against a live client.
            if (EnvName("BUS_ATTACH_PAWN", 0) == 1 && pc.Pawn is { } ridingPawn) {
                ridingPawn.AttachParent = aircraft;
                ridingPawn.AttachLocationOffset = new Core.Math.FVector();
                ridingPawn.AttachRotationOffset = new Core.Math.FRotator();
                ridingPawn.AttachRelativeScale3D = new Core.Math.FVector { X = 1f, Y = 1f, Z = 1f };
            } else if (pc.Pawn is { } warmupPawn) {
                // THE PAWN GOES AWAY. This is what a real server does, and it is why the player is
                // put into NAME_Spectating with the aircraft as view target - a spectator has no
                // pawn to stand anywhere. Leaving the warmup pawn alive is what left the character
                // standing on the spawn island while the camera flew off.
                pc.UnPossess();
                warmupPawn.Destroy();

                if (pc.PlayerState?.AbilitySystemComponent is { } asc) asc.AvatarActor = null;

                Console.WriteLine($"AGameModeBase: destroyed {warmupPawn.GetFName()} for the bus - " +
                                  "the player is a spectator until they jump");
            }

            // The capture's order, and it is not arbitrary: camera mode, then state, then view
            // target - see UActorChannel.SendClientGotoState.
            pcChannel.SendClientSetCameraMode(EnvName("BUS_CAMERA_MODE_NAME", 204));   // Default
            pcChannel.SendClientGotoState(EnvName("BUS_GOTO_STATE_NAME", 322));        // Spectating
            pcChannel.SendClientSetViewTarget(aircraft);

            Console.WriteLine($"AGameModeBase: put {pc.GetFName()}'s camera on the battle bus " +
                              "(CameraMode=Default, State=Spectating, ViewTarget=aircraft)");
        }

        // AFTER THE JUMP: hand control of the brand new pawn back.
        //
        // The player was put into NAME_Spectating with no pawn at all to board the bus, so getting
        // out of the bus is not just spawning a pawn - the client has to be told to stop
        // spectating and to possess it. ClientRestart's parameter is the PAWN, which is the third
        // object reference in this feature that cannot go out before the actor exists on the
        // client (the camera RPCs and AttachParent were the first two), so it waits for the pawn's
        // own channel exactly the same way.
        //
        // Only ever reached once the bus has launched - TickBoarding returns early otherwise - so
        // this cannot fight UWorld's login-time ClientRestart for the warmup pawn.
        foreach (var connection in netDriver.ClientConnections) {
            if (connection.PlayerController is not { } pc) continue;
            if (pc.Pawn is not { } pawn || _restartedPawns.Contains(pawn)) continue;
            if (connection.FindActorChannel(pawn) is null) continue;
            if (connection.FindActorChannel(pc) is not { } pcChannel) continue;

            _restartedPawns.Add(pawn);

            pcChannel.SendClientGotoState(EnvName("PLAYING_STATE_NAME", 320));   // Playing
            pcChannel.SendClientRestart(pawn);
            pcChannel.SendClientSetRotation(pawn.GetActorRotation(), true);

            Console.WriteLine($"AGameModeBase: handed {pc.GetFName()} control of {pawn.GetFName()} " +
                              "after the jump (State=Playing, ClientRestart)");
        }
    }

    /// <summary>
    ///     Advance out of warmup when its countdown runs out, then keep trying to seat anyone the
    ///     bus has not reached yet. Called every world tick.
    /// </summary>
    public void TickPhases(Runtime.UWorld world, float now) {
        TickBoarding(world);

        if (!_warmupStarted || _aircraftLaunched || GameState == null) return;
        if (now < _warmupEndTime) return;

        _aircraftLaunched = true;

        // SpawnAircraft is a no-op unless AIRCRAFT_ENABLED=1, and silently leaving the player on
        // the island with no way off would be the worst kind of failure - so say it out loud.
        if (Environment.GetEnvironmentVariable("AIRCRAFT_ENABLED") is not "1") {
            Console.WriteLine($"AGameModeBase: warmup ended at {now:F1} but AIRCRAFT_ENABLED is not 1, " +
                              "so there is no bus and the player STAYS ON THE SPAWN ISLAND. " +
                              "Set AIRCRAFT_ENABLED=1, or WARMUP_SECONDS=0 plus SPAWN_LOCATION to " +
                              "start on the main island instead.");
            return;
        }

        SpawnAircraft(world, new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient });
    }

    /// <summary>Where solo team indices start - 0 is "no team" and 1/2 are reserved, see Login.</summary>
    private const int FirstTeamIndex = 3;

    private int _playersJoined;

    /// <summary>
    ///     The battle bus, OFF BY DEFAULT and switched on with AIRCRAFT_ENABLED=1.
    ///
    ///     Off by default deliberately: the join flow currently works precisely because
    ///     bGameModeWillSkipAircraft is true and the client never waits for a bus. Turning that off
    ///     hands the client a phase this server has never been through, and if any part of the flight
    ///     plan is wrong the player is left sitting in a bus that never opens - a much worse failure
    ///     than not having a bus at all. So the default path stays byte-identical to what already
    ///     works, and this is opt-in until it has actually been flown.
    ///
    ///     The flight plan itself is a straight line and the numbers are chosen, not sourced: a real
    ///     playlist derives the path from the safe-zone plan, which this server has no equivalent of.
    ///     All of it is env-overridable so a path can be retried without a rebuild.
    /// </summary>
    private void SpawnAircraft(Runtime.UWorld world, FActorSpawnParameters spawnInfo) {
        if (GameState == null) return;
        if (Environment.GetEnvironmentVariable("AIRCRAFT_ENABLED") is not "1") return;

        var aircraft = world.SpawnActor<AFortAthenaAircraft>(
            GUClassArray.StaticClass<AFortAthenaAircraft>(), spawnInfo);
        if (aircraft == null) return;

        var startX = EnvFloat("AIRCRAFT_START_X", -40000f);
        var startY = EnvFloat("AIRCRAFT_START_Y", 0f);
        var startZ = EnvFloat("AIRCRAFT_START_Z", 15000f);
        var yaw = EnvFloat("AIRCRAFT_YAW", 0f);
        var flightDuration = EnvFloat("AIRCRAFT_FLIGHT_SECONDS", 40f);
        var dropStart = EnvFloat("AIRCRAFT_DROP_START_SECONDS", 5f);
        var dropEnd = EnvFloat("AIRCRAFT_DROP_END_SECONDS", 35f);

        aircraft.SetRole(ENetRole.ROLE_Authority);
        aircraft.SetReplicates(true);
        aircraft.AircraftIndex = 0;
        aircraft.FlightSpeed = EnvFloat("AIRCRAFT_SPEED", 4000f);
        aircraft.PlanFlight(world.TimeSeconds,
            new Core.Math.FVector { X = startX, Y = startY, Z = startZ },
            yaw, flightDuration, dropStart, dropEnd);

        GameState.Aircrafts.Add(aircraft);
        GameState.bGameModeWillSkipAircraft = false;
        GameState.GamePhase = EAthenaGamePhase.Aircraft;
        GameState.AircraftStartTime = aircraft.FlightStartTime;
        // Doors shut until the drop window opens. Nothing ticks this yet - see
        // NativeRpcHandlers.ServerAttemptAircraftJump for why the client's own DropStartTime check
        // is what actually gates jumping, and why leaving this false is the safer default.
        GameState.bAircraftIsLocked = false;

        // EVERYONE ALREADY IN THE WORLD BOARDS. Login only sets bInAircraft for a player who joins
        // while the bus already exists, which used to cover everyone because the bus was spawned
        // during InitGameState. Now that it launches at the END of warmup, the players who matter
        // are precisely the ones who joined BEFORE it existed - and without this the phase would
        // flip to Aircraft while they stood on the island watching nothing happen.
        var boarded = 0;
        if (world.NetDriver is { } netDriver) {
            foreach (var connection in netDriver.ClientConnections) {
                if (connection.PlayerController?.PlayerState is not { } playerState) continue;
                if (playerState.bInAircraft) continue;

                playerState.bInAircraft = true;
                boarded++;

            }
        }

        // The camera RPCs CANNOT go out yet - see TickBoarding.
        _pendingBoarding = aircraft;

        Console.WriteLine($"AGameModeBase.SpawnAircraft: battle bus at ({startX}, {startY}, {startZ}) " +
                          $"Yaw={yaw} Speed={aircraft.FlightSpeed} flight={flightDuration}s " +
                          $"drop={dropStart}s..{dropEnd}s (FlightStartTime={aircraft.FlightStartTime}), " +
                          $"GamePhase={GameState.GamePhase}, boarded {boarded} player(s)");
    }

    public void PreLogin(string options, string address, FUniqueNetIdRepl uniqueId, out string? errorMessage) {
        // Login unique id must match server expected unique id type OR No unique id could mean game doesn't use them
        errorMessage = null;
    }

    public APlayerController? Login(UPlayer newPlayer, ENetRole inRemoteRole, string portal, string options, FUniqueNetIdRepl uniqueId, out string errorMessage) {
        errorMessage = string.Empty;

        if (GameSession == null)
        {
            errorMessage = "Failed to spawn player controller, GameSession is null";
            return null;
        }

        errorMessage = GameSession.ApproveLogin(options);

        if (!string.IsNullOrEmpty(errorMessage)) return null;

        var world = GetWorld();
        if (world == null) {
            errorMessage = "Failed to spawn player controller, World is null";
            return null;
        }

        var spawnInfo = new FActorSpawnParameters {
            ObjectFlags = EObjectFlags.RF_Transient
        };

        var newPlayerController = world.SpawnActor<APlayerController>(GUClassArray.StaticClass<APlayerController>(), spawnInfo);

        // Before anything else the controller does: the client can name InteractionComp by path the
        // moment it decides to interact, and an unresolved sub-object costs the WHOLE content block.
        newPlayerController?.CreateInteractionComponent();

        if (newPlayerController == null) {
            errorMessage = "Failed to spawn player controller";
            return null;
        }

        // See AFortInventory's doc comment - without this, ClientRestart_Implementation refuses to
        // finish possessing a pawn client-side. Spawned as a real actor (not a subobject - a live
        // client confirmed "/Script/FortniteGame.FortInventory" is an actor class, not a UObject,
        // via "Sub-object cannot be actor class"), so it needs its own actor channel just like
        // GameState/PlayerState - see UWorld.SpawnPlayActor, which opens one right after this
        // returns. WorldInventory itself is then just a plain ObjectRef Cmd on the PlayerController
        // (NativeRepLayouts.PlayerControllerProps), exactly like AController.PlayerState already is.
        var worldInventory = world.SpawnActor<AFortInventory>(GUClassArray.StaticClass<AFortInventory>(), spawnInfo);
        if (worldInventory != null) {
            worldInventory.SetRole(ENetRole.ROLE_Authority);
            worldInventory.SetReplicates(true);
            // A real server owns this actor with the PlayerController that holds it - see
            // AActor.Owner. Erbium does the same (WorldInventory->SetOwner(PlayerController)).
            worldInventory.SetOwner(newPlayerController);

            // Starting inventory, in the order a real server sends it. Add() runs MarkItemDirty,
            // which is what assigns the ReplicationID and moves the array's replication key - never
            // set the id by hand, or MarkItemDirty skips its own counter and the next item collides
            // with this one.
            worldInventory.Inventory.Add(new FFortItemEntry {
                ItemDefinition = UAssetRegistry.GetOrCreate("/Game/Athena/Items/Weapons/WID_Harvest_Pickaxe_Athena_C_T01.WID_Harvest_Pickaxe_Athena_C_T01"),
                Count = 1
            });

            // The build menu, and the reason a player could harvest resources and then do nothing
            // with them: the building quickbar draws its four slots from these items, so without
            // them there is nothing to select and building is simply unavailable. Nothing was
            // broken - they had never been handed out.
            //
            // These five paths are transcribed from a working Project-Reboot-3.0 server's own
            // NetGUID export bunches (packet #253), so they are what a real match sends rather than
            // paths guessed from the pak layout. Reading them took fixing the export decoder's
            // missing network checksum, which had been shearing the object name off every exported
            // path - see UPackageMapClient.ReadObjectReference.
            foreach (var buildingTool in new[] {
                "/Game/Items/Weapons/BuildingTools/BuildingItemData_Wall.BuildingItemData_Wall",
                "/Game/Items/Weapons/BuildingTools/BuildingItemData_Floor.BuildingItemData_Floor",
                "/Game/Items/Weapons/BuildingTools/BuildingItemData_Stair_W.BuildingItemData_Stair_W",
                "/Game/Items/Weapons/BuildingTools/BuildingItemData_RoofS.BuildingItemData_RoofS",
                "/Game/Items/Weapons/BuildingTools/EditTool.EditTool"
            }) {
                worldInventory.Inventory.Add(new FFortItemEntry {
                    ItemDefinition = UAssetRegistry.GetOrCreate(buildingTool),
                    Count = 1
                });
            }

            // Wood/Stone/Metal resource stacks - without these, placement never has anything to pay
            // with. Confirmed 2026-08-29: the ghost preview, its edit-pattern cycling and the cost
            // UI are all purely client-local (work with zero resources), but with the ghost/edit/
            // cost gates all now cleared, ServerCreateBuildingActor still never gets sent at all -
            // the simplest remaining explanation is a client-side "can afford this?" check refusing
            // silently before the RPC is even attempted, exactly like every other gate this project
            // has hit. Paths match FortHarvestResources' own private ItemPaths table (the same three assets a
            // harvested tree/rock/wall already hands out as loot). 500 is arbitrary - enough that
            // running out mid-test is not itself a confound; STARTING_RESOURCES overrides it.
            var startingResources = int.TryParse(Environment.GetEnvironmentVariable("STARTING_RESOURCES"), out var res) && res >= 0 ? res : 500;
            foreach (var resourcePath in new[] {
                "/Game/Items/ResourcePickups/WoodItemData.WoodItemData",
                "/Game/Items/ResourcePickups/StoneItemData.StoneItemData",
                "/Game/Items/ResourcePickups/MetalItemData.MetalItemData"
            }) {
                worldInventory.Inventory.Add(new FFortItemEntry {
                    ItemDefinition = UAssetRegistry.GetOrCreate(resourcePath),
                    Count = startingResources
                });
            }

            // A common Assault Rifle. Two reasons it is here rather than just the pickaxe:
            //
            // 1. A match needs a weapon, and this is a real asset a real match hands out.
            // 2. The pickaxe CANNOT be dropped, so it can never exercise the fast-array delete path.
            //    Confirmed from the cooked asset rather than assumed - Tools/MapActorDump
            //    "props:" over Athena/Items/Weapons shows WID_Harvest_Pickaxe_Athena_C_T01 with
            //    bCanBeDropped=False (and bNeverPersisted=True) written explicitly. A cooked asset
            //    omits default-valued properties, so an explicit False means the native default on
            //    UFortWorldItemDefinition is True and the 133 Athena WIDs that never mention the
            //    flag - this rifle among them - are the droppable ones.
            //
            // This is also a second consumer of the must-be-mapped GUID work: unlike the pickaxe,
            // the client does not have this loaded at spawn, so the channel has to hold its bunches
            // while the asset streams in.
            // LoadedAmmo was hard-coded to 30, which is only correct for the default assault
            // rifle - point STARTING_WEAPON at a shotgun and it handed out 30 shells in a 5-round
            // magazine. WorldLootEntry reads the weapon's real ClipSize.
            var startingWeaponItem = FortWeaponActorClasses.WorldLootEntry(
                Environment.GetEnvironmentVariable("STARTING_WEAPON") is { Length: > 0 } weapon
                    ? weapon
                    : "/Game/Athena/Items/Weapons/WID_Assault_Auto_Athena_C_Ore_T02.WID_Assault_Auto_Athena_C_Ore_T02",
                1);

            worldInventory.Inventory.Add(startingWeaponItem);

            // Ammunition for the starting weapon. Reloading pulls from a SEPARATE inventory item,
            // not from the weapon, so a player holding only a rifle is told - correctly - that
            // there is not enough ammo to reload. Project-Reboot-3.0 hands both out together
            // whenever a weapon is spawned as loot (FortLootPackage.cpp:349-355: GetAmmoData()
            // plus GetDropCount()).
            //
            // The count is ours to choose - a real Battle Royale player starts with nothing but a
            // pickaxe, so there is no authentic number to copy. Ten drops' worth of the real
            // DropCount (12 for AthenaAmmoDataBulletsMedium, read from the cooked asset), so the
            // figure is at least anchored to real data. STARTING_AMMO overrides it.
            // Named directly rather than fished back out with LastOrDefault(): the weapon stopped
            // being the last thing added the moment anything was appended after it, and that would
            // have looked up ammo for whatever happened to be on the end of the list instead.
            if (FortWeaponActorClasses.AmmoItemFor(startingWeaponItem.ItemDefinition) is { } ammoItem) {
                worldInventory.Inventory.Add(new FFortItemEntry {
                    ItemDefinition = ammoItem,
                    Count = int.TryParse(Environment.GetEnvironmentVariable("STARTING_AMMO"), out var ammo) && ammo > 0 ? ammo : 120
                });
            }

            newPlayerController.WorldInventory = worldInventory;
        }

        // See AFortBroadcastRemoteClientInfo's own doc comment: without this, the client's
        // ServerSetPlayerBuildableClass call (sent the instant a building tool is equipped) finds
        // BroadcastRemoteClientInfo null and is silently skipped. Same shape as WorldInventory above
        // - its own actor/channel, owned by the controller that holds it.
        var broadcastRemoteClientInfo = world.SpawnActor<AFortBroadcastRemoteClientInfo>(
            GUClassArray.StaticClass<AFortBroadcastRemoteClientInfo>(), spawnInfo);
        if (broadcastRemoteClientInfo != null) {
            broadcastRemoteClientInfo.SetRole(ENetRole.ROLE_Authority);
            broadcastRemoteClientInfo.SetReplicates(true);
            broadcastRemoteClientInfo.SetOwner(newPlayerController);
            broadcastRemoteClientInfo.bActive = true;
            newPlayerController.BroadcastRemoteClientInfo = broadcastRemoteClientInfo;
        }

        // Simplified stand-in for AController::InitPlayerState - real UE spawns this automatically
        // as part of PlayerController construction. bHasStartedPlaying is set true immediately for
        // the same reason GameState.bReplicatedHasBegunPlay is (see InitGameState) - no warmup flow
        // yet, so there's nothing to gate it on.
        var playerState = world.SpawnActor<APlayerState>(GUClassArray.StaticClass<APlayerState>(), spawnInfo);
        if (playerState != null) {
            playerState.SetRole(ENetRole.ROLE_Authority);
            playerState.SetReplicates(true);
            // Hand the client's own id (from NMT_Login) straight back on the PlayerState - see
            // APlayerState.UniqueId. Real UE does this in AGameModeBase::Login via
            // PlayerState->SetUniqueId.
            playerState.UniqueId = uniqueId;

            // TEAMS. Solo gives every player a team of their own, and Fortnite reserves the low
            // indices (0 is no-team, 1/2 are the special cases a BR playlist never uses), which is
            // why AFortPlayerStateAthena::TeamIndex defaults to 3 here rather than 0. Handing out
            // 3, 4, 5, ... is what a solo playlist does; a squads playlist would instead group four
            // players per index and give them a shared SquadId, which is the only thing that would
            // change here.
            //
            // Until this existed every player was team 3, i.e. the same team - harmless with one
            // player and wrong the moment there are two.
            playerState.TeamIndex = (byte) Math.Min(byte.MaxValue, FirstTeamIndex + _playersJoined);
            playerState.SquadId = (byte) _playersJoined;
            _playersJoined++;

            // First player on the island starts the warmup countdown - see StartWarmupClock.
            StartWarmupClock(world.TimeSeconds);

            if (GameState != null) {
                GameState.TeamCount = _playersJoined;
                GameState.TotalPlayers = _playersJoined;
                GameState.PlayersLeft = _playersJoined;

                // The battle bus phase, if it is on: a player who joins during it starts ABOARD.
                playerState.bInAircraft = GameState.Aircrafts.Count > 0;
            }

            Console.WriteLine($"AGameModeBase.Login: team assignment - TeamIndex={playerState.TeamIndex} " +
                              $"SquadId={playerState.SquadId} bInAircraft={playerState.bInAircraft}, " +
                              $"TeamCount={GameState?.TeamCount}");

            // AController::InitPlayerState does this, and APlayerState::GetOwningController is just
            // Cast<AController>(GetOwner()) - so without it the player state has no way back to its
            // controller, and anything that starts from the PlayerState (an ability RPC arrives on
            // ITS channel) cannot find the pawn.
            playerState.SetOwner(newPlayerController);

            // The AbilitySystemComponent, flagged RF_DefaultSubObject - and that flag is the whole
            // ballgame. UActorChannel::ReadContentBlockHeader branches on the "stably named" bit:
            //
            //   bStablyNamed = 1 -> the client RESOLVES an existing sub-object and never creates one
            //   bStablyNamed = 0 -> the client reads a class and NewObjects a fresh one
            //
            // and we write that bit from IsNameStableForNetworking(). Without the flag we wrote 0,
            // so a real client dutifully built a SECOND AbilitySystemComponent
            // ("Instantiating sub-object. Class: FortAbilitySystemComponentAthena") while
            // AFortPlayerState::AbilitySystemComponent - a non-replicated pointer set by the
            // client's own constructor - went on pointing at the original. Every ability we granted
            // landed in a component nothing referenced. No error, no warning, simply inert.
            //
            // The name must match the real component's exactly, because that is how the client
            // finds it: the export carries the outer's GUID plus this name.
            //
            // Note the two stability rules are NOT the same and both are already implemented
            // correctly here: the GUID stays DYNAMIC because IsFullNameStableForNetworking walks
            // the outer chain and this PlayerState is runtime-spawned (matching the real capture,
            // where the player's ASC is the even guid 584), while the path export and the
            // stably-named bit both use IsNameStableForNetworking, which looks only at this object.
            playerState.AbilitySystemComponent = UObjectGlobals.NewObject<UFortAbilitySystemComponent>(
                playerState,
                GUClassArray.StaticClass<UFortAbilitySystemComponent>(),
                new FName("AbilitySystemComponent"),
                EObjectFlags.RF_Transient | EObjectFlags.RF_DefaultSubObject);

            if (playerState.AbilitySystemComponent != null) {
                playerState.AbilitySystemComponent.OwnerActor = playerState;

                // The attribute sets. These are DEFAULT SUBOBJECTS OF THE PLAYER STATE, not of the
                // component - a real Project-Reboot-3.0 capture exports all ten of them in one
                // packet (#189) right after Default__FortPlayerStateAthena, and in this order.
                //
                // Nothing here creates or fills anything: the client's own PlayerState constructor
                // already built these with their real defaults (its log prints WalkSpeed 200,
                // RunSpeed 410). Being stably named, each is referenced by PATH - outer plus name -
                // so no class and no attribute values go on the wire. All the server has to do is
                // INTRODUCE them through the ASC's SpawnedAttributes array, because
                // GetNumericAttribute finds a set by searching that array and an empty one makes
                // every attribute read as zero. That is what left walk speed clamped at 1 uu/s.
                foreach (var setName in AttributeSetNames) {
                    // Two sets get a typed stand-in, because they are the only ones this server
                    // sends attribute VALUES for rather than merely introducing: MovementSet
                    // (speeds) and PlayerAttrSet (stamina, which a jump spends).
                    var set = setName switch {
                        "MovementSet" => (UFortAttributeSet?) UObjectGlobals.NewObject<UFortMovementSet>(
                            playerState, GUClassArray.StaticClass<UFortMovementSet>(), new FName(setName),
                            EObjectFlags.RF_Transient | EObjectFlags.RF_DefaultSubObject),
                        // The third set this server sends VALUES for. Unlike the other two it is
                        // not a fix for something the client reads as zero - it is the health a
                        // player is damaged out of, so it has to start full and stay authoritative
                        // here. See FortDamageSystem.
                        "HealthSet" => UObjectGlobals.NewObject<UFortHealthSet>(
                            playerState, GUClassArray.StaticClass<UFortHealthSet>(), new FName(setName),
                            EObjectFlags.RF_Transient | EObjectFlags.RF_DefaultSubObject),
                        "PlayerAttrSet" => UObjectGlobals.NewObject<UFortPlayerAttrSet>(
                            playerState, GUClassArray.StaticClass<UFortPlayerAttrSet>(), new FName(setName),
                            EObjectFlags.RF_Transient | EObjectFlags.RF_DefaultSubObject),
                        _ => UObjectGlobals.NewObject<UFortAttributeSet>(
                            playerState, GUClassArray.StaticClass<UFortAttributeSet>(), new FName(setName),
                            EObjectFlags.RF_Transient | EObjectFlags.RF_DefaultSubObject)
                    };

                    if (set == null) continue;

                    playerState.AbilitySystemComponent.SpawnedAttributes.Add(set);
                    if (set is UFortMovementSet movementSet) playerState.MovementSet = movementSet;
                    if (set is UFortPlayerAttrSet playerAttrSet) playerState.PlayerAttrSet = playerAttrSet;

                    if (set is UFortHealthSet healthSet) {
                        playerState.HealthSet = healthSet;
                        // Fortnite's real starting values: 100 health, no shield, both capped at
                        // 100. Env-overridable so a value can be tried against a live client
                        // without a rebuild - the same knob arrangement UFortPlayerAttrSet uses.
                        healthSet.MaxHealth = EnvFloat("PLAYER_MAX_HEALTH", 100.0f);
                        healthSet.Health = EnvFloat("PLAYER_HEALTH", healthSet.MaxHealth);
                        healthSet.Shield = EnvFloat("PLAYER_MAX_SHIELD", 100.0f);
                        healthSet.CurrentShield = EnvFloat("PLAYER_SHIELD", 0.0f);
                    }
                }
            }
            // ONE infinite-duration GameplayEffect, applied for its side effect rather than its
            // number: a client that receives an active effect naming an attribute creates an
            // AGGREGATOR for it, and an attribute with an aggregator takes the loud branch of
            // FActiveGameplayEffectsContainer::SetBaseAttributeValueFromReplication - the
            // OnAttributeAggregatorDirty -> InternalUpdateNumericalAttribute path the HUD health bar
            // is built on - instead of the silent one. Without it, every health value this server
            // replicates arrives correctly and redraws nothing; see FActiveGameplayEffect for the
            // whole evidence chain.
            //
            // OFF BY DEFAULT, and it took a live regression to earn that. The first candidate,
            // GE_GM_HealthIncrease, was picked for its SHAPE (Infinite, exactly one modifier) without
            // reading what that modifier did, and the client applied it for real: the top-left health
            // readout vanished and the player was locked into build mode until something damaged them.
            // Nothing was wrong with the encoding (every member and its order was re-checked against
            // GameplayEffect.h afterwards); the client simply did what it was told.
            //
            // Both halves of that failure are now confirmed straight out of the pak
            // (`pakreader exports .../GE_GM_HealthIncrease`): it modifies MaxHealth with
            // EGameplayModOp::Multiplicitive, so magnitude 0 gave the client MaxHealth = base * 0 =
            // ZERO - exactly what a vanished readout and a player who cannot leave build mode look
            // like - AND it carries a non-empty GameplayCues array, which
            // AddActiveGameplayEffectGrantedTagsAndModifiers fires on top of that. Two independent
            // reasons that asset could never have been inert.
            //
            // SO THE EFFECT MUST BE CHOSEN BY READING IT, NOT BY ITS SHAPE. What it has to satisfy,
            // read off AddActiveGameplayEffectGrantedTagsAndModifiers and FActiveGameplayEffect::
            // PostReplicatedAdd in 4.23's GameplayEffect.cpp:
            //   * EXACTLY the modifier count this server replicates (one), or PostReplicatedAdd hits
            //     `Spec.Modifiers.Num() != Spec.Def->Modifiers.Num()`, empties Modifiers and returns
            //     before any aggregator is touched;
            //   * that modifier on FortHealthSet.Health, since HasAttributeSetForAttribute gates it
            //     and an aggregator on the wrong attribute redraws nothing;
            //   * ModifierOp Additive, so HEALTH_AGGREGATOR_MAGNITUDE=0 is NEUTRAL (0 is neutral for
            //     Additive only; 1 is neutral for Multiplicitive and Division);
            //   * empty InheritableOwnedTagsContainer, GameplayCues and GrantedAbilities - the three
            //     things applied to the owner regardless of magnitude.
            // DurationPolicy does NOT matter here, contrary to the original guess: 4.23's
            // UpdateAllAggregatorModMagnitudes has no Instant early-out, it gates on period and
            // bIsInhibited only.
            //
            // The known-good candidate, verified against all five points above, is
            // /Game/Abilities/Player/Generic/Gadgets/RomanCandle/GE_RomanCandleCost.Default__GE_RomanCandleCost
            // with HEALTH_AGGREGATOR_MAGNITUDE=0: one Additive modifier on FortHealthSet.Health whose
            // own authored magnitude is already 0, no owned tags, no cues, no granted abilities - its
            // only tags are the asset tags Gameplay.Mod.Cost / Gameplay.Mod.Stamina, which are
            // metadata for queries and are never applied to anyone.
            //
            // Still a THEORY until a client confirms the bar moves - what is settled is that this
            // candidate cannot repeat the GE_GM_HealthIncrease regression. Unset, this server applies
            // nothing and behaves as it did before the experiment.
            if (playerState.AbilitySystemComponent != null &&
                Environment.GetEnvironmentVariable("HEALTH_AGGREGATOR_EFFECT") is { Length: > 0 } effectPath) {
                playerState.AbilitySystemComponent.AddActiveGameplayEffect(
                    UAssetRegistry.GetOrCreate(effectPath),
                    magnitude: EnvFloat("HEALTH_AGGREGATOR_MAGNITUDE", 0.0f),
                    startServerWorldTime: GetWorld()?.TimeSeconds ?? 0.0f);
            }

            Console.WriteLine($"AGameModeBase.Login: PlayerState UniqueId={uniqueId.ToDebugString()}");

            // Mirror the same player into the GameState's roster. Add() hands out the ReplicationID
            // via MarkItemDirty - see the inventory call above.
            GameState?.GameMemberInfoArray.Add(new FGameMemberInfo {
                SquadId = playerState.SquadId,
                TeamIndex = playerState.TeamIndex,
                MemberUniqueId = uniqueId
            });
            playerState.bHasFinishedLoading = true;
            // FALSE deliberately. In the working capture the server logs "EXPDEBUG: ... Player has
            // not started playing" at this point - bHasStartedPlaying is what a real Athena server
            // sets once the player is actually out of the bus and playing, not at join. Sending it
            // true alongside MatchState=InProgress told the client a story no real server tells.
            playerState.bHasStartedPlaying =
                Environment.GetEnvironmentVariable("HAS_STARTED_PLAYING") == "1";
            // AFortPlayerState::HeroType (handle 40, live-probe-confirmed). Athena's quickbars are
            // built from the hero loadout, which makes this the leading candidate for the client's
            // "Quickbars are invalid" stall. Path taken from Erbium's FindObject call; the object
            // itself is confirmed present in a live 10.40 object-table dump as
            // "FortHeroType HID_001_Athena_Commando_F.HID_001_Athena_Commando_F". A static asset, so
            // it needs a path-exported NetGUID rather than a spawned actor's - see UAssetRegistry.
            playerState.HeroType = UAssetRegistry.GetOrCreate("/Game/Athena/Heroes/HID_001_Athena_Commando_F.HID_001_Athena_Commando_F");

            // AFortPlayerState::CharacterData.Parts[6], indexed by EFortCustomPartType
            // (Head=0, Body=1, Hat=2, Backpack=3, Charm=4, Face=5). Without these the client warns
            // "Customization for PlayerPawn_Athena_C_… still hasn't completed after N secs" forever.
            //
            // These three exact object paths are not guesses: the client itself tried to load
            // F_Med_Head1_ATH and CP_001_Athena_Body by full path (LogStreamableManager), and all
            // three appear in the PR3.0 capture (packet #462).
            playerState.CharacterParts[(int) EFortCustomPartType.Head] =
                UAssetRegistry.GetOrCreate("/Game/Athena/Heroes/Meshes/Heads/F_Med_Head1_ATH.F_Med_Head1_ATH");
            playerState.CharacterParts[(int) EFortCustomPartType.Body] =
                UAssetRegistry.GetOrCreate("/Game/Athena/Heroes/Meshes/Bodies/CP_001_Athena_Body.CP_001_Athena_Body");
            playerState.CharacterParts[(int) EFortCustomPartType.Backpack] =
                UAssetRegistry.GetOrCreate("/Game/Characters/CharacterParts/Backpacks/NoBackpack.NoBackpack");
            // One bit per slot actually replicated - Head | Body | Backpack.
            playerState.WasPartReplicatedFlags =
                (byte) ((1 << (int) EFortCustomPartType.Head)
                      | (1 << (int) EFortCustomPartType.Body)
                      | (1 << (int) EFortCustomPartType.Backpack));
            newPlayerController.PlayerState = playerState;
        }

        return newPlayerController;
    }

    /// <summary>
    ///     An explicit "X,Y,Z" in SPAWN_LOCATION, or null to use the spawn island.
    ///
    ///     THE ISLAND IS THE DEFAULT NOW, which it could not be before because of a local-vs-world
    ///     mix-up. The first attempt used a FortPlayerStartWarmup out of
    ///     /Game/Athena/Maps/POI/Athena_POI_Lobby_004 at (6816, 2420, 92) - right actor class, right
    ///     numbers, but that is SUBLEVEL-LOCAL space. The island is placed by
    ///     LF_Athena_POI_50x50_446 at (-125440, -113664, 3840), so using the local coordinate as a
    ///     world one put the player in the middle of the map at an underground Z; they fell to the
    ///     ocean plane at Z=-5042.85. That was read at the time as "Athena_POI_Lobby_004 is not
    ///     streamed in, there is nothing to stand on" - a reasonable guess from the symptom, and
    ///     wrong. The coordinate was simply somewhere else.
    ///
    ///     The stopgap that replaced it - a BP_BGACSpawner out of Athena_ForagedItems at
    ///     352,-9512,2774, picked because foraged-item spawners sit ON the terrain and so sample
    ///     ground height - is what dropped players onto the MAIN island instead of the warmup one.
    ///     It stays available through SPAWN_LOCATION, which is still the right escape hatch if a
    ///     particular start turns out to be inside geometry.
    ///
    ///     See FortWarmupStarts for the 121 real starts.
    /// </summary>
    private static readonly FVector? SpawnLocationOverride =
        ParseSpawnLocation(Environment.GetEnvironmentVariable("SPAWN_LOCATION"));

    private static FVector? ParseSpawnLocation(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !float.TryParse(parts[0], out var x)
            || !float.TryParse(parts[1], out var y)
            || !float.TryParse(parts[2], out var z)) {
            Console.WriteLine($"AGameModeBase: SPAWN_LOCATION='{value}' is not \"X,Y,Z\" - using the spawn island instead.");
            return null;
        }

        return new FVector { X = x, Y = y, Z = z };
    }

    /// <summary>Reads a float knob from the environment, falling back when unset or unparseable.</summary>
    /// <summary>An EName index from the environment - see UActorChannel.SendClientGotoState.</summary>
    private static uint EnvName(string name, uint fallback) =>
        uint.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    private static float EnvFloat(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    /// <summary>
    ///     The PlayerState's attribute sets, in the order a real server sends them (recovered from
    ///     Project-Reboot-3.0 packet #189). Only the NAMES matter: the client resolves each by path
    ///     against the subobject its own constructor already made.
    /// </summary>
    private static readonly string[] AttributeSetNames = {
        "HealthSet", "ControlResistanceSet", "DamageSet", "MovementSet", "AdvancedMovementSet",
        "ConstructionSet", "PlayerAttrSet", "CharacterAttrSet", "WeaponAttrSet", "HomebaseSet"
    };

    public void PostLogin(APlayerController newPlayer) {
        var world = GetWorld();
        if (world == null) return;

        newPlayer.bHasServerFinishedLoading = true;
        // Real UE flips this in AFortPlayerController's spawn path, which is exactly here: the pawn
        // is spawned and possessed a few lines below. See APlayerController.bHasInitiallySpawned.
        newPlayer.bHasInitiallySpawned = true;

        // Simplified stand-in for AGameModeBase::RestartPlayer.
        //
        // Stand-in for AGameModeBase::RestartPlayerAtPlayerStart, which spawns the pawn at a
        // PlayerStart actor found in the level. This server has no map loaded, so instead of
        // GetAllActorsOfClass(FortPlayerStartWarmup) at runtime - what a real server and
        // Project-Reboot-3.0 both do - the same 121 actors are read out of the paks offline.
        // Nothing else here needs the map: the client owns collision and this server accepts
        // whatever ClientLoc arrives in ServerMoveNoBase.
        var start = SpawnLocationOverride ?? FortWarmupStarts.Next();

        SpawnAndPossessPawn(world, newPlayer, start);

        Console.WriteLine($"AGameModeBase.RestartPlayer: pawn starts at {start}" +
                          $"{(SpawnLocationOverride == null ? $" (spawn island, 1 of {FortWarmupStarts.Count} warmup starts)" : " (SPAWN_LOCATION override)")}");
    }

    /// <summary>
    ///     Spawn a pawn for <paramref name="pc"/> at <paramref name="at"/>, possess it, give the
    ///     ability system an avatar and put the first inventory item in its hands.
    ///
    ///     RestartPlayer's body, factored out because LEAVING THE BATTLE BUS needs exactly the same
    ///     thing somewhere else. A real server destroys the warmup pawn when the bus phase starts
    ///     and spawns a brand new one when the player jumps - the capture shows the local pawn's
    ///     identity change across the bus (PlayerPawn_Athena_C_2147476394 in warmup,
    ///     PlayerPawn_Athena_C_2147474071 after the jump), which is also why ClientGotoState sends
    ///     NAME_Spectating: a spectator has no pawn. See NativeRpcHandlers.ServerAttemptAircraftJump.
    /// </summary>
    public static APawn? SpawnAndPossessPawn(Runtime.UWorld world, APlayerController pc, FVector at) {
        var spawnInfo = new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient };
        var pawn = world.SpawnActor<APawn>(GUClassArray.StaticClass<APawn>(), spawnInfo);
        if (pawn == null) return null;

        pawn.SetRole(ENetRole.ROLE_Authority);
        pawn.SetReplicates(true);
        pawn.SetAutonomousProxy(true); // possessed by pc's own connection
        pawn.SetActorLocation(at);

        pc.Possess(pawn);

        // The ASC acts THROUGH the pawn. UAbilitySystemComponent::InitAbilityActorInfo is what
        // binds movement attributes to a character's CharacterMovement, and it needs an avatar;
        // without one the client had WalkSpeed 200 / RunSpeed 410 applying to nothing and moved
        // at a velocity clamped to exactly 1.0 uu/s. See NativeRepLayouts handle 8.
        if (pc.PlayerState?.AbilitySystemComponent is { } abilitySystem) {
            abilitySystem.AvatarActor = pawn;
        }

        // Spawn holding the pickaxe, the way a real match starts. The client will also ask for
        // this itself the moment the player touches a quickbar slot
        // (ServerExecuteInventoryItem -> APawn.EquipInventoryItem), and asking for what is
        // already equipped is a no-op there - so doing it here only removes the window in which
        // the pawn stands around empty-handed, it does not fight the client for control of the
        // slot.
        var firstItem = pc.WorldInventory?.Inventory.Items.FirstOrDefault();
        if (firstItem != null) pawn.EquipInventoryItem(firstItem);

        return pawn;
    }
}