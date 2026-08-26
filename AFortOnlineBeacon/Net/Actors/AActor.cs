using AFortOnlineBeacon.Core;

namespace AFortOnlineBeacon.Net.Actors;

public class AActor : UObject {
    private bool bActorInitialized;
    private bool bActorIsBeingDestroyed;

    public ENetRole Role { get; private set; } = ENetRole.ROLE_Authority;
    public ENetRole RemoteRole { get; private set; } = ENetRole.ROLE_None;
    public bool bReplicates { get; private set; }

    /// <summary>
    ///     Standing in for the RootComponent-based transform real UE actors have - this project has
    ///     no component system yet (see SerializeNewActor's own comment), so location/rotation just
    ///     live directly on the actor.
    /// </summary>
    public FVector Location { get; private set; } = new();
    public FRotator Rotation { get; private set; } = new();

    public FVector GetActorLocation() => Location;
    public void SetActorLocation(FVector newLocation) => Location = newLocation;
    public FRotator GetActorRotation() => Rotation;
    public void SetActorRotation(FRotator newRotation) => Rotation = newRotation;
    
    // TODO: UPROPERTY(BlueprintReadWrite, ReplicatedUsing=OnRep_Instigator, meta=(ExposeOnSpawn=true, AllowPrivateAccess=true), Category=Actor)
    /// <summary>
    ///     Pawn responsible for damage and other gameplay events caused by this actor.
    /// </summary>
    private APawn? _Instigator;
    
    // TODO: UPROPERTY(EditAnywhere, BlueprintReadWrite, Category=Actor)
    /// <summary>
    ///     Controls how to handle spawning this actor in a situation where it's colliding with something else. "Default" means AlwaysSpawn here.
    /// </summary>
    public ESpawnActorCollisionHandlingMethod SpawnCollisionHandlingMethod { get; set; }
    
    /// <summary>
    ///     Sets the value of Role without causing other side effects to this instance.
    /// </summary>
    public void SetRole(ENetRole inRole) => Role = inRole;

    /// <summary>Actors are always network-supported, even though they're never name-stable - the server assigns them a dynamic NetGUID and tells the client to spawn one.</summary>
    public override bool IsSupportedForNetworking() => true;

    /// <summary>
    ///     AActor::bAlwaysRelevant (Actor.h:158) - "always relevant for network (overrides
    ///     bOnlyRelevantToOwner)". False on AActor; AGameStateBase and APlayerState set it true.
    /// </summary>
    public bool bAlwaysRelevant { get; protected init; }

    /// <summary>
    ///     AActor::bOnlyRelevantToOwner (Actor.h:154). False on AActor; AController sets it true
    ///     (Controller.cpp:42), which is what keeps one player's PlayerController off every other
    ///     player's connection.
    /// </summary>
    public bool bOnlyRelevantToOwner { get; protected init; }

    /// <summary>AActor::IsOwnedBy - walks the whole owner chain, not just the immediate owner.</summary>
    public bool IsOwnedBy(AActor? testOwner) {
        for (var actor = this; actor != null; actor = actor.Owner) {
            if (actor == testOwner) return true;
        }

        return false;
    }

    /// <summary>
    ///     Reduced AActor::IsNetRelevantFor (ActorReplication.cpp:291). The distance-culling tail of
    ///     the real function is deliberately absent: nothing here simulates or tracks positions well
    ///     enough to cull on them, and culling an actor the client should have is far worse than
    ///     replicating one it does not strictly need. So this answers only the ownership questions,
    ///     which are the ones that would otherwise leak one player's private actors to another.
    /// </summary>
    public virtual bool IsNetRelevantFor(AActor? realViewer) {
        if (bAlwaysRelevant || IsOwnedBy(realViewer) || this == realViewer) return true;

        return !bOnlyRelevantToOwner;
    }

    /// <summary>
    ///     AActor::NetUpdateFrequency (Actor.cpp:106) - how many times a second this actor is
    ///     considered for replication. UE 4.23 leaves every class in the login path on this default;
    ///     none of PlayerState/GameState/Pawn/PlayerController overrides it. It is a ceiling, not a
    ///     rate: the server tick (60Hz here) clamps it, and a pass that finds no changed property
    ///     sends nothing at all, so the frequency only bounds how quickly a change can go out.
    /// </summary>
    public float NetUpdateFrequency { get; set; } = 100.0f;

    /// <summary>
    ///     Set whether this actor replicates to network clients. When this actor is spawned on the server it will be sent to clients as well.
    ///     Properties flagged for replication will update on clients if they change on the server.
    ///     Internally changes the RemoteRole property and handles the cases where the actor needs to be added to the network actor list.
    /// </summary>
    public void SetReplicates(bool bInReplicates) {
        if (bReplicates == bInReplicates) return;

        bReplicates = bInReplicates;
        RemoteRole = bInReplicates ? ENetRole.ROLE_SimulatedProxy : ENetRole.ROLE_None;
    }

    /// <summary>
    ///     Sets whether or not this Actor is an autonomous proxy, which is an actor on a network client that is controlled by a user on that client.
    /// </summary>
    public void SetAutonomousProxy(bool bInAutonomousProxy, bool bAllowForcePropertyCompare = true) {
        if (!bReplicates) return;

        RemoteRole = bInAutonomousProxy ? ENetRole.ROLE_AutonomousProxy : ENetRole.ROLE_SimulatedProxy;
    }

    public UWorld? GetWorld() {
        if (!HasAnyFlags(EObjectFlags.RF_ClassDefaultObject)) {
            var outer = GetOuter();
            if (outer == null) return null;

            if (!outer.HasAnyFlags(EObjectFlags.RF_BeginDestroyed) && !outer.IsUnreachable()) {
                var level = GetLevel();
                if (level != null) return level.OwningWorld;
            }
        }

        return null;
    }

    public ULevel? GetLevel() => GetTypedOuter<ULevel>();

    public APawn? GetInstigator() => _Instigator;

    public bool IsActorInitialized() => bActorInitialized;

    public bool IsPendingKillPending() => bActorIsBeingDestroyed || IsPendingKill();

    /// <summary>
    ///     AActor::Destroy, reduced to the part that matters on the wire. Nothing here tears down
    ///     components or unregisters from a level - there are none - but flagging the actor is what
    ///     makes UNetDriver.ServerReplicateActors close its channels, which is what actually removes
    ///     it from every client: UActorChannel::CleanUp on the receiving end calls
    ///     DestroyActorAndComponents() for a channel closed with EChannelCloseReason::Destroyed.
    /// </summary>
    public void Destroy() {
        if (bActorIsBeingDestroyed) return;

        bActorIsBeingDestroyed = true;
        GetWorld()?.NetDriver?.RemoveNetworkActor(this);
    }

    /// <summary>
    ///     AActor::Owner - wire handle 13, live-probe-confirmed. Replicated as a plain ObjectRef, so
    ///     the client can rebuild the same ownership link the server has. It matters beyond
    ///     bookkeeping: real UE derives an actor's net relevancy and its owning connection from this
    ///     chain, and game code routinely reaches for GetOwner() to find "my" actor - e.g. the
    ///     inventory actor behind AFortPlayerController::WorldInventory is owned by that
    ///     PlayerController on a real server.
    /// </summary>
    public AActor? Owner { get; private set; }

    public void SetOwner(AActor? newOwner) => Owner = newOwner;

    /// <summary>
    ///     Called right after PackageMap->SerializeNewActor writes this actor's spawn header, letting a
    ///     subclass append its own extra bytes to that same bunch (e.g. APlayerController writes
    ///     NetPlayerIndex here so the client can match it to a local viewport). No-op by default.
    /// </summary>
    public virtual void OnSerializeNewActor(FOutBunch bunch) {}

    public void PostSpawnInitialize(FTransform userSpawnTransform, AActor? inOwner, AActor? inInstigator, bool bRemoteOwned, bool bNoFail, bool bDeferConstruction) {
        // General flow here is like so
        // - Actor sets up the basics.
        // - Actor gets PreInitializeComponents()
        // - Actor constructs itself, after which its components should be fully assembled
        // - Actor components get OnComponentCreated
        // - Actor components get InitializeComponent
        // - Actor gets PostInitializeComponents() once everything is set up
        //
        // This should be the same sequence for deferred or nondeferred spawning.
    }
}