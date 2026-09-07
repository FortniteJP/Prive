namespace AFortOnlineBeacon.Runtime;

public abstract partial class UWorld : FNetworkNotify, IAsyncDisposable {
    private UGameInstance? _OwningGameInstance;
    private AGameModeBase? _AuthorityGameMode;

    /// <summary>
    ///     AWorldSettings::GetGameState's stand-in - the replicated match state, reachable from
    ///     anything that already has a UWorld. An RPC handler needs it to read the battle bus out of
    ///     AGameState.Aircrafts, and going back through the game mode is the only route there.
    /// </summary>
    public AGameState? GameState => _AuthorityGameMode?.GameState;
    
    /// <summary>
    ///     Array of levels currently in this world. Not serialized to disk to avoid hard references.
    /// </summary>
    private List<ULevel> _Levels;
    
    /// <summary>
    ///     Pointer to the current level being edited.
    ///     Level has to be in the Levels array and == PersistentLevel in the game.
    /// </summary>
    private ULevel? _CurrentLevel;

    public UWorld() {
        _OwningGameInstance = null;
        _AuthorityGameMode = null;
        _Levels = new List<ULevel>();

        Url = new FUrl();

        // We don't stream/load levels from disk - a single persistent level is created up front and
        // used for the lifetime of this world, which is enough for actors to have a valid Outer/Level
        // chain (required for GetWorld()/GetLevel() and for network-actor registration to work).
        PersistentLevel = new ULevel();
        PersistentLevel.InitializeObjectProperties(null, new FName(EName.PersistentLevel), GUClassArray.StaticClass<ULevel>());
        PersistentLevel.OwningWorld = this;

        _Levels.Add(PersistentLevel);
        _CurrentLevel = PersistentLevel;
    }
    
    /// <summary>
    ///     Persistent level containing the world info, default brush and actors spawned during gameplay among other things
    /// </summary>
    public ULevel? PersistentLevel { get; private set; }
    
    /// <summary>
    ///     The NAME_GameNetDriver game connection(s) for client/server communication
    /// </summary>
    public UNetDriver? NetDriver { get; private set; }
    
    /// <summary>
    ///     Whether actors have been initialized for play
    /// </summary>
    public bool bActorsInitialized { get; private set; }
    
    /// <summary>
    ///     Is the world in its actor initialization phase.
    /// </summary>
    public bool bStartup { get; private set; }
    
    /// <summary>
    ///     Whether BeginPlay has been called on actors
    /// </summary>
    public bool bBegunPlay { get; private set; }
    
    /// <summary>
    ///     The URL that was used when loading this World.
    /// </summary>
    public FUrl Url { get; private set; }
    
    /// <summary>
    ///     Time in seconds since level began play, but IS paused when the game is paused, and IS dilated/clamped.
    /// </summary>
    public float TimeSeconds { get; private set; }
    
    /// <summary>
    ///     Time in seconds since level began play, but IS NOT paused when the game is paused, and IS dilated/clamped.
    /// </summary>
    public float UnpausedTimeSeconds { get; private set; }
    
    /// <summary>
    ///     Time in seconds since level began play, but IS NOT paused when the game is paused, and IS NOT dilated/clamped.
    /// </summary>
    public float RealTimeSeconds { get; private set; }
    
    /// <summary>
    ///     Time in seconds since level began play, but IS paused when the game is paused, and IS NOT dilated/clamped.
    /// </summary>
    public float AudioTimeSeconds { get; private set; }
    
    /// <summary>
    ///     Frame delta time in seconds adjusted by e.g. time dilation.
    /// </summary>
    public float DeltaTimeSeconds { get; private set; }
    
    public void Tick(float deltaTime) {
        // Advance world time, as UWorld::Tick does. These were initialised to zero and then never
        // touched again, which quietly broke anything that measures an interval against
        // TimeSeconds - UActorChannel.SafeRetryClientRestart's throttle compared 0 against 0 every
        // time and so fired exactly once, which looked exactly like "the client stopped asking".
        DeltaTimeSeconds = deltaTime;
        TimeSeconds += deltaTime;
        UnpausedTimeSeconds += deltaTime;
        RealTimeSeconds += deltaTime;
        AudioTimeSeconds += deltaTime;

        // AGameStateBase::PostInitializeComponents starts a repeating timer for this on the
        // authority (GameStateBase.cpp:56-59). There is no timer manager here, so it is driven off
        // the world tick against the same clock instead.
        var gameState = _AuthorityGameMode?.GameState;
        if (gameState != null && TimeSeconds >= _nextServerTimeUpdate) {
            _nextServerTimeUpdate = TimeSeconds + AGameState.ServerWorldTimeSecondsUpdateFrequency;
            gameState.UpdateServerTimeSeconds();
        }

        // Deferred structural-integrity recheck, the same shape real Fortnite gives it (queued by
        // ABuildingSMActor::MarkConnectedBuildingsForStructuralIntegrityCheck, paced by
        // BuildingRetestSupportedByWorldDelay) rather than running inline off each destruction.
        // Ahead of the NetDriver below so a cascade's channel closes go out on this same tick.
        BuildingStructuralSupportSystem.Tick(TimeSeconds);

        // Finishes any death that has been started this frame or an earlier one. Ahead of the
        // storm tick so a storm kill is reported on the same tick the damage landed.
        Net.FortDamageSystem.Tick(this, TimeSeconds);

        // Collected pickups still flying to whoever took them - see FortPickupFlightSystem.
        Net.FortPickupFlightSystem.Tick(this, TimeSeconds);

        // WELCOME_MESSAGE resends, so a HUD that is not up yet can be told apart from a channel
        // nobody reads. No-op unless that variable is set - see FortWelcomeMessage.
        Net.FortWelcomeMessage.Tick(this, TimeSeconds);

        // The storm. Off unless SAFEZONE_ENABLED=1 - see FortSafeZoneSystem for why it is opt-in.
        // Placed with the structural tick rather than after the NetDriver so a radius change and the
        // damage it causes go out on the same tick they happen.
        FortSafeZoneSystem.Tick(this, TimeSeconds);

        // Floor loot, rolled lazily near players rather than all at match start - see FortFloorLoot
        // for why. On by default (FLOOR_LOOT_ENABLED=0 turns it off); unlike the storm and the bus it
        // cannot strand or kill anyone, it only adds pickups.
        FortFloorLoot.Tick(this, TimeSeconds);
        FortVehicleSpawns.Tick(this, TimeSeconds);
        FortSupplyLlamas.Tick(this, TimeSeconds);

        // Prints the ABSENCE of a jump, with everything a jump depends on, until one happens - see
        // Net.JumpDiagnostics for why an absent log line is not good enough.
        Net.JumpDiagnostics.Tick(this, TimeSeconds);

        // The running verdict on the baked height map, measured against where players actually
        // stand. Silent unless TERRAIN_GROUNDTRUTH names a file.
        Net.TerrainGroundTruth.Tick(TimeSeconds);

        // Diagnostic only, off unless HEALTH_DEBUG_RAMP=1 - see FortDamageSystem.DebugRamp for the
        // question it answers.
        FortDamageSystem.DebugRamp(this, TimeSeconds);

        // Warmup -> Aircraft. Nothing happens until the first player joins and starts the clock.
        _AuthorityGameMode?.TickPhases(this, TimeSeconds);

        if (NetDriver != null) {
            NetDriver.TickDispatch(deltaTime);
            NetDriver.PostTickDispatch();
            
            NetDriver.TickFlush(deltaTime);
            NetDriver.PostTickFlush();
        }
    }

    private float _nextServerTimeUpdate;

    public void SetGameInstance(UGameInstance instance) => _OwningGameInstance = instance;

    public UGameInstance GetGameInstance() => _OwningGameInstance ?? throw new UnrealException($"Attempted to retrieve null {nameof(UGameInstance)}");
    
    public bool SetGameMode(FUrl worldUrl) {
        if (IsServer() && _AuthorityGameMode == null) {
            _AuthorityGameMode = GetGameInstance().CreateGameModeForURL(worldUrl, this);
            
            if (_AuthorityGameMode != null) return true;

            // Logger.Error("Failed to spawn GameMode actor");
            return false;
        }

        return false;
    }

    public AGameModeBase? GetAuthGameMode() => _AuthorityGameMode;

    public void InitializeActorsForPlay(FUrl inUrl, bool bResetTime) {
        // Say out loud what this server knows about the world's shape. All three are optional files
        // and all three fail SILENTLY when absent - the symptom is a grenade going through a wall or
        // through the ground, which looks like a physics bug rather than a missing file.
        Console.WriteLine($"World collision: hulls " +
                          $"{(Net.WorldCollision.Loaded ? $"{Net.WorldCollision.ShapeCount:N0} shape(s) x {Net.WorldCollision.InstanceCount:N0} placement(s)" : "NONE")}, " +
                          $"voxel walls {(Net.TerrainWalls.Loaded ? $"{Net.TerrainWalls.SpanCount:N0} span(s)" : "NONE")}, " +
                          $"placed-mesh heights {(Net.TerrainHeightMap.HasMeshGrid ? "loaded" : "NONE")}.");

        // Don't reset time for seamless world transitions.
        if (bResetTime) {
            TimeSeconds = 0.0f;
            UnpausedTimeSeconds = 0.0f;
            RealTimeSeconds = 0.0f;
            AudioTimeSeconds = 0.0f;
        }

        // Get URL Options
        var options = inUrl.OptionsToString();

        // Set level info.
        if (string.IsNullOrEmpty(inUrl.GetOption("load", null))) Url = inUrl;
        
        // Init level gameplay info.
        if (!AreActorsInitialized()) {
            // Initialize network actors and start execution.
            foreach (var level in _Levels) level.InitializeNetworkActors();

            // Enable actor script calls.
            bStartup = true;
            bActorsInitialized = true;

            // Spawn server actors
            // TODO: GEngine SpawnServerActors.
            
            // Init the game mode.
            if (_AuthorityGameMode != null && !_AuthorityGameMode.IsActorInitialized()) _AuthorityGameMode.InitGame(inUrl.Map /* TODO: FPaths.GetBaseFilename */, options, out _);
        }
    }

    public bool Listen() {
        if (NetDriver != null) {
            // Logger.Error("NetDriver already exists");
            return false;
        }
        
        NetDriver = new UIpNetDriver(Url.Host, Url.Port, IsServer());
        NetDriver.SetWorld(this);

        if (!((UIpNetDriver)NetDriver).InitListen(this)) {
            // Logger.Error("Failed to listen");
            NetDriver = null;
            return false;
        }
        
        return true;
    }

    public EAcceptConnection NotifyAcceptingConnection() => EAcceptConnection.Accept;

    public void NotifyAcceptedConnection(UNetConnection connection) {}

    public bool NotifyAcceptingChannel(UChannel channel) {
        if (channel.Connection?.Driver == null) throw new UnrealNetException();
        
        var driver = channel.Connection.Driver;
        if (!driver.IsServer()) throw new NotSupportedException("Client code");
        else {
            // We are the server.
            if (driver.ChannelDefinitionMap[channel.ChName].ClientOpen) {
                // The client has opened initial channel.
                // Logger.Verbose("NotifyAcceptingChannel {ChName} {ChIndex} server {FullName}: Accepted", channel.ChName, channel.ChIndex, typeof(UWorld).FullName);
                return true;
            }

            // Client can't open any other kinds of channels.
            // Logger.Verbose("NotifyAcceptingChannel {ChName} {ChIndex} server {FullName}: Refused", channel.ChName, channel.ChIndex, typeof(UWorld).FullName);
            return false;
        }
    }

    public void NotifyControlMessage(UNetConnection connection, NMT messageType, FInBunch bunch) {
        if (NetDriver == null) throw new UnrealNetException();
        
        if (!NetDriver.IsServer()) throw new NotSupportedException("Client code");
        else {
            // Logger.Verbose("Level server received: {MessageType}", messageType);

            if (!connection.IsClientMsgTypeValid(messageType)) {
                //Logger.Error("IsClientMsgTypeValid FAILED ({MessageType}): Remote Address = {Address}", (int)messageType, connection.LowLevelGetRemoteAddress());
                bunch.SetError();
                return;
            }

            switch (messageType) {
                case NMT.Hello: {
                    const int localNetworkVersion = 0;
                    
                    if (NMT_Hello.Receive(bunch, out var isLittleEndian, out var remoteNetworkVersion, out var encryptionToken)) {
                        // Logger.Information("Client connecting with version. LocalNetworkVersion: {Local}, RemoteNetworkVersion: {Remote}", localNetworkVersion, remoteNetworkVersion);
                        
                        // TODO: Version check.

                        if (string.IsNullOrEmpty(encryptionToken)) connection.SendChallengeControlMessage();
                        else {
                            throw new NotImplementedException("Encryption");
                        }
                    }
                    break;
                }

                case NMT.Netspeed: {
                    if (NMT_Netspeed.Receive(bunch, out var rate)) {
                        connection.CurrentNetSpeed = Math.Clamp(rate, 1800, NetDriver.MaxClientRate);
                        // Logger.Debug("Client netspeed is {Num}", connection.CurrentNetSpeed);
                    }

                    break;
                }

                case NMT.Abort: {
                    break;
                }

                case NMT.Skip: {
                    break;
                }

                case NMT.Login: {
                    // Admit or deny the player here.
                    if (NMT_Login.Receive(bunch, out var clientResponse, out var tmpRequestUrl, out var uniqueIdRepl, out var onlinePlatformName)) {
                        connection.ClientResponse = clientResponse;
                        connection.RequestURL = tmpRequestUrl;
                        
                        // Only the options/portal for the URL should be used during join
                        var newRequestUrl = connection.RequestURL;
                        
                        var oneIndex = newRequestUrl.IndexOf('?');
                        var twoIndex = newRequestUrl.IndexOf('#');

                        if (oneIndex != -1 && twoIndex != -1) newRequestUrl = newRequestUrl.Substring(Math.Min(oneIndex, twoIndex));
                        else if (oneIndex != -1) newRequestUrl = newRequestUrl.Substring(oneIndex);
                        else if (twoIndex != -1) newRequestUrl = newRequestUrl.Substring(twoIndex);
                        else newRequestUrl = string.Empty;
                        
                        // Logger.Debug("Login request: {RequestUrl} userId: {UserId} platform: {Platform}", newRequestUrl, uniqueIdRepl.ToDebugString(), onlinePlatformName);
                        
                        // Compromise for passing splitscreen playercount through to gameplay login code,
                        // without adding a lot of extra unnecessary complexity throughout the login code.
                        // NOTE: This code differs from NMT_JoinSplit, by counting + 1 for SplitscreenCount
                        //			(since this is the primary connection, not counted in Children)
                        var inUrl = FUrl.FromString(Url.Map + newRequestUrl);

                        if (!inUrl.Valid) {
                            connection.RequestURL = newRequestUrl;
                            // Logger.Error("NMT_Login: Invalid URL {Url}", connection.RequestURL);
                            bunch.SetError();
                            break;
                        }

                        var splitscreenCount = Math.Min(connection.Children.Count + 1, 255);
                        
                        // Don't allow clients to specify this value
                        inUrl.RemoveOption("SplitscreenCount");
                        inUrl.Options.Add($"SplitscreenCount={splitscreenCount}");

                        connection.RequestURL = inUrl.ToString();
                        
                        // skip to the first option in the URL
                        var tmp = connection.RequestURL.Substring(connection.RequestURL.IndexOf('?'));

                        // keep track of net id for player associated with remote connection
                        connection.PlayerId = uniqueIdRepl;

                        // keep track of the online platform the player associated with this connection is using.
                        connection.SetPlayerOnlinePlatformName(new FName(onlinePlatformName));
                        
                        // ask the game code if this player can join
                        string? errorMsg = null;
                        
                        var gameMode = GetAuthGameMode();
                        if (gameMode != null) gameMode.PreLogin(tmp, connection.LowLevelGetRemoteAddress(), connection.PlayerId, out errorMsg);

                        if (!string.IsNullOrEmpty(errorMsg)) {
                            // Logger.Debug("PreLogin failure: {Error}", errorMsg);
                            NMT_Failure.Send(connection, errorMsg);
                            connection.FlushNet(true);
                        } else WelcomePlayer(connection);
                    } else connection.ClientResponse = string.Empty;

                    break;
                }

                case NMT.Join: {
                    if (connection.PlayerController == null) {
                        // Spawn the player-actor for this network player.
                        // Logger.Debug("Join request: {Request}", connection.RequestURL);

                        // THE CLIENT'S OWN URL, not a blank one. This was `new FUrl()`, so the
                        // option string handed to AGameModeBase.Login was empty and every option the
                        // client sent - ?Name=, ?Platform=, ?AuthTicket= - was discarded here rather
                        // than anywhere interesting. connection.RequestURL is the one rewritten
                        // during NMT_Login above, so it still carries all of them.
                        var inURL = FUrl.FromString(connection.RequestURL);

                        Console.WriteLine($"UWorld: join request {connection.RequestURL}");

                        connection.PlayerController = SpawnPlayActor(connection, ENetRole.ROLE_AutonomousProxy, inURL, connection.PlayerId, out var errorMsg);
                    }
                    break;
                }

                default: {
                    throw new NotImplementedException($"Unhandled control message {messageType}");
                }
            }
        }
    }

    private void WelcomePlayer(UNetConnection connection) {
        var levelName = Url.Map;

        // We don't have Fortnite's real GameMode class path yet (needs SDK dumps to identify) - send
        // it if the spawned GameMode's class has a known native path, otherwise omit it. This matches
        // real UE's own client-side handling of NMT_Welcome, which only adds the "?game=" URL option
        // when GameName is non-empty, so sending nothing here is honest rather than a guess.
        var gameName = GetAuthGameMode()?.GetClass().NativePackagePath ?? string.Empty;
        var redirectUrl = string.Empty;
        
        NMT_Welcome.Send(connection, levelName, gameName, redirectUrl);

        connection.FlushNet();
        // connection.QueuedBits = 0;
        connection.SetClientLoginState(EClientLoginState.Welcomed);
    }

    private void AddNetworkActor(AActor? actor) {
        if (actor == null) {
            // Logger.Verbose("Failed to add actor, null");
            return;
        }

        if (actor.IsPendingKillPending()) {
            // Logger.Verbose("Failed to add actor, IsPendingKillPending");
            return;
        }

        var level = actor.GetLevel();
        if (level == null || !ContainsLevel(level)) {
            // Logger.Verbose("Failed to add actor, world does not contain level");
            return;
        }

        if (NetDriver != null) NetDriver.AddNetworkActor(actor);
    }

    private UActorChannel OpenActorChannelFor(UNetConnection connection, AActor actor) {
        var channel = (UActorChannel) connection.CreateChannelByName(EName.Actor, EChannelCreateFlags.OpenedLocally, UnrealConstants.IndexNone);
        channel.SetChannelActor(actor);
        channel.ReplicateActor();
        return channel;
    }

    private void RemoveNetworkActor(AActor? actor) {
        if (actor != null) {
            if (NetDriver != null) NetDriver.RemoveNetworkActor(actor);
        }
    }

    private bool ContainsLevel(ULevel inLevel) => _Levels.Contains(inLevel);

    public bool IsServer() => NetDriver?.IsServer() ?? true;

    public bool HasBegunPlay() => bBegunPlay && PersistentLevel != null && PersistentLevel.Actors.Count != 0;

    public bool AreActorsInitialized() => bActorsInitialized && PersistentLevel != null && PersistentLevel.Actors.Count != 0;
    
    public async ValueTask DisposeAsync() {
        if (NetDriver != null) await NetDriver.DisposeAsync();
    }

    /// <summary>
    ///     Parameterless server-&gt;client PlayerController RPCs to fire once the connection's own
    ///     PlayerController channel is open. Overridable via CLIENT_INIT_RPCS (comma-separated; set it
    ///     to an empty string to send none) so candidates can be swapped without a rebuild - see the
    ///     call site in SpawnPlayActor for why this list exists at all.
    /// </summary>
    private static readonly string[] ClientInitRpcs =
        (Environment.GetEnvironmentVariable("CLIENT_INIT_RPCS") ?? "ClientOnGenericPlayerInitialization")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    ///     AGameModeBase::HUDClass for Athena - the class ClientSetHUD tells the client to
    ///     SpawnActor&lt;AHUD&gt;.
    ///
    ///     It MUST be an AHUD subclass. The first attempt used
    ///     /Game/Athena/HUD/AthenaHUD.AthenaHUD_C because the client loads that during map load, but
    ///     the client's own log gave it away: "InternalLoadObject loaded
    ///     WidgetBlueprintGeneratedClass /Game/Athena/HUD/AthenaHUD.AthenaHUD_C" - that asset is a UMG
    ///     *widget*, not a HUD actor, so SpawnActor&lt;AHUD&gt; returned null and nothing appeared. The
    ///     RPC itself was fine: it was received and the object resolved.
    ///
    ///     The real chain in this build is AHUD -&gt; AFortUIBaseClass -&gt; AFortUIZone -&gt;
    ///     AFortUIPvP, and AFortUIPvP is final with no subclasses - it is the only concrete Fortnite
    ///     HUD actor class ("PvP" is Battle Royale here, as opposed to Save the World). Athena_GameMode_C
    ///     does not override HUDClass (checked its CDO - it sets GameStateClass/PlayerControllerClass/
    ///     SpectatorClass and not this), so the value comes from the native AFortGameModeAthena
    ///     default, which this is.
    ///
    ///     Overridable via HUD_CLASS.
    /// </summary>
    private static readonly string HudClassPath =
        Environment.GetEnvironmentVariable("HUD_CLASS") ?? "/Script/FortniteGame.FortUIPvP";

    private APlayerController? SpawnPlayActor(UPlayer newPlayer, ENetRole remoteRole, FUrl inURL, FUniqueNetIdRepl uniqueId, out string error, byte inNetPlayerIndex = 0) {
        error = string.Empty;
        
        // Make the option string.
        var options = inURL.OptionsToString();

        var gameMode = GetAuthGameMode();
        if (gameMode != null) {
            var newPlayerController = gameMode.Login(newPlayer, remoteRole, inURL.Portal, options, uniqueId, out error);
            if (newPlayerController == null) {
                // Logger.Warning("Login failed: {Error}", error);
                return null;
            }
            
            // Logger.Debug("{A} got player {B} [{C}]", newPlayerController);
            
            // Possess the newly-spawned player.
            newPlayerController.NetPlayerIndex = inNetPlayerIndex;
            newPlayerController.SetRole(ENetRole.ROLE_Authority);
            newPlayerController.SetReplicates(remoteRole != ENetRole.ROLE_None);
            if (remoteRole == ENetRole.ROLE_AutonomousProxy) newPlayerController.SetAutonomousProxy(true);
            newPlayerController.SetPlayer(newPlayer);
            gameMode.PostLogin(newPlayerController);

            // Real UE does this from ServerReplicateActors' per-connection relevancy pass, which
            // isn't implemented yet - this is a one-shot stand-in that just opens a channel for the
            // connection's own PlayerController (and Pawn, once PostLogin possesses one). No ongoing
            // per-tick property replication happens yet - see AFortOnlineBeacon.Net.UPackageMapClient.
            if (newPlayer is UNetConnection ownerConnection) {
                // Before the GameState, so its NetGUID is already assigned when the GameState's own
                // property push references it at handle 22 - same ordering rule as WorldInventory
                // below.
                if (gameMode.GameState?.FortTimeOfDayManager != null)
                    OpenActorChannelFor(ownerConnection, gameMode.GameState.FortTimeOfDayManager);

                // The five management actors, for the same reason and under the same rule: the
                // GameState names each of them (handles 32, 38, 105, 106, 185) and an ObjectRef can
                // only carry a NetGUID that already exists. Getting this order wrong does not fail
                // loudly - the client just reads null and carries on - which is precisely the
                // failure mode these actors exist to remove. See Net/Actors/FortManagementActors.cs.
                foreach (var manager in new AActor?[] {
                             gameMode.GameState?.PoiManager, gameMode.GameState?.AnnouncementManager,
                             gameMode.GameState?.SpecialActorData, gameMode.GameState?.ReplOverrideData,
                             gameMode.GameState?.VolumeManager
                         }) {
                    if (manager != null) OpenActorChannelFor(ownerConnection, manager);
                }

                // The playlist's mutators. Nothing names them at a handle, so this is not the
                // ordering rule - it is just that they are part of the match's fixed setup and the
                // real server opens them in this same burst, so they go out with it rather than
                // trailing in on the next newly-relevant sweep.
                foreach (var mutator in gameMode.Mutators) OpenActorChannelFor(ownerConnection, mutator);

                if (gameMode.GameState != null) OpenActorChannelFor(ownerConnection, gameMode.GameState);
                // Before the PlayerState, which names it at handle 69 - the same ordering rule again.
                if (newPlayerController.PlayerState?.PlayerTeamPrivate != null)
                    OpenActorChannelFor(ownerConnection, newPlayerController.PlayerState.PlayerTeamPrivate);
                if (newPlayerController.PlayerState != null) OpenActorChannelFor(ownerConnection, newPlayerController.PlayerState);
                // See AFortInventory's doc comment - a real actor with its own channel, opened before
                // the PlayerController so its GUID is already assigned when the PC's own property
                // push (WorldInventory, an ObjectRef Cmd) references it, matching PlayerState's order.
                if (newPlayerController.WorldInventory != null) OpenActorChannelFor(ownerConnection, newPlayerController.WorldInventory);
                // Same ordering rule again - see AFortBroadcastRemoteClientInfo's doc comment.
                var broadcastInfoChannel = newPlayerController.BroadcastRemoteClientInfo != null
                    ? OpenActorChannelFor(ownerConnection, newPlayerController.BroadcastRemoteClientInfo)
                    : null;
                var pcChannel = OpenActorChannelFor(ownerConnection, newPlayerController);

                // The ordering rule above is a CYCLE for this one actor, and only re-sending breaks it.
                // The PlayerController must be able to name BroadcastRemoteClientInfo (handle 80), so
                // that channel has to open first - but the info actor's own Owner (handle 13) names the
                // PlayerController right back, and at that moment the PC has no channel, so the client
                // reads Owner as null and keeps it null.
                //
                // That is not cosmetic. A client sends a Server RPC through
                // UNetDriver::ProcessRemoteFunction, which does `Connection = Actor->GetNetConnection()`
                // and gives up silently if it is null - and AActor::GetNetConnection() is
                // `Owner ? Owner->GetNetConnection() : nullptr`. With Owner null the client drops every
                // ServerSetPlayerBuildableClass on the floor without logging anything, which is exactly
                // what a real client log of this server showed: the piece-select sound cue fired 100
                // times and the RPC was sent 0 times, while `InternalLoadObject loaded NULL from
                // NetGUID <10>` (the PlayerController) sat right after the info actor's own bunch.
                broadcastInfoChannel?.MarkPropertyDirty("Owner");

                // AFortPlayerController::ClientOnGenericPlayerInitialization - a parameterless client
                // RPC Fortnite hangs off AGameModeBase::GenericPlayerInitialization, which real UE
                // calls from PostLogin. It is sent here rather than from AGameModeBase.PostLogin
                // because the channel only exists once the actor channel above is open.
                //
                // Why it matters: on 10.40 AFortPlayerController::ClientQuickBars is
                // "ZeroConstructor, IsPlainOldData, NoDestructor, Protected" - NO Net flag - and it
                // is the only AFortQuickBars* field in the entire SDK. So the quickbars that
                // ClientRestart_Implementation refuses to continue without ("Quickbars are invalid")
                // can never be handed over from the server the way WorldInventory is; the client has
                // to spawn AFortQuickBars itself, and something has to tell it to. (Erbium and
                // Project-Reboot-3.0 do assign PlayerController->QuickBars server-side, but PR3.0
                // guards that with `Fortnite_Version <= 2.5` - on those builds the field really was
                // replicated. It is not on this one.)
                //
                // CLIENT_INIT_RPCS overrides the list (comma-separated, empty string sends none), so
                // the next candidate can be tried without a rebuild.
                //
                // THE ORDER OF THIS WHOLE BLOCK IS THE CAPTURE'S, not a guess, and it is not the
                // order this server used to send it in. PriveDev/PacketProxy/decoded_new.txt has the
                // real server's login RPCs in exactly two bursts on the PlayerController's channel:
                //
                //     #56  (the same bunch that spawns the PlayerController)
                //          field[135] = ClientRegisterWithParty            (0 bits)
                //          field[48]  = ClientSetHUD                       (17 bits)
                //          field[19]  = ClientEnableNetworkVoice           (1 bit)
                //
                //     #215 (after the client has acked its way through the map)
                //          field[60]  = ClientUpdateMultipleLevelsStreamingStatus (333829 bits)
                //          field[21]  = ClientFlushLevelStreaming          (0 bits)
                //          field[127] = ClientOnGenericPlayerInitialization(0 bits)
                //          field[16]  = ClientCapBandwidth                 (33 bits)
                //          field[142] = ClientSetSpectatorCamera           (117 bits)
                //          field[24]  = ClientGotoState                    (18 bits)
                //
                // The bit counts are what make this trustworthy rather than a decoder's guess at a
                // field name: 33 = 1 presence + int32, 1 = a lone bool with no presence bit of its
                // own, 17 = 1 presence + a 16-bit NetGUID, 0 = no parameters. Four independent size
                // checks against four different signatures all land, so the field mapping is real.
                //
                // Two things follow that this server had wrong. ClientSetHUD comes BEFORE
                // ClientOnGenericPlayerInitialization, not after - the HUD is up before Fortnite's
                // own init hook runs. And ClientSetSpectatorCamera is part of the login sequence at
                // all, which is where the pre-bus camera comes from.
                //
                // Still not sent: ClientUpdateMultipleLevelsStreamingStatus. That is the server
                // driving the client's sublevel streaming, and this server has nothing to drive it
                // with - every actor here goes out with a null level reference. See
                // PriveDev/dumpwork/GAP-vs-PR30.md.
                pcChannel.SendParameterlessRpc("ClientRegisterWithParty");

                // AGameModeBase::InitializeHUDForPlayer - the OTHER half of GenericPlayerInitialization:
                //
                //     void AGameModeBase::InitializeHUDForPlayer_Implementation(APlayerController* NewPlayer)
                //     { NewPlayer->ClientSetHUD(HUDClass); }
                //
                // and APlayerController::ClientSetHUD_Implementation is what actually spawns the HUD
                // actor client-side (PlayerController.cpp:1241 - SpawnActor<AHUD>(NewHUDClass)).
                // Confirmed missing from a 675k-line client log of a stuck join: zero occurrences of
                // ClientSetHUD, and zero AthenaHUD_C instances - the client had the class loaded and
                // was never told to spawn it, so it sat on the loading screen with possession and
                // movement both already working.
                //
                // TSubclassOf<AHUD> is just an object reference to a UClass on the wire, so this goes
                // out the same way HeroType does: a path-exported static asset (UAssetRegistry).
                pcChannel.SendObjectRpc("ClientSetHUD", UAssetRegistry.GetOrCreate(HudClassPath));

                // The rest of GenericPlayerInitialization / PostLogin's client-facing calls
                // (GameModeBase.cpp:914-986). Neither does much on its own -
                // ClientEnableNetworkVoice_Implementation is just ToggleSpeaking(bEnable), and
                // ClientCapBandwidth_Implementation only stores ClientCap - but they are part of the
                // sequence a real client is written against, and sending nothing at all is exactly
                // the class of omission that hid ClientSetHUD.
                //
                // AGameSession::RequiresPushToTalk() defaults to true, so the real argument here is
                // false; NetSpeed matches UNetConnection's own default.
                pcChannel.SendBoolRpc("ClientEnableNetworkVoice", false);

                // --- the capture's second burst (#215) starts here ---

                // APlayerController::ClientFlushLevelStreaming_Implementation (PlayerController.cpp:303)
                // blocks the client on its pending streaming requests until they finish. The real
                // server sends it right after handing over the level status list; this server has no
                // list to hand over, so all it does here is make the client settle whatever it
                // started for itself before the init hook below runs.
                pcChannel.SendParameterlessRpc("ClientFlushLevelStreaming");

                foreach (var rpcName in ClientInitRpcs) pcChannel.SendParameterlessRpc(rpcName);

                pcChannel.SendIntRpc("ClientCapBandwidth", newPlayerController.Player?.CurrentNetSpeed ?? 10000);

                // The pre-bus camera - see UActorChannel.SendClientSetSpectatorCamera. The player has
                // not been given a pawn yet at this point in a real match, so the only sensible
                // aiming point is where this connection is about to be put; the pawn's own transform
                // once it exists, and the warmup anchor before that.
                if (Environment.GetEnvironmentVariable("CLIENT_SPECTATOR_CAMERA") is not "0") {
                    var cameraPawn = newPlayerController.Pawn;
                    pcChannel.SendClientSetSpectatorCamera(
                        cameraPawn?.GetActorLocation() ?? FortWarmupStarts.Anchor,
                        cameraPawn?.GetActorRotation() ?? new FRotator());
                }

                if (newPlayerController.Pawn != null) {
                    // Before the pawn, for the third time in this block and for the same reason:
                    // the pawn's opening property push names CurrentWeapon (handle 66) as an
                    // ObjectRef, so the weapon it points at needs a NetGUID first. PostLogin equips
                    // the starting pickaxe (AGameModeBase.RestartPlayer), so unlike a mid-match
                    // equip - which UNetDriver.ServerReplicateActors sequences for free - this one
                    // happens before any channel exists and has to be ordered by hand.
                    if (newPlayerController.Pawn.CurrentWeapon != null)
                        OpenActorChannelFor(ownerConnection, newPlayerController.Pawn.CurrentWeapon);

                    OpenActorChannelFor(ownerConnection, newPlayerController.Pawn);

                    // Same cycle as broadcastInfoChannel's Owner above, one property later: PostLogin
                    // (line 466) already possessed this pawn before pcChannel opened and pushed its
                    // initial properties, so that first push named AController::Pawn (handle 17) as an
                    // ObjectRef to an actor with no NetGUID yet - the client reads it NULL and never
                    // reconsiders. Dirty it now that the pawn's own channel exists, so the next
                    // ServerReplicateActors tick resends it resolvable.
                    pcChannel.MarkPropertyDirty("Pawn");

                    // See UActorChannel.SendClientRestart's doc comment - without this, a real client
                    // never recognizes it controls this pawn and keeps calling
                    // ServerSetSpectatorLocation forever instead of actually moving.
                    pcChannel.SendClientRestart(newPlayerController.Pawn);

                    // AFortPlayerController::ClientForceWorldInventoryUpdate() - parameterless, and it
                    // drives the client's HandleWorldInventoryLocalUpdate.
                    //
                    // The capture puts it HERE and nowhere else: decoded_new.txt #356 is the login
                    // restart burst and its field order is ClientGotoState, ClientRestart,
                    // ClientForceWorldInventoryUpdate, ClientSetViewTarget, ClientSetRotation - i.e.
                    // immediately after the restart that hands the client its pawn, before the camera
                    // is pointed at it. It appears exactly once in the whole session, so it is a
                    // login-time kick and not something the real server repeats.
                    //
                    // WorldInventory already has its own channel and its own property push by this
                    // point (that happens ~80 lines up); what this adds is the client being told to
                    // re-read it now rather than whenever it next notices.
                    if (Environment.GetEnvironmentVariable("CLIENT_FORCE_INVENTORY_UPDATE") is not "0") {
                        pcChannel.SendParameterlessRpc("ClientForceWorldInventoryUpdate");
                    }

                    // And tell the client which way it is facing. A real server sends this at login
                    // too (see UActorChannel.SendClientSetRotation); without it the client's initial
                    // control rotation is whatever it happened to be, which is the shape of the
                    // "spawned looking 90 degrees off" report.
                    pcChannel.SendClientSetRotation(newPlayerController.Pawn.GetActorRotation(), true);

                    // And which quickbar slot to hold - see UActorChannel.SendClientActivateSlot for
                    // why every parameter goes out at its default, and for the capture that proves
                    // the real server sends exactly this.
                    //
                    // KEPT ON THE STRENGTH OF THE CAPTURE, NOT BECAUSE IT FIXED ANYTHING. It was
                    // added to stop the client picking a BUILDING TOOL for itself on joining and it
                    // did not - what fixed that was removing this server's own pre-equip, which had
                    // been pre-empting the client's slot choice (see AGameModeBase.SpawnAndPossessPawn).
                    // It stays because the real server demonstrably sends it, and it stays switchable
                    // because its benefit HERE has never been demonstrated: CLIENT_ACTIVATE_SLOT=0.
                    if (Environment.GetEnvironmentVariable("CLIENT_ACTIVATE_SLOT") is not "0") {
                        pcChannel.SendClientActivateSlot();
                    }
                }
            }

            return newPlayerController;
        }
        
        // Logger.Warning("Login failed: No game mode set");
        return null;
    }

    public AActor SpawnActor(UClass? clazz, FVector? location, FRotator? rotation, FActorSpawnParameters spawnParameters) {
        var transform = new FTransform();
        
        if (location != null) transform.Location = location;

        if (rotation != null) {
            // TODO: FQuat
            // transform.Rotation = 
        }

        return SpawnActor(clazz, transform, spawnParameters);
    }

    public AActor SpawnActor(UClass? clazz, FTransform? userTransformPtr, FActorSpawnParameters spawnParameters) {
        if (clazz == null) {
            // Logger.Warning("SpawnActor failed because no class was specified");
            return null;
        }
        
        // TODO: Bunch of if checks
        var levelToSpawnIn = spawnParameters.OverrideLevel;
        if (levelToSpawnIn == null) {
            // Spawn in the same level as the owner if we have one.
            levelToSpawnIn = (spawnParameters.Owner != null) ? spawnParameters.Owner.GetLevel() : _CurrentLevel;
        }

        var newActorName = spawnParameters.Name;
        var template = spawnParameters.Template;

        if (template == null) template = (AActor)clazz.GetDefaultObject<AActor>();

        if (newActorName == EName.None) {
            // If we are using a template object and haven't specified a name, create a name relative to the template, otherwise let the default object naming behavior in Stat
            if (!template.HasAnyFlags(EObjectFlags.RF_ClassDefaultObject)) {
                throw new NotImplementedException();
            }
        } 
        /* else if (StaticFindObjectFast(nullptr, LevelToSpawnIn, NewActorName)) */

        // See if we can spawn on ded.server/client only etc (check NeedsLoadForClient & NeedsLoadForServer)
        // TODO: CanCreateInCurrentContext

        var userTransform = userTransformPtr ?? new FTransform(); // TODO: FTransform::Identity
        var collisionHandlingOverride = spawnParameters.SpawnCollisionHandlingOverride;
        
        // "no fail" take preedence over collision handling settings that include fails
        if (spawnParameters.bNoFail) {
            // maybe upgrade to disallow fail
            if (collisionHandlingOverride == ESpawnActorCollisionHandlingMethod.AdjustIfPossibleButDontSpawnIfColliding) collisionHandlingOverride = ESpawnActorCollisionHandlingMethod.AdjustIfPossibleButAlwaysSpawn;
            else if (collisionHandlingOverride == ESpawnActorCollisionHandlingMethod.DontSpawnIfColliding) collisionHandlingOverride = ESpawnActorCollisionHandlingMethod.AlwaysSpawn;
        }

        // use override if set, else fall back to actor's preference
        var collisionHandlingMethod = (collisionHandlingOverride == ESpawnActorCollisionHandlingMethod.Undefined)
            ? template.SpawnCollisionHandlingMethod
            : collisionHandlingOverride;
        
        // see if we can avoid spawning altogether by checking native components
        // note: we can't handle all cases here, since we don't know the full component hierarchy until after the actor is spawned
        if (collisionHandlingMethod == ESpawnActorCollisionHandlingMethod.DontSpawnIfColliding) {
            throw new NotImplementedException();
        }

        var actorFlags = spawnParameters.ObjectFlags;
        UPackage? externalPackage = null;
        
        // actually make the actor object
        var actor = UObjectGlobals.NewObject<AActor>(levelToSpawnIn, clazz, newActorName, actorFlags, template, false, null, externalPackage);

        if (actor == null)
        {
            throw new UnrealException("Failed to create actor");
        }

        if (actor.GetLevel() != levelToSpawnIn) throw new UnrealException("Actor spawned with the incorrect level");
        
        // tell the actor what method to use, in case it was overridden
        actor.SpawnCollisionHandlingMethod = collisionHandlingMethod;
        
        actor.PostSpawnInitialize(userTransform, spawnParameters.Owner, spawnParameters.Instigator, spawnParameters.bRemoteOwned, spawnParameters.bNoFail, spawnParameters.bDeferConstruction);
        
        // if we are spawning an external actor, clear the dirty flag after post spawn initialize which might have dirtied the level package through running construction scripts
        if (externalPackage != null) {
            throw new NotImplementedException();
        }

        if (actor.IsPendingKill() && !spawnParameters.bNoFail) {
            // TODO: GetPathName
            // Logger.Debug("SpawnActor failed because the spawned actor %s IsPendingKill");
            return null;
        }

        actor.CheckDefaultSubobjects();
        
        // Add this newly spawned actor to the network actor list. Do this after PostSpawnInitialize so that actor has "finished" spawning.
        AddNetworkActor(actor);
        
        return actor;
    }

    public T SpawnActor<T>(UClass? clazz, FActorSpawnParameters spawnParameters) where T : AActor => (T)SpawnActor(clazz, null, null, spawnParameters);
}