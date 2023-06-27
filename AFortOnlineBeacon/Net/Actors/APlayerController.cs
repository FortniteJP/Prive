namespace AFortOnlineBeacon.Net.Actors;

public class APlayerController : AController {
    public byte NetPlayerIndex { get; set; }

    public void SetPlayer(UPlayer inPlayer) => throw new NotImplementedException();
}