using System.IO.Compression;

public class MainWindow : Window {
    public static readonly string ClientNativeDllLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher/Prive.Native.Client.dll");
    
    public MainWindow() : base("Prive") {
        Console.Title = "Prive Launcher";
        ColorScheme.Normal = new(Color.BrightMagenta, Color.Black);
        Add(
            new Label() {
                Text = "Hello!",
                X = 1
            }
        );

        DownloadClientNativeDll();

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
            
            if (!File.Exists(ClientNativeDllLocation)) {
                Utils.MessageBox("Prive.Native.Client.dll is not found!", "Prive", 0x00000000 | 0x00000010);
                return;
            }

            var config = Configurations.GetConfiguration();
            if (string.IsNullOrWhiteSpace(config.GamePath)) {
                Utils.MessageBox("Game path is not set!", "Prive", 0x00000000 | 0x00000010);
                return;
            }
            
            Instance = new(config.GamePath);
            Instance.Launch();

            launchButton.Text = "Running...";
            launchButton.Enabled = false;

            Task.Run(() => {
                Instance.InjectDll(ClientNativeDllLocation);
                Instance.WaitForExit();
                launchButton.Text = "Launch";
                launchButton.Enabled = true;
            });
        };
        Add(launchButton);
    }

    private async void DownloadClientNativeDll() {
        if (Path.GetDirectoryName(ClientNativeDllLocation)! is var dir && !Directory.Exists(dir))  Directory.CreateDirectory(dir);
        
        #if DEBUG
        var zipBin = await (new HttpClient()).GetByteArrayAsync("https://nightly.link/FortniteJP/Prive/workflows/msbuild/main/build-result.zip?h=6080f158f6a0765a5f4f2619c808f5fddfc58ee8");
        #else
        throw new NotImplementedException();
        #endif

        using (var ms = new MemoryStream(zipBin))
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Read)) {
            var entry = archive.GetEntry("Prive.Native.Client.dll");
            if (entry is null) throw new FileNotFoundException("Failed to get Prive.Native.Client.dll from zip archive.");
            using (var stream = entry.Open())
            using (var fs = new FileStream(ClientNativeDllLocation, FileMode.Create)) {
                await stream.CopyToAsync(fs);
            }
        }
    }
}