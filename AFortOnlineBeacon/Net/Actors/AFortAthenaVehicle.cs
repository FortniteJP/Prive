using AFortOnlineBeacon.Runtime;
using AFortOnlineBeacon.Core.Objects;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     AFortAthenaVehicle - a drivable vehicle sitting where the map placed its spawner.
///
///     WHAT THIS IS AND IS NOT. It is a PARKED vehicle: the client sees it, walks up to it, and it
///     looks and sits exactly like the real one. It cannot be driven yet - entering a vehicle needs
///     ServerAttemptInteract to resolve a seat, ServerAttemptExitVehicle to give it back, and the
///     driver-input replication that AFortAthenaVehicle's handles 22-38 exist for, none of which
///     this server has. That gap is deliberate and is why this is worth doing on its own: 325
///     vehicles are a large and very visible part of the map that was simply absent, and making them
///     present is separable from making them drive.
///
///     NOT AN APawn IN THIS PROJECT, although it is one in Fortnite
///     (AFortAthenaVehicle : AFortPhysicsPawn : APawn). Deriving it from APawn here would hand it
///     NativeRepLayouts.Pawn, which models AFortPlayerPawnAthena - a CHARACTER, whose handles diverge
///     from a vehicle's at 16. Sending a character's handle 29 to a vehicle is the exact shape of
///     failure that killed connections over the building tool's handle 36: the client's
///     HandleToCmdIndex lookup fails, ReceiveProperties_r reports BunchIsError, and the connection
///     dies a few ticks later with nothing logged server-side. Deriving from AActor keeps it on
///     NativeRepLayouts.Actor (the `_ =>` arm of Get), which is the layout of the only handles this
///     server actually sends.
///
///     WHAT REACHES THE CLIENT: Role, RemoteRole, and the spawn header's Location and Rotation. That
///     is all a stationary vehicle needs - it has no driver, so bHasDriver (22) is already false in
///     the CDO, and the client runs its own physics on a simulated proxy, which is what settles it
///     onto the ground.
///
///     Every vehicle class is a Blueprint, so they all rely on the MustBeMappedGuids machinery to
///     survive the client's async load - see GUClassArray's TODM_BR note. One C# type covers all
///     seven, with GUClassArray.StaticClassForPath choosing what the client spawns, the same split
///     the weapons use.
/// </summary>
public class AFortAthenaVehicle : AActor {
    /// <summary>
    ///     Same reasoning as ABuildingActor's, and the same admission that 400 m is a chosen margin
    ///     and not a read value: a vehicle is large and visible from a long way off, and there are
    ///     tens of them rather than thousands, so a generous radius costs almost nothing.
    ///     VEHICLE_CULL_DISTANCE overrides it in units; NET_CULL=0 disables culling entirely.
    /// </summary>
    /// <summary>VEHICLE_CULL_DISTANCE, from this world's options - see AActor.NetCullDistanceKnob.</summary>
    protected override string? NetCullDistanceKnob => "VEHICLE_CULL_DISTANCE";

    public AFortAthenaVehicle() {
        // VEHICLE_CULL_DISTANCE overrides this per world - see NetCullDistanceKnob.
        NetCullDistanceSquared = 40000f * 40000f;

        // DORMANT AS SOON AS IT HAS BEEN SENT. A parked vehicle is the textbook case: up to 325 of
        // them map-wide, nothing about one ever changes after the spawn bunch, and none is ever
        // destroyed - so every tick spent diffing its channel is wasted, for the whole match. This is
        // only the REQUEST; UNetDriver decides when, and its gate is "the open was acked and nothing
        // changed this tick", which is exactly the right moment and needs no timing here.
        //
        // Safe precisely BECAUSE a vehicle is never destroyed - see AActor.Destroy for what a
        // destroyed dormant actor would do to a client and what is missing to support it. If vehicles
        // ever become destructible, this line has to go in the same change.
        SetNetDormancy(ENetDormancy.DormantAll);
    }

    /// <summary>
    ///     The Blueprint class path this instance was spawned as - for logging only.
    ///
    ///     Read from the UClass rather than stored: it was an `init` property that
    ///     FortVehicleSpawns.SpawnClass never set (SpawnActor constructs the object, so an object
    ///     initialiser was never in the picture), so every log line that used it printed an empty
    ///     "()". Deriving it from the class the actor was actually spawned as cannot go stale.
    /// </summary>
    public string VehicleClassPath => GetClass()?.NativePackagePath ?? string.Empty;

    /// <summary>
    ///     The mesh the driver's movement base points at - see UFortVehicleSkelMeshComponent. Built on
    ///     demand rather than in the constructor because UObjectGlobals.NewObject needs the outer to
    ///     exist first, the same order APlayerController.CreateInteractionComponent works in.
    /// </summary>
    public UFortVehicleSkelMeshComponent? MeshComponent { get; private set; }

    /// <summary>Who is driving, or null. Server-side; the client learns it from the movement base.</summary>
    public APawn? Driver { get; set; }

    /// <summary>
    ///     AFortAthenaVehicle::bHasDriver - handle 22, and true exactly when Driver is set.
    ///
    ///     Derived rather than stored so the two can never disagree: every path that seats or unseats
    ///     someone goes through Driver, and a bool that has to be maintained alongside it is a bool
    ///     that will eventually be left behind.
    /// </summary>
    public bool bHasDriver => Driver != null;

    /// <summary>
    ///     The seat array the client reads its own seating from - see UFortVehicleSeatComponent.
    ///     Built on demand for the same reason the mesh component is.
    /// </summary>
    public UFortVehicleSeatComponent? SeatComponent { get; private set; }

    /// <summary>
    ///     Whether this server knows this vehicle's seats. FALSE for a Blueprint Tools/VehicleSeats
    ///     never baked, and that is a refusal rather than an approximation: an array sent with the
    ///     wrong element count makes the client RESIZE its own, throwing away the sockets and camera
    ///     offsets its Blueprint configured. Better no seat array than a shorter one.
    /// </summary>
    public bool HasSeatData => FortVehicleSeats.For(GetClass()?.GetFName().ToString()).Length > 0;

    public UFortVehicleSeatComponent? GetOrCreateSeatComponent() {
        if (SeatComponent != null) return SeatComponent;

        var seats = FortVehicleSeats.For(GetClass()?.GetFName().ToString());
        if (seats.Length == 0) return null;

        SeatComponent = UObjectGlobals.NewObject<UFortVehicleSeatComponent>(
            this,
            GUClassArray.StaticClass<UFortVehicleSeatComponent>(),
            new FName(UFortVehicleSeatComponent.SubObjectName),
            EObjectFlags.RF_Transient | EObjectFlags.RF_DefaultSubObject);

        if (SeatComponent != null) SeatComponent.PlayerSlots = seats;

        return SeatComponent;
    }

    /// <summary>
    ///     Puts <paramref name="pawn" /> in seat <paramref name="seatIndex" /> (or, with -1, out of
    ///     the vehicle) and reports whether that changed anything.
    ///
    ///     Deliberately does NOT touch <see cref="Driver" />, the movement base or VehicleStateRep:
    ///     those three are what make someone ride, and this is what makes the client agree that they
    ///     are riding. Keeping them separate is why the seat array could be added without disturbing
    ///     a boarding path that already worked.
    /// </summary>
    public bool SeatPawn(APawn? pawn, int seatIndex, float entryTime) =>
        GetOrCreateSeatComponent() is { } seats && seats.Seat(pawn, seatIndex, entryTime);

    public UFortVehicleSkelMeshComponent? GetOrCreateMeshComponent() {
        if (MeshComponent != null) return MeshComponent;

        MeshComponent = UObjectGlobals.NewObject<UFortVehicleSkelMeshComponent>(
            this,
            GUClassArray.StaticClass<UFortVehicleSkelMeshComponent>(),
            new FName(UFortVehicleSkelMeshComponent.SubObjectName),
            EObjectFlags.RF_Transient | EObjectFlags.RF_DefaultSubObject);

        return MeshComponent;
    }
}
