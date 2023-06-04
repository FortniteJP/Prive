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