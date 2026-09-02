namespace AFortOnlineBeacon.Net.Actors;

public class APlayerController : AController {
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