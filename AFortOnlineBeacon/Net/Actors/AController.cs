namespace AFortOnlineBeacon.Net.Actors;

public class AController : AActor {
    public APawn? Pawn { get; private set; }
    public APlayerState? PlayerState { get; set; }

    /// <summary>
    ///     Port of AController::Possess plus the part of APawn::PossessedBy that matters on the wire.
    ///     Real UE's PossessedBy does three things this used to skip entirely:
    ///
    ///         SetOwner(NewController);
    ///         Controller = NewController;
    ///         if (Controller->PlayerState != NULL) { PlayerState = Controller->PlayerState; }
    ///
    ///     All three are replicated - Owner is handle 13 on every actor, PlayerState 17 and
    ///     Controller 18 on a Pawn - and none of them were being sent, so a client had no way to see
    ///     the pawn as owned by, or belonging to, its own controller.
    /// </summary>
    public virtual void Possess(APawn inPawn) {
        Pawn = inPawn;
        inPawn.SetOwner(this);
        inPawn.SetController(this);
        if (PlayerState != null) inPawn.PlayerState = PlayerState;
    }
}