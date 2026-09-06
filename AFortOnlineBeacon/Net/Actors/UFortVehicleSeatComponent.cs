namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     UFortVehicleSeatComponent - the vehicle's seat array, and the one thing that tells a client
///     it is genuinely SEATED rather than merely standing on a moving mesh.
///
///     WHY IT HAS TO EXIST. Boarding already worked without it: a movement base plus VehicleStateRep
///     puts the driver in the seat and lets them drive. Getting OUT did not, and neither did
///     switching seats - the client never sent `ServerAttemptExitVehicle` at all, so the server had
///     nothing to answer. The exit and seat-change paths on the client run off this component's
///     `PlayerSlots`, which is the only replicated property it has (`Net | RepNotify`, handle 3),
///     and until the server writes into it every slot's `Player` is null on the client: nobody is in
///     any seat, so there is nothing to get out of.
///
///     WHAT IS ACTUALLY SENT is one member of one element - see
///     <see cref="Replication.ERepPropertyKind.StructArray" />. The array's other 30 members per
///     seat (sockets, camera offsets, an FText, a nested FName array) are the Blueprint's, the
///     client already holds them, and `PrepReceivedArray` leaves them alone as long as the count on
///     the wire matches the count it already has. That is why <see cref="PlayerSlots" /> is baked
///     from the Blueprint by Tools/VehicleSeats and not invented here: the COUNT has to be right or
///     the client resizes and loses its own configuration.
///
///     Name-stable through RF_DefaultSubObject, exactly like UFortVehicleSkelMeshComponent, so the
///     client resolves it as `&lt;vehicle's GUID&gt;.VehicleSeatComponent` with no path export. The
///     name is not a guess: every vehicle spawn in the PR3.0 capture exports it under precisely
///     that leaf name.
/// </summary>
public class UFortVehicleSeatComponent : Core.Objects.UObject {
    /// <summary>
    ///     The name the Blueprint gives it, and therefore the only name this can have.
    /// </summary>
    public const string SubObjectName = "VehicleSeatComponent";

    /// <summary>
    ///     UActorComponent::IsSupportedForNetworking is `GetIsReplicated() || IsNameStableForNetworking()`,
    ///     and this component IS replicated - so it says true here even though its full path is not
    ///     stable.
    ///
    ///     WITHOUT THIS THE COMPONENT CANNOT BE REFERENCED AT ALL, and it fails silently on the
    ///     server. `IsFullNameStableForNetworking` walks the OUTER chain, and the outer is a
    ///     runtime-spawned vehicle, so the base implementation returns false; FNetGUIDCache then
    ///     refuses to assign a NetGUID and the reference goes out as the invalid guid 0. The server
    ///     logs a perfectly happy send. The client reports:
    ///
    ///         LogNet: Warning: UActorChannel::ProcessBunch: ReadContentBlockPayload failed to
    ///                 find/create object. RepObj: NULL, Channel: 18
    ///
    ///     Name-stability and networkability are separate questions, and this is the case that
    ///     separates them: the NAME is stable (RF_DefaultSubObject is what lets the client resolve
    ///     `&lt;vehicle guid&gt;.VehicleSeatComponent` by path), the full PATH is not.
    /// </summary>
    public override bool IsSupportedForNetworking() => true;

    /// <summary>
    ///     The seats, baked from the vehicle's Blueprint. Its LENGTH is load-bearing on the wire
    ///     (see the class remarks); its contents are the server's own copy, of which only
    ///     <see cref="FVehicleSeat.Player" /> is ever replicated.
    /// </summary>
    public FVehicleSeat[] PlayerSlots { get; set; } = System.Array.Empty<FVehicleSeat>();

    /// <summary>The seat this pawn is sitting in, or -1.</summary>
    public int IndexOf(APawn? pawn) {
        if (pawn == null) return -1;

        for (var index = 0; index < PlayerSlots.Length; index++) {
            if (PlayerSlots[index].Player == pawn) return index;
        }

        return -1;
    }

    /// <summary>
    ///     Seats <paramref name="pawn" /> at <paramref name="seatIndex" />, clearing wherever they
    ///     were before. Null clears them out of the vehicle entirely.
    ///
    ///     The clear-first pass is what makes a seat CHANGE work: without it a pawn would occupy
    ///     both seats at once, which on the client is not two seats taken but one array whose
    ///     entries contradict each other.
    /// </summary>
    /// <returns>Whether anything changed - the caller only needs to replicate if so.</returns>
    public bool Seat(APawn? pawn, int seatIndex, float entryTime) {
        var changed = false;

        for (var index = 0; index < PlayerSlots.Length; index++) {
            var occupant = index == seatIndex ? pawn : null;

            // Only the pawn being moved: another seat's occupant is not this call's business.
            if (PlayerSlots[index].Player != pawn && occupant == null) continue;
            if (PlayerSlots[index].Player == occupant) continue;

            PlayerSlots[index].Player = occupant;
            PlayerSlots[index].PlayerEntryTime = occupant == null ? 0f : entryTime;
            changed = true;
        }

        return changed;
    }
}
