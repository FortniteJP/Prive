using AFortOnlineBeacon.Net;
using AFortOnlineBeacon.Net.Replication;

namespace PacketReplayDecoder;

/// <summary>
///     Local copies of AFortOnlineBeacon's ground-truth wire tables (NativeClassNetCache.cs /
///     NativeRepLayouts.cs are `internal` to that assembly, so we can't reference them directly -
///     this is a plain data copy, not a reimplementation of any algorithm). Used to name RepLayout
///     property handles and ClassNetCache RPC field indices when decoding a captured session from a
///     DIFFERENT (real, unmodified) server, purely for cross-referencing what that real server sends
///     against what this project already knows about.
/// </summary>
internal static class GroundTruth {
    private static string[] Sorted(params string[] fields) {
        var sorted = (string[]) fields.Clone();
        Array.Sort(sorted, StringComparer.OrdinalIgnoreCase);
        return sorted;
    }

    // ---- FClassNetCache (RPC / legacy-field dispatch) chains ----

    private static readonly string[] ActorOwnFields = Sorted(
        "bHidden", "bReplicateMovement", "bTearOff", "bCanBeDamaged", "RemoteRole",
        "ReplicatedMovement", "AttachmentReplication", "Owner", "Role", "Instigator"
    );

    private static readonly string[] ControllerOwnFields = Sorted(
        "PlayerState", "Pawn", "ClientSetRotation", "ClientSetLocation"
    );

    private static readonly string[] PlayerControllerOwnFields = Sorted(
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

    private static readonly string[] PawnOwnFields = Sorted(
        "RemoteViewPitch", "PlayerState", "Controller"
    );

    private static readonly string[] FortPlayerControllerOwnFields = Sorted(
        "bFailedToRespawn", "bHasInitiallySpawned", "bHasServerFinishedLoading", "IntensityGraphInfo",
        "PIDValuesGraphInfo", "PIDContributionsGraphInfo", "bBuildFree", "bCraftFree", "DelayedQuickBarActions",
        "PinnedSchematics", "bAutoEquipBetterItems", "WorldInventory", "OutpostInventory", "LatestRewardReport",
        "UpdatedObjectiveStats", "bHasUnsavedPrimaryMissionProgress", "TutorialCompletedState", "bInfiniteAmmo",
        "bNoCoolDown", "bInfiniteDurability", "bCheatGhost", "bCheatFly", "bEnableShotLogging",
        "bIsNearActiveEncounters", "OverriddenBackpackSize", "AimHelpMode", "JumpStaminaCost", "CameraPrototypeName",
        "bFinalXPUpdateFailed",
        "TogglePersonalVehicle", "ServerUpgradeBuildingActor", "ServerUpdatePlayerData",
        "ServerUpdateMessageComponents", "ServerUpdateItemListOptions", "ServerUpdateGameplayOptions",
        "ServerUpdateGameDescriptionData", "ServerUpdateActorOptions", "ServerUIChoiceCompleted",
        "ServerTriggerGenericObjectiveEvent", "ServerTouchActiveTime", "ServerTeleportToReticle",
        "ServerTeamChatRoomReady", "ServerSuicide", "ServerStartPIDValueGraphing",
        "ServerStartPIDContributionsGraphing", "ServerStartIntensityGraphing", "ServerSpotActor", "ServerSpawnMark",
        "ServerSetShouldUsePilotComponent", "ServerSetShouldUseBotManager", "ServerSetReadyToContinue",
        "ServerSetPartyOwner", "ServerSetMarkText", "ServerSetInventoryStateValue", "ServerSetHero",
        "ServerSetClientHasFinishedLoading", "ServerSetAutoEquipBetterItems",
        "ServerSetAntiAddictionPlayTimeMultiplier", "ServerSendClientProgressUpdate", "ServerReturnToMainMenu",
        "ServerRequestGameplayAction", "ServerRequestAttributeSources", "ServerRequestAIDebug",
        "ServerReportClientFPS", "ServerReplyToReadyCheck", "ServerRepairBuildingActor",
        "ServerRemoveInventoryStateValue", "ServerRemoveInventoryItem", "ServerRemoveDefender",
        "ServerReleaseInventoryItemKey", "ServerReadyToStartMatch", "ServerPlayEmoteItem", "ServerPingMinimap",
        "ServerOnMaterialSelection", "ServerModifyStat", "ServerLoadingScreenDropped",
        "ServerKillAllAIPawnsAroundPlayer", "ServerKickPlayer", "ServerItemWillBeDestroyed",
        "ServerHandleMissionEvent_ToggledEditMode", "ServerHandleMissionEvent_ToggledCursorMode",
        "ServerHandleMissionEvent_StartLeavingZone", "ServerGiftInventoryItemToOtherPlayer",
        "ServerExecutePresetTeamChat", "ServerExecuteInventoryItem", "ServerEndEditingBuildingActor", "ServerEmote",
        "ServerEditBuildingActor", "ServerDropCarriedObject", "ServerDropAllItems",
        "ServerDisassembleInventoryItems", "ServerDeveloper_GetConsoleVariable", "ServerDeployDefender",
        "ServerCreateBuildingActor", "ServerCraftSchematic", "ServerCombineInventoryItems",
        "ServerClientPawnLoaded", "ServerClearItemList", "ServerCheatAll", "ServerCheat",
        "ServerBroadcastUIFeedbackEvent", "ServerBroadcastPlayerChangedBuildMode", "ServerBeginEditingBuildingActor",
        "ServerAttemptUnpinSchematic", "ServerAttemptPinSchematic", "ServerAttemptInventoryDrop",
        "ServerAttemptAircraftJump", "ServerAnnouncementStoppedOnClient", "ServerAddPawnMovementInput",
        "ServerAcknowledgeDelayedQuickBarAction", "FortClientPlaySoundAtLocation", "FortClientPlaySound",
        "EnterAircraftClient", "ClientUpdateServerOnPlayerChangedBuildMode", "ClientUpdateRichPresence",
        "ClientUpdatePlayerList", "ClientTriggerUIFeedbackEvent", "ClientSwapQuickBarFocus",
        "ClientStopUIFeedbackEvent", "ClientStopAutoRun", "ClientSpawnWeakSpotOnBuildingActor",
        "ClientSetSpectatorCamera", "ClientSetInviteFlags", "ClientSetActionMappingEnabled", "ClientSendMessage",
        "ClientSendConfirmationMessage", "ClientRequestReadyCheck", "ClientReportDamagedResourceBuilding",
        "ClientRegisterWithParty", "ClientRefreshPlayerList", "ClientReceivePresetTeamChat",
        "ClientReceivedAttributeSources", "ClientReadyCheckComplete", "ClientPrecacheMediaSource",
        "ClientPingMinimap", "ClientOpenChoiceUI", "ClientOnGenericPlayerInitialization", "ClientJoinConsoleSession",
        "ClientGivePlayerLocalAccountItem", "ClientForceWorldInventoryUpdate", "ClientForceUpdateQuickbar",
        "ClientForceProfileQuery", "ClientForceCancelBuildingTool", "ClientFinishedInteractionInZone",
        "ClientFailedToBeginEditingBuildingActor", "ClientExecuteInventoryItem", "ClientEquipItem",
        "ClientDeveloper_GetConsoleVariable", "ClientCreateOrJoinChatRoom", "ClientConfirmTargetData",
        "ClientCancelCrafting", "ClientBotSetModuleToUse", "ClientBotEnqueueCommand", "ClientAddScoreNumber",
        "ClientActivateSlot", "Cheat_StopObjectiveServer", "Cheat_ForcePlayEmoteItem",
        "Cheat_ForceAthenaCosmeticItemInSlot", "Cheat_ClearForcedCosmeticItems"
    );

    private static readonly string[] FortPlayerControllerGameplayOwnFields = Sorted(
        "PoiTagContainerTableID", "CreativeQuickbarComponent", "GhostModeRepData", "ServerNumNPCs",
        "ServerMaxNumNPCs", "bDisplayNPCNumbers", "FlyingModifierIndex", "bIsFlightSprinting",
        "bIsCreativeModeEnabled", "bIsCreativeQuickbarEnabled",
        "ServerRPCSetDisplayNPCNumbers", "ServerQueryCreativeAssetsByCategory", "ServerCreativeToggleFly",
        "ServerCreativeStopGhost", "ServerCreativeStopFlyUp", "ServerCreativeStopFlyDown", "ServerCreativeStopFly",
        "ServerCreativeStartFlyUp", "ServerCreativeStartFlyDown", "ServerCreativeSetGhost",
        "ServerCreativeSetFlightSprint", "ServerCreativeSetFlightSpeedIndex", "ServerAwardVehicleTrickPoints",
        "ClientReceiveCreativeAssetsByCategory", "ClientCreativeStopFly", "ClientCreativePhoneCreated"
    );

    private static readonly string[] FortPlayerControllerZoneOwnFields = Sorted(
        "VoiceChatChannel", "DesyncNotifyList",
        "ServerVoiceChatRequestJoinToken", "ServerSubmitGameplayVote", "ServerSpectatePlayerState",
        "ServerSpectatePlayer", "ServerSetShouldDisablePlayerTeleportingDuringMissionResults",
        "ServerSendPartyJoinInfoToPlayer", "ServerSendLoadoutConfig", "ServerRequestSeatChange",
        "ServerEndGameplayVote", "ServerDetachFromRemoteControlledPawn", "ServerDestroyFromRemoteControlledPawn",
        "ServerBeginGameplayVote", "ServerAttemptExitVehicle", "ServerActivateMission",
        "ClientVoiceChatSendJoinToken", "ClientSendPartyJoinInfoToPlayer", "ClientPresentGameplayVote",
        "ClientOnZoneEndScoreReports", "ClientOnPawnSpawned", "ClientOnPawnRevived", "ClientOnPawnDied",
        "ClientClearDeathNotification", "ClientAckLoadoutConfig"
    );

    private static readonly string[] FortPlayerControllerPvPOwnFields = Sorted(
        "ClientReceiveKillNotification"
    );

    private static readonly string[] FortPlayerControllerAthenaOwnFields = Sorted(
        "SkydiveLeader", "ViewTargetInventory", "bNextRespawnInAir", "bCanUseSolaris", "MaxPlotCount",
        "bMarkedAlive", "CreativeIslands", "LastUsedCreativeIsland", "bIsAllowedToPublish",
        "PartyAssistedMemberData", "BroadcastRemoteClientInfo", "CreativePlotLinkedVolume", "OwnedPortal",
        "CachedPurchasedItems", "CurrentPlayset", "CreativeUserContentManager",
        "UpdateCreativePlotName", "UpdateCreativePlotData", "TellServer_ClientReceivedPlaysetDataForVolume",
        "ServerUpdateSolarisString", "ServerToggleAutoRestartMinigame", "ServerThankBusDriver",
        "ServerTeleportToPlaygroundLobbyIsland", "ServerTeleportToPlaygroundIslandDock",
        "ServerStopSavingCreativePlot", "ServerStartUnloadingVolume", "ServerStartMinigame",
        "ServerStartLoadingVolume", "ServerSpawnCreativeSupplyDrop", "ServerSetUseSolaris", "ServerSetTeam",
        "ServerSetShouldSwapPickup", "ServerSetPlayset", "ServerSetMinigameClassSlot", "ServerSendSquadFriend",
        "ServerSaveIslandCheckpoint", "ServerRestartMinigame", "ServerRespondToAbandonMatch",
        "ServerRequestNewSkydiveLeader", "ServerReloadCreativePlot", "ServerReleasePortal",
        "ServerPlaySquadQuickChatMessage", "ServerNotifyOstrichShieldOvercharge",
        "ServerNotifyOstrichSelfDestruct", "ServerLoadPlotForPortalFromMnemonic", "ServerLoadPlotForPortal",
        "ServerLoadIslandCheckpoint", "ServerGiveCreativeItem", "ServerGenerateMockMatchReport",
        "ServerEndUnloadingVolume", "ServerEndMinigame", "ServerEndLoadingVolume", "ServerEnableAnonymousMode",
        "ServerEnableAnonymousCharacterMode", "ServerDBNOReviveStarted", "ServerDBNOReviveInterrupted",
        "ServerCreateProfileGoCollectionForSublevels", "ServerClientIsReadyToRespawn", "ServerClearSkydiveLeader",
        "ServerClaimPortal", "ServerAddToCachedPurchased", "SendPhysicsBallHitToServer",
        "SendClientPhysicsBallStateToServer", "RestoreCreativePlot", "ResetMyCurrentCreativePlot",
        "PublishCreativePlot", "MakeNewCreativePlotFromLinkCode", "MakeNewCreativePlot", "DuplicateCreativePlot",
        "DestroyCreativePlot", "ClientStartRespawnPreparation", "ClientSetDeathReport",
        "ClientSendTeamStatsForPlayer", "ClientSendStateEncryptionKey", "ClientSendMatchStatsForPlayer",
        "ClientSendEndMatchReportHeartbeat", "ClientSendEndBattleRoyaleMatchForPlayer",
        "ClientSendDebugPoiVolumeData", "ClientSendDebugPoiLocationTags",
        "ClientReportTournamentPlacementPointsScored", "ClientReportPhaseFound",
        "ClientReceiveSquadQuickChatMessage", "ClientPublishCreativePlotComplete", "ClientNotifyWon",
        "ClientNotifyTeamWon", "ClientNotifyTeamLost", "ClientNotifyLost", "ClientNotifyAbortRespawn",
        "ClientHideScreenWhileRespawning", "ClientEnterCameraMode", "ClientCycleQuickBarToCreativeItem",
        "ClientBroadcastOnUpdateCreativePlotName", "ClientBroadcastOnRestoreCreativePlotFinished",
        "ClientBroadcastOnMakeNewCreativePlotFinished", "ClientBroadcastOnDuplicateCreativePlotFinished",
        "ClientBroadcastOnDestroyCreativePlotFinished", "ClientBotStopDogpile", "ClientBotStartDogpile",
        "ClientAutoEquipFirstItem", "ClientAlertLeaveIsland", "ClientAddProfileGoCollection",
        "Client_DisplayQuestUpdate_Self", "Client_DisplayQuestUpdate_Assist"
    );

    private static readonly string[] PlayerStateOwnFields = Sorted(
        "Score", "PlayerID", "Ping", "bIsSpectator", "bOnlySpectator", "bIsABot", "bIsInactive",
        "bFromPreviousLevel", "StartTime", "UniqueId", "PlayerNamePrivate"
    );

    private static readonly string[] FortPlayerStateOwnFields = Sorted(
        "bIsWorldDataOwner", "bIsGameSessionOwner", "bIsGameSessionAdmin", "bIsReadyToContinue",
        "bHasFinishedLoading", "bHasStartedPlaying", "bShowHeroBackpack", "bShowHeroHeadAccessories",
        "bRepFlag1", "PlayerRole", "PartyOwnerUniqueId", "WorldPlayerId", "HeroId", "HeroType", "CurrentCharXP",
        "MyBackpackPickup", "InitialExperienceLevel", "InitialExperienceAmount", "ExperienceDeltas", "Platform",
        "CharacterGender", "CharacterBodyType", "CharacterData", "CharacterColorSwatches",
        "CharacterPartColorSwatches", "PlayerTeam", "PlayerTeamPrivate", "ReplicatedStats_Campaign",
        "ReplicatedStats_Zone", "bAreZoneStatsFinalized", "ReadyCheckState", "HomeActor", "PlatformUniqueNetId",
        "ServerSetShowHeroHeadAccessories", "ServerSetShowHeroBackpack", "ClientNotifyAwardGranted"
    );

    private static readonly string[] FortPlayerStateZoneOwnFields = Sorted(
        "SpectatingTarget", "Spectators", "KickedFromSessionReason", "CarriedObject", "NumRejoins",
        "bInvincibleDueToUI", "CurrentHealth", "MaxHealth", "CurrentShield", "MaxShield", "CurrentSignalInStorm",
        "MaxSignalInStorm", "AccumulatedItems", "SimulatedAttributes", "bHasEverSkydivedFromBus",
        "bHasEverSkydivedFromBusAndLanded", "QuickbarEquippedItems",
        "MulticastTriggerOnGadgetTrackedAttributeDestroyedFX"
    );

    private static readonly string[] FortPlayerStateAthenaOwnFields = Sorted(
        "PersonalLobbyAction", "ReplicatedTeamMemberState", "TeamKillScore", "TeamIndex", "TeamScorePlacement",
        "TeamScore", "Place", "DownScore", "KillScore", "NumChestsOpened", "NumAmmoCansOpened",
        "NumSupplyDropsOpened", "NumLlamasOpened", "NumForagedItemsConsumed", "NumMinutesAlive",
        "NumBronzeCoinsCollected", "NumSilverCoinsCollected", "NumGoldCoinsCollected", "TotalPlayerScore",
        "StormSurgeEffectCount", "TeamAverageDamageDealt", "SquadId", "Banner", "bThankedBusDriver",
        "bInAircraft", "bUsingAnonymousMode", "bUsingAnonymousCharacterMode", "StreamerModeName",
        "bIsDisconnected", "DeathInfo", "ChangeTeamInfo", "ResurrectionChipAvailable", "bResurrectingNow",
        "RebootCounter", "bHoldsRebootVanLock", "MatchAbandonState",
        "ServerSetInAircraft", "Server_SetCanEditCreativeIsland", "ClientReportTeamKill", "ClientReportKill",
        "ClientReportDBNO", "ClientNotifyMatchEntered", "ClientAddKillFeedErrorMessage"
    );

    private static readonly FClassNetCache ActorCache = new(null, ActorOwnFields);
    private static readonly FClassNetCache ControllerCache = new(ActorCache, ControllerOwnFields);
    private static readonly FClassNetCache PlayerControllerBaseCache = new(ControllerCache, PlayerControllerOwnFields);
    private static readonly FClassNetCache FortPlayerControllerCache = new(PlayerControllerBaseCache, FortPlayerControllerOwnFields);
    private static readonly FClassNetCache FortPlayerControllerGameplayCache = new(FortPlayerControllerCache, FortPlayerControllerGameplayOwnFields);
    private static readonly FClassNetCache FortPlayerControllerZoneCache = new(FortPlayerControllerGameplayCache, FortPlayerControllerZoneOwnFields);
    private static readonly FClassNetCache FortPlayerControllerPvPCache = new(FortPlayerControllerZoneCache, FortPlayerControllerPvPOwnFields);
    public static readonly FClassNetCache PlayerControllerCache = new(FortPlayerControllerPvPCache, FortPlayerControllerAthenaOwnFields);
    private static readonly FClassNetCache PawnBaseCache = new(ActorCache, PawnOwnFields);
    public static readonly FClassNetCache PawnCache = PawnBaseCache; // trimmed - Character/FortPawn/etc chain not needed for our RPC decode targets

    private static readonly FClassNetCache PlayerStateBaseCache = new(ActorCache, PlayerStateOwnFields);
    private static readonly FClassNetCache FortPlayerStateCache = new(PlayerStateBaseCache, FortPlayerStateOwnFields);
    private static readonly FClassNetCache FortPlayerStateZoneCache = new(FortPlayerStateCache, FortPlayerStateZoneOwnFields);
    public static readonly FClassNetCache PlayerStateCache = new(FortPlayerStateZoneCache, FortPlayerStateAthenaOwnFields);

    public static readonly FClassNetCache GenericActorCache = ActorCache;

    // ---- FRepLayout (ordinary UPROPERTY replication) handle tables ----
    //
    // Unlike the ClassNetCache field loop (self-describing via a packed bit-count per field), the
    // RepLayout property blob has NO per-property length prefix - the reader must know each
    // property's real C++ type to know how many bits its value occupies. AFortOnlineBeacon's own
    // NativeRepLayouts.cs only actually models a handful of properties it sends (RemoteRole/Role as
    // a 2-bit ByteEnum, PlayerState as an ObjectRef, and three Bools) - everything else there is a
    // "Reserved" placeholder that exists purely to give later properties the right handle NUMBER,
    // with NO confirmed wire width. Re-declaring all of them as Bool/StructAtomic here would be
    // GUESSING - if a guess is wrong the whole rest of the blob silently desyncs. So each entry
    // below is either FixedBits (a width we're actually confident of - real UE bools are always
    // exactly 1 bit, a plain byte/TEnumAsByte-without-enum is always 8 bits, and the properties this
    // project actually sends have a getter, confirming Kind/width), IsObjectRef (self-describing via
    // a NetGUID read), or neither (name known from the live handle-numbering probe, width unknown -
    // decode stops there and the remainder is hex-dumped for manual follow-up).
    public sealed record RepHandleDef(string Name, int? FixedBits, bool IsObjectRef = false, RepHandleDef[]? Children = null);

    private static RepHandleDef Bit(string name) => new(name, 1);
    private static RepHandleDef Byte(string name) => new(name, 8);
    private static RepHandleDef Obj(string name) => new(name, null, true);
    private static RepHandleDef Unknown(string name) => new(name, null);

    public static readonly RepHandleDef[] ActorProps = {
        Bit("bHidden"), Bit("bReplicateMovement"), Bit("bTearOff"), Bit("bCanBeDamaged"),
        new("RemoteRole", 2), // TEnumAsByte<ENetRole>, CeilLogTwo(ROLE_MAX=4) = 2 bits - GetByteValue-confirmed in NativeRepLayouts
        Unknown("ReplicatedMovement"), // FRepMovement - variable/quantized, not modeled
        new("AttachmentReplication", null, false, new[] {
            Unknown("AttachmentReplication[0]"), Unknown("AttachmentReplication[1]"), Unknown("AttachmentReplication[2]"),
            Unknown("AttachmentReplication[3]"), Unknown("AttachmentReplication[4]"), Unknown("AttachmentReplication[5]")
        }),
        Obj("Owner"),
        new("Role", 2), // GetByteValue-confirmed
        Obj("Instigator")
    };

    public static readonly RepHandleDef[] ControllerProps = ActorProps.Concat(new[] {
        Obj("PlayerState"), // GetObjectValue-confirmed
        Obj("Pawn")
    }).ToArray();

    public static readonly RepHandleDef[] PlayerControllerProps = ControllerProps.Concat(new[] {
        Unknown("TargetViewRotation"), // FRotator - not modeled
        Unknown("SpawnLocation"), // FVector - not modeled
        Bit("bFailedToRespawn"),
        Bit("bHasInitiallySpawned"),
        Bit("bHasServerFinishedLoading") // GetByteValue-confirmed
    }).ToArray();

    public static readonly RepHandleDef[] PawnProps = ActorProps.Concat(new[] {
        Byte("RemoteViewPitch"), // plain uint8, always 8 bits
        Obj("PlayerState"),
        Obj("Controller")
    }).ToArray();

    public static readonly RepHandleDef[] GameStateProps = ActorProps.Concat(new[] {
        Obj("GameModeClass"),
        Obj("SpectatorClass"),
        Bit("bReplicatedHasBegunPlay"), // GetByteValue-confirmed
        Unknown("ReplicatedWorldTimeSeconds") // float in real UE, but width/quantization not confirmed here
    }).ToArray();

    public static readonly RepHandleDef[] PlayerStateProps = ActorProps.Concat(new[] {
        Unknown("Score"), Unknown("PlayerID"), Unknown("Ping"),
        Bit("bIsSpectator"), Bit("bOnlySpectator"), Bit("bIsABot"), Bit("bIsInactive"), Bit("bFromPreviousLevel"),
        Unknown("StartTime"), Unknown("UniqueId"), Unknown("PlayerNamePrivate"),
        Bit("bIsGameSessionOwner"), Bit("bIsWorldDataOwner"), Bit("bHasFinishedLoading"),
        Bit("bHasStartedPlaying") // GetByteValue-confirmed
    }).ToArray();

    /// <summary>Flattens a table into (handle -&gt; def), replicating FRepLayout's own numbering: sequential ++ per leaf, recursing into children instead of assigning the parent a handle.</summary>
    public static Dictionary<uint, RepHandleDef> BuildHandleMap(RepHandleDef[] topLevel) {
        var map = new Dictionary<uint, RepHandleDef>();
        uint handle = 0;
        void Visit(RepHandleDef def) {
            if (def.Children != null) { foreach (var c in def.Children) Visit(c); return; }
            handle++;
            map[handle] = def;
        }
        foreach (var d in topLevel) Visit(d);
        return map;
    }
}
