public class MainWindow : Window {
    public MainWindow() : base("Prive Launcher") {
        ColorScheme.Normal = new(Color.BrightMagenta, Color.Black);
        Add(
            new Label() {
                Text = "Hello!",
                X = 1
            }
        );
        var settingsButton = new Button() {
            Text = "Settings",
            X = Pos.Right(this) - 14,
        };
        settingsButton.Clicked += () => {
            if (SettingsProcess != null && !SettingsProcess.HasExited) {
                if (!Utils.SetForegroundWindow(SettingsProcess)) {
                    #if DEBUG
                    Utils.MessageBox("Failed to set foreground window!", "Prive Debug", 0x00000000 | 0x00000010);
                    #endif
                }
            } else {
                SettingsProcess = CreateNewWindow<SettingsWindow>();
            }
        };
        Add(settingsButton);
    }
}