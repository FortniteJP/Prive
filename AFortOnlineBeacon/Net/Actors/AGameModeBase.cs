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
        }
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
            var startingWeaponItem = new FFortItemEntry {
                ItemDefinition = UAssetRegistry.GetOrCreate(
                    Environment.GetEnvironmentVariable("STARTING_WEAPON") is { Length: > 0 } weapon
                        ? weapon
                        : "/Game/Athena/Items/Weapons/WID_Assault_Auto_Athena_C_Ore_T02.WID_Assault_Auto_Athena_C_Ore_T02"),
                Count = 1,
                LoadedAmmo = 30
            };

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
                }
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
    ///     Where to place a newly spawned pawn, as "X,Y,Z" in SPAWN_LOCATION.
    ///
    ///     The default USED TO BE a FortPlayerStartWarmup out of
    ///     /Game/Athena/Maps/POI/Athena_POI_Lobby_004 (6816,2420,92) - the same actor class
    ///     Project-Reboot-3.0 finds with GetAllActorsOfClass(FortPlayerStartWarmup). It is the right
    ///     class and the coordinates were read correctly, but the pawn fell straight through it:
    ///     a live run had the player at Z=-5042.85 with X/Y unchanged, standing on the ocean plane
    ///     under the map. Athena_POI_Lobby_004 is NOT one of Athena_Terrain's always-loaded
    ///     sublevels, so there was nothing there to stand on.
    ///
    ///     The default is now a BP_BGACSpawner out of /Game/Athena/Maps/Athena_ForagedItems, which
    ///     IS always loaded. Those are foraged-item spawners - bushes and the like - so they sit ON
    ///     the terrain, which makes their Z a direct sample of ground height. 352,-9512,2624 is the
    ///     most central of the 38 that carry an explicit location; the default adds ~150 so the pawn
    ///     drops the last bit onto the ground rather than starting inside it.
    ///
    ///     Coordinates are world coordinates: every LevelStreaming entry in Athena_Terrain carries
    ///     LevelTransform=identity (verified - a cooked asset omits default-valued properties, and
    ///     none of the 21 serialise one), so sublevel-local and world space coincide here.
    ///
    ///     Re-run the tool to sample other ground heights:
    ///         dotnet run --project Tools/MapActorDump -- &lt;PaksDir&gt; &lt;AesKeyHex&gt;
    ///             FortniteGame/Content/Athena/Maps/Athena_ForagedItems BP_BGACSpawner_C
    ///
    ///     If the pawn STILL ends up on the ocean plane, the coordinate is not the problem: it means
    ///     the terrain around it had not streamed in before the pawn started falling, and the fix is
    ///     level streaming (ServerUpdateLevelVisibility is still undecoded), not another location.
    /// </summary>
    private static readonly FVector SpawnLocation = ParseSpawnLocation(
        Environment.GetEnvironmentVariable("SPAWN_LOCATION") ?? "352,-9512,2774");

    private static FVector ParseSpawnLocation(string value) {
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !float.TryParse(parts[0], out var x)
            || !float.TryParse(parts[1], out var y)
            || !float.TryParse(parts[2], out var z)) {
            Console.WriteLine($"AGameModeBase: SPAWN_LOCATION='{value}' is not \"X,Y,Z\" - falling back to the origin.");
            return new FVector();
        }

        return new FVector { X = x, Y = y, Z = z };
    }

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
        var spawnInfo = new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient };
        var pawn = world.SpawnActor<APawn>(GUClassArray.StaticClass<APawn>(), spawnInfo);

        if (pawn != null) {
            pawn.SetRole(ENetRole.ROLE_Authority);
            pawn.SetReplicates(true);
            pawn.SetAutonomousProxy(true); // possessed by newPlayer's own connection

            // Stand-in for AGameModeBase::RestartPlayerAtPlayerStart, which spawns the pawn at a
            // PlayerStart actor found in the level. This server has no map data at all - a real
            // server (and Project-Reboot-3.0, which is injected into the running game) calls
            // GetAllActorsOfClass(FortPlayerStartWarmup) to find them. Nothing here needs the map
            // otherwise: the client owns collision and we simply accept the ClientLoc it reports in
            // ServerMoveNoBase, so the only thing actually missing was a starting point.
            pawn.SetActorLocation(SpawnLocation);

            newPlayer.Possess(pawn);

            // The ASC acts THROUGH the pawn. UAbilitySystemComponent::InitAbilityActorInfo is what
            // binds movement attributes to a character's CharacterMovement, and it needs an avatar;
            // without one the client had WalkSpeed 200 / RunSpeed 410 applying to nothing and moved
            // at a velocity clamped to exactly 1.0 uu/s. See NativeRepLayouts handle 8.
            if (newPlayer.PlayerState?.AbilitySystemComponent is { } abilitySystem) {
                abilitySystem.AvatarActor = pawn;
            }

            // Spawn holding the pickaxe, the way a real match starts. The client will also ask for
            // this itself the moment the player touches a quickbar slot
            // (ServerExecuteInventoryItem -> APawn.EquipInventoryItem), and asking for what is
            // already equipped is a no-op there - so doing it here only removes the window in which
            // the pawn stands around empty-handed, it does not fight the client for control of the
            // slot.
            var firstItem = newPlayer.WorldInventory?.Inventory.Items.FirstOrDefault();
            if (firstItem != null) pawn.EquipInventoryItem(firstItem);
        }
    }
}