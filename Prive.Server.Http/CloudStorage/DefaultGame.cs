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
                new IniElementKeyValue() {
                    Option = IniElementOption.None,
                    Key = "bBattleRoyaleMatchmakingEnabled",
                    Value = "true"
                }
            }
        }
    };
}