namespace AFortOnlineBeacon.Net.Actors;

public class APawn : AActor {
    public AController? Controller { get; private set; }

    public void SetController(AController? controller) => Controller = controller;
}