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

    // APlayerController's spawn archetype was upgraded from bare /Script/Engine.PlayerController to
    // the real /Script/FortniteGame.FortPlayerControllerAthena (see GUClassArray) so
    // bHasServerFinishedLoading could be sent at all - but ClassNetCache's FieldNetIndex bounded-int
    // WIDTH (WriteIntWrapped/ReadInt's bit count) depends on the class's TOTAL own+inherited field
    // count, not just the fields this project recognizes by name. Without these 5 extra levels,
    // GetMaxIndex() was far too small, so even a correctly-indexed field like ServerShortTimeout
    // decoded with a too-narrow bit width and desynced the very next field read (crashed with
    // "BitReader::SerializeInt Overflow"). Ground-truthed via the same live UEDumper NetFields.txt
    // dump as everything else in this file.
    private static readonly string[] FortPlayerControllerOwnFields = OwnFieldsSorted(
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

    private static readonly string[] FortPlayerControllerGameplayOwnFields = OwnFieldsSorted(
        "PoiTagContainerTableID", "CreativeQuickbarComponent", "GhostModeRepData", "ServerNumNPCs",
        "ServerMaxNumNPCs", "bDisplayNPCNumbers", "FlyingModifierIndex", "bIsFlightSprinting",
        "bIsCreativeModeEnabled", "bIsCreativeQuickbarEnabled",
        "ServerRPCSetDisplayNPCNumbers", "ServerQueryCreativeAssetsByCategory", "ServerCreativeToggleFly",
        "ServerCreativeStopGhost", "ServerCreativeStopFlyUp", "ServerCreativeStopFlyDown", "ServerCreativeStopFly",
        "ServerCreativeStartFlyUp", "ServerCreativeStartFlyDown", "ServerCreativeSetGhost",
        "ServerCreativeSetFlightSprint", "ServerCreativeSetFlightSpeedIndex", "ServerAwardVehicleTrickPoints",
        "ClientReceiveCreativeAssetsByCategory", "ClientCreativeStopFly", "ClientCreativePhoneCreated"
    );

    private static readonly string[] FortPlayerControllerZoneOwnFields = OwnFieldsSorted(
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

    private static readonly string[] FortPlayerControllerPvPOwnFields = OwnFieldsSorted(
        "ClientReceiveKillNotification"
    );

    private static readonly string[] FortPlayerControllerAthenaOwnFields = OwnFieldsSorted(
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

    // APlayerState : AInfo : AActor (AInfo adds nothing - see NativeRepLayouts.GameStateProps for
    // the same fact used there).
    private static readonly string[] PlayerStateOwnFields = OwnFieldsSorted(
        "Score", "PlayerID", "Ping", "bIsSpectator", "bOnlySpectator", "bIsABot", "bIsInactive",
        "bFromPreviousLevel", "StartTime", "UniqueId", "PlayerNamePrivate"
    );

    private static readonly string[] FortPlayerStateOwnFields = OwnFieldsSorted(
        "bIsWorldDataOwner", "bIsGameSessionOwner", "bIsGameSessionAdmin", "bIsReadyToContinue",
        "bHasFinishedLoading", "bHasStartedPlaying", "bShowHeroBackpack", "bShowHeroHeadAccessories",
        "bRepFlag1", "PlayerRole", "PartyOwnerUniqueId", "WorldPlayerId", "HeroId", "HeroType", "CurrentCharXP",
        "MyBackpackPickup", "InitialExperienceLevel", "InitialExperienceAmount", "ExperienceDeltas", "Platform",
        "CharacterGender", "CharacterBodyType", "CharacterData", "CharacterColorSwatches",
        "CharacterPartColorSwatches", "PlayerTeam", "PlayerTeamPrivate", "ReplicatedStats_Campaign",
        "ReplicatedStats_Zone", "bAreZoneStatsFinalized", "ReadyCheckState", "HomeActor", "PlatformUniqueNetId",
        "ServerSetShowHeroHeadAccessories", "ServerSetShowHeroBackpack", "ClientNotifyAwardGranted"
    );

    private static readonly string[] FortPlayerStateZoneOwnFields = OwnFieldsSorted(
        "SpectatingTarget", "Spectators", "KickedFromSessionReason", "CarriedObject", "NumRejoins",
        "bInvincibleDueToUI", "CurrentHealth", "MaxHealth", "CurrentShield", "MaxShield", "CurrentSignalInStorm",
        "MaxSignalInStorm", "AccumulatedItems", "SimulatedAttributes", "bHasEverSkydivedFromBus",
        "bHasEverSkydivedFromBusAndLanded", "QuickbarEquippedItems",
        "MulticastTriggerOnGadgetTrackedAttributeDestroyedFX"
    );

    // FortPlayerStatePvP declares no NetFields of its own (confirmed empty in the live dump).

    private static readonly string[] FortPlayerStateAthenaOwnFields = OwnFieldsSorted(
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

    // Everything below is ground-truthed the same way (live UEDumper NetFields.txt dump) for the
    // full class chain a real Athena pawn actually is: APlayerPawn_Athena_C ->
    // APlayerPawn_Athena_Generic_C -> APlayerPawn_Athena_Generic_Parent_C -> AFortPlayerPawnAthena
    // -> AFortPlayerPawn -> AFortPawn -> ACharacter -> APawn (this project's single APawn class
    // stands in for the whole chain - there's no per-level C# subclass, but FClassNetCache still
    // needs the FULL real chain to compute correct FieldNetIndex values, since RPCs/fields further
    // up the chain - like ServerMoveNoBase on ACharacter - shift every index after them). The two
    // Blueprint layers PlayerPawn_Athena_Generic_Parent_C/PlayerPawn_Athena_Generic_C add no
    // NetFields of their own (empty in the dump), so they're skipped entirely below - an empty
    // link would contribute 0 fields either way, so omitting it changes no downstream index.
    private static readonly string[] CharacterOwnFields = OwnFieldsSorted(
        "ReplicatedBasedMovement", "AnimRootMotionTranslationScale", "ReplicatedServerLastTransformUpdateTimeStamp",
        "ReplayLastTransformUpdateTimeStamp", "ReplicatedMovementMode", "bIsCrouched", "bProxyIsJumpForceApplied",
        "JumpMaxHoldTime", "JumpMaxCount", "RepRootMotion",
        "ServerMoveOld", "ServerMoveNoBase", "ServerMoveDualNoBase", "ServerMoveDualHybridRootMotion",
        "ServerMoveDual", "ServerMove", "RootMotionDebugClientPrintOnScreen", "ClientVeryShortAdjustPosition",
        "ClientCheatWalk", "ClientCheatGhost", "ClientCheatFly", "ClientAdjustRootMotionSourcePosition",
        "ClientAdjustRootMotionPosition", "ClientAdjustPosition", "ClientAckGoodMove"
    );

    private static readonly string[] FortPawnOwnFields = OwnFieldsSorted(
        "bIgnoreNextFallingDamage", "bIsDying", "bIsHiddenForDeath", "bIsKnockedback", "bIsStaggered",
        "bMovingEmote", "bMovingEmoteForwardOnly", "bIsInvulnerable", "bSpotted", "bWeaponActivated",
        "bWeaponHolstered", "bIsDBNO", "CurrentMovementStyle", "TeleportCounter", "StormShieldComponent",
        "PawnUniqueID", "CurrentWeapon", "SpawnImmunityTime", "bIsStunned", "PushMomentum", "LocalSpin",
        "DamageZoneActiveBitMask", "JumpFlashCountPacked", "LandingFlashCountPacked", "LastReplicatedEmoteExecuted",
        "EmoteWalkSpeed", "VocalChords", "DisplayName", "CurrentCalloutTag", "CurrentSentence",
        "ServerTeleportNearLocation", "ServerInternalEquipWeapon", "PlaySound",
        "NetMulticast_InvokeGameplayCuesExecuted_WithParams", "NetMulticast_InvokeGameplayCuesExecuted",
        "NetMulticast_InvokeGameplayCuesAddedAndWhileActive_WithParams",
        "NetMulticast_InvokeGameplayCueExecuted_WithParams", "NetMulticast_InvokeGameplayCueExecuted_FromSpec",
        "NetMulticast_InvokeGameplayCueExecuted", "NetMulticast_InvokeGameplayCueAddedAndWhileActive_WithParams",
        "NetMulticast_InvokeGameplayCueAddedAndWhileActive_FromSpec", "NetMulticast_InvokeGameplayCueAdded_WithParams",
        "NetMulticast_InvokeGameplayCueAdded", "NetMulticast_Athena_BatchedDamageCues", "ClientInternalEquipWeapon"
    );

    private static readonly string[] FortPlayerPawnOwnFields = OwnFieldsSorted(
        "VehicleInputStateReliable", "bIsNearSafeZoneEdge", "bIsTargeting", "StasisMode", "BuildingState",
        "AccelerationZPack", "bIsInWaterVolume", "CachedTeamControllingRC", "BalloonActiveCount", "bIsSkydiving",
        "bIsParachuteOpen", "bIsParachuteForcedOpen", "bIsSkydivingFromBus", "bIsSkydivingFromLaunchPad",
        "bReplicatedIsInVortex", "bReplicatedIsInSlipperyMovement", "bIsSlopeSliding", "bIsProxySimulationTimedOut",
        "bInGliderRedeploy", "bStartedInteractSearch", "bIsUsingJetpack", "bIsPlayingEmote", "bIsRespawning",
        "bIsRespawningInAir", "VehicleInputStateUnreliable", "bIsInAnyStorm", "bIsInsideSafeZone", "ZiplineState",
        "bCanPredictJumpApex", "VehicleStateRep", "PossessedProp", "CosmeticLoadout", "RepCharPartAnimMontageInfo",
        "ClientObservedStats", "AnimBPOverride", "FootstepBankOverride", "PackedReplicatedSlopeAngles",
        "PlayerStatus", "AccelerationPack", "RepAnimMontageInfo", "RepAnimMontageStartSection",
        "bNetMovementPrioritized", "LandingMontagePair", "bParachuteLockedOpen", "AttachmentMesh", "VortexParams",
        "ReplicatedSkyTube", "PetState", "GliderOverrideStack", "RemoteViewData32", "ControlledRCPawn",
        "StoredControlRotation",
        "ServerUpdateVehicleInputStateUnreliable", "ServerUpdateVehicleInputStateReliable", "ServerUnmarkRespawned",
        "ServerToggleGender", "ServerToggleBodyType", "ServerSetAttachment", "ServerSetAimbotDetection",
        "ServerSendZiplineState", "ServerSendAimbotDetectionStatus", "ServerRootMotionInterruptNotifyStopMontage",
        "ServerReviveFromDBNO", "ServerRespawnFromDBNO", "ServerPlayUnableToPerformActionMontage",
        "ServerHandlePickupWithSwap", "ServerHandlePickupWithRequestedSwap", "ServerHandlePickup",
        "ServerEquipLastWeaponOrGadget", "ServerCyclePart", "ServerCycleColorSwatch", "ServerCycleAccessoryColorSwatch",
        "ServerChoosePart", "ServerChooseGender", "MulticastUpdateVehicleInputStateReliable",
        "ClientResetAbilitySystemComponent", "ClientNotifyAbilityFailed", "ClientAcknowledgeVehicleInputState"
    );

    private static readonly string[] FortPlayerPawnAthenaOwnFields = OwnFieldsSorted(
        "ItemInteractionActor", "DBNORevivalStacking", "ItemSpecialActorID", "ItemSpecialActorCategoryTag",
        "CapsuleRadiusAthena", "CapsuleHalfHeightAthena", "MeshHeightAdjustAthena", "AttributeReplicationProxy",
        "ReplayRepAnimMontageInfo", "SimulatedProxyGameplayCues", "FastReplicationMinimalReplicationTags",
        "EncryptedPawnReplayData", "bIsCreativeGhostModeActivated",
        "ServerSuicide", "ServerSetInteractingItem", "NetMulticast_SuccessfulBuildingEdit", "FastSharedReplication"
    );

    private static readonly string[] PlayerPawnAthenaOwnFields = OwnFieldsSorted(
        "PlayResOut", "ClientRunSnowGC", "AddSafeZoneGameplayCue", "RemoveSafeZoneGameplayCueServerToClient",
        "PlayRespawnFXOnSpawn"
    );

    // /Script/FortniteGame.FortInventory derives straight from AActor. Its own net fields are the
    // three CPF_Net properties Dumper-7 lists on it - Inventory (FFortItemList), InventoryType
    // (ByteProperty) and ReplayPawn (ObjectProperty). Its ONLY function, HandleInventoryLocalUpdate,
    // is Final|Native|Public with no Net flag (checked in Dumper-7's Dumpspace/FunctionsInfo.json),
    // so it is not a net field and does not take an index. UEDumper's NetFields.txt has no
    // FortInventory section at all, which is why this one is sourced from Dumper-7 instead.
    private static readonly string[] FortInventoryOwnFields = OwnFieldsSorted(
        "Inventory", "InventoryType", "ReplayPawn"
    );

    // /Script/FortniteGame.FortBroadcastRemoteClientInfo derives straight from AActor. Own net
    // fields checked directly against Dumper-7's Dumpspace/FunctionsInfo.json (2026-08-29): 14
    // CPF_Net properties plus exactly 13 FUNC_Net functions - the "Net" flag distinguishes these
    // from the many OnRep_*/OnPlayer*Changed/OnServer*Changed methods on this class, none of which
    // carry it (plain native callbacks, not RPCs - not net fields at all).
    private static readonly string[] FortBroadcastRemoteClientInfoOwnFields = OwnFieldsSorted(
        "bActive", "bRemoteIsInteracting", "RemoteEditActor", "RemoteEditTileData",
        "RemoteBuildableClass", "RemoteBuildingMaterial", "bRemoteIsFullScreenMapActive",
        "bRemoteIsInventoryActive", "bRemoteCanDBNORevive", "RemoteChatEntry", "RemoteWeakspotData",
        "RemoteRespawnTime", "RemotePoiTagID", "RemoteEventScore",
        "ClientRemotePlayerAddMapMarker", "ClientRemotePlayerDamagedResourceBuilding",
        "ClientRemotePlayerHitMarkers", "ClientRemotePlayerRemoveMapMarker",
        "ServerSetPlayerBuildableClass", "ServerSetPlayerBuildingMaterial",
        "ServerSetPlayerCanDBNORevive", "ServerSetPlayerEditTileData", "ServerSetPlayerEventScore",
        "ServerSetPlayerFullScreenMapActive", "ServerSetPlayerHitMarkers",
        "ServerSetPlayerInteracting", "ServerSetPlayerInventoryActive"
    );

    // ------------------------------------------------------------------------------------------
    // AGameState's chain. GENERATED by Tools/NetFieldVerify/gen_net_fields.py from the Dumper-7
    // 10.40 SDK and checked by Tools/NetFieldVerify/verify_net_fields.py, using the same rule as
    // every table above: a class's OWN CPF_Net properties plus its FUNC_Net functions, sorted by
    // name (ordinal, case-insensitive).
    //
    // This exists for exactly one field so far: AFortGameStateAthena::CurrentPlaylistInfo, a
    // Custom Delta (FastArraySerializer) property that never appears in the RepLayout handle
    // stream and so needs a real FieldNetIndex. A working 10.40 capture (a private server on
    // 127.0.0.1:7777, client log 2026-08-23 16:58) shows the whole in-match UI hanging off it:
    //
    //   16:58:21.201  LogRepTraffic:  Athena_GameState_C - CurrentPlaylistInfo
    //   16:58:21.216  PLAYLIST: Playlist Object replicated to client in OnRep_CurrentPlaylistInfo()
    //                 PlaylistName is Playlist_DefaultSolo (Client Only)
    //   16:58:21.834  PLAYLIST: ... finished loading its assets in OnPlaylistDataLoadCompleted()
    //   16:58:21.927  [UFortUIManagerWidget_NUI::NativeTick] change state to InGame_BR
    //
    // i.e. the client cannot pick an in-game UI state until it knows which playlist it is in, and
    // without a UI state the Athena loading screen never drops ("The UI is not ready yet
    // (UIManagerWidget->GetCurrentUIState() == Invalid)").
    //
    // Athena_GameState_C, the Blueprint class the GameState is actually spawned as, adds NO net
    // fields of its own (its four UFunctions are all non-Net and none of its properties carry
    // CPF_Net), so it needs no entry here and does not affect GetMaxIndex().
    // ------------------------------------------------------------------------------------------

    // /Script/Engine.GameStateBase - 4 own net fields (4 properties + 0 functions).
    private static readonly string[] GameStateBaseOwnFields = OwnFieldsSorted(
        "bReplicatedHasBegunPlay", "GameModeClass", "ReplicatedWorldTimeSeconds", "SpectatorClass"
    );
    // /Script/Engine.GameState - 2 own net fields (2 properties + 0 functions).
    private static readonly string[] GameStateOwnFields = OwnFieldsSorted(
        "ElapsedTime", "MatchState"
    );
    // /Script/FortniteGame.FortGameStateBase - 2 own net fields (2 properties + 0 functions).
    private static readonly string[] FortGameStateBaseOwnFields = OwnFieldsSorted(
        "FortTimeOfDayManager", "StormShield"
    );
    // /Script/FortniteGame.FortGameState - 34 own net fields (27 properties + 7 functions).
    private static readonly string[] FortGameStateOwnFields = OwnFieldsSorted(
        "AdditionalPlaylistLevelsStreamed", "AmmoBoxInfos", "AnnouncementManager",
        "bDBNOEnabledForGameMode", "bPlayerRespawningBlocked_Temporarily", "Client_InitiateEndOfDayRecap",
        "Client_RefreshEventCalendar", "CraftingBonus", "CurrentReadyToContinueTimer", "CurrentWUID",
        "DbgBoxSendToAllInternal_DoNotCall", "DbgCapsuleSendToAllInternal_DoNotCall",
        "DbgLineSendToAllInternal_DoNotCall", "DbgSphereSendToAllInternal_DoNotCall", "FeedbackManager",
        "GameFlagData", "GameplayState", "GameSessionId", "MissionManager", "MusicManagerBank",
        "MusicManagerSubclass", "ParTime", "PawnForReplayRelevancy", "PendingTeamChangeRequests",
        "PoiManager", "RecorderPlayerState", "RunPerfMemCheatScript", "TeamCount", "Teams",
        "TreasureChestInfos", "VisibilityManager", "WorldDaysElapsed", "WorldLevel", "WorldManager"
    );
    // /Script/FortniteGame.FortGameState_InGame - 0 own net fields (0 properties + 0 functions).
    private static readonly string[] FortGameState_InGameOwnFields = OwnFieldsSorted(
       
    );
    // /Script/FortniteGame.FortGameStateZone - 48 own net fields (43 properties + 5 functions).
    private static readonly string[] FortGameStateZoneOwnFields = OwnFieldsSorted(
        "ActiveGameplayModifiers", "AllSpawnGroupUpgradeModifierDefs", "bAllowBuildingAtLayoutRequirements",
        "bAllowBuildingWithoutLayoutRequirements", "bAllowLayoutRequirementsFeature", "bDBNODeathEnabled",
        "bGlobalCeaseFire", "bInvitesRestricted", "bIsGroupContent", "ClientPreloadMissionClasses",
        "CompletionResult", "CreativeRealEstatePlotManager", "DifficultyIncreaseRewards",
        "DifficultyIncreaseRewardTier", "ExplicitGloballyBlockedAbilityTags", "GameDifficulty",
        "GameplayVotesArray", "GlobalEnvironmentAbilityActor", "HostilityMeterPercent", "IntensityPercent",
        "MaxEncounterAI", "MaxEncounterSP", "MaxPlayerStructures", "MaxTotalAI", "MissionAlertData",
        "MissionGeneratorClass", "MissionLogDebugString", "MissionRewards",
        "NotifyEndFailedGameplayVoteLockout", "NumSurvivorsRescued", "OnWaveBasedModifiersAppliedMulticast",
        "OnWaveStart", "PlayerBuildingSkillLevel", "PlayerSharedMaxTrapAttributes", "ReplicatedMontageMap",
        "ServerFireAIDirectorEvent", "ServerFireAIDirectorEventBatch", "ServerGameplayTagIndexHash",
        "SpawnPointsAllocated", "SpawnPointsCap", "TheaterUniqueId", "ThreatParticleActor",
        "ThreatVisualsManager", "TotalPlayerStructures", "UIMapManager", "WaitingToLeaveZoneTimeLeft",
        "ZoneDifficultyInfoRow", "ZoneTheme"
    );
    // /Script/FortniteGame.FortGameStatePvP - 0 own net fields (0 properties + 0 functions).
    private static readonly string[] FortGameStatePvPOwnFields = OwnFieldsSorted(
       
    );
    // /Script/FortniteGame.FortGameStateAthena - 80 own net fields (79 properties + 1 functions).
    private static readonly string[] FortGameStateAthenaOwnFields = OwnFieldsSorted(
        "ActiveTeamNums", "AirCraftBehavior", "Aircrafts", "AircraftStartTime", "bAircraftIsLocked",
        "bAllowUserPickedCosmeticBattleBus", "bCheatRespawnEnabled", "bGameModeWillSkipAircraft",
        "bIsInCountdown", "bIsInFinalCountdown", "bIsLargeTeamGame", "bPlaylistStoppedSafeZonePhases",
        "BroadcastSpectatorInfo", "bSafeZonePaused", "bSkyTubesDisabled", "bSkyTubesShuttingDown",
        "bStormReachedFinalPosition", "ClientVehicleClassesToLoad", "CurrentHighScore",
        "CurrentHighScoreTeam", "CurrentPlaylistId", "CurrentPlaylistInfo", "DamageForStormCapMarking",
        "DefaultAllowNeutralWallEditing", "DefaultBattleBus", "DefaultGliderRedeployCanRedeploy",
        "DefaultParachuteDeployTraceForGroundDistance", "DefaultRebootMachineHotfix",
        "DefaultRedeployGliderHeightLimit", "DefaultRedeployGliderLateralVelocityMult",
        "EndGameKickPlayerTime", "EndGameStartTime", "EventTournamentRound", "FlightPathMidLine",
        "FriendlyFireType", "GameMemberInfoArray", "GamePhase", "GoldenPoiLocationTags", "LobbyAction",
        "MapInfo", "MeshNetworkStatus", "MutatorEventData", "MutatorGenericInt_0", "MutatorGenericInt_1",
        "MutatorGenericInt_2", "MutatorObjectDataArray", "PlayerBotsLeft", "PlayersLeft",
        "PlaylistTimeRemaining", "ReplOverrideData", "RunPerfMemCheatScript_Client_Replicated",
        "SafeZoneIndicator", "SafeZonePhase", "SafeZonesStartTime", "ServerChangelistNumber",
        "ServerToClientPreloadList", "SignalInStormLostSpeed", "SignalInStormRegenSpeed",
        "SpawnMachineRepData", "SpecialActorData", "StormCapState", "StormCNDamageVulnerabilityLevel0",
        "StormCNDamageVulnerabilityLevel1", "StormCNDamageVulnerabilityLevel2",
        "StormCNDamageVulnerabilityLevel3", "SupplyDropWaveStartedSoundCue", "TeamFlightPaths", "TeamsLeft",
        "TeamXPlayersLeft", "TotalFinalCountdownTime", "TotalPlayers", "UtcTimeStartedMatch",
        "VolumeManager", "WarmupCountdownEndTime", "WarmupCountdownStartTime", "WinningPlayerList",
        "WinningPlayerState", "WinningScore", "WinningTeam", "WinningTeamsCN"
    );
    // ------------------------------------------------------------------------------------------
    // UFortAbilitySystemComponentAthena's chain - the first COMPONENT this project models.
    //
    // GENERATED by Tools/NetFieldVerify/gen_net_fields.py (Engine ActorComponent /
    // GameplayTasks GameplayTasksComponent / GameplayAbilities AbilitySystemComponent /
    // FortniteGame FortAbilitySystemComponent). FortAbilitySystemComponentAthena itself declares
    // NOTHING, so it is omitted - an empty link shifts no index.
    //
    // Total GetMaxIndex = 2 + 1 + 47 + 3 = 53, i.e. a 6-bit field index. CONFIRMED on the wire
    // against a Project-Reboot-3.0 capture: a ServerSetReplicatedTargetData sub-object block
    // measured 2879 bits = 6 (index 45) + 16 (packed length) + 2857 (payload), with no slack.
    // Live-observed indices from that capture: ActivatableAbilities=3, ServerAbilityRPCBatch=32,
    // ServerEndAbility=38, ServerSetReplicatedTargetData=45, ServerTryActivateAbility=47.
    // ------------------------------------------------------------------------------------------
    // /Script/Engine.ActorComponent - 2 own net fields (2 properties + 0 functions).
    private static readonly string[] ActorComponentOwnFields = OwnFieldsSorted(
        "bIsActive", "bReplicates"
    );

    // /Script/GameplayTasks.GameplayTasksComponent - 1 own net fields (1 properties + 0 functions).
    private static readonly string[] GameplayTasksComponentOwnFields = OwnFieldsSorted(
        "SimulatedTasks"
    );

    // /Script/GameplayAbilities.AbilitySystemComponent - 47 own net fields (13 properties + 34 functions).
    private static readonly string[] AbilitySystemComponentOwnFields = OwnFieldsSorted(
        "ActivatableAbilities", "ActiveGameplayCues", "ActiveGameplayEffects", "AvatarActor",
        "BlockedAbilityBindings", "ClientActivateAbilityFailed", "ClientActivateAbilitySucceed",
        "ClientActivateAbilitySucceedWithEventData", "ClientCancelAbility", "ClientDebugStrings",
        "ClientEndAbility", "ClientPrintDebug_Response", "ClientSetReplicatedEvent",
        "ClientTryActivateAbility", "MinimalReplicationGameplayCues", "MinimalReplicationTags",
        "NetMulticast_InvokeGameplayCueAdded", "NetMulticast_InvokeGameplayCueAdded_WithParams",
        "NetMulticast_InvokeGameplayCueAddedAndWhileActive_FromSpec",
        "NetMulticast_InvokeGameplayCueAddedAndWhileActive_WithParams",
        "NetMulticast_InvokeGameplayCueExecuted", "NetMulticast_InvokeGameplayCueExecuted_FromSpec",
        "NetMulticast_InvokeGameplayCueExecuted_WithParams",
        "NetMulticast_InvokeGameplayCuesAddedAndWhileActive_WithParams",
        "NetMulticast_InvokeGameplayCuesExecuted", "NetMulticast_InvokeGameplayCuesExecuted_WithParams",
        "OwnerActor", "RepAnimMontageInfo", "ReplicatedPredictionKeyMap", "ServerAbilityRPCBatch",
        "ServerCancelAbility", "ServerCurrentMontageJumpToSectionName",
        "ServerCurrentMontageSetNextSectionName", "ServerCurrentMontageSetPlayRate", "ServerDebugStrings",
        "ServerEndAbility", "ServerPrintDebug_Request", "ServerPrintDebug_RequestWithStrings",
        "ServerSetInputPressed", "ServerSetInputReleased", "ServerSetReplicatedEvent",
        "ServerSetReplicatedEventWithPayload", "ServerSetReplicatedTargetData",
        "ServerSetReplicatedTargetDataCancelled", "ServerTryActivateAbility",
        "ServerTryActivateAbilityWithEventData", "SpawnedAttributes"
    );

    // /Script/FortniteGame.FortAbilitySystemComponent - 3 own net fields (2 properties + 1 functions).
    private static readonly string[] FortAbilitySystemComponentOwnFields = OwnFieldsSorted(
        "LandingMontagePair", "NetMulticast_RefreshActiveGameplayEffectCueEvents", "RepSharedAnimInfo"
    );

    private static readonly FClassNetCache ActorCache = new(null, ActorOwnFields);
    private static readonly FClassNetCache FortInventoryCache = new(ActorCache, FortInventoryOwnFields);
    private static readonly FClassNetCache FortBroadcastRemoteClientInfoCache = new(ActorCache, FortBroadcastRemoteClientInfoOwnFields);

    // Components hang off UObject, not AActor - ActorCache is not in this chain at all.
    private static readonly FClassNetCache ActorComponentCache = new(null, ActorComponentOwnFields);
    private static readonly FClassNetCache GameplayTasksComponentCache = new(ActorComponentCache, GameplayTasksComponentOwnFields);
    private static readonly FClassNetCache AbilitySystemComponentCache = new(GameplayTasksComponentCache, AbilitySystemComponentOwnFields);
    public static readonly FClassNetCache FortAbilitySystemComponentCache = new(AbilitySystemComponentCache, FortAbilitySystemComponentOwnFields);
    private static readonly FClassNetCache ControllerCache = new(ActorCache, ControllerOwnFields);
    private static readonly FClassNetCache PlayerControllerBaseCache = new(ControllerCache, PlayerControllerOwnFields);
    private static readonly FClassNetCache FortPlayerControllerCache = new(PlayerControllerBaseCache, FortPlayerControllerOwnFields);
    private static readonly FClassNetCache FortPlayerControllerGameplayCache = new(FortPlayerControllerCache, FortPlayerControllerGameplayOwnFields);
    private static readonly FClassNetCache FortPlayerControllerZoneCache = new(FortPlayerControllerGameplayCache, FortPlayerControllerZoneOwnFields);
    private static readonly FClassNetCache FortPlayerControllerPvPCache = new(FortPlayerControllerZoneCache, FortPlayerControllerPvPOwnFields);
    private static readonly FClassNetCache PlayerControllerCache = new(FortPlayerControllerPvPCache, FortPlayerControllerAthenaOwnFields);
    private static readonly FClassNetCache PawnBaseCache = new(ActorCache, PawnOwnFields);
    private static readonly FClassNetCache CharacterCache = new(PawnBaseCache, CharacterOwnFields);
    private static readonly FClassNetCache FortPawnCache = new(CharacterCache, FortPawnOwnFields);
    private static readonly FClassNetCache FortPlayerPawnCache = new(FortPawnCache, FortPlayerPawnOwnFields);
    private static readonly FClassNetCache FortPlayerPawnAthenaCache = new(FortPlayerPawnCache, FortPlayerPawnAthenaOwnFields);
    private static readonly FClassNetCache PawnCache = new(FortPlayerPawnAthenaCache, PlayerPawnAthenaOwnFields);

    private static readonly FClassNetCache GameStateBaseCache = new(ActorCache, GameStateBaseOwnFields);
    private static readonly FClassNetCache GameStateEngineCache = new(GameStateBaseCache, GameStateOwnFields);
    private static readonly FClassNetCache FortGameStateBaseCache = new(GameStateEngineCache, FortGameStateBaseOwnFields);
    private static readonly FClassNetCache FortGameStateCache = new(FortGameStateBaseCache, FortGameStateOwnFields);
    private static readonly FClassNetCache FortGameStateInGameCache = new(FortGameStateCache, FortGameState_InGameOwnFields);
    private static readonly FClassNetCache FortGameStateZoneCache = new(FortGameStateInGameCache, FortGameStateZoneOwnFields);
    private static readonly FClassNetCache FortGameStatePvPCache = new(FortGameStateZoneCache, FortGameStatePvPOwnFields);
    private static readonly FClassNetCache GameStateCache = new(FortGameStatePvPCache, FortGameStateAthenaOwnFields);

    private static readonly FClassNetCache PlayerStateBaseCache = new(ActorCache, PlayerStateOwnFields);
    private static readonly FClassNetCache FortPlayerStateCache = new(PlayerStateBaseCache, FortPlayerStateOwnFields);
    private static readonly FClassNetCache FortPlayerStateZoneCache = new(FortPlayerStateCache, FortPlayerStateZoneOwnFields);
    private static readonly FClassNetCache PlayerStateCache = new(FortPlayerStateZoneCache, FortPlayerStateAthenaOwnFields);

    /// <summary>
    ///     AFortWeapon is the one actor whose cache cannot live here: its real class is a Blueprint
    ///     chosen per item, so its field count - and therefore the BIT WIDTH of every FieldNetIndex
    ///     on its channel - differs between an assault rifle and a pickaxe. See FortWeaponNetCaches,
    ///     which builds one chain per weapon class from a table generated off an IN-MATCH Dumper-7
    ///     dump.
    ///
    ///     Until 2026-08-27 this fell through to ActorCache with a comment explaining that the
    ///     weapon Blueprints could not be ground-truthed, because every dump had been taken in the
    ///     lobby where none of them are loaded. Dumping again from inside a match settled it - and
    ///     also turned the "PlayerPawn_Athena_Generic_C/_Parent_C add no NetFields" note below from
    ///     an assumption into a checked fact (both really are empty).
    /// </summary>
    public static FClassNetCache Get(AActor actor) => actor switch {
        AFortWeapon weapon => FortWeaponNetCaches.For(weapon, ActorCache),
        AFortInventory => FortInventoryCache,
        AFortBroadcastRemoteClientInfo => FortBroadcastRemoteClientInfoCache,
        AGameState => GameStateCache,
        APlayerController => PlayerControllerCache,
        AController => ControllerCache,
        APawn => PawnCache,
        APlayerState => PlayerStateCache,
        _ => ActorCache
    };
}
