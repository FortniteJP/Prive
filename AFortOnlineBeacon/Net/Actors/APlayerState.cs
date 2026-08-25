namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Simplified port of APlayerState/AFortPlayerState - just enough to replicate
///     bHasStartedPlaying, the other half (alongside AFortPlayerController::
///     bHasServerFinishedLoading) of the Fortnite-specific loading-screen-dismissal gate a real
///     working server (PriveDev/Project-Reboot-3.0) identifies in a disabled workaround comment.
///     Real UE spawns one of these automatically per-connection via AController::InitPlayerState -
///     see AGameModeBase.Login, which does the same here.
/// </summary>
public class APlayerState : AInfo {
    public bool bHasStartedPlaying { get; set; }
}
