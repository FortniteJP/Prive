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

    /// <summary>
    ///     RelativeScale3D, in the same stand-in-for-the-RootComponent sense as Location/Rotation.
    ///     Defaults to (1,1,1) because that is what a client that receives no scale in the spawn bunch
    ///     falls back to (PackageMapClient.cpp's SerializeCompressedInitial), so anything else here has
    ///     to actually be sent.
    ///
    ///     Not cosmetic: a NEGATIVE component is how Fortnite mirrors a building piece. See
    ///     ABuildingActor.SetMirrored.
    /// </summary>
    public FVector Scale3D { get; private set; } = new() { X = 1f, Y = 1f, Z = 1f };

    public FVector GetActorLocation() => Location;

    public void SetActorLocation(FVector newLocation) {
        Location = newLocation;
        bHasKnownLocation = true;
    }

    /// <summary>
    ///     Whether anything has ever told this actor WHERE IT IS. False means Location is still its
    ///     zero-initialised default, which is the origin only by accident.
    ///
    ///     THIS FLAG EXISTS BECAUSE ITS ABSENCE BROKE DESTRUCTIBLE SCENERY (2026-09-04, live). Level
    ///     actors - world props, chests, ammo boxes - are registered by PATH the first time a player
    ///     hits one (NativeRpcHandlers.DamageLevelActor -&gt;
    ///     UAssetRegistry.GetOrCreateSubObject&lt;ABuildingActor&gt;), and nothing ever gives them a
    ///     Location: the server knows the actor exists and what class it is, never where it stands.
    ///     Distance culling then measured every one of them from (0,0,0), found them ~170000 units
    ///     from a player on the warmup island, and refused to open their channels - so nothing broke
    ///     any more, anywhere, and NET_CULL=0 "fixed" it.
    ///
    ///     The lesson is not "special-case scenery". It is that (0,0,0) here means UNKNOWN, not the
    ///     origin, and an unknown position cannot be culled against - the same rule that already
    ///     makes a null viewer location mean "relevant".
    /// </summary>
    public bool bHasKnownLocation { get; private set; }

    /// <summary>
    ///     AActor::bReplicateMovement - wire handle 2. The client's own gate: OnRep_AttachmentReplication
    ///     and the movement path both check it before doing anything with ReplicatedMovement.
    /// </summary>
    public bool bReplicateMovement { get; set; }

    /// <summary>AActor::ReplicatedMovement - wire handle 6. See FRepMovement.</summary>
    public FRepMovement ReplicatedMovement { get; } = new();

    private FVector? _lastGatheredLocation;
    private float _lastGatheredTime;

    /// <summary>
    ///     AActor::GatherCurrentMovement - copies the actor's live transform into
    ///     <see cref="ReplicatedMovement"/> so the next comparison pass has something to notice.
    ///
    ///     VELOCITY IS DERIVED, not measured, and that is a real difference from UE: a real server
    ///     runs the character movement component and has a velocity to copy. This one does not - the
    ///     owning client reports positions through ServerMoveNoBase and nothing here integrates
    ///     anything - so velocity comes from the distance between two gathers over the time between
    ///     them. It is worth computing rather than sending zero: the receiving client feeds it to
    ///     PostNetReceiveVelocity, and the animation blueprint picks the run/idle state off it, so a
    ///     zero would leave remote players sliding around in an idle pose.
    /// </summary>
    public void GatherCurrentMovement(float now) {
        var location = GetActorLocation();
        var elapsed = now - _lastGatheredTime;

        if (_lastGatheredLocation is { } previous && elapsed > 0.0001f) {
            ReplicatedMovement.LinearVelocity = new FVector {
                X = (location.X - previous.X) / elapsed,
                Y = (location.Y - previous.Y) / elapsed,
                Z = (location.Z - previous.Z) / elapsed
            };
        }

        ReplicatedMovement.Location = new FVector { X = location.X, Y = location.Y, Z = location.Z };
        ReplicatedMovement.Rotation = GetActorRotation();

        _lastGatheredLocation = ReplicatedMovement.Location;
        _lastGatheredTime = now;
    }
    public FRotator GetActorRotation() => Rotation;
    public void SetActorRotation(FRotator newRotation) => Rotation = newRotation;
    public FVector GetActorScale3D() => Scale3D;
    public void SetActorScale3D(FVector newScale) => Scale3D = newScale;
    
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
    ///     AActor::NetCullDistanceSquared (Actor.cpp:121) - beyond this distance from the viewer an
    ///     actor is not relevant. The engine default is 225000000, i.e. 15000 units / 150 m, and UE
    ///     4.23 leaves it there for everything in this project's login path.
    /// </summary>
    public float NetCullDistanceSquared { get; protected init; } = 225000000f;

    /// <summary>
    ///     AActor::IsNetRelevantFor (ActorReplication.cpp:291), now WITH its distance tail.
    ///
    ///     The tail used to be deliberately absent, on the grounds that nothing here tracked
    ///     positions well enough to cull on them. That reason expired: pawns move, and buildings,
    ///     vehicles, llamas and 2895 floor-loot spawners all have real world locations now. What
    ///     replaced it is the real rule -
    ///
    ///         FVector::DistSquared(SrcLocation, GetActorLocation()) &lt; NetCullDistanceSquared
    ///
    ///     - gated on <see cref="UNetDriver"/> having a viewer location to measure from at all.
    ///
    ///     TWO DELIBERATE DEPARTURES FROM THE REAL FUNCTION, both in the safe direction:
    ///
    ///       * NO LOCATION MEANS RELEVANT. Real UE always has a view target; this server can be
    ///         between pawns (the warmup pawn is destroyed on boarding the bus and a new one is
    ///         spawned on jump), and culling the whole world during that window would be a far worse
    ///         failure than replicating too much. srcLocation null therefore short-circuits to true.
    ///       * CULLING ONLY EVER DECIDES WHETHER TO **OPEN** A CHANNEL. Nothing closes a channel
    ///         because its actor drifted out of range - so a client accumulates the world as it
    ///         moves through it and never loses anything it has already been told about. Real UE
    ///         closes an irrelevant channel after AActor::NetUpdateFrequency-driven RelevantTimeout,
    ///         and closing is destructive here (a close IS the client-side destroy - see
    ///         UNetDriver.TickFlush), so that half is left out until there is a reason for it.
    ///
    ///     The engine's own escape hatch is kept too: AGameNetworkManager::bUseDistanceBasedRelevancy
    ///     (true by default) becomes NET_CULL=0, which restores the pre-culling behaviour exactly.
    /// </summary>
    public virtual bool IsNetRelevantFor(AActor? realViewer, FVector? srcLocation = null) {
        if (bAlwaysRelevant || IsOwnedBy(realViewer) || this == realViewer) return true;
        if (bOnlyRelevantToOwner) return false;
        if (srcLocation == null || !bHasKnownLocation || !bUseDistanceBasedRelevancy) return true;

        return FVector.DistSquared(srcLocation, GetActorLocation()) < NetCullDistanceSquared;
    }

    /// <summary>AGameNetworkManager::bUseDistanceBasedRelevancy (GameNetworkManager.cpp:51) - true in real UE.</summary>
    private static readonly bool bUseDistanceBasedRelevancy =
        Environment.GetEnvironmentVariable("NET_CULL") is not "0";

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

    /// <summary>
    ///     AActor::Instigator - wire handle 15, and it is not decoration. A real client refuses to
    ///     run a weapon without it:
    ///
    ///         LogFort: Error: AFortWeaponRanged::OwnerIsMoving() B_Assault_Auto_Athena_C_...:
    ///                  The instigator pawn is null when it shouldn't be!
    ///
    ///     repeated every frame the weapon existed. Owner (handle 13) is not a substitute - Owner is
    ///     "who replicates this", Instigator is "whose pawn is responsible for what it does", and
    ///     weapon code reads the second.
    /// </summary>
    public void SetInstigator(APawn? instigator) => _Instigator = instigator;

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
        Destroyed();
    }

    /// <summary>
    ///     AActor::Destroyed - the subclass's chance to drop out of whatever server-side registry it
    ///     put itself in, once per actor no matter which path destroyed it (Destroy's own guard above
    ///     is what makes that "once"). No-op by default.
    /// </summary>
    protected virtual void Destroyed() {}

    /// <summary>
    ///     AActor::Owner - wire handle 13, live-probe-confirmed. Replicated as a plain ObjectRef, so
    ///     the client can rebuild the same ownership link the server has. It matters beyond
    ///     bookkeeping: real UE derives an actor's net relevancy and its owning connection from this
    ///     chain, and game code routinely reaches for GetOwner() to find "my" actor - e.g. the
    ///     inventory actor behind AFortPlayerController::WorldInventory is owned by that
    ///     PlayerController on a real server.
    /// </summary>
    /// <summary>
    ///     AActor::AttachmentReplication (FRepAttachment) - what makes a pawn RIDE something.
    ///
    ///     This is how a player stays on the battle bus. Setting AFortPlayerStateAthena::bInAircraft
    ///     gets the client as far as running EnterAircraft and loading the bus skin, and
    ///     ClientSetViewTarget moves the camera, but neither of them moves the PAWN: the client has
    ///     no reason to take the character off the spawn island until it is told what it is attached
    ///     to. Six replicated members, in the order EngineTypes.h:3197 declares them, which is the
    ///     order FRepLayout flattens them into wire handles 7-12.
    ///
    ///     Engine note worth honouring: "movement replication will not happen while AttachParent is
    ///     non-nullptr". This server does not send ReplicatedMovement at all, so nothing to suppress
    ///     - but a future one must not fight the attachment.
    /// </summary>
    public AActor? AttachParent {
        get => _attachParent;
        set {
            _attachParent = value;

            if (value == null || bAttachmentEverSet) return;

            bAttachmentEverSet = true;
            ReplicatedPropertySetRevision++;
        }
    }

    private AActor? _attachParent;

    /// <summary>
    ///     Bumped whenever something changes WHICH PROPERTIES this actor replicates - as opposed to
    ///     their values. UActorChannel caches that set per channel and rebuilds when this moves.
    ///
    ///     It exists because the first version of vehicle riding did not work and left no trace: the
    ///     pawn's property set gained the six AttachmentReplication handles only once
    ///     <see cref="bAttachmentEverSet"/> turned true, which happens long AFTER the channel opened
    ///     and cached the set. The server logged a successful attach, the diff never looked at those
    ///     handles, and nothing went out. A cached set needs an invalidation the moment it can depend
    ///     on mutable state.
    /// </summary>
    public int ReplicatedPropertySetRevision { get; private set; }

    /// <summary>
    ///     Whether this actor has EVER been attached to anything, and therefore whether the six
    ///     AttachmentReplication handles belong in its replicated set. Sticky on purpose.
    ///
    ///     Sticky because DETACHING has to be sent too. If the handles were included only while
    ///     AttachParent is non-null, the moment a rider stepped off the vehicle they would vanish
    ///     from the diff set and the client would never hear about it - it would keep the pawn glued
    ///     to a vehicle the server thinks it left.
    ///
    ///     And it starts FALSE rather than being on for everyone, because sending these
    ///     unconditionally was measurably harmful: with AttachParent null,
    ///     AActor::OnRep_AttachmentReplication's else branch runs DetachFromActor and then
    ///     OnRep_ReplicatedMovement, and this server sends no ReplicatedMovement, so the client
    ///     applied an all-zero transform. The live symptom was a 90-degree camera roll at spawn.
    ///     See UActorChannel's pawn property set.
    /// </summary>
    public bool bAttachmentEverSet { get; private set; }

    /// <summary>FRepAttachment::LocationOffset - where on the parent this actor sits.</summary>
    public FVector AttachLocationOffset { get; set; } = new();

    /// <summary>FRepAttachment::RelativeScale3D. ForceInit in the engine's own constructor, so zero.</summary>
    public FVector AttachRelativeScale3D { get; set; } = new();

    /// <summary>FRepAttachment::RotationOffset.</summary>
    public FRotator AttachRotationOffset { get; set; } = new();

    /// <summary>FRepAttachment::AttachSocket - NAME_None unless attaching to a named socket.</summary>
    public FName AttachSocket { get; set; } = new();

    public AActor? Owner { get; private set; }

    public void SetOwner(AActor? newOwner) => Owner = newOwner;

    /// <summary>
    ///     Called right after PackageMap->SerializeNewActor writes this actor's spawn header, letting a
    ///     subclass append its own extra bytes to that same bunch (e.g. APlayerController writes
    ///     NetPlayerIndex here so the client can match it to a local viewport). No-op by default.
    /// </summary>
    public virtual void OnSerializeNewActor(FOutBunch bunch) {}

    public void PostSpawnInitialize(FTransform userSpawnTransform, AActor? inOwner, AActor? inInstigator, bool bRemoteOwned, bool bNoFail, bool bDeferConstruction) {
        // Both spawn parameters were being accepted and dropped on the floor - _Instigator was never
        // assigned anywhere (the compiler had been saying so: CS0649 "is never assigned to").
        if (inOwner != null) SetOwner(inOwner);
        if (inInstigator is APawn instigatorPawn) SetInstigator(instigatorPawn);

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