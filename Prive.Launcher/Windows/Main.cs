using System.IO.Compression;

namespace Prive.Launcher;

public class MainWindow : Window {
    public const string ClientNativeURL = "https://nightly.link/FortniteJP/Prive/workflows/Prive.Client.Native/main/Prive.Client.Native.zip";
    public const string FortniteConsoleURL = "https://prive.xthe.org/FortniteConsole.dll";

    public static readonly string ClientNativeDllLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher", "Prive.Client.Native.dll");
    public static readonly string FortniteConsoleDllLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher", "FortniteConsole.dll");
    public static CancellationTokenSource? DownloadCTS { get; set; }

    public static HttpClient Http { get; } = new();
    // public Timer ActivePlayersTimer { get; }

    // public Label ActivePlayersLabel { get; set; } = default!;
    public Button LaunchButton { get; set; } = default!;
    public Button DownloadButton { get; set; } = default!;

    public MainWindow() : base("Prive") {
        Console.Title = "Prive Launcher";
        ColorScheme = Utils.DefaultColorScheme;
        Add(
            new Label() {
                Text = "Hello!",
                X = 1
            }
        );

        /* ActivePlayersLabel = new Label() {
            Text = "Active players: Loading...",
            X = 1,
            Y = 1
        };
        ActivePlayersTimer = new(new(UpdateActivePlayers), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        Add(ActivePlayersLabel); */

        #if !DEBUG
        DownloadClientNativeDll();
        DownloadFortniteConsoleDll();
        #endif

        var settingsButton = new Button() {
            Text = "Settings",
            X = Pos.Right(this) - 14,
        };
        settingsButton.Clicked += () => {
            if (SettingsProcess is not null && !SettingsProcess.HasExited) {
                Utils.SetForegroundWindow(SettingsProcess);
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
                Utils.MessageBox($"{Path.GetFileName(ClientNativeDllLocation)} is not found!", "Prive", 0x00000000 | 0x00000010);
                return;
            }
            if (!File.Exists(FortniteConsoleDllLocation)) {
                Utils.MessageBox($"{Path.GetFileName(FortniteConsoleDllLocation)} is not found!", "Prive", 0x00000000 | 0x00000010);
                return;
            }

            var config = Configurations.GetConfiguration();
            if (string.IsNullOrWhiteSpace(config.GamePath)) {
                Utils.MessageBox("Game path is not set!", "Prive", 0x00000000 | 0x00000010);
                return;
            }

            Instance = new(config.GamePath, config.Username, config.Password);
            Instance.Launch();

            LaunchButton.Text = "Running...";
            LaunchButton.Enabled = false;
            DownloadButton.Enabled = false;
            // ActivePlayersTimer.Change(Timeout.Infinite, Timeout.Infinite);
            // ActivePlayersLabel.Text = $"Active players: -1";

            Task.Run(() => {
                Instance.InjectDll(ClientNativeDllLocation);
                Task.Run(async () => await Instance.WaitForLogAndInjectDll(line => line.Contains("LogHotfixManager: Display: Update State CheckingForPatch -> CheckingForHotfix"), FortniteConsoleDllLocation));
                Instance.WaitForExit();
                Instance.Kill();
                LaunchButton.Text = "Launch";
                LaunchButton.Enabled = true;
                DownloadButton.Enabled = true;
                // ActivePlayersTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(30));
            });
        };
        Add(LaunchButton);

        DownloadButton = new Button() {
            Text = "Downloads",
            X = Pos.Center(),
            Y = Pos.Center() + 5,
        };
        void extractProgressHandler(int e, int p, int max, bool completed) {
            if (completed) {
                LaunchButton.Text = "Launch";
                LaunchButton.Enabled = true;
                DownloadButton.Text = "Downloads";
                DownloadButton.Enabled = true;
                return;
            }
            LaunchButton.Text = $"{string.Format("{0, 3}", e)}% ({p}/{max})";
            LaunchButton.Enabled = false;
        }
        void progressHandler(long p, long max, bool completed) {
            if (completed) {
                DownloadButton.Text = "Extracting..."; // Cancel extract
                DownloadButton.Enabled = false; // TODO: implement
                DownloadsWindow.ExtractDownloaded(extractProgressHandler);
            } else {
                LaunchButton.Text = $"{(int)(((float)p/(float)max)*100)}% ({Utils.BytesToString(p)}/{Utils.BytesToString(max)})";
                LaunchButton.Enabled = false;
            }
        };
        DownloadButton.Clicked += async () => {
            if (DownloadButton.Text == "Cancel download") { // Cancel decompress too
                if (DownloadCTS is not null) await DownloadCTS.CancelAsync();
                LaunchButton.Text = "Launch";
                LaunchButton.Enabled = true;
                DownloadButton.Text = "Downloads";
                return;
            }
            Application.Run<DownloadsWindow>();
            if (!DownloadsWindow.LastCanceled && File.Exists(DownloadsWindow.InstallingInformationLocation)) {
                DownloadCTS = new();
                try {
                    DownloadsWindow.ContinueDownload(progressHandler, DownloadCTS.Token);
                } catch (Exception e) { if (e is not TaskCanceledException || e is not OperationCanceledException) Utils.MessageBox(e.ToString()); }
                DownloadButton.Text = "Cancel download";
                LaunchButton.Text = "Loading...";
                LaunchButton.Enabled = false;
            }
            Console.Title = "Prive";
        };
        Add(DownloadButton);

        var serverButton = new Button() {
            Text = "Server",
            X = 1,
            Y = Pos.Bottom(this) - 3,
        };
        serverButton.Clicked += () => {
            ServerProcess?.Refresh();
            if (ServerProcess is not null && !ServerProcess.HasExited) {
                Utils.SetForegroundWindow(ServerProcess);
            } else {
                ServerProcess = CreateNewWindow<ServerWindow>();
            }
        };
        #if DEBUG
        Add(serverButton);
        #endif

        #if DEBUG
        var dumpSDKButton = new Button() {
            Text = "Dump SDK",
            X = Pos.Right(this) - 14,
            Y = Pos.Bottom(this) - 3,
        };
        dumpSDKButton.Clicked += () => {
            if (!(Instance?.ShippingProcess?.HasExited ?? true)) Instance?.InjectDll(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher", "UEDumper.dll"));
            else Utils.MessageBox("Game is not running!", "Prive", 0x00000000 | 0x00000010);
        };
        Add(dumpSDKButton);
        #endif
    }

    private static async void DownloadClientNativeDll() {
        if (Path.GetDirectoryName(ClientNativeDllLocation) is var dir && !Directory.Exists(dir)) Directory.CreateDirectory(dir!);

        var zipBin = await Http.GetByteArrayAsync(ClientNativeURL);

        using var ms = new MemoryStream(zipBin);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = archive.GetEntry("Prive.Client.Native.dll") ?? throw new FileNotFoundException("Failed to get Prive.Client.Native.dll from zip archive.");
        using var stream = entry.Open();
        using var fs = new FileStream(ClientNativeDllLocation, FileMode.Create);
        await stream.CopyToAsync(fs);
    }

    private static async void DownloadFortniteConsoleDll() {
        if (Path.GetDirectoryName(FortniteConsoleDllLocation) is var dir && !Directory.Exists(dir)) Directory.CreateDirectory(dir!);

        using var fs = new FileStream(FortniteConsoleDllLocation, FileMode.Create);
        await (await Http.GetStreamAsync(FortniteConsoleURL)).CopyToAsync(fs);
    }

    public async void UpdateActivePlayers(object? state) {
        var playersLeft = -1;
        try {
            playersLeft = await GetActivePlayers();
        } catch (Exception) {
            // Utils.MessageBox(e.ToString());
        }
        // ActivePlayersLabel.Text = $"Active players: {playersLeft}";
    }

    public static async Task<int> GetActivePlayers() => int.Parse(await Http.GetStringAsync("https://api-prive.xthe.org/serverapi/activeplayers"));
}