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
                        Namespace = "Fortnite.FortMatchmakingV2",
                        Key = "MMSCommunicationIssue",
                        NativeString = "Issue communicating with Matchmaking Service.",
                        LocalizedStrings = new() {
                            ["en"] = "Fortnite.FortMatchmakingV2::MMSCommunicationIssue",
                            ["ja"] = "既にマッチが始まっています。終わるまでお待ちください。"
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
                },
                new IniTextReplacements() {
                    TextReplacement = new() {
                        Namespace = "",
                        Key = "CF55FCAA45F271CCCC3B1B847B24BDC1",
                        NativeString = "Automatics",
                        LocalizedStrings = new() {
                            ["en"] = "LateGame Solo",
                            ["ja"] = "レイトゲーム ソロ"
                        }
                    }
                },
                new IniTextReplacements() {
                    TextReplacement = new() {
                        Namespace = "",
                        Key = "4A664F244BA0F12E6641C2BF52158907",
                        NativeString = "The only weapons in the game are the automatic firing ones. Spray and pray!",
                        LocalizedStrings = new() {
                            ["en"] = "TEST DESCRIPTION",
                            ["ja"] = "準備中"
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
                },
                new IniFrontEndPlaylistData() {
                    Option = IniElementOption.AddIfMissing,
                    FrontEndPlaylistData = new() {
                        PlaylistName = "Playlist_Auto_Solo",
                        PlaylistAccess = new() { bEnabled = true } // needed ?
                    }
                },
                new IniFrontEndPlaylistData() {
                    Option = IniElementOption.AddIfMissing,
                    FrontEndPlaylistData = new() {
                        PlaylistName = "Playlist_Auto_Duo",
                        PlaylistAccess = new() { bEnabled = false }
                    }
                },
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
        },
        // Uncomment for funny
        // new() {
        //     Section = "AssetHotfix",
        //     Elements = new() {
        //         // new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_R_Ore_T03;FiringRate;1000") { Option = IniElementOption.AddIfMissing },
        //         // new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_R_Ore_T03;ReloadTime;0.1") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T02;ReloadTime;0.1") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T02;Spread;0.1") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T02;SpreadDownsights;0.1") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T02;DmgPB;45.0") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T02;DmgMid;45.0") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T02;DmgLong;45.0") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T02;ClipSize;3000") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T02;FiringRate;7.0") { Option = IniElementOption.AddIfMissing },
        //         
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T03;Spread;0.1") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T03;SpreadDownsights;0.1") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T03;DmgPB;45.0") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T03;DmgMid;45.0") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T03;DmgLong;45.0") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T03;ClipSize;3000") { Option = IniElementOption.AddIfMissing },
        //         new IniElementKeyValue("DataTable", "/Game/Athena/Items/Weapons/AthenaRangedWeapons;RowUpdate;Assault_Auto_Athena_C_Ore_T03;FiringRate;7.0") { Option = IniElementOption.AddIfMissing },
        //     }
        // },
    };
}