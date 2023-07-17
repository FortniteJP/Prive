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
                        Key = "AC35E95A452317CB8F53B88A9D072AB2",
                        NativeString = "We're having trouble retrieving data from the Events Neural Network. \r\nWe'll try unplugging it and plugging it back in. \r\n\r\nPlease try back later.",
                        LocalizedStrings = new() {
                            ["en"] = "https://fortnite.day/",
                            ["ja"] = "ﾌｫｰﾄﾅｲﾄ♡"
                        }
                    }
                },
                // new IniTextReplacements() {
                //     TextReplacement = new() {
                //         Namespace = "Fortnite.FortAthenaMatchmakingWidget",
                //         Key = "Message.PlayersInQueue",
                //         NativeString = "Queued players: {0}\nElapsed: {1}",
                //         LocalizedStrings = new() {
                //             ["en"] = "Message.PlayersInQueue - 0: {0}, 1: {1}",
                //             ["ja"] = "Message.PlayersInQueue - 0: {0}, 1: {1}"
                //         }
                //     }
                // },
                // new IniTextReplacements() {
                //     TextReplacement = new() {
                //         Namespace = "Fortnite.FortAthenaMatchmakingWidget",
                //         Key = "Message.FindingMatch",
                //         NativeString = "Finding match...\nElapsed: {0}, ETA: {1}",
                //         LocalizedStrings = new() {
                //             ["en"] = "Finding match...\nElapsed: {0}, ETA: {1}",
                //             ["ja"] = "Message.FindingMatch - 0: {0}, 1: {1}"
                //         }
                //     }
                // },
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
                            ["en"] = "Maybe\r\nReleased: 2023-07-17",
                            ["ja"] = "多分\r\nリリース: 2023-07-17"
                        }
                    }
                },
                new IniTextReplacements() {
                    TextReplacement = new() {
                        Namespace = "",
                        Key = "5CC62D22428453A9FD896BA82ED8B258",
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
                        Key = "423957A447BCC38E7AF190B60C9E47D2",
                        NativeString = "Spray and Pray",
                        LocalizedStrings = new() {
                            ["en"] = "This is LateGame Solo",
                            ["ja"] = "これはレイトゲーム ソロです"
                        }
                    }
                },
                new IniTextReplacements() {
                    TextReplacement = new() {
                        Namespace = "",
                        Key = "D73A8E6646FA6FC31B0943A53B8F44A2",
                        NativeString = "All weapons in this mode are ones that automatically fire.",
                        LocalizedStrings = new() {
                            ["en"] = "Maybe",
                            ["ja"] = "多分"
                        }
                    }
                },
                new IniTextReplacements() {
                    TextReplacement = new() {
                        Namespace = "",
                        Key = "5E74EA754670747F914C6CBAE14C40ED",
                        NativeString = "Metal is Good",
                        LocalizedStrings = new() {
                            ["en"] = "?",
                            ["ja"] = "ﾌｫｰﾄﾅｲﾄ♡"
                        }
                    }
                },
                new IniTextReplacements() {
                    TextReplacement = new() {
                        Namespace = "",
                        Key = "684E866D44983FB4673764879E7B32BB",
                        NativeString = "Farm resources that build stronger walls to survive the steady stream of fire.",
                        LocalizedStrings = new() {
                            ["en"] = "...",
                            ["ja"] = "?"
                        }
                    }
                },
                new IniTextReplacements() {
                    TextReplacement = new() {
                        Namespace = "",
                        Key = "BACA7EAB4A5B8B50FC11CFAB98055697",
                        NativeString = "Covering Fire",
                        LocalizedStrings = new() {
                            ["en"] = "Prive",
                            ["ja"] = "Prive"
                        }
                    }
                },
                new IniTextReplacements() {
                    TextReplacement = new() {
                        Namespace = "",
                        Key = "6026A926449F96ABDB4622AE0897B09B",
                        NativeString = "Help your team by laying down suppressing fire!",
                        LocalizedStrings = new() {
                            ["en"] = "https://fortnite.day",
                            ["ja"] = "https://fortnite.day"
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
                        PlaylistName = "Playlist_Auto_Solo"
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