using System.Text.Json;
using System.Text.Json.Serialization;

public class DownloadsWindow : Window {
    public static string InstallingInformationLocation { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher/InstallingInformation.json");
    public static string DownloadsDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher/Downloads/");
    public static Dictionary<string, string> AvailableInstalls { get; set; } = new() {
        ["v10.40"] = "https://cdn.fnbuilds.services/10.40.rar"
    };

    public DownloadsWindow() : base("Prive") {
        Console.Title = "Prive Download";
        ColorScheme.Normal = new(Color.BrightMagenta, Color.Black);
        if (!Directory.Exists(DownloadsDirectory)) Directory.CreateDirectory(DownloadsDirectory);

        var cancelButton = new Button() {
            Text = "Cancel",
        };
        cancelButton.Clicked += () => {
            Application.RequestStop();
        };
        Add(cancelButton);

        foreach (var kv in AvailableInstalls) {
            var installButton = new Button() {
                Text = $"Install {kv.Key}",
                Y = Pos.Top(this) + 1 + AvailableInstalls.Keys.ToList().IndexOf(kv.Key),
            };
            installButton.Clicked += () => {
                var installingInformation = new InstallingInformation() {
                    Url = kv.Value,
                    Path = Path.Combine(DownloadsDirectory, Path.GetFileName(kv.Value))
                };
                File.WriteAllText(InstallingInformationLocation, JsonSerializer.Serialize(installingInformation));
                Application.RequestStop();
            };
            Add(installButton);
        }
    }

    public static InstallingInformation GetInstallingInformation() => JsonSerializer.Deserialize<InstallingInformation>(File.ReadAllText(InstallingInformationLocation)) ?? throw new NullReferenceException();

    public static async void ContinueDownload(Action<long, long, bool>? progressCallback = null, CancellationToken cancellationToken = default) {
        if (!File.Exists(InstallingInformationLocation)) return;
        var info = GetInstallingInformation();
        if (!File.Exists(info.Path)) await File.WriteAllBytesAsync(info.Path, new byte[0]);
        var downloaded = new FileInfo(info.Path).Length;

        var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, info.Url);
        request.Headers.Range = new(downloaded, null);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength ?? throw new NullReferenceException();
        using var stream = await response.Content.ReadAsStreamAsync();
        
        using var file = File.OpenWrite(info.Path);
        var buffer = new byte[1024 * 8];
        while (await stream.ReadAsync(buffer) > 0 && !cancellationToken.IsCancellationRequested) {
            await file.WriteAsync(buffer);
            downloaded += buffer.Length;
            progressCallback?.Invoke(downloaded, length, false);
        }
        progressCallback?.Invoke(downloaded, length, true);
    }
}

public class InstallingInformation {
    [JsonPropertyName("Url")] public required string Url { get; set; }
    [JsonPropertyName("Path")] public required string Path { get; set; }
}