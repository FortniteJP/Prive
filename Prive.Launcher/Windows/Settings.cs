using System.Text.RegularExpressions;

public class SettingsWindow : Window {
    public SettingsWindow() : base("Prive") {
        Console.Title = "Prive Settings";
        ColorScheme.Normal = new(Color.BrightMagenta, Color.Black);
        var config = Configurations.GetConfiguration();

        var gamePathLabel = new TextView() {
            Text = $"Current Path:\n{string.Join('\n', Regex.Matches(config.GamePath, @$".{{1,{Console.WindowWidth - 2}}}").Cast<Match>().Select(m => m.Value).ToArray())}",
            X = 1
        };
        Add(gamePathLabel);

        var selectGamePathButton = new Button() {
            Text = "Select Game Path",
            X = 1,
            Y = gamePathLabel.Y + gamePathLabel.Text.Split("\n").Length + 1,
        };
        selectGamePathButton.Clicked += () => {
            var od = new OpenDialog("Select Game Path", "Select the shipping.exe") {
                CanChooseDirectories = false,
                CanChooseFiles = true,
                AllowedFileTypes = new string[] { "exe" },
                AllowsMultipleSelection = false,
                DirectoryPath = "/",
                CanCreateDirectories = false
            };
            Console.SetWindowSize(60, 15);
            Application.Run(od);
            Console.SetWindowSize(10, 10);
            if (File.Exists((string)od.FilePath)) {
                config.GamePath = (string)od.FilePath;
                Configurations.SaveConfiguration(config);
            }
        };
        Add(selectGamePathButton);
    }
}

public class GamePathLabel : Label {}