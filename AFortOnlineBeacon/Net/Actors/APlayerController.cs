namespace AFortOnlineBeacon.Net.Actors;

public class APlayerController : AController {
    public byte NetPlayerIndex { get; set; }
    public UPlayer? Player { get; private set; }

    public void SetPlayer(UPlayer inPlayer) => Player = inPlayer;

    /// <summary>
    ///     The real client reads this immediately after the actor's spawn header (see
    ///     APlayerController::OnActorChannelOpen) to decide whether this is its own main
    ///     PlayerController (index 0) and, if so, binds UNetConnection::PlayerController to it.
    ///     Without this, the client has no way to recognize a replicated PlayerController as its own.
    /// </summary>
    public override void OnSerializeNewActor(FOutBunch bunch) => bunch.WriteByte(NetPlayerIndex);
}