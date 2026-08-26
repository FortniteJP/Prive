namespace Prive.Server.Http.CloudStorage;

public class DefaultEngine : CloudStorageFile {
    public override string Filename => "DefaultEngine.ini";
    public override DateTime LastModified { get; } = DateTime.UtcNow;
    
    public const bool UseSSL = true;

    public override List<IniElementSection> Elements => new() {
        new() {
            Section = "ConsoleVariables",
            Elements = new() {
                new IniElementKeyValue("n.VerifyPeer", "0"),
                new IniElementKeyValue("FortMatchmakingV2.EnableContentBeacon", "0"),
                new IniElementKeyValue("FortMatchmakingV2.ContentBeaconFailureCancelsMatchmaking", "0"),
                new IniElementKeyValue("Fort.ShutdownWhenContentBeaconFails", "0")
            }
        },
        // BROKEN WHY
        new() {
            Section = "OnlineSubsystemMcp.Xmpp",
            Elements = new() {
                new IniElementKeyValue("bUsePlainTextAuth", "true"),
                #if DEBUG
                new IniElementKeyValue("Domain", "localhost"),
                new IniElementKeyValue("ServerAddr", "127.0.0.1"),
                new IniElementKeyValue("ServerPort", "8000"),
                new IniElementKeyValue("Protocol", "http"),
                new IniElementKeyValue("bUseSSL", "false")
                #else
                new IniElementKeyValue("Domain", "!api.fortnite.day"),
                new IniElementKeyValue("ServerAddr", "!api.fortnite.day"),
                #endif
            }
        },
        new() {
            Section = "OnlineSubsystemMcp.Xmpp Prod",
            Elements = new() {
                #if DEBUG
                new IniElementKeyValue("Domain", "localhost"),
                new IniElementKeyValue("ServerAddr", "127.0.0.1"),
                new IniElementKeyValue("ServerPort", "8000"),
                new IniElementKeyValue("Protocol", "http"),
                new IniElementKeyValue("bUseSSL", "false")
                #else
                new IniElementKeyValue("Domain", "!api.fortnite.day"),
                new IniElementKeyValue("ServerAddr", $"{(UseSSL ? "wss" : "ws")}://!api.fortnite.day"),
                new IniElementKeyValue("ServerPort", UseSSL ? "443" : "80"),
                new IniElementKeyValue("Protocol", UseSSL ? "wss" : "ws"),
                new IniElementKeyValue("bUseSSL", $"{UseSSL}")
                #endif
            }
        },
        new() {
            Section = "OnlineSubsystemMcp.OnlinePartySystemMcpV2",
            Elements = new() {
                new IniElementKeyValue("bConnectToMucRooms", "false"),
                new IniElementKeyValue("bDeletePingAfterJoin", "false"),
                // new IniElementKeyValue("CreatePartyWaitForXmppConnectionTimeoutSeconds", "2.0"),
                new IniElementKeyValue("CreatePartyWaitForXmppConnectionTimeoutSeconds", "0.0"),
                new IniElementKeyValue("bRequiresMatchingBuildId", "false")
            }
        },
        new() {
            Section = "LwsWebSocket",
            Elements = new() {
                new IniElementKeyValue("bDisableCertValidation", UseSSL ? "false" : "true")
            }
        },
        new() {
            Section = "/Script/AndroidRuntimeSettings.AndroidRuntimeSettings",
            Elements = new() {
                new IniElementKeyValue("bEnableDynamicMaxFPS", "true")
            }
        },
        new() {
            Section = "PatchCheck",
            Elements = new() {
                new IniElementKeyValue("ModuleName"),
                new IniElementKeyValue("bCheckPlatformOSSForUpdate", "false"),
                new IniElementKeyValue("bCheckOSSForUpdate", "false")
            }
        },
        new() {
            Section = "/Script/Engine.NetworkSettings",
            Elements = new() {
                new IniElementKeyValue("n.VerifyPeer", "false")
            }
        },
        new() {
            Section = "HTTP.Curl",
            Elements = new() {
                new IniElementKeyValue("bAllowSeekFunction", "false")
            }
        },
        new() {
            Section = "/Script/Qos.QosRegionManager",
            Elements = new() {
                new IniElementKeyValue("NumTestsPerRegion", "1"),
                new IniElementKeyValue("PingTimeout", "0.0"),
                new IniElementKeyValue("RegionDefinitions", "ClearArray") { Option = IniElementOption.RemoveIfExisting },
                new IniRegionDefinitions() {
                    RegionDefinition = new() {
                        DisplayName = "Prive Asia",
                        RegionId = "ASIA",
                    }
                }
            }
        },
        new() {
            Section = "OnlineSubsystemGDK",
            Elements = new() {
                new IniElementKeyValue("bXBLGoldRequired", "false")
            }
        },
        #if DEBUG
        new() {
            Section = "Core.Log",
            Elements = new() {
                // FLogSuppressionImplementation treats "Global" specially: it is the default
                // verbosity for every category not named explicitly below. This build has 751 log
                // categories and this file only ever named ~127 of them, so anything that could
                // explain why the Athena loading screen never dismisses was most likely in one of
                // the 600+ we never touched - including whatever the (silent) LogFortLoadingScreen
                // is actually waiting on. Noisy by design; drop back to Display once the gate is
                // identified.
                new IniElementKeyValue("Global", "Verbose"),
                new IniElementKeyValue("LogAnalytics", "All"),
                new IniElementKeyValue("LogBattlEye", "All"),
                new IniElementKeyValue("LogBeacon", "All"),
                new IniElementKeyValue("LogConcert", "All"),
                new IniElementKeyValue("LogContentBeacon", "All"),
                new IniElementKeyValue("LogDiscordRPC", "All"),
                new IniElementKeyValue("LogEasyAntiCheatClient", "All"),
                new IniElementKeyValue("LogEasyAntiCheatServer", "All"),
                new IniElementKeyValue("LogEngine", "All"),
                new IniElementKeyValue("LogEOSSDK", "All"),
                new IniElementKeyValue("LogExec", "All"),
                new IniElementKeyValue("LogFort", "All"),
                new IniElementKeyValue("LogFortAbility", "All"),
                // Building tools occupy quickbar slots, so this covers the same subsystem as the inventory ones below.
                new IniElementKeyValue("LogFortBuilding", "All"),
                new IniElementKeyValue("LogFortChat", "All"),
                new IniElementKeyValue("LogFortCosmetics", "All"),
                new IniElementKeyValue("LogFortCustomization", "All"),
                new IniElementKeyValue("LogFortDayNight", "All"),
                new IniElementKeyValue("LogFortGameUI", "All"),
                // AFortPlayerState::HeroType feeds the Athena hero loadout the quickbars are built from.
                new IniElementKeyValue("LogFortHero", "All"),
                // Default verbosity is Warning, which is why nothing inventory-related has ever appeared in the client log.
                new IniElementKeyValue("LogFortInventory", "All"),
                new IniElementKeyValue("LogFortInventoryUI", "All"),
                // The loading screen never dismissing is the current blocker, so raise every
                // category that could name what it is still waiting on. LogFortLoadingScreen was
                // already here and stayed silent, which is itself a hint that the gate is not in
                // the loading-screen code but in whatever it polls - hence the HUD ones too.
                new IniElementKeyValue("LogFortLoadingScreen", "All"),
                new IniElementKeyValue("LogFortniteEngineLoadingScreen", "All"),
                new IniElementKeyValue("LogLoadingSplash", "All"),
                new IniElementKeyValue("LogAthenaHUDContext", "All"),
                new IniElementKeyValue("LogFortLogin", "All"),
                new IniElementKeyValue("LogFortLoot", "All"),
                new IniElementKeyValue("LogFortMemory", "All"),
                new IniElementKeyValue("LogFortMusic", "All"),
                new IniElementKeyValue("LogFortPlayerPawn", "All"),
                new IniElementKeyValue("LogFortPlayerPawnAthena", "All"),
                new IniElementKeyValue("LogFortPlayerRegistration", "All"),
                new IniElementKeyValue("LogFortPlayerSurvey", "All"),
                // Name suggests it owns the client-side startup sequence ClientRestart is stuck in.
                new IniElementKeyValue("LogFortPlayerStartupController", "All"),
                new IniElementKeyValue("LogFortSettings", "All"),
                new IniElementKeyValue("LogFortSignificance", "All"),
                new IniElementKeyValue("LogFortTeams", "All"),
                new IniElementKeyValue("LogFortUI", "All"),
                new IniElementKeyValue("LogFortVoicePopup", "All"),
                new IniElementKeyValue("LogFortWorld", "All"),
                new IniElementKeyValue("LogGameMode", "All"),
                new IniElementKeyValue("LogGameState", "All"),
                new IniElementKeyValue("LogHandshake", "All"),
                new IniElementKeyValue("LogHotfixManager", "All"),
                // new IniElementKeyValue("LogHttp", "All"),
                new IniElementKeyValue("LogJson", "All"),
                new IniElementKeyValue("LogLoad", "All"),
                new IniElementKeyValue("LogMatchmakingServiceClient", "All"),
                new IniElementKeyValue("LogNet", "All"),
                new IniElementKeyValue("LogNetDormancy", "All"),
                new IniElementKeyValue("LogNetFastTArray", "All"),
                // Defaults to Warning. This is the category that reports a NetGUID whose exported
                // path the client could not resolve - e.g. an item definition asset it has not
                // loaded. Without it, an unresolved ItemDefinition looks like silence: the item
                // still lands in the FastArray ("New Element!") but carries no definition.
                new IniElementKeyValue("LogNetPackageMap", "All"),
                new IniElementKeyValue("LogNetPartialBunch", "All"),
                new IniElementKeyValue("LogNetPlayerMovement", "All"),
                new IniElementKeyValue("LogNetSerialization", "All"),
                new IniElementKeyValue("LogNetSubObject", "All"),
                new IniElementKeyValue("LogNetTraffic", "All"),
                new IniElementKeyValue("LogNetVersion", "All"),
                new IniElementKeyValue("LogOnline", "All"),
                new IniElementKeyValue("LogOnlineAccount", "All"),
                new IniElementKeyValue("LogOnlineGame", "All"),
                new IniElementKeyValue("LogOnlineIdentity", "All"),
                new IniElementKeyValue("LogOnlineInteractions", "All"),
                new IniElementKeyValue("LogParty", "All"),
                new IniElementKeyValue("LogPartyBeacon", "All"),
                new IniElementKeyValue("LogPlayerController", "All"),
                new IniElementKeyValue("LogPlayerManagement", "All"),
                new IniElementKeyValue("LogProcess", "All"),
                new IniElementKeyValue("LogProfileSys", "All"),
                new IniElementKeyValue("LogRep", "All"),
                // NOTE: this one has no effect on a shipping client. RepLayout.cpp gates the whole
                // category at compile time - "#if !(UE_BUILD_SHIPPING || UE_BUILD_TEST)" picks
                // DEFINE_LOG_CATEGORY_STATIC(LogRepProperties, Warning, All) and the shipping branch
                // caps it at Warning, so its Verbose/VeryVerbose per-property traces are compiled out.
                new IniElementKeyValue("LogRepProperties", "All"),
                new IniElementKeyValue("LogRepTraffic", "All"),
                new IniElementKeyValue("LogScript", "All"),
                new IniElementKeyValue("LogScriptCore", "All"),
                new IniElementKeyValue("LogSecurity", "All"),
                new IniElementKeyValue("LogSidecarInventory", "All"),
                new IniElementKeyValue("LogSkinnedMeshComp", "All"),
                new IniElementKeyValue("LogStreamableManager", "All"),
                new IniElementKeyValue("LogUObjectGlobals", "All"),
                new IniElementKeyValue("LogSockets", "All"),
                new IniElementKeyValue("LogSpectatorBeacon", "All"),
                new IniElementKeyValue("LogWindows", "All"),
                new IniElementKeyValue("LogWorld", "All"),
                // new IniElementKeyValue("LogXmpp", "All"),
                new IniElementKeyValue("OodleHandlerComponentLog", "All"),
                new IniElementKeyValue("PacketHandlerLog", "All"),
            }
        },
        #endif
        new() {
            Section = "OnlineSubsystemMcp.AccountServiceMcp Prod",
            Elements = new() {
                new IniElementKeyValue("RedirectUrl", "https://fortnite.day"),
                new IniElementKeyValue("bUpdatesConnectionStatus", "false")
            }
        },
    };
}