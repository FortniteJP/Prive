public class MainWindow : Window {
    public MainWindow() : base("Prive") {
        Console.Title = "Prive Launcher";
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
            if (SettingsProcess is not null && !SettingsProcess.HasExited) {
                // if (!Utils.SetForegroundWindow(SettingsProcess)) { // Doesn't work...
                //     #if DEBUG
                //     Utils.MessageBox("Failed to set foreground window!", "Prive Debug", 0x00000000 | 0x00000010);
                //     #endif
                // }
                SettingsProcess.Kill();
                SettingsProcess = CreateNewWindow<SettingsWindow>();
            } else {
                SettingsProcess = CreateNewWindow<SettingsWindow>();
            }
        };
        Add(settingsButton);
        
        var launchButton = new Button() {
            Text = "Launch",
            X = Pos.Center(),
            Y = Pos.Center(),
        };
        launchButton.Clicked += () => {
            if (IsServer) return;

            var config = Configurations.GetConfiguration();
            Utils.MessageBox(System.Text.Json.JsonSerializer.Serialize(config));
        };
        Add(launchButton);
    }
}