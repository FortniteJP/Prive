namespace AFortOnlineBeacon.Net.Actors;

public class APawn : AActor {
    /// <summary>
    ///     The view rotation the client last sent with a move (the packed "View" parameter of
    ///     ServerMove*). Real UE feeds this into AController::ControlRotation; here it is kept only
    ///     so the server knows which way the player is facing - a dropped item has to land in front
    ///     of them to be reachable, and the pawn's own Rotation is never updated by the move RPCs.
    /// </summary>
    public FRotator? LastClientViewRotation { get; set; }

    /// <summary>APawn::Controller - wire handle 18, an ObjectRef.</summary>
    public AController? Controller { get; private set; }

    /// <summary>
    ///     APawn::PlayerState - wire handle 17, an ObjectRef. Real UE's APawn::PossessedBy copies it
    ///     down from the possessing controller; see AController.Possess.
    /// </summary>
    public APlayerState? PlayerState { get; set; }

    public void SetController(AController? controller) => Controller = controller;

    /// <summary>
    ///     Stand-in for FNetworkPredictionData_Server_Character::PendingAdjustment.TimeStamp with
    ///     bAckGoodMove set - the newest client move timestamp we have accepted and still owe the
    ///     client an acknowledgement for. 0 means nothing pending.
    ///
    ///     This matters more than it looks: a client's FSavedMove_Character list is only freed when
    ///     the server acknowledges a timestamp, so a server that never acks makes the client pile up
    ///     moves until it hits its cap and logs
    ///     "CreateSavedMove: Hit limit of 96 saved moves (timing out or very bad ping?)", throwing
    ///     the whole list away and restarting - which is what movement looks like from a server that
    ///     receives ServerMove and says nothing back.
    /// </summary>
    public float PendingAckGoodMoveTimeStamp { get; set; }

    /// <summary>
    ///     UCharacterMovementComponent::ServerLastClientGoodMoveAckTime. Real UE only sends one ack
    ///     per replication pass and additionally throttles by NetworkMinTimeBetweenClientAckGoodMoves
    ///     (0.10s by default) - acking every single move would put an unreliable bunch on the wire at
    ///     the client's full move rate for no benefit, since one ack frees every saved move up to its
    ///     timestamp.
    /// </summary>
    public float ServerLastClientGoodMoveAckTime { get; set; } = float.NegativeInfinity;

    /// <summary>Records a client move timestamp as accepted. Newest wins - an ack is cumulative.</summary>
    public void MarkGoodMove(float timeStamp) {
        if (timeStamp > PendingAckGoodMoveTimeStamp) PendingAckGoodMoveTimeStamp = timeStamp;
    }
}
