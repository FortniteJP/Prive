namespace Prive.Server.Http.CloudStorage;

public class DefaultGame : CloudStorageFile {
    public override string Filename => "DefaultGame.ini";
    public override DateTime LastModified { get; } = DateTime.UtcNow;

    public override List<IniElementSection> Elements => new() {
        new() {
            Section = "/Script/FortniteGame.FortTextHotfixConfig",
            Elements = new() {
                new IniTextReplacements() {
                    Option = IniElementOption.AddIfMissing,
                    TextReplacements = new() {
                        Namespace = "Fortnite.FortMatchmakingV2",
                        Key = "Unauthorized",
                        NativeString = "You cannot play this game mode at this time.",
                        LocalizedStrings = new() {
                            ["en"] = "NO",
                            ["ja"] = "くたばれ"
                        }
                    }
                }
            }
        },
        new() {
            Section = "/Script/FortniteGame.FortGameInstance",
            Elements = new() {
                new IniElementKeyValue("bBattleRoyaleMatchmakingEnabled", "true")
            }
        },
        new() {
            Section = "VoiceChatManager",
            Elements = new() {
                new IniElementKeyValue("bEnable", "false")
            }
        },
        new() {
            Section = "/Script/FortniteGame.FortOnlineAccount",
            Elements = new() {
                new IniElementKeyValue("bEnableEulaCheck", "false")
            }
        },
        new() {
            Section = "/Script/Account.OnlineAccountCommon",
            Elements = new() {
                new IniElementKeyValue("bEnableWaitingRoom", "false"),
                new IniElementKeyValue("bRequireLightswitchAtStartup", "false")
            }
        }
    };
}