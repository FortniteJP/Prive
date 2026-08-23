namespace AFortOnlineBeacon.Net.Actors;

public class AController : AActor {
    public APawn? Pawn { get; private set; }

    /// <summary>Simplified port of AController::Possess - just wires the Controller/Pawn back-references.</summary>
    public virtual void Possess(APawn inPawn) {
        Pawn = inPawn;
        inPawn.SetController(this);
    }
}