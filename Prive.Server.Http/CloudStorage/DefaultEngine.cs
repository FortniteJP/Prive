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
                new IniElementKeyValue("Domain", "api.fortnite.day"),
                new IniElementKeyValue("ServerAddr", "api.fortnite.day"),
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
                new IniElementKeyValue("Domain", "api.fortnite.day"),
                new IniElementKeyValue("ServerAddr", $"{(UseSSL ? "wss" : "ws")}://api.fortnite.day"),
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
                    Option = IniElementOption.AddIfMissing,
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
        new() {
            Section = "Core.Log",
            Elements = new() {
                new IniElementKeyValue("LogEngine", "All"),
                new IniElementKeyValue("LogNetDormancy", "All"),
                new IniElementKeyValue("LogNetPartialBunch", "All"),
                new IniElementKeyValue("OodleHandlerComponentLog", "All"),
                new IniElementKeyValue("LogSpectatorBeacon", "All"),
                new IniElementKeyValue("LogHttp", "All"),
                new IniElementKeyValue("LogProfileSys", "All"),
                new IniElementKeyValue("LogOnlineAccount", "All"),
                new IniElementKeyValue("LogOnline", "All"),
                new IniElementKeyValue("LogOnlineInteractions", "All"),
                new IniElementKeyValue("LogAnalytics", "All"),
                new IniElementKeyValue("PacketHandlerLog", "All"),
                new IniElementKeyValue("LogPartyBeacon", "All"),
                new IniElementKeyValue("LogNet", "All"),
                new IniElementKeyValue("LogBeacon", "All"),
                new IniElementKeyValue("LogNetTraffic", "All"),
                new IniElementKeyValue("LogDiscordRPC", "All"),
                new IniElementKeyValue("LogEOSSDK", "All"),
                new IniElementKeyValue("LogXmpp", "All"),
                new IniElementKeyValue("LogParty", "All"),
                new IniElementKeyValue("LogHotfixManager", "All"),
                new IniElementKeyValue("LogMatchmakingServiceClient", "All"),
                new IniElementKeyValue("LogScriptCore", "All"),
                new IniElementKeyValue("LogSkinnedMeshComp", "All"),
                new IniElementKeyValue("LogFortAbility", "All"),
                new IniElementKeyValue("LogContentBeacon", "All"),
                new IniElementKeyValue("LogEasyAntiCheatServer", "All"),
                new IniElementKeyValue("LogEasyAntiCheatClient", "All"),
                new IniElementKeyValue("LogBattlEye", "All"),
                new IniElementKeyValue("LogOnlineIdentity", "All"),
            }
        },
        new() {
            Section = "OnlineSubsystemMcp.AccountServiceMcp Prod",
            Elements = new() {
                new IniElementKeyValue("RedirectUrl", "https://fortnite.day"),
                new IniElementKeyValue("bUpdatesConnectionStatus", "false")
            }
        },
    };
}