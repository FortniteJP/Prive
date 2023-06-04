namespace Prive.Server.Http.CloudStorage;

public class DefaultRuntimeOptions : CloudStorageFile {
    public override string Filename => "DefaultRuntimeOptions.ini";
    public override DateTime LastModified { get; } = DateTime.UtcNow;

    public override List<IniElementSection> Elements => new() {
        new() {
            Section = "/Script/FortniteGame.FortRuntimeOptions",
            Elements = new() {
                new IniElementKeyValue("bEnableBlockedList", "false"),
                new IniElementKeyValue("bEnableNickname", "false"),
                new IniElementKeyValue("bEnableEULA", "false"),
                new IniElementKeyValue("bEnableHiddenMatchmakingDelay", "false"),
                new IniElementKeyValue("bShouldSkipAvailabilityCheck", "false"),
                new IniElementKeyValue("bEnableClientSettingsSaveToDisk", "true"),
                new IniElementKeyValue("bEnableClientSettingsSaveToCloud", "false"),
                
                new IniElementKeyValue("bEnableSidekick", "false"),
                new IniElementKeyValue("bEnableSidekickFOMO", "false"),
                new IniElementKeyValue("bEnableSidekickAvatars", "false"),
                new IniElementKeyValue("bSidekickEnableExitFNButton", "false"),
                new IniElementKeyValue("bEnableSidekickFaceAreaInvalidation", "false"),

                new IniElementKeyValue("bSkipInternetCheck", "true"),
                new IniElementKeyValue("bLoginErebusDisabled", "true"),
                new IniElementKeyValue("bLoginXBLDisabled", "true"),
                new IniElementKeyValue("bLoginPSNDisabled", "true"),
                new IniElementKeyValue("bLoginEpicWeb", "true"),

                new IniElementKeyValue("ExperimentalCohortPercent", "ClearArray") { Option = IniElementOption.RemoveIfExisting },
            }
        }
    };
}

/*
from v10.40
class UFortRuntimeOptions : public URuntimeOptionsBase
{
	public:
	    TArray<struct FExperimentalCohortPercent> ExperimentalCohortPercent; // 0x38 Size: 0x10
	    struct FString YoutubeVideoPrefix; // 0x48 Size: 0x10
	    struct FString YoutubeVideoSuffix; // 0x58 Size: 0x10
	    bool bEnableSpectatorUpdates; // 0x68 Size: 0x1
	    bool bIsTournamentMode; // 0x69 Size: 0x1
	    bool bUseTournamentAnonymousOverrideEnabled; // 0x6a Size: 0x1
	    bool bEnableYoutubeLinks; // 0x6b Size: 0x1
	    bool bAllowLoadoutSwitchingInLobby; // 0x6c Size: 0x1
	    char UnknownData0[0x3]; // 0x6d
	    FName TournamentPlaylistName; // 0x70 Size: 0x8
	    int TournamentPlaylistPriorityBase; // 0x78 Size: 0x4
	    float TournamentModeQueueInterval; // 0x7c Size: 0x4
	    int MinimumAccountLevelForTournamentPlay; // 0x80 Size: 0x4
	    bool bEnableManualBroadcasterStart; // 0x84 Size: 0x1
	    bool bCreativeManualBroadcasterStart; // 0x85 Size: 0x1
	    bool bAutoloadRestrictedPlots; // 0x86 Size: 0x1
	    bool bDisableMyIslandDescriptionPanel; // 0x87 Size: 0x1
	    bool bEnableAllRemoteClientInfos; // 0x88 Size: 0x1
	    bool bEnableBuildPreviewForBroadcast; // 0x89 Size: 0x1
	    bool bEnableRemoteAimSnapshotManagerForBroadcast; // 0x8a Size: 0x1
	    bool bShowSquadOnSpectatorPlayerStatusWidget; // 0x8b Size: 0x1
	    float EsportsAnalyticsHeartbeatRate; // 0x8c Size: 0x4
	    bool bUseBroadcastPostProcessing; // 0x90 Size: 0x1
	    bool bUseBroadcastKillFeed; // 0x91 Size: 0x1
	    bool bClientReplayUsesBroadcastHUD; // 0x92 Size: 0x1
	    bool bUseServerReplayActionFeed; // 0x93 Size: 0x1
	    bool bReplayGoToTimeEnabled; // 0x94 Size: 0x1
	    bool bBroadcastPipModeToggle; // 0x95 Size: 0x1
	    bool bShowBroadcastPlayerEventScoreWidget; // 0x96 Size: 0x1
	    bool bBroadcastActivePlayerGridScreenEnabled; // 0x97 Size: 0x1
	    bool bBroadcastEliminatedPlayerGridScreenEnabled; // 0x98 Size: 0x1
	    bool bBroadcastMatchStatusScreenEnabled; // 0x99 Size: 0x1
	    bool bBroadcastScoreboardScreenEnabled; // 0x9a Size: 0x1
	    bool bUseOutsideTopThreeSpectatorLeaderboard; // 0x9b Size: 0x1
	    bool bReplayPauseZeroDeltas; // 0x9c Size: 0x1
	    char UnknownData1[0x3]; // 0x9d
	    int CurrentSocialImportVersion; // 0xa0 Size: 0x4
	    int CurrentVKImportVersion; // 0xa4 Size: 0x4
	    bool CurrentMultiFactorModalVersion; // 0xa8 Size: 0x1
	    bool bEnableMassFriendImport; // 0xa9 Size: 0x1
	    char UnknownData2[0x2]; // 0xaa
	    int NumDaysBeforeFailedImportReattempt; // 0xac Size: 0x4
	    bool bEnableSocialImport; // 0xb0 Size: 0x1
	    bool bEnableSocialBanModal; // 0xb1 Size: 0x1
	    bool bEnableStartupSocialImport; // 0xb2 Size: 0x1
	    bool bEnableStartupErebusFriendImport; // 0xb3 Size: 0x1
	    bool bEnableVKImport; // 0xb4 Size: 0x1
	    bool bEnableSteamImport; // 0xb5 Size: 0x1
	    char UnknownData3[0x2]; // 0xb6
	    struct FString SocialImportURI; // 0xb8 Size: 0x10
	    int DaysBetweenSocialImportPrompts; // 0xc8 Size: 0x4
	    int DaysBetweenVKImportPrompt; // 0xcc Size: 0x4
	    int SocialImportPromptsBeforeOptOut; // 0xd0 Size: 0x4
	    int FriendImportCaptionSelection; // 0xd4 Size: 0x4
	    bool bEnableSplitWalletTextNotice; // 0xd8 Size: 0x1
	    bool bShowAthenaStoreToast; // 0xd9 Size: 0x1
	    bool bShowAthenaStoreToastForFeatured; // 0xda Size: 0x1
	    bool bShowAthenaStoreToastForDaily; // 0xdb Size: 0x1
	    bool bShowAthenaStoreToastForRolloverAlone; // 0xdc Size: 0x1
	    bool bShowAthenaStarsInStoreNotification; // 0xdd Size: 0x1
	    char UnknownData4[0x2]; // 0xde
	    TArray<FName> AthenaStarterGameMode; // 0xe0 Size: 0x10
	    TArray<FName> AthenaStarterGameModeB; // 0xf0 Size: 0x10
	    bool AthenaStarterFill; // 0x100 Size: 0x1
	    char UnknownData5[0x3]; // 0x101
	    float PartyRichPresenceUpdateTime; // 0x104 Size: 0x4
	    float PartySuggestionUpdateTimer; // 0x108 Size: 0x4
	    int MaxPartySuggestionsToConsider; // 0x10c Size: 0x4
	    bool bAllowPartySuggestions; // 0x110 Size: 0x1
	    bool bAllowLFG; // 0x111 Size: 0x1
	    bool bAllowPartyPresenceUpdates; // 0x112 Size: 0x1
	    bool bAllowGameplayPresenceUpdates; // 0x113 Size: 0x1
	    bool bEnablePlaylistNameInRichPresence; // 0x114 Size: 0x1
	    bool bEnableInteractiveConsumables; // 0x115 Size: 0x1
	    bool bEnableContextHelpMenu; // 0x116 Size: 0x1
	    bool bShowAthenaItemShop; // 0x117 Size: 0x1
	    bool bEnableShowdown; // 0x118 Size: 0x1
	    bool bEnableTournamentMatchCaps; // 0x119 Size: 0x1
	    bool bUsePlayingEventIds; // 0x11a Size: 0x1
	    bool bUsePartyRepDataForMatchmakingWidget; // 0x11b Size: 0x1
	    bool bRetryCMSLoads; // 0x11c Size: 0x1
	    char UnknownData6[0x3]; // 0x11d
	    float RefreshScoreDelay; // 0x120 Size: 0x4
	    bool bAlwaysForceTournamentLobbyPanelRefresh; // 0x124 Size: 0x1
	    bool bEnableEventLeaderboards; // 0x125 Size: 0x1
	    char UnknownData7[0x2]; // 0x126
	    int NumCachedLeaderboardPages; // 0x128 Size: 0x4
	    int MaxPagesPerLeaderboard; // 0x12c Size: 0x4
	    int EventLeaderboardLiveRefreshTimeSeconds; // 0x130 Size: 0x4
	    int EventLeaderboardLivePostEventRefreshWindowMinutes; // 0x134 Size: 0x4
	    bool bGetLiveSessionsFromLeaderboards; // 0x138 Size: 0x1
	    bool bUseServerTournamentPlacementNotifications; // 0x139 Size: 0x1
	    char UnknownData8[0x2]; // 0x13a
	    int MaximumEventLengthHoursForCallout; // 0x13c Size: 0x4
	    __int64 MapProperty DivisionPlayerRatingRequirement; // 0x140 Size: 0x50
	    FName CreativePlaylistName; // 0x190 Size: 0x8
	    bool bEnableEventScoreClamping; // 0x198 Size: 0x1
	    char UnknownData9[0x3]; // 0x199
	    int CreativeDisabledTabIndex; // 0x19c Size: 0x4
	    bool bEnableCreativeServerImportFriendsOption; // 0x1a0 Size: 0x1
	    char UnknownData10[0x3]; // 0x1a1
	    int MaxPlayersInCreativeServer; // 0x1a4 Size: 0x4
	    int MaxPlayersInCreativeWhitelist; // 0x1a8 Size: 0x4
	    bool bShowSupportACreatorOnIslandLinkScreen; // 0x1ac Size: 0x1
	    bool bHideServersWithZeroPlayers; // 0x1ad Size: 0x1
	    bool bEnableIslandCodeEntryOnPlayerPortal; // 0x1ae Size: 0x1
	    bool bEnableIslandCodeEntryOnCuratedPortal; // 0x1af Size: 0x1
	    bool bEnableIslandCodeEntryInFrontend; // 0x1b0 Size: 0x1
	    char UnknownData11[0x3]; // 0x1b1
	    float RefreshFavoriteIslandsWaitTime; // 0x1b4 Size: 0x4
	    int IslandCodeLength; // 0x1b8 Size: 0x4
	    bool bApplyCodeFormatting; // 0x1bc Size: 0x1
	    bool bEnableJoinInProgress; // 0x1bd Size: 0x1
	    bool bEnableBattlePass; // 0x1be Size: 0x1
	    bool bEnableBattlePassPurchase; // 0x1bf Size: 0x1
	    bool bEnableBattlePassTokenClaim; // 0x1c0 Size: 0x1
	    bool bEnableBattlePassFAQ; // 0x1c1 Size: 0x1
	    bool bEnableAthenaFavoriting; // 0x1c2 Size: 0x1
	    bool bEnableAthenaCustomPreviewActionForCosmetics; // 0x1c3 Size: 0x1
	    bool bEnableAthenaItemRandomization; // 0x1c4 Size: 0x1
	    bool bEnableProfileStatTracking; // 0x1c5 Size: 0x1
	    bool bEnableProfileStatUI; // 0x1c6 Size: 0x1
	    bool bEnableTrickUI; // 0x1c7 Size: 0x1
	    bool bEnableMultiplayerTricks; // 0x1c8 Size: 0x1
	    bool bShowAthenaChallengesTabWhenOutOfSeason; // 0x1c9 Size: 0x1
	    bool bEnableInGameChallengeTree; // 0x1ca Size: 0x1
	    bool bCreateEpicAccountPinGrantDisabled; // 0x1cb Size: 0x1
	    bool bLoginEpicWeb; // 0x1cc Size: 0x1
	    bool bLoginXBLDisabled; // 0x1cd Size: 0x1
	    bool bLoginPSNDisabled; // 0x1ce Size: 0x1
	    bool bLoginErebusDisabled; // 0x1cf Size: 0x1
	    bool bSkipInternetCheck; // 0x1d0 Size: 0x1
	    bool bEnableClientSettingsSaveToCloud; // 0x1d1 Size: 0x1
	    bool bEnableClientSettingsSaveToDisk; // 0x1d2 Size: 0x1
	    char UnknownData12[0x1]; // 0x1d3
	    int bDedServerEventServiceDownloadTryCount; // 0x1d4 Size: 0x4
	    int TournamentRefreshPayoutMaxRateSeconds; // 0x1d8 Size: 0x4
	    int TournamentRefreshEventsMaxRateSeconds; // 0x1dc Size: 0x4
	    int TournamentRefreshPlayerMaxRateSeconds; // 0x1e0 Size: 0x4
	    int ShowdownTournamentCacheExpirationHours; // 0x1e4 Size: 0x4
	    int ShowdownTournamentJsonRevision; // 0x1e8 Size: 0x4
	    float TournamentHUDPointCounterDelay; // 0x1ec Size: 0x4
	    int MaxNumDisplayNamesOnLiveGameList; // 0x1f0 Size: 0x4
	    int LiveGameListInitialLimit; // 0x1f4 Size: 0x4
	    int LiveGameListQueryIncreaseAmount; // 0x1f8 Size: 0x4
	    bool bEnableLiveGamesScreen; // 0x1fc Size: 0x1
	    bool bLiveGameTimeDurationVisible; // 0x1fd Size: 0x1
	    bool bEnableFlagSelection; // 0x1fe Size: 0x1
	    char UnknownData13[0x1]; // 0x1ff
	    struct FString DefaultFlagRegionId; // 0x200 Size: 0x10
	    struct FString MixedNationTeamFlagRegionId; // 0x210 Size: 0x10
	    TArray<struct FString> DisabledFlagSelections; // 0x220 Size: 0x10
	    int FlagChangeCooldownDays; // 0x230 Size: 0x4
	    bool bEnableEventServicePayouts; // 0x234 Size: 0x1
	    bool bLiveGamesClientAnalyticsEnabled; // 0x235 Size: 0x1
	    char UnknownData14[0x2]; // 0x236
	    float MinimumWaitTimeToRequestNewShowdownScoreForWindow; // 0x238 Size: 0x4
	    int EventServicePayoutRefreshRateSeconds; // 0x23c Size: 0x4
	    int EventServicePayoutRefreshSpreadSeconds; // 0x240 Size: 0x4
	    int NumberOfBattlePassTiersToDisplayPerGroup; // 0x244 Size: 0x4
	    TArray<struct FString> CancelledEvents; // 0x248 Size: 0x10
	    int SecondsShowStartingMatchMessageForScheduledMMEvents; // 0x258 Size: 0x4
	    bool bEnableMatchAbandonProcess; // 0x25c Size: 0x1
	    char UnknownData15[0x3]; // 0x25d
	    float MatchAbandonTimeout; // 0x260 Size: 0x4
	    char UnknownData16[0x4]; // 0x264
	    double CloudSaveIntervalConfig; // 0x268 Size: 0x8
	    bool bSaveToCloudOnMapLoad; // 0x270 Size: 0x1
	    char UnknownData17[0x7]; // 0x271
	    double GiftNotificationRefreshTimer; // 0x278 Size: 0x8
	    bool bEnableUndoPurchase; // 0x280 Size: 0x1
	    bool bMoveUndoToBottomBar; // 0x281 Size: 0x1
	    bool bEnableReplayBrowser; // 0x282 Size: 0x1
	    char UnknownData18[0x5]; // 0x283
	    TArray<bool> bShowFeaturedReplays; // 0x288 Size: 0x10
	    TArray<uint32_t> WhitelistedReplayCLs; // 0x298 Size: 0x10
	    bool bAllowAllReplays; // 0x2a8 Size: 0x1
	    bool bEnableReplayRecording; // 0x2a9 Size: 0x1
	    bool bEnableLargeTeamReplayRecording; // 0x2aa Size: 0x1
	    bool bEnableCreativeModeReplayRecording; // 0x2ab Size: 0x1
	    bool bEnablePlaygroundModeReplayRecording; // 0x2ac Size: 0x1
	    bool bUsingEsportCameras; // 0x2ad Size: 0x1
	    bool bStableReplayPlayback; // 0x2ae Size: 0x1
	    bool bEnableHearingAccessibility; // 0x2af Size: 0x1
	    bool bDisableSpatializationInsteadOfMutingWhenHearingAccessibilityEnabled; // 0x2b0 Size: 0x1
	    bool bDisableGiftXMPPMessageSend; // 0x2b1 Size: 0x1
	    bool bDisableReceiveGiftXMPPMessages; // 0x2b2 Size: 0x1
	    bool bDisableGifting; // 0x2b3 Size: 0x1
	    bool bEnableGiftEligibilityCheck; // 0x2b4 Size: 0x1
	    bool bForceRestrictChat; // 0x2b5 Size: 0x1
	    bool bDisableReceiveGiftOption; // 0x2b6 Size: 0x1
	    bool bLimitGiftingToEligiblePlatforms; // 0x2b7 Size: 0x1
	    bool bCanGiftYourself; // 0x2b8 Size: 0x1
	    char UnknownData19[0x3]; // 0x2b9
	    int GiftLimitAmount; // 0x2bc Size: 0x4
	    int DaysOfFriendshipBeforeCanGift; // 0x2c0 Size: 0x4
	    bool bBattlePassGiftingEmergencyDisable; // 0x2c4 Size: 0x1
	    bool bEnableBattlePassGiftingButton; // 0x2c5 Size: 0x1
	    bool bEnableBattlePassGiftingButtonTokenOnly; // 0x2c6 Size: 0x1
	    bool bShowBPGiftBoxPopup; // 0x2c7 Size: 0x1
	    float EndBattleRoyalUpdateDelay; // 0x2c8 Size: 0x4
	    float LightswitchDownLoginDelay; // 0x2cc Size: 0x4
	    bool bShowStatusButtonOnWaitingRoomScreen; // 0x2d0 Size: 0x1
	    bool bInvertMotionOnUnattachedSwitchControllers; // 0x2d1 Size: 0x1
	    bool bDisableTouchLookVelocityScaling; // 0x2d2 Size: 0x1
	    bool bDisablePurchaseHistoryScreen; // 0x2d3 Size: 0x1
	    bool bAllowProcessedPayoutsToRefreshProfile; // 0x2d4 Size: 0x1
	    char UnknownData20[0x3]; // 0x2d5
	    float TouchAimAssistStrengthScalar; // 0x2d8 Size: 0x4
	    bool bDisableTouchAimAssistAutoTracking; // 0x2dc Size: 0x1
	    bool bProcessGamepadInputOnMobile; // 0x2dd Size: 0x1
	    bool bMobileForceGamepadHUDWhenAttached; // 0x2de Size: 0x1
	    char UnknownData21[0x1]; // 0x2df
	    float GamepadShortThrowLookScale; // 0x2e0 Size: 0x4
	    ECrucibleWhitelistOverride CrucibleWhitelistOverride; // 0x2e4 Size: 0x1
	    bool bDisableCrucibleStatUpload; // 0x2e5 Size: 0x1
	    bool bDisableCrucibleStatDownload; // 0x2e6 Size: 0x1
	    bool bDisableCrucibleGlobalLeaderboards; // 0x2e7 Size: 0x1
	    bool bDisableCrucibleFriendLeaderboards; // 0x2e8 Size: 0x1
	    bool bDisableCrucibleAnalyticsEvents; // 0x2e9 Size: 0x1
	    bool bDisableCrucibleDestroyDeadBots; // 0x2ea Size: 0x1
	    bool bDisableCrucibleForcedGC; // 0x2eb Size: 0x1
	    bool bDisableCrucibleLeaderboardFilterText; // 0x2ec Size: 0x1
	    bool bDisableCrucibleLeaderboardSwitching; // 0x2ed Size: 0x1
	    bool bCrucibleLockToPlatform; // 0x2ee Size: 0x1
	    bool bCrucibleSendStatsEndOfSession; // 0x2ef Size: 0x1
	    bool bCrucibleSendStatsEndOfSessionOnShutdownEvent; // 0x2f0 Size: 0x1
	    char UnknownData22[0x3]; // 0x2f1
	    int CrucibleMinValidStatScoreMilliseconds; // 0x2f4 Size: 0x4
	    int CrucibleLeaderboardFriendQueryMaxSize; // 0x2f8 Size: 0x4
	    bool bEnableFortLeaderboardHelperConsoleDisplayNameFallback; // 0x2fc Size: 0x1
	    bool bUseNativeQuickbar; // 0x2fd Size: 0x1
	    bool bSoundIndicatorsEnabledForTeammates; // 0x2fe Size: 0x1
	    bool bSoundIndicatorsPooled; // 0x2ff Size: 0x1
	    int SoundIndicatorMaxNum; // 0x300 Size: 0x4
	    bool bEquipFirstWeaponOnMobile; // 0x304 Size: 0x1
	    bool bClearLastFireOnAbilityFailed; // 0x305 Size: 0x1
	    char UnknownData23[0x2]; // 0x306
	    float ShowEliminationDistanceOver; // 0x308 Size: 0x4
	    float FadeOutTeamIndicatorsAfter; // 0x30c Size: 0x4
	    float MapIndicatorTouchClearDistance; // 0x310 Size: 0x4
	    struct FVector2D MapIndicatorOffset; // 0x314 Size: 0x8
	    float AthenaMapZoomMax; // 0x31c Size: 0x4
	    float BacchusMapIndicatorSizeMultiplier; // 0x320 Size: 0x4
	    float AthenaMapPanSpeedMultiplier; // 0x324 Size: 0x4
	    float AthenaMapZoomSpeedMultiplier; // 0x328 Size: 0x4
	    float WaitTimeBeforeShowingNewModeViolator; // 0x32c Size: 0x4
	    struct FRuntimeOptionLocalizableString FriendCodeShareWarningMessage; // 0x330 Size: 0x10
	    struct FRuntimeOptionLocalizableString PlatformPlayAllowedErrorMessage; // 0x340 Size: 0x10
	    bool bOnlyShareURLWithNoMessage; // 0x350 Size: 0x1
	    bool bExcludeURLInShareMessage; // 0x351 Size: 0x1
	    bool bShowCreateAccountOnRedirect; // 0x352 Size: 0x1
	    bool bDisableContextTutorial; // 0x353 Size: 0x1
	    bool bEnableABTestingForContextTutorial; // 0x354 Size: 0x1
	    char UnknownData24[0x3]; // 0x355
	    int bContextTutorialMinimumLevelOverride; // 0x358 Size: 0x4
	    char UnknownData25[0x4]; // 0x35c
	    struct FString AthenaCodeOfConductURL; // 0x360 Size: 0x10
	    struct FString KairosCommunityRulesURL; // 0x370 Size: 0x10
	    struct FString BacchusFriendCodeShareURL; // 0x380 Size: 0x10
	    struct FString CreateAccountUrl; // 0x390 Size: 0x10
	    struct FString LinkAccountURL; // 0x3a0 Size: 0x10
	    struct FString AccountMergeMoreInfoURL; // 0x3b0 Size: 0x10
	    struct FString SupportURL; // 0x3c0 Size: 0x10
	    struct FString WaitingListURL; // 0x3d0 Size: 0x10
	    struct FString CheckStatusURL; // 0x3e0 Size: 0x10
	    struct FString iOSAppStoreURL; // 0x3f0 Size: 0x10
	    struct FString TurnOnMfaURL; // 0x400 Size: 0x10
	    struct FString ArenaResetTime; // 0x410 Size: 0x10
	    struct FString ListOfCreatorsURL; // 0x420 Size: 0x10
	    bool bAllowCodeRedemptionInSubgameSelect; // 0x430 Size: 0x1
	    ENewsExternalURLMode BRUpdatesURLMode; // 0x431 Size: 0x1
	    char UnknownData26[0x6]; // 0x432
	    struct FString BRUpdatesURL; // 0x438 Size: 0x10
	    ENewsExternalURLMode STWUpdatesURLMode; // 0x448 Size: 0x1
	    char UnknownData27[0x7]; // 0x449
	    struct FString STWUpdatesURL; // 0x450 Size: 0x10
	    struct FString GiftingInfoURL; // 0x460 Size: 0x10
	    bool bEnableContentControls; // 0x470 Size: 0x1
	    char UnknownData28[0x7]; // 0x471
	    struct FString ContentControlsMoreInfoURL; // 0x478 Size: 0x10
	    struct FString ContentControlsForgotPinURL; // 0x488 Size: 0x10
	    struct FString ContentControlsVerifyEmailURL; // 0x498 Size: 0x10
	    bool bEnableContentControlsPlaytimeReporting; // 0x4a8 Size: 0x1
	    bool bEnableContentControlsPurchaseReporting; // 0x4a9 Size: 0x1
	    char UnknownData29[0x2]; // 0x4aa
	    int MaxNumItemsInCreativeChests; // 0x4ac Size: 0x4
	    int MaxStreamerMatchmakingDelay; // 0x4b0 Size: 0x4
	    bool bEnableHiddenMatchmakingDelay; // 0x4b4 Size: 0x1
	    char UnknownData30[0x3]; // 0x4b5
	    struct FString TencentStoreDetailsURL; // 0x4b8 Size: 0x10
	    int PSALoadingScreenPercentChance; // 0x4c8 Size: 0x4
	    char UnknownData31[0x4]; // 0x4cc
	    struct FString StwDownloadLauncherOption; // 0x4d0 Size: 0x10
	    struct FRuntimeOptionLocalizableString OverrideDefaultBonusXpEventTitleString; // 0x4e0 Size: 0x10
	    struct FRuntimeOptionLocalizableString XBLDisableText; // 0x4f0 Size: 0x10
	    struct FRuntimeOptionLocalizableString XBLPartyFinderPlatformHeaderText; // 0x500 Size: 0x10
	    struct FRuntimeOptionLocalizableString XBLPartyFinderMcpHeaderText; // 0x510 Size: 0x10
	    struct FRuntimeOptionLocalizableString PSNDisableText; // 0x520 Size: 0x10
	    struct FRuntimeOptionLocalizableString PSNPartyFinderPlatformHeaderText; // 0x530 Size: 0x10
	    struct FRuntimeOptionLocalizableString PSNPartyFinderMcpHeaderText; // 0x540 Size: 0x10
	    struct FRuntimeOptionLocalizableString SwitchDisableText; // 0x550 Size: 0x10
	    struct FRuntimeOptionReviewPromptCriteria ReviewPromptCriteria; // 0x560 Size: 0x14
	    char UnknownData32[0x4]; // 0x574
	    struct FRuntimeOptionLocalizableString AffiliateDescriptionText; // 0x578 Size: 0x10
	    struct FRuntimeOptionLocalizableString AffiliateErrorText; // 0x588 Size: 0x10
	    bool bDisableAllKnobs; // 0x598 Size: 0x1
	    bool bDisableAllGameplayMessages; // 0x599 Size: 0x1
	    bool bDisableMatchmakingKnobs; // 0x59a Size: 0x1
	    bool bDisableMinigameKnobs; // 0x59b Size: 0x1
	    bool bDisableGameOptionKnobs; // 0x59c Size: 0x1
	    bool bDisableAffiliateFeature; // 0x59d Size: 0x1
	    bool bUseHotfixedAffiliateNamesArray; // 0x59e Size: 0x1
	    bool bEnablePrerollLlamas; // 0x59f Size: 0x1
	    bool bEnableSubregionNetworkAccelerators; // 0x5a0 Size: 0x1
	    char UnknownData33[0x7]; // 0x5a1
	    TArray<struct FString> DisabledNetworkAcceleratedSubregions; // 0x5a8 Size: 0x10
	    TArray<struct FString> AdvertisedNetworkAcceleratedSubregions; // 0x5b8 Size: 0x10
	    TArray<struct FRuntimeOptionTabStateInfo> DisabledFrontendNavigationTabs; // 0x5c8 Size: 0x10
	    TArray<struct FRuntimeOptionTabStateInfo> TournamentDisabledFrontendNavigationTabs; // 0x5d8 Size: 0x10
	    TArray<struct FString> DisabledMatchmakingKnobs; // 0x5e8 Size: 0x10
	    TArray<struct FString> HiddenMatchmakingKnobs; // 0x5f8 Size: 0x10
	    TArray<struct FRuntimeOptionDisabledGameplayMessage> DisabledGameplayMessages; // 0x608 Size: 0x10
	    int NumGameplayMessageChannels; // 0x618 Size: 0x4
	    char UnknownData34[0x4]; // 0x61c
	    TArray<struct FString> AffiliateNames; // 0x620 Size: 0x10
	    TArray<struct FRuntimeOptionTournamentScoreThreshold> SoloTournamentScoreThresholds; // 0x630 Size: 0x10
	    TArray<struct FRuntimeOptionTournamentScoreThreshold> DuoTournamentScoreThresholds; // 0x640 Size: 0x10
	    TArray<struct FRuntimeOptionTournamentScoreThreshold> SquadsTournamentScoreThresholds; // 0x650 Size: 0x10
	    float PickingInteractDistance; // 0x660 Size: 0x4
	    float PickingInteractHighlightDistanceScaler; // 0x664 Size: 0x4
	    float PickingHighlightMovementUpdateDist; // 0x668 Size: 0x4
	    float PickingHighlightUpdateTime; // 0x66c Size: 0x4
	    float PickingTime; // 0x670 Size: 0x4
	    float AutoPickingInteractDistanceFactor; // 0x674 Size: 0x4
	    float AutoOpenDoorInputMagnitude; // 0x678 Size: 0x4
	    float AutoOpenDoorTraceDistance; // 0x67c Size: 0x4
	    bool bAutofireEnabled; // 0x680 Size: 0x1
	    bool bShowXPWidgets; // 0x681 Size: 0x1
	    bool bAutofireUsesComponent; // 0x682 Size: 0x1
	    bool bAutofireUsesAutoaimTarget; // 0x683 Size: 0x1
	    bool bHoldToFireOnAutofireTarget; // 0x684 Size: 0x1
	    char UnknownData35[0x3]; // 0x685
	    float DefaultAutofireRange; // 0x688 Size: 0x4
	    float AutofireExtraTrackingRange; // 0x68c Size: 0x4
	    bool bServerNetDriverAnalytics; // 0x690 Size: 0x1
	    bool bClientNetDriverAnalytics; // 0x691 Size: 0x1
	    bool bDisableReplicationGraph; // 0x692 Size: 0x1
	    char UnknownData36[0x1]; // 0x693
	    float BRServerMaxTickRate; // 0x694 Size: 0x4
	    float DoubleTapOnEndTouchTime; // 0x698 Size: 0x4
	    float DoubleTapOnStartTouchTime; // 0x69c Size: 0x4
	    float DoubleTapDistance; // 0x6a0 Size: 0x4
	    float SingleTapDistance; // 0x6a4 Size: 0x4
	    float TouchMoveStickRadius; // 0x6a8 Size: 0x4
	    float TouchMoveStickRadiusTargeting; // 0x6ac Size: 0x4
	    float TouchMoveStickRadiusScoped; // 0x6b0 Size: 0x4
	    float TouchMoveStickRadiusDriving; // 0x6b4 Size: 0x4
	    float AutorunLockZoneOffset; // 0x6b8 Size: 0x4
	    float AutorunLockZoneDelay; // 0x6bc Size: 0x4
	    float MoveOriginResetTime; // 0x6c0 Size: 0x4
	    float MoveOriginResetDistance; // 0x6c4 Size: 0x4
	    float MoveOriginFollowDistance; // 0x6c8 Size: 0x4
	    bool bDisableTouchLookInertia; // 0x6cc Size: 0x1
	    char UnknownData37[0x3]; // 0x6cd
	    float RotateInertiaMultiplier; // 0x6d0 Size: 0x4
	    float RotateInertiaMinTime; // 0x6d4 Size: 0x4
	    float RotateInertiaMinLength; // 0x6d8 Size: 0x4
	    float RotateInertiaMinMagnitude; // 0x6dc Size: 0x4
	    int RotateInertiaNumAveragedTouches; // 0x6e0 Size: 0x4
	    bool bTouchQuickbarTapToLockEnabled; // 0x6e4 Size: 0x1
	    bool bTouchInteractInUI; // 0x6e5 Size: 0x1
	    bool bEnableHUDLayoutTool; // 0x6e6 Size: 0x1
	    bool bEnableHUDLayoutCloudSave; // 0x6e7 Size: 0x1
	    bool bEnableHUDLayoutToolPanZoom; // 0x6e8 Size: 0x1
	    char UnknownData38[0x3]; // 0x6e9
	    float EnablePlayButtonTime; // 0x6ec Size: 0x4
	    float AthenaExternalRichPresenceDelayTimeSeconds; // 0x6f0 Size: 0x4
	    bool bEnableExternalPresenceAthenaPlayersRemain; // 0x6f4 Size: 0x1
	    char UnknownData39[0x3]; // 0x6f5
	    float MinimumTimeBetweenConsolePresenceUpdates; // 0x6f8 Size: 0x4
	    float MinimumTimeBetweenMCPPresenceUpdates; // 0x6fc Size: 0x4
	    float EnablePlayButtonTimePostError; // 0x700 Size: 0x4
	    bool bInviteUIDisabled; // 0x704 Size: 0x1
	    bool bDisableBacchusLogin; // 0x705 Size: 0x1
	    bool bEnableFriendSuggestions; // 0x706 Size: 0x1
	    bool bEnableFriendsListButton; // 0x707 Size: 0x1
	    bool bPrioritizeMcpInviteOverConsoleInvite; // 0x708 Size: 0x1
	    bool bForceDisableCrossplatformSquadFill; // 0x709 Size: 0x1
	    bool bRequireCrossplayOptIn; // 0x70a Size: 0x1
	    bool bUseAccountCrossplayPermissions; // 0x70b Size: 0x1
	    bool bSingleCrossplayOptInPrompt; // 0x70c Size: 0x1
	    bool bImmediatelyDisplayCrossplayOptIn_STW; // 0x70d Size: 0x1
	    bool bImmediatelyDisplayCrossplayOptIn_BR; // 0x70e Size: 0x1
	    bool bShowIconForSamePlatformPlayers; // 0x70f Size: 0x1
	    bool bObscuredPlatformIcons; // 0x710 Size: 0x1
	    bool bEnableChatWidget; // 0x711 Size: 0x1
	    bool bShowVoiceChatSettings; // 0x712 Size: 0x1
	    bool bLiveStreamVoiceEnabledServer; // 0x713 Size: 0x1
	    bool bEnableSupportCenter; // 0x714 Size: 0x1
	    bool bPartyInProgress; // 0x715 Size: 0x1
	    bool bShouldAthenaQueryRecentPlayers; // 0x716 Size: 0x1
	    bool bEnableOfflineFriendsList; // 0x717 Size: 0x1
	    bool bEnableRecentPlayerList; // 0x718 Size: 0x1
	    bool bEnableSuggestedFriendList; // 0x719 Size: 0x1
	    bool bEnableBlockedList; // 0x71a Size: 0x1
	    bool bEnableFriendListInGame; // 0x71b Size: 0x1
	    bool bPushJIPInfoToPlatformPresence; // 0x71c Size: 0x1
	    bool bEnableStWInZonePrivacyChange; // 0x71d Size: 0x1
	    bool bEnableSitoutOption; // 0x71e Size: 0x1
	    bool bEnableSitoutOption_STW; // 0x71f Size: 0x1
	    bool bShowAccountBoosts; // 0x720 Size: 0x1
	    bool bShowCustomerSupport; // 0x721 Size: 0x1
	    bool bEnableChannelChangePopup; // 0x722 Size: 0x1
	    bool bEnableVoiceSpeakerWidget; // 0x723 Size: 0x1
	    bool bEnableSpeakerWidgetZonePerfMode; // 0x724 Size: 0x1
	    bool bEnableSquadMemberVoiceVisuals; // 0x725 Size: 0x1
	    bool bShowVoiceIndicatorsWhileLoading; // 0x726 Size: 0x1
	    bool bEnableVoiceChannelSelectionUI; // 0x727 Size: 0x1
	    bool bEnableGlobalChat; // 0x728 Size: 0x1
	    bool bEnableAllTabInChat; // 0x729 Size: 0x1
	    bool bEnableEULA; // 0x72a Size: 0x1
	    bool bEnableEndOfZoneCinematic; // 0x72b Size: 0x1
	    bool bEnableOnboardingCinematics; // 0x72c Size: 0x1
	    bool bShowFounderBannerIcons; // 0x72d Size: 0x1
	    bool bShowCurrentRegionInLobby; // 0x72e Size: 0x1
	    bool bEnableFoundersDailyRewards; // 0x72f Size: 0x1
	    bool bEnableTwitchIntegration; // 0x730 Size: 0x1
	    bool bEnableMatchmakingRegionSetting; // 0x731 Size: 0x1
	    bool bEnableEulaRequiredTournaments; // 0x732 Size: 0x1
	    bool bEnableMFARequiredTournaments; // 0x733 Size: 0x1
	    bool bAllTournamentsRequireMFA; // 0x734 Size: 0x1
	    bool bSpectatorBroadcasterSkipMfaEulaCheck; // 0x735 Size: 0x1
	    bool bEnableNaviationToChat; // 0x736 Size: 0x1
	    bool bEnableLanguageSetting; // 0x737 Size: 0x1
	    bool bEnableFriendCodeSetting; // 0x738 Size: 0x1
	    bool bEnableEarlyAccessLoadingScreenBanner; // 0x739 Size: 0x1
	    bool bClientIgnoreIsTournamentCheck; // 0x73a Size: 0x1
	    char UnknownData40[0x1]; // 0x73b
	    int CampaignMatchEndRetryCount; // 0x73c Size: 0x4
	    bool bShopPurchaseConfirmation; // 0x740 Size: 0x1
	    bool bShopPurchaseConfirmationJapanPS4; // 0x741 Size: 0x1
	    bool bToyMessagingEnabled; // 0x742 Size: 0x1
	    bool bEnableToDMToFFDays; // 0x743 Size: 0x1
	    bool bEnableAthenaHarvestingToolsInSTW; // 0x744 Size: 0x1
	    bool bEnableAthenaDancesInSTW; // 0x745 Size: 0x1
	    bool bEnableAthenaOutfitsInSTW; // 0x746 Size: 0x1
	    bool bEnableAthenaBackpackInSTW; // 0x747 Size: 0x1
	    bool bEnableAthenaGliderInSTW; // 0x748 Size: 0x1
	    bool bEnableAthenaContrailInSTW; // 0x749 Size: 0x1
	    bool bEnableAthenaWrapsInSTW; // 0x74a Size: 0x1
	    bool bEnableAthenaVehicleWrapsInSTW; // 0x74b Size: 0x1
	    bool bEnableAthenaBannerInSTW; // 0x74c Size: 0x1
	    bool bEnableAthenaMusicInSTW; // 0x74d Size: 0x1
	    bool bEnableAthenaLoadingScreenInSTW; // 0x74e Size: 0x1
	    bool bEnableAthenaCharmsInSTW; // 0x74f Size: 0x1
	    bool bEnablePostMatchDancesInSTW; // 0x750 Size: 0x1
	    bool bAllowAccessToAllEmotesForTesting; // 0x751 Size: 0x1
	    bool bEnableCosmeticLockerInSTW; // 0x752 Size: 0x1
	    bool bEnableCosmeticItemShopInSTW; // 0x753 Size: 0x1
	    bool bRequireEmoteOwnershipInPIE; // 0x754 Size: 0x1
	    bool bEnableSTWLootDrops; // 0x755 Size: 0x1
	    bool bEnableSTWContainerItemCacheDrops; // 0x756 Size: 0x1
	    bool bEnableSTWEnemyItemCacheDrops; // 0x757 Size: 0x1
	    bool bEnableHoldToPickupUI; // 0x758 Size: 0x1
	    bool bSkipTrailerMovie; // 0x759 Size: 0x1
	    bool bAlwaysPlayTrailerMovie; // 0x75a Size: 0x1
	    bool bHideUnaffordableMtxPurchases; // 0x75b Size: 0x1
	    bool bHidePlusOnVbucksButton; // 0x75c Size: 0x1
	    bool bAllowXboxStwAccessDuringLiveStoreOutage; // 0x75d Size: 0x1
	    bool bShowReplayTrailerButton_Athena; // 0x75e Size: 0x1
	    bool bEnableAlterationModification; // 0x75f Size: 0x1
	    bool bEnableSchematicRarityUpgrade; // 0x760 Size: 0x1
	    bool bEnableMissionActivationVote; // 0x761 Size: 0x1
	    bool bEnableLtmRetrieveTheData; // 0x762 Size: 0x1
	    bool bEnableUpgradesVideos; // 0x763 Size: 0x1
	    bool bEnableExternalRichPresence; // 0x764 Size: 0x1
	    bool bShowEnableMFAModalAtStartupAthena; // 0x765 Size: 0x1
	    bool bShowEnableMFAModalAtStartupSTW; // 0x766 Size: 0x1
	    bool bEnableAIBuildingHitFX; // 0x767 Size: 0x1
	    int LevelToStartShowingMFAModal; // 0x768 Size: 0x4
	    int DaysBetweenEnableMFAModalPrompts; // 0x76c Size: 0x4
	    float DelayGiftButtonWhenMFANotEnabledSeconds; // 0x770 Size: 0x4
	    int LevelToAutoOpenBattlePassOnNewSeason; // 0x774 Size: 0x4
	    bool bForceBattlePassUpsell; // 0x778 Size: 0x1
	    bool bCanShowSTWUpsellInBR; // 0x779 Size: 0x1
	    bool bShowLeaderboardPrivacySettings; // 0x77a Size: 0x1
	    bool bEnableServerScoreboardLog; // 0x77b Size: 0x1
	    bool bEnableAsyncScoreboardFlush; // 0x77c Size: 0x1
	    bool bEnableInputBasedMatchmaking; // 0x77d Size: 0x1
	    bool bUsingAlternateMatchmakingModel; // 0x77e Size: 0x1
	    bool bNotifyBlockedInput; // 0x77f Size: 0x1
	    int NumberOfFramesBeforeWarnInputBlocked; // 0x780 Size: 0x4
	    bool bDisableVideoOptions; // 0x784 Size: 0x1
	    bool bEnablePlayerReportingFlow; // 0x785 Size: 0x1
	    bool bEnableNewBP; // 0x786 Size: 0x1
	    bool bEnableViewAllBattlePassRewards; // 0x787 Size: 0x1
	    bool bPurchaseBattlePassLevelsViewed; // 0x788 Size: 0x1
	    bool bDisplayOnlyBattlePassFAQ; // 0x789 Size: 0x1
	    bool bEnableBPVideo; // 0x78a Size: 0x1
	    bool bShowBPVideoUpsell; // 0x78b Size: 0x1
	    bool bEnableNewSettingsScreen; // 0x78c Size: 0x1
	    char UnknownData41[0x3]; // 0x78d
	    TArray<struct FString> HiddenSettings; // 0x790 Size: 0x10
	    bool bDisplayPlayerReportingRoles; // 0x7a0 Size: 0x1
	    bool bDisplayRelevantPlayersForPlayerReporting; // 0x7a1 Size: 0x1
	    bool bPreventMultipleReportsOfSamePlayer; // 0x7a2 Size: 0x1
	    bool bAllowReportingFeaturedIslands; // 0x7a3 Size: 0x1
	    bool bForceGamepadPlaytest; // 0x7a4 Size: 0x1
	    bool bForceGamepadXboxOne; // 0x7a5 Size: 0x1
	    bool bEnableNewFireModeSelection; // 0x7a6 Size: 0x1
	    bool bEnableAddFriendWhileSpectating; // 0x7a7 Size: 0x1
	    bool bEnableFriendLink; // 0x7a8 Size: 0x1
	    char UnknownData42[0x3]; // 0x7a9
	    float bPlatformChatToastDisplaySeconds; // 0x7ac Size: 0x4
	    struct FString FriendLinkURL; // 0x7b0 Size: 0x10
	    struct FString MFAEnableURL; // 0x7c0 Size: 0x10
	    bool bAllowForceTouchFire; // 0x7d0 Size: 0x1
	    char UnknownData43[0x3]; // 0x7d1
	    float VehicleSessionMinTimeUsed; // 0x7d4 Size: 0x4
	    float BalloonSessionMinTimeUsed; // 0x7d8 Size: 0x4
	    float UpdateBalloonDistanceEveryXSeconds; // 0x7dc Size: 0x4
	    float ZiplineSessionMinTimeUsed; // 0x7e0 Size: 0x4
	    float RebootChipExpirationTime; // 0x7e4 Size: 0x4
	    float RebootDirectiveDisplayTime; // 0x7e8 Size: 0x4
	    bool bRebootEnableInventoryDisplay; // 0x7ec Size: 0x1
	    char UnknownData44[0x3]; // 0x7ed
	    float UpdateZiplineDistanceEveryXSeconds; // 0x7f0 Size: 0x4
	    bool bUseHordeStormShield; // 0x7f4 Size: 0x1
	    char UnknownData45[0x3]; // 0x7f5
	    float HordeStormShieldStartingRadiusOverride; // 0x7f8 Size: 0x4
	    float HordeStormShieldEndingRadiusOverride; // 0x7fc Size: 0x4
	    float HordeStormShieldBreatherRadiusOverride; // 0x800 Size: 0x4
	    bool bUseHordeRespawnAtLastPawnLocation; // 0x804 Size: 0x1
	    bool bAllowHordePlayerTriggeredRespawn; // 0x805 Size: 0x1
	    char UnknownData46[0x2]; // 0x806
	    int MaxQuickScopeAimAssistPulls; // 0x808 Size: 0x4
	    float QuickScopeAimAssistPullWatchTime; // 0x80c Size: 0x4
	    float MaxQuickScopeAimAssistPullWatchTime; // 0x810 Size: 0x4
	    float QuickScopeAimAssistOriginalInitialDownsightStrength; // 0x814 Size: 0x4
	    bool bShouldDisablePickaxeFXFrontendPreview; // 0x818 Size: 0x1
	    bool bRegisterPawnsWithSignificanceManagerInFrontEnd; // 0x819 Size: 0x1
	    bool bHideExclusiveCosmeticsFromOtherPlatformsOnPS4; // 0x81a Size: 0x1
	    bool bHideExclusiveCosmeticsFromOtherPlatformsOnXB1; // 0x81b Size: 0x1
	    bool bHideExclusiveCosmeticsFromOtherPlatformsOnSwitch; // 0x81c Size: 0x1
	    bool bHideExclusiveCosmeticsFromOtherPlatformsOnPS4_STWOnly; // 0x81d Size: 0x1
	    bool bHideExclusiveCosmeticsFromOtherPlatformsOnXB1_STWOnly; // 0x81e Size: 0x1
	    bool bHideExclusiveCosmeticsFromOtherPlatformsOnSwitch_STWOnly; // 0x81f Size: 0x1
	    bool bSimpleHeistVanEntrance; // 0x820 Size: 0x1
	    char UnknownData47[0x7]; // 0x821
	    struct FString LobbyGenericLinkButtonURL; // 0x828 Size: 0x10
	    bool bEnableLobbyGenericLinkButton; // 0x838 Size: 0x1
	    char UnknownData48[0x7]; // 0x839
	    struct FRuntimeOptionLocalizableString LobbyGenericLinkButtonText; // 0x840 Size: 0x10
	    TArray<struct FString> PlaylistOptionWhitelist; // 0x850 Size: 0x10
	    int HighlightClipRewindTimeInSeconds; // 0x860 Size: 0x4
	    bool bEnableAntiTaxi; // 0x864 Size: 0x1
	    char UnknownData49[0x3]; // 0x865
	    float StopFlyingParachuteCooldownTime; // 0x868 Size: 0x4
	    float FlushLoadingScreenRefreshSeconds; // 0x86c Size: 0x4
	    bool bEnableVehicleSpawnMissionInStw; // 0x870 Size: 0x1
	    bool bEnableJackalInStw; // 0x871 Size: 0x1
	    bool bEnableDownTierCraftingInStw; // 0x872 Size: 0x1
	    bool bShowBugReportsButton; // 0x873 Size: 0x1
	    bool bShowCommentReportsButton; // 0x874 Size: 0x1
	    bool bShowPlayerReportsButton; // 0x875 Size: 0x1
	    bool bShowContentReportsButton; // 0x876 Size: 0x1
	    bool bEnableItemRefundingInStw; // 0x877 Size: 0x1
	    bool bDisableCareerStatsButton; // 0x878 Size: 0x1
	    bool bDisableCareerLeaderboardButton; // 0x879 Size: 0x1
	    bool bEnableInputMethodThrashingDetection; // 0x87a Size: 0x1
	    bool bDisableCareerStatsPagePlatformProfileButton; // 0x87b Size: 0x1
	    bool bUsePlatformSpecificTextOnCareerPage; // 0x87c Size: 0x1
	    bool bDisableViewOtherProfilesFromCompLeaderboards; // 0x87d Size: 0x1
	    bool bShowOtherPlayerStatsOnCareerPage; // 0x87e Size: 0x1
	    char UnknownData50[0x1]; // 0x87f
	    int InputMethodThrashingLimit; // 0x880 Size: 0x4
	    float InputMethodThrashingWindowInSeconds; // 0x884 Size: 0x4
	    bool bAllowPartialBackgroundAudio; // 0x888 Size: 0x1
	    bool bAllowPvEImprovedIdleDetection; // 0x889 Size: 0x1
	    bool bDuplicateRemovedPlayersOnClient; // 0x88a Size: 0x1
	    char UnknownData51[0x1]; // 0x88b
	    struct FPlayerMarkerConfig PlayerMarkerConfig; // 0x88c Size: 0x40
	    bool bIsCreativeMultiSelectEnabled; // 0x8cc Size: 0x1
	    bool bEnableUserProfilePictures; // 0x8cd Size: 0x1
	    bool bUseProfilePicturePresence; // 0x8ce Size: 0x1
	    bool bEnableChannelsServiceLoadTesting; // 0x8cf Size: 0x1
	    bool bAllowMimicingEmotes; // 0x8d0 Size: 0x1
	    bool bAllowMimicingEmotesInFrontend; // 0x8d1 Size: 0x1
	    bool bAllowAsyncTooltipLoading; // 0x8d2 Size: 0x1
	    bool bAllowListViewAsyncLoading; // 0x8d3 Size: 0x1
	    bool bEnableBackToPartyHubButton; // 0x8d4 Size: 0x1
	    char UnknownData52[0x3]; // 0x8d5
	    int PreloadRevision; // 0x8d8 Size: 0x4
	    bool bEnableLiveStoreTilePreviews; // 0x8dc Size: 0x1
	    bool bAllowedToEnableUIGlobalInvalidation; // 0x8dd Size: 0x1
	    bool AllowTimeWasterAtNight; // 0x8de Size: 0x1
	    char UnknownData53[0x1]; // 0x8df
	    int HotfixVersionId; // 0x8e0 Size: 0x4
	    float MaxBuildingIntoTerrainIntersectionPercentage; // 0x8e4 Size: 0x4
	    bool bUsingBuildingExtraPiece; // 0x8e8 Size: 0x1
	    char UnknownData54[0x3]; // 0x8e9
	    int AnalyticsBuildingWallTooLowLocations; // 0x8ec Size: 0x4
	    bool bDisableClientEngagementsAnalytics; // 0x8f0 Size: 0x1
	    char UnknownData55[0x3]; // 0x8f1
	    float AnalyticsClientEngagementsTimeoutSeconds; // 0x8f4 Size: 0x4
	    int AnalyticsClientEngagementsMaxSendPerMinute; // 0x8f8 Size: 0x4
	    int AnalyticsClientEngagementsMaxSendOnCleanup; // 0x8fc Size: 0x4
	    bool bAnalyticsClientEngagementsRequireTimeToReturnFireToSend; // 0x900 Size: 0x1
	    char UnknownData56[0x3]; // 0x901
	    int AnalyticsClientEngagementsParticipationPercent; // 0x904 Size: 0x4
	    bool PublishingEnabledForWhitelistedAccounts; // 0x908 Size: 0x1
	    char UnknownData57[0x7]; // 0x909
	    struct FString IslandCodeLinkMnemonicExampleText; // 0x910 Size: 0x10
	    struct FString IslandCodeLinkURLText; // 0x920 Size: 0x10
	    struct FString FeaturedCreativeLTMAffiliateName; // 0x930 Size: 0x10
	    bool bEnableCreativeLTMSupportCreator; // 0x940 Size: 0x1
	    char UnknownData58[0x7]; // 0x941
	    struct FString CreativePublishCodeURLPrefix; // 0x948 Size: 0x10
	    bool bCreativeMinimapRendering; // 0x958 Size: 0x1
	    bool bCreativeMinimapCaptureLighting; // 0x959 Size: 0x1
	    char UnknownData59[0x6]; // 0x95a
	    TArray<struct FString> CuratedLinkCodes; // 0x960 Size: 0x10
	    TArray<struct FString> CuratedIslandTemplateCodes; // 0x970 Size: 0x10
	    __int64 MapProperty PlaylistCuratedContent; // 0x980 Size: 0x50
	    bool bEnableIslandCheckpoints; // 0x9d0 Size: 0x1
	    bool bEnableIslandLoadNetSafeGuards; // 0x9d1 Size: 0x1
	    char UnknownData60[0x6]; // 0x9d2
	    __int64 MapProperty PlaylistCuratedHub; // 0x9d8 Size: 0x50
	    bool bLoadingScreenInputPreprocessorEnabled; // 0xa28 Size: 0x1
	    bool AllowInputTypeFilterForAccessibility; // 0xa29 Size: 0x1
	    bool AllowLockPrimaryInputMethodToMouseForAccessibility; // 0xa2a Size: 0x1
	    bool bEnableSolaris; // 0xa2b Size: 0x1
	    bool bEnableLiveStream; // 0xa2c Size: 0x1
	    bool bEnableLiveStreamCountdown; // 0xa2d Size: 0x1
	    char UnknownData61[0x2]; // 0xa2e
	    struct FDateTime LiveStreamStartTime; // 0xa30 Size: 0x8
	    bool bEnableLiveStreamInMatch; // 0xa38 Size: 0x1
	    bool bShowLiveStreamInMatchByDefault; // 0xa39 Size: 0x1
	    char UnknownData62[0x6]; // 0xa3a
	    TArray<struct FString> PlaylistConditionalFlags; // 0xa40 Size: 0x10
	    bool bIsUserChoiceAllowedForForcedAndroidStore; // 0xa50 Size: 0x1
	    bool bHideCharacterCustomizationNullTile; // 0xa51 Size: 0x1
	    bool bDoEditModeStayInGrid; // 0xa52 Size: 0x1
	    bool bEnablePlaylistRequireCrossplay; // 0xa53 Size: 0x1
	    bool bRequireCrossplayOptInForFill; // 0xa54 Size: 0x1
	    bool bUseConcurrentCrossplayPromptGuard; // 0xa55 Size: 0x1
	    char UnknownData63[0x2]; // 0xa56
	    int MaxSquadSize; // 0xa58 Size: 0x4
	    int MaxPartySizeCampaign; // 0xa5c Size: 0x4
	    int MaxPartySizeAthena; // 0xa60 Size: 0x4
	    bool bShouldFollowersSendSquadMatchmakingInfo; // 0xa64 Size: 0x1
	    bool bAllowAthenaNavSystemForCreative; // 0xa65 Size: 0x1
	    bool bAthenaAIAllowAbilitiesLooping; // 0xa66 Size: 0x1
	    char UnknownData64[0x1]; // 0xa67
	    int MaxNumAthenaNavTilesPerFrame; // 0xa68 Size: 0x4
	    char UnknownData65[0x4]; // 0xa6c
	    double AthenaCreativeNavMeshGeneratorTimeSliceDuration; // 0xa70 Size: 0x8
	    bool bEnablePlayerSurveys; // 0xa78 Size: 0x1
	    bool bEnablePlayerStatsPrecache; // 0xa79 Size: 0x1
	    bool bEnableStreamingReplayViewingUI; // 0xa7a Size: 0x1
	    char UnknownData66[0x1]; // 0xa7b
	    float LiveReplayDiscoverabilityDelay; // 0xa7c Size: 0x4
	    bool bSkipPlayingFortniteChecks; // 0xa80 Size: 0x1
	    bool bReplayBattleMapCameraMode; // 0xa81 Size: 0x1
	    bool bReplayBattleMapEvents; // 0xa82 Size: 0x1
	    bool bReplayKeepLocalClientEvents; // 0xa83 Size: 0x1
	    bool bReplaySampleAthenaPawnMovement; // 0xa84 Size: 0x1
	    char UnknownData67[0x3]; // 0xa85
	    float ReplaySampleAthenaPawnTimeRate; // 0xa88 Size: 0x4
	    float ReplaySampleAthenaPawnSpaceRate; // 0xa8c Size: 0x4
	    float ReplaySampleAthenaPawnUpdateTimeRate; // 0xa90 Size: 0x4
	    bool bDisablePartyJoinInOutpost; // 0xa94 Size: 0x1
	    char UnknownData68[0x3]; // 0xa95
	    __int64 MapProperty MashSpecialScores; // 0xa98 Size: 0x50
	    bool bEnableMissedInvitesEntry; // 0xae8 Size: 0x1
	    bool bOnlyShowMissedInvitesEntryIfMissedInvites; // 0xae9 Size: 0x1
	    bool bEnableNotifyWhenPlaying; // 0xaea Size: 0x1
	    char UnknownData69[0x1]; // 0xaeb
	    int NumDaysBetweenPlayingNotifications; // 0xaec Size: 0x4
	    int NumHoursBetweenPlayingNotifications; // 0xaf0 Size: 0x4
	    bool bForceAutoChangeMaterialOn; // 0xaf4 Size: 0x1
	    bool bActiveDisplayDeviceTemperature; // 0xaf5 Size: 0x1
	    bool bAllowOfflineInvites; // 0xaf6 Size: 0x1
	    bool bEnablePlatformVoiceLeave; // 0xaf7 Size: 0x1
	    bool bEnablePlatformVoicePrompts; // 0xaf8 Size: 0x1
	    bool bEnableVoiceChatEnablePrompt; // 0xaf9 Size: 0x1
	    bool bEnableQuickHealing; // 0xafa Size: 0x1
	    bool bLoginMOTDEnabled; // 0xafb Size: 0x1
	    bool bAllowDeferredPedestalPawnSpawn; // 0xafc Size: 0x1
	    bool bRunUnicornOnServer; // 0xafd Size: 0x1
	    bool bShowSamsungSensorButtonWarning; // 0xafe Size: 0x1
	    char UnknownData70[0x1]; // 0xaff
	    int SamsungSensorButtonGamesPerWarning; // 0xb00 Size: 0x4
	    bool EnableCommunityVotingScreen; // 0xb04 Size: 0x1
	    char UnknownData71[0x3]; // 0xb05
	    float CommunityVotingRevealDelay; // 0xb08 Size: 0x4
	    bool ScrollToWinnerTileAfterReveal; // 0xb0c Size: 0x1
	    char UnknownData72[0x3]; // 0xb0d
	    TArray<EFortItemShopOrderingOptions> ItemShopOrdering; // 0xb10 Size: 0x10
	    float CommunityVotingVelocityRefreshDelay; // 0xb20 Size: 0x4
	    bool CommunityVotingTileAnimated; // 0xb24 Size: 0x1
	    bool ScrollToComTileOnEventPopupClosed; // 0xb25 Size: 0x1
	    bool bSkipPostMatchFlow; // 0xb26 Size: 0x1
	    bool bAutoPickupConsumables; // 0xb27 Size: 0x1
	    TArray<struct FRuntimeOptionScheduledNotification> ScheduledNotifications; // 0xb28 Size: 0x10
	    bool bEnableLocalNotifications; // 0xb38 Size: 0x1
	    char UnknownData73[0x7]; // 0xb39
	    __int64 MapProperty LocalNotificationsToScheduleByName; // 0xb40 Size: 0x50
	    bool bEnableUnicornHighlightsOnClient; // 0xb90 Size: 0x1
	    bool bEnableHighlightsPromptInCompeteScreen; // 0xb91 Size: 0x1
	    bool bUseReturnToKairosLoadingScreen; // 0xb92 Size: 0x1
	    bool bForceReturnToKairosLoadingScreen; // 0xb93 Size: 0x1
	    bool bUseActivityBrowser; // 0xb94 Size: 0x1
	    bool bDebugForceLoginRelaunch; // 0xb95 Size: 0x1
	    char UnknownData74[0x2]; // 0xb96
	    struct FString RockyRidgeMode; // 0xb98 Size: 0x10
	    struct FString RockyRidgePath; // 0xba8 Size: 0x10
	    struct FString RockyRidgeFlags; // 0xbb8 Size: 0x10
	    TArray<FName> MaintenanceSections; // 0xbc8 Size: 0x10
	    bool bEnableLiveSpectateButton; // 0xbd8 Size: 0x1
	    bool bEnableGuidedTutorial; // 0xbd9 Size: 0x1
	    bool bEnableMinigameStartupStats; // 0xbda Size: 0x1
	    bool bEnableFastForwardOneDay; // 0xbdb Size: 0x1
	    bool bShouldPlaySoundWhenFastForwardingOneDay; // 0xbdc Size: 0x1
	    char UnknownData75[0x3]; // 0xbdd
	    int MinDaysToFastForward; // 0xbe0 Size: 0x4
	    int MaxDaysToFastForward; // 0xbe4 Size: 0x4
	    float FastForwardOneDayVolumeMultiplier; // 0xbe8 Size: 0x4
	    float MinTimeBetweenDayFastForwarding; // 0xbec Size: 0x4
	    float MaxTimeBetweenDayFastForwarding; // 0xbf0 Size: 0x4
	    float FastForwardMinOneDaySpeed; // 0xbf4 Size: 0x4
	    float FastForwardMaxOneDaySpeed; // 0xbf8 Size: 0x4
	    char UnknownData76[0xbfc]; // 0xbfc
	    bool ShouldShowLeaderboardPrivacySettings(); // 0x0 Size: 0x7feb
	    char UnknownData77[0x7feb]; // 0x7feb
	    bool ShouldDisableReceiveGiftOption(); // 0x0 Size: 0x7feb
	    char UnknownData78[0x7feb]; // 0x7feb
	    void SetEnableMainMenuSocialButton(bool NewValue); // 0x0 Size: 0x7feb
	    char UnknownData79[0x7feb]; // 0x7feb
	    static bool IsSitoutOptionEnabled(); // 0x0 Size: 0x7feb
	    char UnknownData80[0x7feb]; // 0x7feb
	    bool IsShippingBuild(); // 0x0 Size: 0x7feb
	    char UnknownData81[0x7feb]; // 0x7feb
	    static bool IsPartyInProgressEnabled(); // 0x0 Size: 0x7feb
	    char UnknownData82[0x7feb]; // 0x7feb
	    bool IsMatchmakingKnobVisible(struct FString KnobName); // 0x0 Size: 0x7feb
	    char UnknownData83[0x7feb]; // 0x7feb
	    bool IsMatchmakingKnobEnabled(struct FString KnobName); // 0x0 Size: 0x7feb
	    char UnknownData84[0x7feb]; // 0x7feb
	    bool IsInviteUIDisabled(); // 0x0 Size: 0x7feb
	    char UnknownData85[0x7feb]; // 0x7feb
	    bool IsGiftingDisabled(); // 0x0 Size: 0x7feb
	    char UnknownData86[0x7feb]; // 0x7feb
	    bool IsGameplayMessageEnabled(FName MessageOwnerClassName, FName MessageName); // 0x0 Size: 0x7feb
	    char UnknownData87[0x7feb]; // 0x7feb
	    TArray<struct FString> GetValidAffiliateNames(); // 0x0 Size: 0x7feb
	    char UnknownData88[0x7feb]; // 0x7feb
	    bool GetShowReplayTrailerButton_Athena(); // 0x0 Size: 0x7feb
	    char UnknownData89[0x7feb]; // 0x7feb
	    static class UFortRuntimeOptions* GetRuntimeOptions(); // 0x0 Size: 0x7feb
	    char UnknownData90[0x7feb]; // 0x7feb
	    bool GetRebootShowInInventory(); // 0x0 Size: 0x7feb
	    char UnknownData91[0x7feb]; // 0x7feb
	    float GetRebootDirectiveDisplayTime(); // 0x0 Size: 0x7feb
	    char UnknownData92[0x7feb]; // 0x7feb
	    float GetRebootChipExpirationTime(); // 0x0 Size: 0x7feb
	    char UnknownData93[0x7feb]; // 0x7feb
	    struct FText GetOverrideBonusEventXpTitleText(); // 0x0 Size: 0x7feb
	    char UnknownData94[0x7feb]; // 0x7feb
	    struct FText GetOverrideAffiliateErrorText(); // 0x0 Size: 0x7feb
	    char UnknownData95[0x7feb]; // 0x7feb
	    struct FText GetOverrideAffiliateDescriptionText(); // 0x0 Size: 0x7feb
	    char UnknownData96[0x7feb]; // 0x7feb
	    int GetNumGameplayMessageChannels(); // 0x0 Size: 0x7feb
	    char UnknownData97[0x7feb]; // 0x7feb
	    bool GetNewSettingsScreenEnabled(); // 0x0 Size: 0x7feb
	    char UnknownData98[0x7feb]; // 0x7feb
	    TArray<FName> GetMaintenanceSections(); // 0x0 Size: 0x7feb
	    char UnknownData99[0x7feb]; // 0x7feb
	    struct FString GetLobbyGenericLinkButtonURL(); // 0x0 Size: 0x7feb
	    char UnknownData100[0x7feb]; // 0x7feb
	    struct FText GetLobbyGenericLinkButtonOverrideText(); // 0x0 Size: 0x7feb
	    char UnknownData101[0x7feb]; // 0x7feb
	    bool GetIsPlayerReportingFlowEnabled(); // 0x0 Size: 0x7feb
	    char UnknownData102[0x7feb]; // 0x7feb
	    bool GetIsFriendLinkEnabled(); // 0x0 Size: 0x7feb
	    char UnknownData103[0x7feb]; // 0x7feb
	    struct FString GetGameVersion(); // 0x0 Size: 0x7feb
	    char UnknownData104[0x7feb]; // 0x7feb
	    void GetExternalNewsURL(ESubGame CurrentMode, bool bHasValidExternalURL, struct FString ExternalURL, ENewsExternalURLMode ButtonMode); // 0x0 Size: 0x7feb
	    char UnknownData105[0x7feb]; // 0x7feb
	    bool GetEnableSplitWalletTextNotice(); // 0x0 Size: 0x7feb
	    char UnknownData106[0x7feb]; // 0x7feb
	    bool GetEnableNotifyWhenPlaying(); // 0x0 Size: 0x7feb
	    char UnknownData107[0x7feb]; // 0x7feb
	    bool GetEnableMainMenuSocialButton(); // 0x0 Size: 0x7feb
	    char UnknownData108[0x7feb]; // 0x7feb
	    bool GetEnableLocalNotifications(); // 0x0 Size: 0x7feb
	    char UnknownData109[0x7feb]; // 0x7feb
	    bool GetEnableLobbyGenericLinkButton(); // 0x0 Size: 0x7feb
	    char UnknownData110[0x7feb]; // 0x7feb
	    bool GetEnableLFG(); // 0x0 Size: 0x7feb
	    char UnknownData111[0x7feb]; // 0x7feb
	    bool GetDisablePurchaseHistoryScreen(); // 0x0 Size: 0x7feb
	    char UnknownData112[0x7feb]; // 0x7feb
	    TArray<struct FRuntimeOptionTabStateInfo> GetDisabledFrontendNavigationTabs(); // 0x0 Size: 0x7feb
	    char UnknownData113[0x7feb]; // 0x7feb
	    float GetDelayGiftButtonWhenMFANotEnabledSeconds(); // 0x0 Size: 0x7feb
	    char UnknownData114[0x-73eb];

		static class UClass* StaticClass()
	    {
			static auto ptr = UObject::FindObject("/Script/FortniteGame.FortRuntimeOptions");
			return (class UClass*)ptr;
		};

};
*/