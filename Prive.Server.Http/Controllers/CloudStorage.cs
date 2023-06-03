using Microsoft.AspNetCore.Mvc;
using Prive.Server.Http.CloudStorage;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("fortnite")]
public class CloudStorageController : ControllerBase {
    public static List<CloudStorageFile> CloudStorageFiles = typeof(CloudStorageFile).Assembly.GetTypes().Where(x => x.IsSubclassOf(typeof(CloudStorageFile))).Select(x => (CloudStorageFile)Activator.CreateInstance(x)!).ToList();
    
    #if DEBUG
    [NoAuth]
    #endif
    [HttpGet("api/cloudstorage/system")]
    public object[] CloudStorageSystem() {
        var result = new List<object>();

        foreach (var file in CloudStorageFiles) {
            result.Add(new {
                uniqueFilename = file.Filename,
                filename = file.Filename,
                hash = file.ComputeSHA1(),
                hash256 = file.ComputeSHA256(),
                length = file.Length,
                contentType = "application/octet-stream",
                uploaded = file.LastModified,
                storageType = "S3",
                doNotCache = false
            });
        }
        return result.ToArray();
    }

    #if DEBUG
    [NoAuth]
    #endif
    [HttpGet("api/cloudstorage/system/{filename}")]
    public object CloudStorageSystemFile() {
        var filename = Request.RouteValues["filename"] as string;
        // var filepath = Path.Combine(CloudStorageLocation, filename ?? "");
        // Console.WriteLine($"{System.Text.Json.JsonSerializer.Serialize(CloudStorageFiles.Select(x => x.Filename))} {filename} {CloudStorageFiles.Any(x => x.Filename == filename)}");
        if (string.IsNullOrEmpty(filename) || !CloudStorageFiles.Any(x => x.Filename == filename)) {
            Response.StatusCode = 404;
            return EpicError.Create(
                "errors.com.epicgames.cloudstorage.file_not_found", 12004,
                $"Sorry, we couldn't find a system file for {filename}",
                "fortnite", "prod-live", new[] { filename ?? "" }
            );
        }
        return File(CloudStorageFiles.First(x => x.Filename == filename).Data, "application/octet-stream", filename);
    }

    [HttpGet("api/cloudstorage/user/{accountId}")]
    public object CloudStorageUser() => new object[0];

    [HttpGet("api/cloudstorage/user/{accountId}/{filename}")]
    public IActionResult CloudStorageUserFile() => NoContent();
    
    [HttpPut("api/cloudstorage/user/{accountId}/{filename}")]
    public IActionResult CloudStorageUserFilePut() => NoContent();
}

/* public class CloudStorageFile {
    static CloudStorageFile() {
        if (!Directory.Exists(CloudStorageLocation)) {
            Directory.CreateDirectory(CloudStorageLocation);
        }
    }

    public static CloudStorageFile[] LoadAll() => Directory.GetFiles(CloudStorageLocation, "*.ini").Aggregate<string, IEnumerable<CloudStorageFile>>(new List<CloudStorageFile>(), (r, x) => r.Append(Load(x))).ToArray();

    public static CloudStorageFile Load(string filepath) {
        if (!File.Exists(filepath)) throw new FileNotFoundException();
        var filename = Path.GetFileName(filepath);
        using var file = File.OpenRead(filepath);
        return new() {
            Filepath = filepath,
            Filename = filename,
            Length = file.Length,
            LastModified = File.GetLastWriteTime(filepath)
        };
    }

    public required string Filepath { get; init; }
    public required string Filename { get; init; }
    public required long Length { get; init; }
    public required DateTime LastModified { get; init; }

    public async Task<string> ComputeSHA1() {
        using var sha1 = SHA1.Create();
        using var stream = File.OpenRead(Filepath);
        return Convert.ToHexString(await sha1.ComputeHashAsync(stream));
    }

    public async Task<string> ComputeSHA256() {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(Filepath);
        return Convert.ToHexString(await sha256.ComputeHashAsync(stream));
    }
} */