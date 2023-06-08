using System.IO.Compression;

public class MainWindow : Window {
    public static readonly string ClientNativeDllLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher/Prive.Client.Native.dll");
    public static CancellationTokenSource? DownloadCTS { get; set; }

    public Button LaunchButton { get; set; } = default!;
    public Button DownloadButton { get; set; } = default!;
    
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
        
        LaunchButton = new Button() {
            Text = "Launch",
            X = Pos.Center(),
            Y = Pos.Center(),
        };
        LaunchButton.Clicked += () => {
            if (IsServer) return;
            
            if (!File.Exists(ClientNativeDllLocation)) {
                Utils.MessageBox("Prive.Client.Native.dll is not found!", "Prive", 0x00000000 | 0x00000010);
                return;
            }

            var config = Configurations.GetConfiguration();
            if (string.IsNullOrWhiteSpace(config.GamePath)) {
                Utils.MessageBox("Game path is not set!", "Prive", 0x00000000 | 0x00000010);
                return;
            }
            
            Instance = new(config.GamePath);
            Instance.Launch();

            LaunchButton.Text = "Running...";
            LaunchButton.Enabled = false;
            DownloadButton.Enabled = false;

            Task.Run(() => {
                Instance.InjectDll(ClientNativeDllLocation);
                Instance.WaitForExit();
                LaunchButton.Text = "Launch";
                LaunchButton.Enabled = true;
                DownloadButton.Enabled = true;
            });
        };
        Add(LaunchButton);
        
        DownloadButton = new Button() {
            Text = "Downloads",
            X = Pos.Center(),
            Y = Pos.Center() + 5,
        };
        void progressHandler(long p, long max, bool completed) {
            if (completed) {
                LaunchButton.Text = "Launch";
                LaunchButton.Enabled = true;
                DownloadsWindow.UnzipDownloaded();
            } else {
                LaunchButton.Text = $"{(int)(((float)p/(float)max)*100)}% ({Utils.BytesToString(p)}/{Utils.BytesToString(max)})";
                LaunchButton.Enabled = false;
            }
        };
        DownloadButton.Clicked += () => {
            if (DownloadButton.Text == "Cancel download") {
                DownloadCTS?.Cancel();
                LaunchButton.Text = "Launch";
                LaunchButton.Enabled = true;
                DownloadButton.Text = "Downloads";
                return;
            }
            Application.Run<DownloadsWindow>();
            if (File.Exists(DownloadsWindow.InstallingInformationLocation)) {
                DownloadCTS = new();
                DownloadsWindow.ContinueDownload(progressHandler, DownloadCTS.Token);
                DownloadButton.Text = "Cancel download";
                LaunchButton.Text = "Loading...";
                LaunchButton.Enabled = false;
            }
        };
        // Add(DownloadButton);
        // Make a download server before implementing this...

        #if DEBUG
        var dumpSDKButton = new Button() {
            Text = "Dump SDK",
            X = Pos.Right(this) - 14,
            Y = Pos.Bottom(this) - 3,
        };
        dumpSDKButton.Clicked += () => {
            if (!(Instance?.ShippingProcess?.HasExited ?? true)) Instance?.InjectDll(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher/UEDumper.dll"));
            else Utils.MessageBox("Game is not running!", "Prive", 0x00000000 | 0x00000010);
        };
        Add(dumpSDKButton);
        #endif
    }

    private async void DownloadClientNativeDll() {
        if (Path.GetDirectoryName(ClientNativeDllLocation)! is var dir && !Directory.Exists(dir))  Directory.CreateDirectory(dir);
        
        var zipBin = await (new HttpClient()).GetByteArrayAsync("https://nightly.link/FortniteJP/Prive/workflows/msbuild/main/build-result.zip?h=6080f158f6a0765a5f4f2619c808f5fddfc58ee8");
        
        using (var ms = new MemoryStream(zipBin))
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Read)) {
            var entry = archive.GetEntry("Prive.Client.Native.dll");
            if (entry is null) throw new FileNotFoundException("Failed to get Prive.Client.Native.dll from zip archive.");
            using (var stream = entry.Open())
            using (var fs = new FileStream(ClientNativeDllLocation, FileMode.Create)) {
                await stream.CopyToAsync(fs);
            }
        }
    }
}