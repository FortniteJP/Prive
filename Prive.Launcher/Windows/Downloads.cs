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

        InstallingInformation? info = null;
        if (File.Exists(InstallingInformationLocation)) info = GetInstallingInformation();

        foreach (var kv in AvailableInstalls) {
            var installButton = new Button() {
                Text = $"Install {kv.Key} {(info?.Url == kv.Value ? $"(paused {Utils.BytesToString(new FileInfo(info.Path).Length)})" : "")}",
                Y = Pos.Top(this) + 1 + AvailableInstalls.Keys.ToList().IndexOf(kv.Key),
            };
            installButton.Clicked += () => {
                var installingInformation = new InstallingInformation() {
                    Url = kv.Value,
                    Path = Path.Combine(DownloadsDirectory, Path.GetFileName(kv.Value)),
                    Length = GetContentLength(kv.Value)
                };
                File.WriteAllText(InstallingInformationLocation, JsonSerializer.Serialize(installingInformation));
                Application.RequestStop();
            };
            Add(installButton);
        }
    }

    public static long GetContentLength(string url) {
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).Result;
        return response.Content.Headers.ContentLength ?? throw new NullReferenceException();
    }

    public static InstallingInformation GetInstallingInformation() => JsonSerializer.Deserialize<InstallingInformation>(File.ReadAllText(InstallingInformationLocation)) ?? throw new NullReferenceException();

    public static void ContinueDownload(Action<long, long, bool>? progressCallback = null, CancellationToken cancellationToken = default) => ContinueDownload(progressCallback, cancellationToken, 0);
    
    private static async void ContinueDownload(Action<long, long, bool>? progressCallback = null, CancellationToken cancellationToken = default, int r = 0) {
        if (!File.Exists(InstallingInformationLocation)) return;
        var info = GetInstallingInformation();
        if (!File.Exists(info.Path)) await File.WriteAllBytesAsync(info.Path, new byte[0]);
        var downloaded = new FileInfo(info.Path).Length;

        var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, info.Url);
        request.Headers.Range = new(downloaded, null);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) {
            progressCallback?.Invoke(info.Length, info.Length, true);
            return;
        }
        // response.EnsureSuccessStatusCode();
        // var length = response.Content.Headers.ContentLength ?? throw new NullReferenceException();
        var length = info.Length;
        using var stream = await response.Content.ReadAsStreamAsync();
        
        using var file = new FileStream(info.Path, FileMode.Append);
        var buffer = new byte[1024 * 8];
        try {
            while (await stream.ReadAsync(buffer) > 0 && !cancellationToken.IsCancellationRequested) {
                await file.WriteAsync(buffer);
                await file.FlushAsync();
                downloaded += buffer.Length;
                progressCallback?.Invoke(downloaded, length, false);
            }
            file.Close();
            await file.DisposeAsync();
        } catch (IOException) {
            if (r > 5) throw;
            file.Close();
            await file.DisposeAsync();
            ContinueDownload(progressCallback, cancellationToken, r + 1);
        }
        if (cancellationToken.IsCancellationRequested) return;
        progressCallback?.Invoke(downloaded, length, true);
    }

    public static void UnzipDownloaded() {}
}

public class InstallingInformation {
    [JsonPropertyName("Url")] public required string Url { get; set; }
    [JsonPropertyName("Path")] public required string Path { get; set; }
    [JsonPropertyName("Length")] public required long Length { get; set; }
}