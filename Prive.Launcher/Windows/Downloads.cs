using System.Text.Json;
using System.Text.Json.Serialization;
using SharpCompress.Archives.Rar;

namespace Prive.Launcher;

public class DownloadsWindow : Window {
    public const string SevenZipUrl = "https://www.7-zip.org/a/7zr.exe";
    public static string SevenZipPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher", "7zr.exe");

    public static bool LastCanceled { get; set; } = false;
    public static string InstallingInformationLocation { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher", "InstallingInformation.json");
    public static string DownloadsDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher", "Downloads");
    public static Dictionary<string, string> AvailableInstalls { get; set; } = new() {
        #if DEBUG
        ["v10.40"] = "http://localhost:9080/10.40.rar"
        #else
        ["v10.40"] = "https://public.simplyblk.xyz/10.40.rar"
        #endif
    };

    public static HttpClient Http { get; } = new();

    public DownloadsWindow() : base("Prive") {
        Console.Title = "Prive Download";
        ColorScheme = Utils.DefaultColorScheme;
        if (!Directory.Exists(DownloadsDirectory)) Directory.CreateDirectory(DownloadsDirectory);

        // USING THIS IS FASTER
        // if (!File.Exists(SevenZipPath)) Task.Run(async () => {
        //     using var file = File.Create(SevenZipPath);
        //     await file.WriteAsync(await Http.GetByteArrayAsync(SevenZipUrl));
        // });

        var cancelButton = new Button() {
            Text = "Cancel",
        };
        cancelButton.Clicked += () => {
            LastCanceled = true;
            Application.RequestStop();
        };
        Add(cancelButton);

        InstallingInformation? info = null;
        if (File.Exists(InstallingInformationLocation)) info = GetInstallingInformation();

        foreach (var kv in AvailableInstalls) {
            var installButton = new Button() {
                Text = $"Install {kv.Key} {(info?.Url == kv.Value ? $"(paused {Utils.BytesToString(new FileInfo(info.Path).Length)} / {Utils.BytesToString(info.Length)})" : "")}",
                Y = Pos.Top(this) + 1 + AvailableInstalls.Keys.ToList().IndexOf(kv.Key),
            };
            installButton.Clicked += () => {
                var installingInformation = new InstallingInformation() {
                    Version = kv.Key,
                    Url = kv.Value,
                    Path = Path.Combine(DownloadsDirectory, Path.GetFileName(kv.Value)),
                    Length = GetContentLength(kv.Value)
                };
                File.WriteAllText(InstallingInformationLocation, JsonSerializer.Serialize(installingInformation));
                LastCanceled = false;
                Application.RequestStop();
            };
            Add(installButton);
        }
    }

    public static long GetContentLength(string url) {
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        var response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).Result;
        return response.Content.Headers.ContentLength ?? throw new NullReferenceException();
    }

    public static InstallingInformation GetInstallingInformation() => JsonSerializer.Deserialize<InstallingInformation>(File.ReadAllText(InstallingInformationLocation)) ?? throw new NullReferenceException();

    public static void ContinueDownload(Action<long, long, bool>? progressCallback = null, CancellationToken cancellationToken = default) => ContinueDownload(progressCallback, 0, cancellationToken);

    private static async void ContinueDownload(Action<long, long, bool>? progressCallback = null, int r = 0, CancellationToken cancellationToken = default) {
        if (!File.Exists(InstallingInformationLocation)) return;
        var info = GetInstallingInformation();
        if (!File.Exists(info.Path)) await File.WriteAllBytesAsync(info.Path, [], cancellationToken);
        var downloaded = new FileInfo(info.Path).Length;

        var request = new HttpRequestMessage(HttpMethod.Get, info.Url);
        request.Headers.Range = new(downloaded, null);
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) {
            progressCallback?.Invoke(info.Length, info.Length, true);
            return;
        }
        response.EnsureSuccessStatusCode();
        var length = info.Length;
        if (response.Content.Headers.ContentLength.ToString() == length.ToString()) {
            progressCallback?.Invoke(length, length, true);
            return;
        }
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        using var file = new FileStream(info.Path, FileMode.Append);
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent) {
            // To save your storage life <3
            long toSkip = downloaded;
            var discardBuffer = new byte[1024 * 64];
            try {
                while (toSkip > 0) {
                    int read = await stream.ReadAsync(discardBuffer, 0, (int)Math.Min(discardBuffer.Length, toSkip), cancellationToken);
                    if (read <= 0) break;
                    progressCallback?.Invoke(toSkip, downloaded, false);
                    toSkip -= read;
                }
            } catch (Exception e) { if (e is not TaskCanceledException || e is not OperationCanceledException) Utils.MessageBox(e.ToString()); }
        }
        var buffer = new byte[1024 * 64];
        try {
            while (!cancellationToken.IsCancellationRequested && await stream.ReadAsync(buffer, cancellationToken) is int read && read > 0) {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await file.FlushAsync(cancellationToken);
                downloaded += read;
                progressCallback?.Invoke(downloaded, length, false);
            }
            file.Close();
            await file.DisposeAsync();
        } catch (IOException e) {
            if (r > 5) throw;
            file.Close();
            await file.DisposeAsync();
            ContinueDownload(progressCallback, e.Message.StartsWith("The response ended prematurely") ? r : r + 1, cancellationToken);
            return;
        } catch (Exception e) { if (e is not TaskCanceledException || e is not OperationCanceledException) Utils.MessageBox(e.ToString()); }
        if (cancellationToken.IsCancellationRequested) return;
        progressCallback?.Invoke(downloaded, length, true);
    }

    public static async void ExtractDownloaded(Action<int, int, int, bool>? progressCallback = null) {
        var info = GetInstallingInformation();
        var extractPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher", info.Version);
        if (!Directory.Exists(extractPath)) Directory.CreateDirectory(extractPath);
        var archive = RarArchive.Open(info.Path);
        var fileCount = archive.Entries.Count(entry => !entry.IsDirectory);
        var reader = archive.ExtractAllEntries();
        var p = 0;
        while (reader.MoveToNextEntry()) {
            // Utils.MessageBox($"{p}/{fileCount}, {file.Key}");
            p += 1;
            if (!reader.Entry.IsDirectory) {
                // TODO: Blocking happens around here, someone please fix
                var path = Path.Combine(extractPath, reader.Entry.Key);
                if (!Directory.Exists(Path.GetDirectoryName(path))) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length == reader.Entry.Size) {
                    progressCallback?.Invoke((int)(((float)reader.Entry.Size/reader.Entry.Size)*100), p, fileCount, false);
                    continue;
                }
                using var file = File.OpenWrite(path);
                using var stream = reader.OpenEntryStream();
                var length = reader.Entry.Size;
                var wrote = 0L;
                var buffer = new byte[1024 * 64];
                while (await stream.ReadAsync(buffer) is int read && read > 0) {
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    await file.FlushAsync();
                    wrote += read;
                    progressCallback?.Invoke((int)(((float)wrote/length)*100), p, fileCount, false);
                }
            }
        }
        archive.Dispose();
        if (!Directory.Exists(Path.Combine(extractPath, "FortniteGame")) && new DirectoryInfo(extractPath).GetDirectories() is var directories && directories.Length == 1) {
            foreach (var directory in new DirectoryInfo(Path.Combine(extractPath, directories.First().FullName)).GetDirectories()) {
                directory.MoveTo(Path.Combine(extractPath, directory.Name));
            }
            directories.First().Delete();
        }
        File.Delete(info.Path);
        File.Delete(InstallingInformationLocation);
        var config = Configurations.GetConfiguration();
        config.GamePath = Path.Combine(extractPath, "FortniteGame", "Binaries", "Win64", Utils.ShippingExecutableName);
        Configurations.SaveConfiguration(config);
        progressCallback?.Invoke(p, fileCount, 0, true);
    }
}

public class InstallingInformation {
    [JsonPropertyName("Version")] public required string Version { get; set; }
    [JsonPropertyName("Url")] public required string Url { get; set; }
    [JsonPropertyName("Path")] public required string Path { get; set; }
    [JsonPropertyName("Length")] public required long Length { get; set; }
}