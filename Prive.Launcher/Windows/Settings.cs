using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

public class SettingsWindow : Window {
    private string GamePathLabelText(Configuration config) => $"Current Game Path:\n{string.Join('\n', Regex.Matches(Path.GetDirectoryName(config.GamePath) ?? "", @$".{{1,{Console.WindowWidth - 2}}}").Cast<Match>().Select(m => m.Value).ToArray())}";

    public SettingsWindow() : base("Prive") {
        Console.Title = "Prive Settings";
        ColorScheme.Normal = new(Color.BrightMagenta, Color.Black);
        var config = Configurations.GetConfiguration();

        var gamePathLabel = new Label() {
            Text = GamePathLabelText(config),
            X = 1
        };
        Add(gamePathLabel);

        var selectGamePathButton = new Button() {
            Text = "Select Game Path",
            X = 1,
            Y = gamePathLabel.Y + gamePathLabel.Text.Split("\n").Length + 1,
        };
        selectGamePathButton.Clicked += () => {
            var openFile = new Utils.OpenFileName() {
                lStructSize = Marshal.SizeOf(typeof(Utils.OpenFileName)),
                lpstrFilter = "Shipping Executable(*.exe)\0*.exe\0",
                lpstrFile = Utils.ShippingExecutableName,
                nMaxFile = 260,
                lpstrTitle = "Select Game Path",
                Flags = 0x00080000 | 0x00001000 // OFN_EXPLORER | OFN_FILEMUSTEXIST
            };
            if (Utils.GetOpenFileName(ref openFile)) {
                if (!openFile.lpstrFile.EndsWith(Utils.ShippingExecutableName)) {
                    Utils.MessageBox("Selected file is not a shipping executable!", type: 0x00000000 | 0x00000010);
                    return;
                }
                // This window closes wtf
                config.GamePath = openFile.lpstrFile;
                Configurations.SaveConfiguration(config);
                gamePathLabel.Text = GamePathLabelText(config);
                selectGamePathButton.Y = gamePathLabel.Y + gamePathLabel.Text.Split("\n").Length + 1;
            } else Utils.MessageBox("Canceled");
        };
        Add(selectGamePathButton);
    }
}