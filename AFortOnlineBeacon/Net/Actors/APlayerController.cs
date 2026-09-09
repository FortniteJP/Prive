namespace AFortOnlineBeacon.Net.Actors;

public class APlayerController : AController {
    /// <summary>
    ///     What this player has in their locker, read from MongoDB once at login (null when there is
    ///     no profile, or LOCKER_FROM_DB=0). Kept on the controller rather than applied and forgotten
    ///     because a pawn is spawned MORE THAN ONCE per match - the warmup pawn is destroyed when the
    ///     bus phase starts and a fresh one is spawned on the jump - and the glider has to be set on
    ///     every one of them. See FortLockerProfile and AGameModeBase.SpawnAndPossessPawn.
    /// </summary>
    internal FortLockerProfile.FLockerLoadout? Locker { get; set; }

    public byte NetPlayerIndex { get; set; }
    public UPlayer? Player { get; private set; }

    /// <summary>
    ///     The controller's `InteractionComp`. Created eagerly rather than on demand because the
    ///     client can reference it before this server has any reason to think about interaction - the
    ///     reference has to resolve the first time it arrives, or the whole content block is skipped.
    ///     See UFortControllerComponent_Interaction.
    /// </summary>
    public UFortControllerComponent_Interaction? InteractionComponent { get; private set; }

    /// <summary>
    ///     Resolves a sub-object the CLIENT named by path. Real UE resolves such a reference against
    ///     the outer's own sub-objects; this is the narrow version of that - only components this
    ///     server actually has, looked up by the leaf name the client sent.
    ///
    ///     Deliberately NOT a general "create whatever the client asks for": real UE refuses that too
    ///     (DataChannel.cpp's "Client attempted to create sub-object"), and a server that invents
    ///     objects from client-supplied names is a server a client can make do anything.
    /// </summary>
    public virtual UObject? ResolveNamedSubObject(string leafName) =>
        leafName == UFortControllerComponent_Interaction.SubObjectName ? InteractionComponent : null;

    /// <summary>
    ///     Builds the sub-objects this controller owns. Separate from the constructor because
    ///     UObjectGlobals.NewObject needs the object to already exist as an outer.
    /// </summary>
    public void CreateInteractionComponent() {
        if (InteractionComponent != null) return;

        InteractionComponent = UObjectGlobals.NewObject<UFortControllerComponent_Interaction>(
            this,
            GUClassArray.StaticClass<UFortControllerComponent_Interaction>(),
            new FName(UFortControllerComponent_Interaction.SubObjectName),
            EObjectFlags.RF_Transient | EObjectFlags.RF_DefaultSubObject);

        if (InteractionComponent != null) InteractionComponent.Owner = this;
    }

    /// <summary>Last location/rotation reported by ServerSetSpectatorLocation, mirroring the real fields of the same name.</summary>
    public FVector? LastSpectatorSyncLocation { get; set; }

    /// <summary>
    ///     Where the client says its CAMERA is, from APlayerController::ServerUpdateCamera - the
    ///     single most frequent RPC this server receives and, until now, one it dropped on the floor.
    ///
    ///     Not the same thing as the pawn's location, and that is the point: real
    ///     ServerReplicateActors builds its relevancy and priority from the connection's VIEWER
    ///     position, which is the camera, not the actor. A third-person camera sits several metres
    ///     behind and above the pawn, and a spectator's is not attached to a pawn at all.
    ///
    ///     THIS IS WHAT DISTANCE CULLING MEASURES FROM (UNetDriver.OpenChannelsForNewlyRelevantActors),
    ///     and that is not an approximation of what the engine does, it IS what the engine does:
    ///     FNetViewer's ViewLocation comes from APlayerController::GetPlayerViewPoint, which returns
    ///     PlayerCameraManager->GetCameraLocation(), which is the camera cache that
    ///     ServerUpdateCamera_Implementation fills (PlayerController.cpp) - this very value.
    /// </summary>
    public FVector? LastClientCameraLocation { get; set; }

    /// <summary>
    ///     When <see cref="LastClientCameraLocation"/> was last set, in world seconds. A client that
    ///     stops reporting - or one whose camera manager never had bUseClientSideCameraUpdates on -
    ///     must not leave relevancy anchored to a stale point forever, so this is what lets the
    ///     driver fall back to the pawn.
    /// </summary>
    public float LastClientCameraTime { get; set; } = float.NegativeInfinity;

    /// <summary>
    ///     ServerUpdateCamera's second parameter, unpacked the way
    ///     APlayerController::ServerUpdateCamera_Implementation unpacks it:
    ///
    ///         Yaw   = FRotator::DecompressAxisFromShort( (CamPitchAndYaw &gt;&gt; 16) &amp; 65535 )
    ///         Pitch = FRotator::DecompressAxisFromShort(  CamPitchAndYaw        &amp; 65535 )
    ///
    ///     Note the order - YAW is the HIGH half - which is the opposite of what the name suggests.
    /// </summary>
    public FRotator? LastClientCameraRotation { get; set; }
    public FRotator? LastSpectatorSyncRotation { get; set; }

    /// <summary>
    ///     AFortPlayerController::bHasInitiallySpawned - wire handle 21, one bit. Real UE sets this
    ///     in AFortPlayerController's spawn path once the player has actually been put into the
    ///     zone; nothing on the client can turn it on by itself. Sent alongside
    ///     bHasServerFinishedLoading (handle 22) since both describe the same "this player is really
    ///     in the match now" fact and the client checks them in different places.
    /// </summary>
    public bool bHasInitiallySpawned { get; set; }

    /// <summary>AFortPlayerController's own property - see NativeRepLayouts.PlayerControllerProps for why this matters.</summary>
    public bool bHasServerFinishedLoading { get; set; }

    // ------------------------------------------------------------------------------------------
    // WHAT THE CLIENT HAS TOLD US ABOUT ITS OWN READINESS.
    //
    // The client sends three separate "I am ready" signals and this server decoded all three and
    // threw the values away, logging them only under NET_VERBOSE. That was expensive: Round 148's
    // bug was opening actor channels while the client was still resolving its own pawn, and the
    // client had been SAYING when it finished the whole time. The gate there had to be inferred
    // from ServerAcknowledgePossession instead, which is a proxy for this.
    //
    // Set only, never replicated - these are the client's statements about itself.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    ///     AFortPlayerController::ServerClientPawnLoaded(bool) - the client has finished loading and
    ///     spawning the pawn it was told to possess. The most precise "safe to talk to me about
    ///     other actors now" signal there is.
    /// </summary>
    public bool bClientPawnLoaded { get; set; }

    /// <summary>
    ///     AFortPlayerController::ServerSetClientHasFinishedLoading(bool) - the client's own copy of
    ///     the flag this server replicates back out as APlayerState.bHasFinishedLoading (handle 29).
    ///     Until now that property was set true by the server on a timer of its own reasoning; this
    ///     is the client actually saying so.
    /// </summary>
    public bool bClientHasFinishedLoading { get; set; }

    /// <summary>
    ///     AFortPlayerController::ServerLoadingScreenDropped() - the loading screen is gone and the
    ///     player can see the world. Later than the two above.
    /// </summary>
    public bool bLoadingScreenDropped { get; set; }

    /// <summary>
    ///     AFortPlayerControllerAthena::bMarkedAlive - wire handle 75.
    ///
    ///     The client's answer to "is this player alive?", and it is FALSE until a server says
    ///     otherwise. A real match sets it as part of spawning the player in.
    /// </summary>
    public bool bMarkedAlive { get; set; } = true;

    /// <summary>    ///     AFortPlayerController::OverriddenBackpackSize - wire handle 52. How many inventory slots
    ///     the client believes it has. Zero until told otherwise, which is why every pickup was
    ///     refused as "inventory full". 5 is Battle Royale's real backpack size and what
    ///     Project-Reboot-3.0 sets; raider3.5 uses 100. BACKPACK_SIZE overrides it.
    /// </summary>
    public int OverriddenBackpackSize { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("BACKPACK_SIZE"), out var size) && size > 0 ? size : 5;

    /// <summary>
    ///     AFortPlayerController::WorldInventory - see AFortInventory's doc comment for why
    ///     ClientRestart_Implementation needs this to resolve to something non-null client-side.
    /// </summary>
    /// <summary>
    ///     APlayerController::AcknowledgedPawn - set when the client sends ServerAcknowledgePossession.
    ///     While it differs from Pawn, the server keeps retrying ClientRestart (see
    ///     UActorChannel.HandlePossessionRpc); once they match, possession is complete and the
    ///     retries stop.
    /// </summary>
    public APawn? AcknowledgedPawn { get; set; }

    public AFortInventory? WorldInventory { get; set; }

    /// <summary>
    ///     AFortPlayerControllerAthena::BroadcastRemoteClientInfo - a plain ObjectRef Cmd, exactly
    ///     like WorldInventory above. See AFortBroadcastRemoteClientInfo's doc comment for why this
    ///     matters: without it, the client's ServerSetPlayerBuildableClass call (sent the instant a
    ///     building tool is equipped) silently finds nothing to call on.
    /// </summary>
    public AFortBroadcastRemoteClientInfo? BroadcastRemoteClientInfo { get; set; }

    public void SetPlayer(UPlayer inPlayer) => Player = inPlayer;

    /// <summary>
    ///     The real client reads this immediately after the actor's spawn header (see
    ///     APlayerController::OnActorChannelOpen) to decide whether this is its own main
    ///     PlayerController (index 0) and, if so, binds UNetConnection::PlayerController to it.
    ///     Without this, the client has no way to recognize a replicated PlayerController as its own.
    /// </summary>
    public override void OnSerializeNewActor(FOutBunch bunch) => bunch.WriteByte(NetPlayerIndex);
}