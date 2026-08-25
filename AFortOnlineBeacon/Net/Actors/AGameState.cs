namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Simplified port of AGameStateBase - just enough to replicate bReplicatedHasBegunPlay, the
///     property real UE's OnRep_ReplicatedHasBegunPlay (GameStateBase.cpp) uses to call
///     UWorld::NotifyBeginPlay/NotifyMatchStarted client-side, which is what a client's loading
///     screen is waiting on before it dismisses. Real UE only ever flips this true once
///     AGameModeBase::StartMatch actually runs; this project doesn't have a warmup/countdown flow
///     yet, so it's set true as soon as this actor is spawned - see AGameModeBase.InitGameState.
/// </summary>
public class AGameState : AInfo {
    public bool bReplicatedHasBegunPlay { get; set; }

    /// <summary>
    ///     AGameStateBase::MatchState (GameStateBase.cpp) - the standard UE match-state machine
    ///     (EnteringMap -> WaitingToStart -> InProgress -> ...) that OnRep_MatchState uses to drive
    ///     HandleMatchIsWaitingToStart/HandleMatchHasStarted client-side. A real PR3.0 client capture
    ///     (2026-08-24) showed this transitioning to InProgress a few seconds BEFORE the loading
    ///     screen actually dismissed and the client sent ServerLoadingScreenDropped - bHasBegunPlay
    ///     alone was not enough. Defaults to the real engine's own default value.
    /// </summary>
    public FName MatchState { get; set; } = new("EnteringMap");
}
