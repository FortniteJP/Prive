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