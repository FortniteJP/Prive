namespace Prive.Server.Http.CloudStorage;

public class DefaultGame : CloudStorageFile {
    public override string Filename => "DefaultGame.ini";
    public override DateTime LastModified { get; } = DateTime.UtcNow;

    public override List<IniElementSection> Elements => new() {
        new() {
            Section = "/Script/FortniteGame.FortTextHotfixConfig",
            Elements = new() {
                new IniTextReplacements() {
                    TextReplacement = new() {
                        Namespace = "Fortnite.FortMatchmakingV2",
                        Key = "Unauthorized",
                        NativeString = "You cannot play this game mode at this time.",
                        LocalizedStrings = new() {
                            ["en"] = "NO",
                            ["ja"] = "くたばれ"
                        }
                    }
                },
                new IniTextReplacements() {
                    TextReplacement = new() {
                        Namespace = "",
                        Key = "010435774CF42540ABD5B3B847DBEF5A",
                        NativeString = "New Player Guide",
                        LocalizedStrings = new() {
                            ["en"] = "Prive Website",
                            ["ja"] = "Prive ウェブサイト"
                        }
                    }
                },
                new IniTextReplacements() {
                    TextReplacement = new() {
                        Namespace = "",
                        Key = "4D36840A41019FF3527139AA29B51509",
                        NativeString = "New Player Guide",
                        LocalizedStrings = new() {
                            ["en"] = "Prive Website",
                            ["ja"] = "Prive ウェブサイト"
                        }
                    }
                }
            }
        },
        new() {
            Section = "/Script/FortniteGame.FortGameInstance",
            Elements = new() {
                new IniElementKeyValue("bBattleRoyaleMatchmakingEnabled", "true"),

                new IniElementKeyValue("FrontEndPlaylistData", "ClearArray") { Option = IniElementOption.RemoveIfExisting },
                new IniFrontEndPlaylistData() {
                    Option = IniElementOption.AddIfMissing,
                    FrontEndPlaylistData = new() {
                        PlaylistName = "Playlist_DefaultSolo",
                        PlaylistAccess = new() { bIsDefaultPlaylist = true }
                    }
                }
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
                new IniElementKeyValue("bRequireLightswitchAtStartup", "false"),
                new IniElementKeyValue("AccessGrantDelaySeconds", "0.0")
            }
        }
    };
}