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
            GameState.MatchState = new FName("InProgress");

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

            // Starting inventory. A real PR3.0 capture (packet #253) shows a working server sends
            // the pickaxe plus BuildingItemData_Wall/Floor/Stair_W/RoofS and EditTool here; this
            // starts with just the pickaxe, the minimum that should make the client build its
            // quickbars at all. ReplicationIDs must be unique and non-negative - real UE hands them
            // out from FFastArraySerializer::MarkItemDirty.
            worldInventory.Inventory.Add(new FFortItemEntry {
                ReplicationId = 1,
                ItemDefinition = UAssetRegistry.GetOrCreate("/Game/Athena/Items/Weapons/WID_Harvest_Pickaxe_Athena_C_T01.WID_Harvest_Pickaxe_Athena_C_T01"),
                Count = 1,
                ParentInventory = worldInventory
            });

            newPlayerController.WorldInventory = worldInventory;
        }

        // Simplified stand-in for AController::InitPlayerState - real UE spawns this automatically
        // as part of PlayerController construction. bHasStartedPlaying is set true immediately for
        // the same reason GameState.bReplicatedHasBegunPlay is (see InitGameState) - no warmup flow
        // yet, so there's nothing to gate it on.
        var playerState = world.SpawnActor<APlayerState>(GUClassArray.StaticClass<APlayerState>(), spawnInfo);
        if (playerState != null) {
            playerState.SetRole(ENetRole.ROLE_Authority);
            playerState.SetReplicates(true);
            playerState.bHasFinishedLoading = true;
            playerState.bHasStartedPlaying = true;
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
    ///     The default is a real FortPlayerStartWarmup taken out of
    ///     /Game/Athena/Maps/POI/Athena_POI_Lobby_004 with Tools/MapActorDump - the same actor class
    ///     Project-Reboot-3.0 looks up with GetAllActorsOfClass(FortPlayerStartWarmup), except read
    ///     from the cooked .umap because this server is external and has no loaded map to query.
    ///     That map holds 121 of them; any is as good as another. Coordinates are world coordinates:
    ///     every LevelStreaming entry in Athena_Terrain carries LevelTransform=identity (verified -
    ///     a cooked asset omits default-valued properties, and none of the 21 serialise one), so
    ///     sublevel-local and world space coincide here.
    ///
    ///     Re-run the tool to pick a different one:
    ///         dotnet run --project Tools/MapActorDump -- &lt;PaksDir&gt; &lt;AesKeyHex&gt;     ///             FortniteGame/Content/Athena/Maps PlayerStart
    /// </summary>
    private static readonly FVector SpawnLocation = ParseSpawnLocation(
        Environment.GetEnvironmentVariable("SPAWN_LOCATION") ?? "6816,2420,92");

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

    public void PostLogin(APlayerController newPlayer) {
        var world = GetWorld();
        if (world == null) return;

        newPlayer.bHasServerFinishedLoading = true;

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
        }
    }
}