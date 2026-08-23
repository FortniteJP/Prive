using AFortOnlineBeacon.Net.Actors;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     Ground-truth field *membership* for the real native engine classes (/Script/Engine.Actor,
///     .Controller, .PlayerController, .Pawn) that our AActor/AController/APlayerController/APawn
///     are mapped to (see GUClassArray.NativePackagePaths) - transcribed from a live dump of the
///     real, running Fortnite 10.40 client's reflection data (UClass::GetChildren() walked
///     in-process via a modified PriveDev/UEDumper, filtered to CPF_Net properties / FUNC_Net
///     functions). Notably it also caught a Fortnite-specific addition not present in vanilla
///     engine source: PlayerController has an extra "ServerExecRPC" function.
///
///     The WIRE ORDER is NOT UClass::GetChildren()'s order, though - a first attempt assumed it
///     was and produced garbage (decoded RepIndex values that pointed at Client-direction RPCs on
///     received client-to-server traffic). Reading UClass::SetUpRuntimeReplicationData (Class.cpp)
///     revealed the real rule: after NetFields is built by walking Children, it gets explicitly
///     re-sorted - "Sort(NetFields.GetData(), NetFields.Num(), FCompareUFieldNames())" - by name,
///     via FString's operator&lt; (case-INsensitive, see UnrealString.h). FClassNetCacheMgr::
///     GetClassNetCache (CoreNet.cpp) then assigns FieldNetIndex by walking that POST-sort
///     NetFields array in order. So each class's own net fields, in wire order, is simply that
///     class's own field set sorted alphabetically (ordinal, case-insensitive) - applied in
///     OwnFieldsSorted below rather than by hand-ordering the arrays.
/// </summary>
internal static class NativeClassNetCache {
    private static string[] OwnFieldsSorted(params string[] fields) {
        var sorted = (string[]) fields.Clone();
        Array.Sort(sorted, StringComparer.OrdinalIgnoreCase);
        return sorted;
    }
    private static readonly string[] ActorOwnFields = OwnFieldsSorted(
        "bHidden", "bReplicateMovement", "bTearOff", "bCanBeDamaged", "RemoteRole",
        "ReplicatedMovement", "AttachmentReplication", "Owner", "Role", "Instigator"
    );

    private static readonly string[] ControllerOwnFields = OwnFieldsSorted(
        "PlayerState", "Pawn", "ClientSetRotation", "ClientSetLocation"
    );

    private static readonly string[] PlayerControllerOwnFields = OwnFieldsSorted(
        "TargetViewRotation", "SpawnLocation",
        "ServerViewSelf", "ServerViewPrevPlayer", "ServerViewNextPlayer", "ServerVerifyViewTarget",
        "ServerUpdateMultipleLevelsVisibility", "ServerUpdateLevelVisibility", "ServerUpdateCamera",
        "ServerUnmutePlayer", "ServerToggleAILogging", "ServerShortTimeout", "ServerSetSpectatorWaiting",
        "ServerSetSpectatorLocation", "ServerRestartPlayer", "ServerPause", "ServerNotifyLoadedWorld",
        "ServerMutePlayer", "ServerExecRPC", "ServerCheckClientPossessionReliable", "ServerCheckClientPossession",
        "ServerChangeName", "ServerCamera", "ServerAcknowledgePossession", "OnServerStartedVisualLogger",
        "ClientWasKicked", "ClientVoiceHandshakeComplete", "ClientUpdateMultipleLevelsStreamingStatus",
        "ClientUpdateLevelStreamingStatus", "ClientUnmutePlayer", "ClientTravelInternal", "ClientTeamMessage",
        "ClientStopForceFeedback", "ClientStopCameraShake", "ClientStopCameraAnim", "ClientStartOnlineSession",
        "ClientSpawnCameraLensEffect", "ClientSetViewTarget", "ClientSetSpectatorWaiting", "ClientSetHUD",
        "ClientSetForceMipLevelsToBeResident", "ClientSetCinematicMode", "ClientSetCameraMode",
        "ClientSetCameraFade", "ClientSetBlockOnAsyncLoading", "ClientReturnToMainMenuWithTextReason",
        "ClientReturnToMainMenu", "ClientRetryClientRestart", "ClientRestart", "ClientReset", "ClientRepObjRef",
        "ClientReceiveLocalizedMessage", "ClientPrestreamTextures", "ClientPrepareMapChange",
        "ClientPlaySoundAtLocation", "ClientPlaySound", "ClientPlayForceFeedback_Internal", "ClientPlayCameraShake",
        "ClientPlayCameraAnim", "ClientMutePlayer", "ClientMessage", "ClientIgnoreMoveInput", "ClientIgnoreLookInput",
        "ClientGotoState", "ClientGameEnded", "ClientForceGarbageCollection", "ClientFlushLevelStreaming",
        "ClientEndOnlineSession", "ClientEnableNetworkVoice", "ClientCommitMapChange", "ClientClearCameraLensEffects",
        "ClientCapBandwidth", "ClientCancelPendingMapChange", "ClientAddTextureStreamingLoc"
    );

    private static readonly string[] PawnOwnFields = OwnFieldsSorted(
        "RemoteViewPitch", "PlayerState", "Controller"
    );

    private static readonly FClassNetCache ActorCache = new(null, ActorOwnFields);
    private static readonly FClassNetCache ControllerCache = new(ActorCache, ControllerOwnFields);
    private static readonly FClassNetCache PlayerControllerCache = new(ControllerCache, PlayerControllerOwnFields);
    private static readonly FClassNetCache PawnCache = new(ActorCache, PawnOwnFields);

    public static FClassNetCache Get(AActor actor) => actor switch {
        APlayerController => PlayerControllerCache,
        AController => ControllerCache,
        APawn => PawnCache,
        _ => ActorCache
    };
}
