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
        return true;
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
        foreach (var connection in ClientConnections) {
            connection.FindActorChannel(owner)?.SendClientActivateAbilitySucceed(abilitySystem, abilityHandle, predictionKey);
        }
    }

    /// <summary>
    ///     Pushes <paramref name="owner"/>'s AbilitySystemComponent on every connection that has a
    ///     channel for it, without waiting for the next replication tick. Here for the same reason
    ///     SendClientActivateAbilitySucceed is: a component's traffic rides its owner's channel, and
    ///     only the driver knows which connections have one. See UActorChannel.FlushAbilitySystemComponent.
    /// </summary>
    public void FlushAbilitySystemComponent(AActor owner) {
        foreach (var connection in ClientConnections) {
            connection.FindActorChannel(owner)?.FlushAbilitySystemComponent();
        }
    }

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

        foreach (var connection in ClientConnections) {
            updated += OpenChannelsForNewlyRelevantActors(connection);

            // Snapshotted because a send can close the connection (a reliable overflow does), which
            // mutates OpenChannels underneath the walk.
            foreach (var channel in connection.OpenChannels.ToArray()) {
                if (channel is not UActorChannel actorChannel) continue;

                if (actorChannel.ReplicateActorUpdate()) updated++;

                // A destroyed actor's channel is closed AFTER its last property update, so a final
                // state change (a pickup's bPickedUp, say) still gets a chance to go out ahead of
                // the close. Closing is the only thing that removes the actor from the client -
                // marking it destroyed server-side is invisible on its own.
                if (actorChannel.Actor is { } actor && actor.IsPendingKillPending() && !actorChannel.Closing) {
                    actorChannel.Close(EChannelCloseReason.Destroyed);
                    updated++;
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

        var opened = 0;

        foreach (var actor in NetworkObjectList.ToArray()) {
            if (!actor.bReplicates || actor.IsPendingKillPending()) continue;
            if (connection.FindActorChannel(actor) != null) continue;
            if (!actor.IsNetRelevantFor(viewer)) continue;

            var channel = (UActorChannel) connection.CreateChannelByName(
                EName.Actor, EChannelCreateFlags.OpenedLocally, UnrealConstants.IndexNone);

            channel.SetChannelActor(actor);

            Console.WriteLine($"ServerReplicateActors: opening ChIndex={channel.ChIndex} for newly relevant " +
                              $"{actor.GetType().Name} '{actor.GetFName()}'");

            channel.ReplicateActor();
            opened++;

            // AFortPawn::ClientInternalEquipWeapon(AFortWeapon*) - experimental, 2026-08-29. Sent
            // HERE, once this weapon's OWN channel has actually opened (ReplicateActor above just
            // gave it a resolvable NetGUID), rather than at the moment it's equipped: sending it
            // earlier left the client logging "Unable to resolve RPC parameter ... Parameter Weap"
            // and dropping the call outright, since the weapon had no NetGUID yet. Must go out on
            // the PAWN's own channel (ClientInternalEquipWeapon is a FortPawnOwnFields entry, so its
            // field index only resolves against the pawn's ClassNetCache), found the same way
            // OpenChannelsForNewlyRelevantActors always does.
            if (actor is AFortWeapon { bNeedsClientInternalEquipWeaponRpc: true } weapon
                && weapon.Owner is APawn pawn) {
                weapon.bNeedsClientInternalEquipWeaponRpc = false;

                if (connection.FindActorChannel(pawn) is { } pawnChannel) {
                    pawnChannel.SendObjectRpc("ClientInternalEquipWeapon", weapon);
                    Console.WriteLine($"UNetDriver.OpenChannelsForNewlyRelevantActors: sent ClientInternalEquipWeapon({weapon.GetFName()})");
                } else {
                    Console.WriteLine("UNetDriver.OpenChannelsForNewlyRelevantActors: pawn has no channel yet, ClientInternalEquipWeapon not sent");
                }
            }
        }

        return opened;
    }

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