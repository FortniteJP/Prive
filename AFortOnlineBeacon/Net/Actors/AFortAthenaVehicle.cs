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
    public AFortAthenaVehicle() =>
        NetCullDistanceSquared =
            float.TryParse(Environment.GetEnvironmentVariable("VEHICLE_CULL_DISTANCE"), out var units) && units > 0
                ? units * units
                : 40000f * 40000f;

    /// <summary>
    ///     The Blueprint class path this instance was spawned as - for logging only.
    ///
    ///     Read from the UClass rather than stored: it was an `init` property that
    ///     FortVehicleSpawns.SpawnClass never set (SpawnActor constructs the object, so an object
    ///     initialiser was never in the picture), so every log line that used it printed an empty
    ///     "()". Deriving it from the class the actor was actually spawned as cannot go stale.
    /// </summary>
    public string VehicleClassPath => GetClass()?.NativePackagePath ?? string.Empty;
}
