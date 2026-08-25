namespace AFortOnlineBeacon.Net.Actors;

public class APawn : AActor {
    /// <summary>APawn::Controller - wire handle 18, an ObjectRef.</summary>
    public AController? Controller { get; private set; }

    /// <summary>
    ///     APawn::PlayerState - wire handle 17, an ObjectRef. Real UE's APawn::PossessedBy copies it
    ///     down from the possessing controller; see AController.Possess.
    /// </summary>
    public APlayerState? PlayerState { get; set; }

    public void SetController(AController? controller) => Controller = controller;
}