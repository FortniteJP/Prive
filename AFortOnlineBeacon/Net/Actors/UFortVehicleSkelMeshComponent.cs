namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     A vehicle's `SkeletalMeshComponent`, as something the client can be pointed AT.
///
///     WHY IT HAS TO EXIST. Entering a vehicle is not an attachment - the PR3.0 capture shows the real
///     server setting the driver's MOVEMENT BASE to this component
///     (`LogCharacter: Setting base on Server for 'PlayerPawn_Athena_C_...' to
///     'FortVehicleSkelMeshComponent ...JackalVehicle_Athena_C_2147463352.SkeletalMeshComponent'`), and
///     `ACharacter::ReplicatedBasedMovement.MovementBase` is a `UPrimitiveComponent*`. An object
///     reference needs an object, so the server needs something in its own world to hand to the
///     NetGUID cache.
///
///     IT HOLDS NO STATE, exactly like UFortControllerComponent_Interaction, and for the same reason:
///     the vehicle's mesh belongs to the client's copy of the Blueprint, and all this side has to do is
///     be nameable. `FNetGUIDCache.RegisterNetGUID_Server` records an OuterGUID and a PathName, so a
///     sub-object of a dynamically spawned actor resolves on the client as
///     `&lt;vehicle's GUID&gt;.SkeletalMeshComponent` without anything else being sent.
/// </summary>
public class UFortVehicleSkelMeshComponent : Core.Objects.UObject {
    /// <summary>
    ///     The name the Blueprint gives it, and therefore the only name this can have - the client
    ///     resolves the reference by matching this path under the vehicle.
    /// </summary>
    public const string SubObjectName = "SkeletalMeshComponent";

    /// <summary>
    ///     Same override, same reason, and it fixes a bug that had never been noticed: this
    ///     component's outer is a runtime-spawned vehicle, so the default
    ///     `IsFullNameStableForNetworking` rule refused it a NetGUID and every
    ///     `ReplicatedBasedMovement.MovementBase` referencing it went out as the invalid guid 0.
    ///
    ///     That went unseen because the movement base is COND_SimulatedOnly - it never reaches the
    ///     driver, only the other clients watching them - and there has only ever been one player in
    ///     a vehicle at a time. The one report of it ("ReplicatedBasedMovement.MovementBase does not
    ///     seem to be there") was read at the time as the property not being sent. It was being sent,
    ///     pointing at nothing.
    /// </summary>
    public override bool IsSupportedForNetworking() => true;
}
