using System.Collections.Concurrent;
using System.Net;

namespace AFortOnlineBeacon.Net;

public abstract class UNetDriver {
    private float _elapsedTime; // use float for 4.23.0, double is for 4.26.2
    
    protected UNetDriver() {
        GuidCache = new FNetGUIDCache(this);
        // TODO: Load from Engine ini
        ChannelDefinitionMap = new Dictionary<FName, FChannelDefinition>();
        ChannelDefinitions = new List<FChannelDefinition>
        {
            new FChannelDefinition(EName.Control, typeof(string), 0, true, false, true, false, true),
            new FChannelDefinition(EName.Voice, typeof(string), 1, true, true, true, true, true),
            new FChannelDefinition(EName.Actor, typeof(string), -1, false, true, false, false, false)
        };
        ClientConnections = new List<UNetConnection>();
        MappedClientConnections = new ConcurrentDictionary<IPEndPoint, UNetConnection>();
        
        foreach (var channel in ChannelDefinitions) ChannelDefinitionMap[channel.Name] = channel;
    }

    // TODO: From Engine ini
    public float KeepAliveTime { get; } = 0.2f;

    // TODO: From Engine ini
    public int MaxClientRate { get; } = 100000;
    
    /// <summary>
    ///     World this net driver is associated with
    /// </summary>
    public UWorld? World { get; private set; }
    
    public FNetworkNotify Notify { get; private set; }
    
    public FNetGUIDCache GuidCache { get; }
    
    /// <summary>
    ///     Used to specify available channel types and their associated UClass
    /// </summary>
    public List<FChannelDefinition> ChannelDefinitions { get; } 
    
    /// <summary>
    ///     Used for faster lookup of channel definitions by name.
    /// </summary>
    public Dictionary<FName, FChannelDefinition> ChannelDefinitionMap { get; }

    /// <summary>
    ///     AConnection to the server (this net driver is a client)
    /// </summary>
    public UNetConnection ServerConnection { get; set; }

    /// <summary>
    ///     Array of connections to clients (this net driver is a host) - unsorted, and ordering changes depending on actor replication
    /// </summary>
    public List<UNetConnection> ClientConnections { get; }

    /// <summary>
    ///     Map of <see cref="IPEndPoint"/> to <see cref="UNetConnection"/>.
    /// </summary>
    public ConcurrentDictionary<IPEndPoint, UNetConnection> MappedClientConnections { get; }
    
    /// <summary>
    ///     Serverside PacketHandler for managing connectionless packets
    /// </summary>
    public PacketHandler? ConnectionlessHandler { get; private set; }
    
    /// <summary>
    ///     Reference to the PacketHandler component, for managing stateless connection handshakes
    /// </summary>
    public StatelessConnectHandlerComponent? StatelessConnectComponent { get; private set; }

    public void InitConnectionlessHandler() {
        ConnectionlessHandler = new PacketHandler();
        ConnectionlessHandler.Initialize(HandlerMode.Server, UNetConnection.MaxPacketSize, true);

        StatelessConnectComponent = (StatelessConnectHandlerComponent) ConnectionlessHandler.AddHandler<StatelessConnectHandlerComponent>();
        StatelessConnectComponent.SetDriver(this);

        ConnectionlessHandler.InitializeComponents();
    }

    public virtual bool Init(FNetworkNotify notify) {
        Notify = notify;
        // Run the FieldNetIndex cross-check now, at startup, rather than whenever the first client
        // happens to touch a class cache - a wrong index is a whole-protocol fault and belongs in
        // the first few lines of the log, not buried after a join. See its doc comment.
        NativeClassNetCache.EnsureVerified();
        UActorChannel.VerifyLifetimeConditions();

        // ...and that every RPC table actually built. See VerifyRpcTables for what goes wrong when a
        // shared parameter list is declared below the dictionary that uses it, and why finding out
        // at startup is worth a reflection walk.
        Rpc.NativeRpcHandlers.VerifyRpcTables();
        BuildingStructuralSupportSystem.VerifyCollisionGeometry();
        Actors.FortBuildingHulls.VerifyDoorway();
        WarmCollisionData();
        return true;
    }

    /// <summary>
    ///     Reads the big collision bakes NOW, at startup, instead of leaving them to whichever tick
    ///     first asks a geometry question.
    ///
    ///     THEY ARE LAZY ON PURPOSE - a tool run or a self-test should not pay for a 24 MB height
    ///     grid it will never query - but "lazy" means the cost lands wherever the first query
    ///     happens, and that is inside a tick, with players connected: the whole server stops for as
    ///     long as the read takes. The log line that gave it away is the giveaway itself, because it
    ///     appears mid-match rather than at boot:
    ///
    ///         TerrainHeightMap: loaded 3920x3061 landscape heightmap cells ... from TerrainHeightMap.bin
    ///
    ///     Init is the right place: it runs before a socket is listening, so the time is spent where
    ///     nothing is waiting on it, and every later query finds the data already there. Each read is
    ///     timed so the log says what it cost rather than leaving it to be guessed at.
    ///
    ///     Touching the `Loaded` flags is what triggers each loader; there is nothing else to call.
    /// </summary>
    private static void WarmCollisionData() {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var landscape = TerrainHeightMap.LandscapeLoaded;
        var meshes = TerrainHeightMap.HasMeshGrid;
        var hulls = WorldCollision.Loaded;
        var pieces = Actors.FortBuildingHulls.Ready;

        Console.WriteLine($"UNetDriver: collision data warmed in {started.ElapsedMilliseconds} ms " +
                          $"(landscape={landscape}, placed-mesh grid={meshes}, world hulls={hulls}, " +
                          $"build pieces={pieces}) - none of it will stall a tick now.");
    }
    
    /// <summary>
    ///     handle time update: read and process packets
    /// </summary>
    public virtual void TickDispatch(float deltaTime) {
        _elapsedTime += deltaTime;
        
        // Delete closed connections.
        for (var i = ClientConnections.Count - 1; i >= 0; i--) {
            if (ClientConnections[i].State == EConnectionState.USOCK_Closed) ClientConnections[i].CleanUp();
        }
    }

    /// <summary>
    ///     PostTickDispatch actions
    /// </summary>
    public virtual void PostTickDispatch() {
        foreach (var connection in ClientConnections) {
            /* TODO: (When UObject) if (!connection.IsPendingKill()) */
            {
                // connection.PostTickDispatch();
                connection.FlushPacketOrderCache(true);
            }
        }
    }

    /// <summary>
    ///     ReplicateActors and Flush
    /// </summary>
    public virtual void TickFlush(float deltaTime) {
        if (IsServer() && ClientConnections.Count > 0) {
            ServerReplicateActors();
        }

        foreach (var connection in ClientConnections) connection.Tick(deltaTime);

        if (ConnectionlessHandler != null) {
            ConnectionlessHandler.Tick(deltaTime);
            
            // TODO: FlushHandler
        }
        
        // TODO: (When actors are implemented) CleanupStaleDormantReplicators
    }

    /// <summary>
    ///     Sends ClientActivateAbilitySucceed on whichever connection owns this actor's channel.
    ///
    ///     It lives here rather than on the actor because a component RPC has to go out on its
    ///     OWNER'S channel, and only the driver knows which connections have one - the same reason
    ///     the ASC's property updates are driven from ServerReplicateActors.
    /// </summary>
    public void SendClientActivateAbilitySucceed(AActor owner, UObject abilitySystem, int abilityHandle,
                                                 FPredictionKey predictionKey) {
        foreach (var connection in OwningConnectionsOf(owner, nameof(SendClientActivateAbilitySucceed))) {
            connection.FindActorChannel(owner)?.SendClientActivateAbilitySucceed(abilitySystem, abilityHandle, predictionKey);
        }
    }

    /// <summary>
    ///     The connections an OWNER-ONLY (`UFUNCTION(Client, ...)`) RPC may go to - one, or none.
    ///
    ///     THIS EXISTS BECAUSE THE THREE CALLERS BELOW USED TO BROADCAST, and with a single player
    ///     that is indistinguishable from correct: the only connection with a channel for a
    ///     PlayerState IS its owner. The moment a second player joins, everyone who can SEE that
    ///     PlayerState has a channel for it too - and `ClientActivateAbilitySucceed`,
    ///     `ClientActivateAbilitySucceedWithEventData` and `ClientEndAbility` are all
    ///     `UFUNCTION(Client, reliable)` in UE's own GAS, which means the owning connection and
    ///     nobody else. Broadcasting them tells every other client to activate an ability on a
    ///     PlayerState it merely observes, against a spec handle that is only meaningful in the
    ///     owner's copy of that ASC.
    ///
    ///     Ownership is asked four ways because a PlayerState's Owner is not reliably set here: the
    ///     connection's controller, its PlayerState, its Pawn, and finally UE's own IsOwnedBy.
    ///
    ///     DEGRADES TO A BROADCAST rather than to silence, with a loud line, on the reasoning in
    ///     [[feedback-guards-degrade-dont-refuse]]: sending to everyone is what this did for months
    ///     and is survivable, whereas sending to nobody is a feature that silently stops working.
    /// </summary>
    private IEnumerable<UNetConnection> OwningConnectionsOf(AActor owner, string what) {
        var owning = ClientConnections.Where(connection =>
            connection.PlayerController is { } pc
            && (owner == pc || owner == pc.PlayerState || owner == pc.Pawn || owner.IsOwnedBy(pc))).ToArray();

        if (owning.Length > 0) return owning;

        Console.WriteLine($"UNetDriver.{what}: no connection OWNS {owner.GetFName()} " +
                          $"({owner.GetType().Name}) - falling back to every connection that can see it. " +
                          "An owner-only ability RPC is about to reach clients that only observe this actor.");
        return ClientConnections;
    }

    /// <summary>
    ///     The same activation WITH an event payload, for an ability authored with "Activate Ability
    ///     From Event" - see UActorChannel.SendClientActivateAbilitySucceedWithEventData.
    /// </summary>
    public void SendClientActivateAbilitySucceedWithEventData(AActor owner, UObject abilitySystem, int abilityHandle,
                                                              FPredictionKey predictionKey,
                                                              AActor? instigator, AActor? target) {
        foreach (var connection in OwningConnectionsOf(owner, nameof(SendClientActivateAbilitySucceedWithEventData))) {
            connection.FindActorChannel(owner)?.SendClientActivateAbilitySucceedWithEventData(
                abilitySystem, abilityHandle, predictionKey, instigator, target);
        }
    }

    /// <summary>The end of the same handshake - see UActorChannel.SendClientEndAbility.</summary>
    public void SendClientEndAbility(AActor owner, UObject abilitySystem, int abilityHandle,
                                     FPredictionKey predictionKey) {
        foreach (var connection in OwningConnectionsOf(owner, nameof(SendClientEndAbility))) {
            connection.FindActorChannel(owner)?.SendClientEndAbility(abilitySystem, abilityHandle, predictionKey);
        }
    }

    /// <summary>
    ///     Pushes <paramref name="owner"/>'s AbilitySystemComponent on every connection that has a
    ///     channel for it, without waiting for the next replication tick. Here for the same reason
    ///     SendClientActivateAbilitySucceed is: a component's traffic rides its owner's channel, and
    ///     only the driver knows which connections have one. See UActorChannel.FlushAbilitySystemComponent.
    /// </summary>
    /// <summary>
    ///     Pushes <paramref name="actor"/>'s changed properties on every connection NOW, ignoring its
    ///     NetUpdateFrequency.
    ///
    ///     For a value whose whole meaning is that it CHANGED. A RepNotify fires on the client only
    ///     when the received value differs from the one it holds, so a property that goes A -> null
    ///     -> A inside one replication interval collapses to no change at all and the client is never
    ///     told anything. Setting it back is not enough; the null has to be a bunch of its own.
    /// </summary>
    public void FlushActorProperties(AActor actor) {
        foreach (var connection in ClientConnections) {
            connection.FindActorChannel(actor)?.ReplicateActorUpdate(force: true);
        }
    }

    public void FlushAbilitySystemComponent(AActor owner) {
        foreach (var connection in ClientConnections) {
            connection.FindActorChannel(owner)?.FlushAbilitySystemComponent();
        }
    }

    /// <summary>
    ///     UNetDriver::NotifyActorDestroyed - the destroy path for a client that has no channel to
    ///     close.
    ///
    ///     A connection WITH a channel needs nothing here: the per-tick walk closes it with
    ///     EChannelCloseReason::Destroyed and that is what removes the actor. This covers the two
    ///     cases where there is no channel and the client nevertheless knows the actor:
    ///
    ///       * it went DORMANT - the channel was closed deliberately and the client was told to keep
    ///         the actor, which is exactly the trap that kept pickups out of dormancy;
    ///       * distance culling never opened one, but some other object reference taught the client
    ///         the NetGUID anyway.
    ///
    ///     Both used to end with the actor alive on the client forever, silently. Real UE keeps a
    ///     DestroyedStartupOrDormantActors map and drains it during ServerReplicateActors; this sends
    ///     immediately, which is simpler and costs one small bunch per connection at the moment of
    ///     destruction rather than a per-tick sweep.
    /// </summary>
    public void NotifyActorDestroyed(AActor destroyed) {
        if (!destroyed.bReplicates) return;

        // Never introduced to anyone - the guid cache would happily mint one here, and destroying an
        // actor the client has never heard of is a bunch that can only confuse it.
        if (!GuidCache.NetGUIDLookup.TryGetValue(destroyed, out var knownGuid) || !knownGuid.IsValid()) return;

        foreach (var connection in ClientConnections) {
            if (connection.FindActorChannel(destroyed) != null) continue;   // the ordinary close covers it

            // ONLY FOR ACTORS THIS CONNECTION WAS TOLD TO KEEP. The guid cache is driver-wide, not
            // per connection, so "a NetGUID exists" says one client saw the actor - not this one. The
            // dormant set is the one thing that is per connection and certain: we closed its channel
            // ourselves and told it to hold on to the actor.
            //
            // The other case - never culled into view, but taught the guid by some other object
            // reference - is left alone deliberately. Sending a destroy for an actor a client never
            // knew is a bunch it can only misread, and getting it right needs per-connection
            // ack tracking (real UE's FNetGUIDCache NetGUIDAckStatus), which this port does not have.
            if (!connection.DormantActors.Remove(destroyed)) continue;

            var channel = (UActorChannel) connection.CreateChannelByName(
                EName.Actor, EChannelCreateFlags.OpenedLocally, UnrealConstants.IndexNone);

            channel.SendDestructionInfo(destroyed);

            if (_LoggedDestructionInfoClasses.Add(destroyed.GetType().Name)) {
                Console.WriteLine($"UNetDriver.NotifyActorDestroyed: {destroyed.GetType().Name} was DORMANT on " +
                                  "this connection, so a channel-free destruction info went instead of a channel " +
                                  "close. THIS IS WHAT LETS DESTROYABLE ACTORS BE DORMANT. ONE LINE PER CLASS.");
            }
        }
    }

    /// <summary>Actor classes a destruction info has already been reported for - once each.</summary>
    private readonly HashSet<string> _LoggedDestructionInfoClasses = new();

    /// <summary>
    ///     Set false by REP_TICK=0. The escape hatch for the whole ongoing-replication pass: before
    ///     this existed every channel was a one-shot burst at join, so turning it off restores
    ///     exactly the behaviour every earlier live test ran against.
    /// </summary>
    private static readonly bool RepTickEnabled = Environment.GetEnvironmentVariable("REP_TICK") != "0";

    /// <summary>
    ///     Heavily reduced UNetDriver::ServerReplicateActors. Real UE builds a prioritised,
    ///     relevancy-filtered list of every network actor per connection and opens/closes channels
    ///     as actors come and go; Fortnite replaces that wholesale with UReplicationGraph. This
    ///     project opens its channels by hand in UWorld.SpawnPlayActor and never closes them, so
    ///     the only part that is actually missing is the last step of the real function: walk the
    ///     connection's open channels and ask each one to replicate what changed.
    ///
    ///     Everything that decides WHETHER to send lives in UActorChannel.ReplicateActorUpdate -
    ///     the NetUpdateFrequency gate, the diff against the shadow state, and the "changed nothing,
    ///     send nothing" early out. This just drives it.
    /// </summary>
    protected virtual int ServerReplicateActors() {
        if (!RepTickEnabled) return 0;

        var updated = 0;

        // AActor::PreReplication's one job that matters here. Done ONCE for every replicating actor
        // before any connection is walked, not per connection: the comparison that follows is
        // against a per-connection shadow, but the value being compared has to be the same for all
        // of them or two clients would be told different positions for the same pawn on the same
        // tick. It also has to happen before the loop rather than inside UActorChannel, because the
        // first connection's pass would otherwise gather and the second would compare against an
        // already-updated value and send nothing.
        var now = World?.TimeSeconds ?? 0f;
        foreach (var actor in NetworkObjectList) {
            if (actor.bReplicateMovement && !actor.IsPendingKillPending()) actor.GatherCurrentMovement(now);
        }

        // WAKE anything that asked to be woken, BEFORE the sweep that would otherwise skip it.
        // AActor.FlushNetDormancy cannot reach the connections itself; this is the driver doing it on
        // the actor's behalf, and doing it FIRST is what makes "flush then destroy" work - the channel
        // reopens on this pass and the destroy close goes out on the next.
        foreach (var actor in NetworkObjectList.ToArray()) {
            if (!actor.bDormancyFlushed) continue;

            actor.bDormancyFlushed = false;
            foreach (var connection in ClientConnections) {
                if (connection.DormantActors.Remove(actor)) {
                    Console.WriteLine($"UNetDriver: {actor.GetType().Name} '{actor.GetFName()}' WOKE from " +
                                      "dormancy - its channel reopens on this pass.");
                }
            }
        }

        foreach (var connection in ClientConnections) {
            updated += OpenChannelsForNewlyRelevantActors(connection);

            // Snapshotted because a send can close the connection (a reliable overflow does), which
            // mutates OpenChannels underneath the walk.
            foreach (var channel in connection.OpenChannels.ToArray()) {
                if (channel is not UActorChannel actorChannel) continue;

                var wroteThisTick = actorChannel.ReplicateActorUpdate();
                if (wroteThisTick) updated++;

                if (actorChannel.Actor is not { } actor) continue;

                // A destroyed actor's channel is closed AFTER its last property update, so a final
                // state change (a pickup's bPickedUp, say) still gets a chance to go out ahead of
                // the close. Closing is the only thing that removes the actor from the client -
                // marking it destroyed server-side is invisible on its own.
                if (actor.IsPendingKillPending() && !actorChannel.Closing) {
                    actorChannel.Close(EChannelCloseReason.Destroyed);
                    updated++;
                    continue;
                }

                // TORN OFF. Same shape as the destroy above and the opposite outcome on the client:
                // the channel closes with ::TearOff and the actor STAYS, as a purely local one that
                // will never be updated again. A dead player's corpse is what this is for - see
                // AActor.TearOff and the capture line it quotes. Ordered after the destroy so an
                // actor that is both simply gets destroyed, which is the stronger statement.
                if (actor.bTearOff && !actorChannel.Closing) {
                    actorChannel.Close(EChannelCloseReason.TearOff);

                    // AND THEN OUT OF THE LIST, or the next pass opens a fresh channel for an actor
                    // that is still relevant and this closes that one too - a reliable bunch per
                    // tick, forever. See AActor.FinishTearOff for the live log that showed it. The
                    // ReplicateActorUpdate above has already carried bTearOff=true, which is the
                    // half that has to happen while the actor is still in the list.
                    actor.FinishTearOff();

                    updated++;
                    continue;
                }

                // DORMANCY. An actor that says it will not change again gets its channel CLOSED, and
                // the client keeps it: UActorChannel::CleanUp (DataChannel.cpp:1978) has a branch
                // for exactly this close reason that sets NetDormancy on its own copy and skips
                // DestroyActorAndComponents. Nothing else in this server may close a channel and
                // leave the actor standing.
                //
                // Not until it has actually SENT everything, though. The gate is "wrote nothing this
                // tick and the open has been acked" - a channel closed before its open lands, or
                // while a property is still pending, would leave the client holding an actor that
                // never received the state that made it worth having.
                if (DormancyEnabled && actor.NetDormancy == ENetDormancy.DormantAll
                    && !wroteThisTick && actorChannel.OpenAcked && !actorChannel.Closing) {
                    actorChannel.Close(EChannelCloseReason.Dormancy);
                    connection.DormantActors.Add(actor);
                    updated++;

                    if (_LoggedDormantClasses.Add(actor.GetType().Name)) {
                        Console.WriteLine($"UNetDriver: {actor.GetType().Name} went DORMANT (first one: " +
                                          $"'{actor.GetFName()}', ChIndex={actorChannel.ChIndex}). Its channel is " +
                                          "closed and the client keeps the actor. ONE LINE PER CLASS - later ones " +
                                          "are silent; count them with `grep -c reason=Dormancy`. NET_DORMANCY=0 " +
                                          "disables this.");
                    }
                }
            }
        }

        return updated;
    }

    /// <summary>
    ///     The half of UNetDriver::ServerReplicateActors this project never had: giving a connection
    ///     a channel for an actor that became relevant AFTER it joined. Until this existed, the only
    ///     channels that ever opened were the fixed set UWorld.SpawnPlayActor opens by hand during
    ///     login, so nothing spawned mid-match - a dropped weapon's pickup, a projectile, a second
    ///     player - could ever reach a client.
    ///
    ///     Deliberately does nothing until the connection's own PlayerController has a channel.
    ///     SpawnPlayActor opens the login channels in a specific ORDER that matters (the
    ///     TimeOfDayManager before the GameState, so handle 22 has a NetGUID to point at; the
    ///     inventory before the PlayerController, likewise for WorldInventory) and this pass must
    ///     not interleave itself into the middle of that.
    /// </summary>
    private int OpenChannelsForNewlyRelevantActors(UNetConnection connection) {
        var viewer = connection.PlayerController;
        if (viewer == null || connection.FindActorChannel(viewer) == null) return 0;

        // POSSESSION COMES FIRST, AND NOTHING ELSE GOES OUT UNTIL IT IS DONE.
        //
        // This cost a long hunt (Rounds 144-147). Adding the supply llamas and the vehicles broke
        // JUMPING - and also collision with every building and prop, and picking items up - and the
        // cause was not the actors at all, it was WHEN their channels opened. Two console logs, one
        // working and one broken, differ at exactly one place:
        //
        //   working:  pawn bunch -> ClientRestart -> ClientSetRotation -> (quiet, acks)
        //   broken:   pawn bunch -> ClientRestart -> ClientSetRotation -> 7 vehicle channels, each
        //                                                                carrying its own Blueprint
        //                                                                must-be-mapped GUID
        //
        // The pawn's own bunch is HELD client-side while PlayerPawn_Athena_C and DefaultGlider async
        // load (`AppendMustBeMappedGuids: ChIndex=17 count=2 guids=[123,127]`). Dropping seven more
        // Blueprint class loads into that same window is what left the client with a pawn it had
        // possessed but not finished: visible, moved by its own prediction, and with none of the
        // state that collision and jumping need. Note the total channel count was almost the same in
        // both runs (37 vs 30) - it is not volume, it is WHAT lands in the possession window.
        //
        // Real UE never has this problem because ServerReplicateActors PRIORITISES: FActorPriority
        // sorts every relevant actor per connection and the owner's own actors sort to the front.
        // This port has no priority sort at all, so this is the smallest faithful stand-in for one -
        // hold everything that is not the viewer's own until the client says it is ready.
        //
        // TWO SIGNALS, AND THE CLIENT'S OWN IS THE BETTER ONE. ServerAcknowledgePossession means
        // "I have taken the pawn"; ServerClientPawnLoaded(true) means "I have finished LOADING and
        // spawning it", which is precisely the window that was being trampled - the pawn's bunch sits
        // queued client-side while its Blueprint async loads, and possession can be acknowledged
        // before that finishes. Either one satisfies the gate, because a client that never sends
        // ServerClientPawnLoaded must not be able to stall this forever.
        var possessionComplete = viewer.Pawn == null
                                 || viewer.bClientPawnLoaded
                                 || viewer.AcknowledgedPawn == viewer.Pawn;

        // And a per-tick cap even after that, so a burst of newly relevant actors can never again
        // arrive as one indivisible wall of channel opens. Real UE bounds this by bandwidth; this
        // bounds it by count, which is the same idea with the information available here.
        var budget = int.TryParse(Environment.GetEnvironmentVariable("NEWLY_RELEVANT_PER_TICK"), out var cap)
            ? cap
            : 4;

        var opened = 0;

        // FNetViewer's ViewLocation (UNetDriver.cpp) - what distance culling measures from.
        //
        // THE CAMERA, NOT THE PAWN, and that is not an approximation of the engine, it IS the engine:
        // FNetViewer takes ViewLocation from APlayerController::GetPlayerViewPoint, which returns
        // PlayerCameraManager->GetCameraLocation(), which is the camera cache that
        // APlayerController::ServerUpdateCamera_Implementation fills from the client's own report.
        // That RPC is the most frequent one this connection sends, and its whole purpose on a real
        // server is to answer this question.
        //
        // It used to use the pawn, because ServerUpdateCamera's decode was believed wrong. It is not
        // - a live client put the reported camera 254uu from its pawn, which is a third-person boom
        // to the centimetre (see NativeRpcHandlers' ServerUpdateCamera handler).
        //
        // THE PAWN IS STILL THE FALLBACK, for two cases that are not hypothetical: a client whose
        // PlayerCameraManager has bUseClientSideCameraUpdates off never sends the RPC at all, and one
        // that stops sending must not leave relevancy anchored to wherever it was last looking. The
        // staleness bound is generous - the client sends these several times a second, so a whole
        // second of silence already means something has stopped.
        //
        // Null when the connection has neither - between pawns, on the bus, mid-jump - and null means
        // "do not cull", which is why that window is safe rather than empty. See AActor.IsNetRelevantFor.
        var now = World?.TimeSeconds ?? 0f;
        var cameraIsFresh = viewer.LastClientCameraLocation != null
                            && CameraViewpointTimeout >= 0f
                            && now - viewer.LastClientCameraTime <= CameraViewpointTimeout;

        var viewLocation = cameraIsFresh ? viewer.LastClientCameraLocation : viewer.Pawn?.GetActorLocation();

        var considered = 0;
        var culled = 0;

        foreach (var actor in NetworkObjectList.ToArray()) {
            if (!actor.bReplicates || actor.IsPendingKillPending()) continue;
            if (connection.FindActorChannel(actor) != null) continue;

            // Already told this connection to keep it and stop listening. Re-opening a channel would
            // undo the dormancy and hand the client a second copy of everything - AActor.FlushNetDormancy
            // is the only way back, and the driver clears this set for it below.
            if (connection.DormantActors.Contains(actor)) continue;

            considered++;

            if (!actor.IsNetRelevantFor(viewer, viewLocation)) {
                culled++;

                // NAME EVERY CLASS THE CULL EVER REFUSES, ONCE. Culling fails SILENTLY - the actor
                // simply never appears, and the symptom shows up somewhere else entirely: the first
                // live test of this feature lost the battle bus (the aircraft is off-map and was
                // being culled) and lost ALL destructible scenery (level actors registered by path
                // have no Location, so they measured from (0,0,0)). Neither pointed at relevancy in
                // any log. One line per class turns the next case of that into something a log
                // search finds in seconds instead of a bisect.
                if (_LoggedCulledClasses.Add(actor.GetType().Name)) {
                    Console.WriteLine($"ServerReplicateActors: distance culling is REFUSING " +
                                      $"{actor.GetType().Name} (first one: '{actor.GetFName()}' at " +
                                      $"{(actor.bHasKnownLocation ? actor.GetActorLocation().ToString() : "NO KNOWN LOCATION")}, " +
                                      $"viewer at {viewLocation}). If that class should always reach " +
                                      "the client, it wants bAlwaysRelevant, not a bigger radius.");
                }

                continue;
            }

            // The viewer's own things still go out during the possession window - the pawn itself,
            // its weapons (owned by the pawn), the inventory - because those ARE the possession.
            if (!possessionComplete && !actor.IsOwnedBy(viewer) && actor != viewer.Pawn
                && !(viewer.Pawn is { } ownPawn && actor.IsOwnedBy(ownPawn))) {
                continue;
            }

            if (budget-- <= 0) break;

            var channel = (UActorChannel) connection.CreateChannelByName(
                EName.Actor, EChannelCreateFlags.OpenedLocally, UnrealConstants.IndexNone);

            channel.SetChannelActor(actor);

            Console.WriteLine($"ServerReplicateActors: opening ChIndex={channel.ChIndex} for newly relevant " +
                              $"{actor.GetType().Name} '{actor.GetFName()}'");

            channel.ReplicateActor();
            opened++;

            // ClientInternalEquipWeapon USED TO BE SENT HERE, keyed on a weapon's channel opening.
            // It now lives in UActorChannel.ReplicateEquippedWeapon, keyed on the pawn's CurrentWeapon
            // CHANGING, which is the thing it was always about. Keying it on the channel meant a
            // RE-equip sent nothing at all (the channel is already open and never opens twice), and
            // that is what left a build ghost on screen after the client switched off a building
            // tool - see that method for the whole chain.
        }

        // One line, once per connection, the first time culling actually had a location to work
        // from - so the very first live test measures the effect instead of guessing at it. Anything
        // per-tick here would drown the log; this is the number that says whether culling is doing
        // what it was added for.
        // REPORTED WHEN THE VIEWPOINT SOURCE CHANGES, not once per connection.
        //
        // The first version fired on the first tick that had any viewpoint at all, which is the one
        // moment it could not answer the question it was asked: a pawn exists several hundred log
        // lines before the client's first ServerUpdateCamera arrives, so it always said "the pawn"
        // and then never spoke again. Keying it on the SOURCE means it says "pawn" once at spawn,
        // "camera" once when the client starts reporting, and nothing at all while that holds - and
        // it says "pawn" again if the camera ever goes stale, which is the case actually worth
        // hearing about.
        if (viewLocation != null) {
            var source = cameraIsFresh ? "the CLIENT'S CAMERA, as real UE measures from"
                                       : "the pawn - no fresh camera report";

            if (!_LastViewpointSource.TryGetValue(connection, out var previous) || previous != source) {
                _LastViewpointSource[connection] = source;
                Console.WriteLine($"ServerReplicateActors: distance culling measuring from {viewLocation} " +
                                  $"({source}) - {culled} of {considered} channel-less actors out of range " +
                                  $"this tick ({considered - culled} relevant). NET_CULL=0 disables it.");
            }
        }

        return opened;
    }

    /// <summary>Which viewpoint each connection's culling last measured from, so a CHANGE can be reported.</summary>
    private readonly Dictionary<UNetConnection, string> _LastViewpointSource = new();

    /// <summary>
    ///     Dormancy, ON by default since 2026-09-04. NET_DORMANCY=0 turns it off.
    ///
    ///     It shipped opt-in first, because a channel closed for the wrong reason - or at the wrong
    ///     moment - leaves an actor on the client the server will never speak about again, and that
    ///     failure looks like nothing at all until somebody tries to interact with it. A live session
    ///     then closed 144 channels as dormant and destroyed 10 dormant actors through
    ///     UActorChannel.SendDestructionInfo with no errors, no wakes needed, no reopens, and both
    ///     vehicles and picked-up items behaving correctly - so the default flipped.
    ///
    ///     IF SOMETHING GOES WRONG LATER, SUSPECT THIS FIRST. The symptom it produces is an actor
    ///     that is present on the client and inert - visible but unusable, or gone server-side and
    ///     still drawn. NET_DORMANCY=0 restores exactly the old behaviour in one run, which makes it
    ///     a cheap thing to rule in or out.
    /// </summary>
    private static readonly bool DormancyEnabled = Environment.GetEnvironmentVariable("NET_DORMANCY") is not "0";

    /// <summary>Actor classes that have already been reported as going dormant - once each.</summary>
    private readonly HashSet<string> _LoggedDormantClasses = new();

    /// <summary>Actor classes distance culling has already been reported as refusing at least once.</summary>
    private readonly HashSet<string> _LoggedCulledClasses = new();

    /// <summary>
    ///     How stale a client-reported camera may be before relevancy stops trusting it, in seconds.
    ///
    ///     THE DEFAULT IS "NEVER", WHICH IS THE ENGINE'S BEHAVIOUR - and the first attempt at one
    ///     second was measurably wrong. `GetPlayerViewPoint` reads
    ///     `PlayerCameraManager->GetCameraLocation()`, a CACHE: it holds the last reported value
    ///     indefinitely and real UE has no staleness concept here at all. A one-second bound made
    ///     the viewpoint FLAP between camera and pawn about every 50 log lines, because the client
    ///     does not send on a fixed clock - UPlayerCameraManager::UpdateCamera only sends when the
    ///     camera actually moved or turned (or after ServerUpdateCameraTimeout), so a player
    ///     standing still legitimately says nothing for a long time.
    ///
    ///     The null check is the guard that was actually needed: a client whose camera manager has
    ///     bUseClientSideCameraUpdates off never sends the RPC, so LastClientCameraLocation stays
    ///     null and the pawn is used. That case needs no timer.
    ///
    ///     CAMERA_VIEWPOINT_TIMEOUT sets a bound in seconds if one is ever wanted; -1 turns the
    ///     camera viewpoint off entirely and pins relevancy to the pawn.
    /// </summary>
    private static readonly float CameraViewpointTimeout =
        float.TryParse(Environment.GetEnvironmentVariable("CAMERA_VIEWPOINT_TIMEOUT"), out var timeout)
            ? timeout
            : float.PositiveInfinity;

    /// <summary>
    ///     PostTick actions
    /// </summary>
    public virtual void PostTickFlush() {
        // ClearVoicePackets?
    }

    public virtual void LowLevelSend(IPEndPoint address, byte[] data, int countBits, FOutPacketTraits traits) {
        throw new NotImplementedException();
    }
    
    public abstract bool IsNetResourceValid();
    
    public void SetWorld(UWorld? inWorld) {
        if (World != null) World = null;

        if (inWorld != null) {
            World = inWorld;
            
            // TODO: AddInitialObjects?
        }
    }

    public bool IsServer() => ServerConnection == null;

    public bool IsKnownChannelName(FName name) => ChannelDefinitionMap.ContainsKey(name);

    public virtual bool ShouldIgnoreRPCs() => false;
    
    public float GetElapsedTime() => _elapsedTime;

    public void ResetElapsedTime() => _elapsedTime = 0.0f;

    private protected void AddClientConnection(UNetConnection newConnection) {
        ClientConnections.Add(newConnection);

        if (newConnection.RemoteAddr != null) {
            MappedClientConnections[newConnection.RemoteAddr] = newConnection;
            
            // TODO: RecentlyDisconnectedClients ?
        }

        CreateInitialServerChannels(newConnection);

        // TODO: NetworkObjectList > HandleConnectionAdded
    }

    protected void CreateInitialClientChannels() {
        foreach (var channelDef in ChannelDefinitions) {
            if (channelDef.InitialServer) ServerConnection.CreateChannelByName(channelDef.Name, EChannelCreateFlags.OpenedLocally, channelDef.StaticChannelIndex);
        }
    }

    private void CreateInitialServerChannels(UNetConnection clientConnection) {
        foreach (var channelDef in ChannelDefinitions) {
            if (channelDef.InitialServer) clientConnection.CreateChannelByName(channelDef.Name, EChannelCreateFlags.OpenedLocally, channelDef.StaticChannelIndex);
        }
    }

    public UChannel GetOrCreateChannelByName(FName chName) {
        // TODO: Pool actor channels (?)

        var name = chName.ToEName();
        if (name == null) throw new UnrealNetException($"Unsupported channel name specified {chName}");
        
        return name switch {
            EName.Actor => new UActorChannel(),
            EName.Control => new UControlChannel(),
            EName.Voice => new UVoiceChannel(),
            _ => throw new UnrealNetException($"Attempted to create unknown channel {chName}")
        };
    }

    /// <summary>
    ///     FNetworkObjectList - every actor the world has spawned that COULD replicate. Membership is
    ///     not the same as "replicates": UWorld.SpawnActor adds an actor here before anything calls
    ///     SetReplicates on it, exactly as real UE does, so the bReplicates test belongs at
    ///     replication time (ServerReplicateActors) rather than here.
    /// </summary>
    public HashSet<AActor> NetworkObjectList { get; } = new();

    public void AddNetworkActor(AActor actor) => NetworkObjectList.Add(actor);

    public void RemoveNetworkActor(AActor actor) => NetworkObjectList.Remove(actor);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}