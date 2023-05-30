using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("fortnite")]
public class CloudStorageController : ControllerBase {
    [HttpGet("api/cloudstorage/system")]
    public async Task<object[]> CloudStorageSystem() {
        var files = CloudStorageFile.LoadAll();
        var result = new object[files.Length];

        foreach (var file in files) {
            result.Append(new {
                uniqueFilename = file.Filename,
                filename = file.Filename,
                hash = await file.ComputeSHA1(),
                hash256 = await file.ComputeSHA256(),
                length = file.Length,
                contentType = "application/octet-stream",
                uploaded = file.LastModified,
                storageType = "S3",
                doNotCache = false
            });
        }
        return result;
    }

    [HttpGet("api/cloudstorage/system/{filename}")]
    public async Task<object> CloudStorageSystemFile() {
        var filename = Request.RouteValues["filename"] as string;
        if (string.IsNullOrEmpty(filename) || !System.IO.File.Exists(filename)) return EpicError.Create(
            "errors.com.epicgames.cloudstorage.file_not_found", 12004,
            $"Sorry, we couldn't find a system file for {filename}",
            "fortnite", "prod-live", new[] { filename ?? "" }
        );
        Response.Headers.ContentType = "application/octet-stream";
        return await System.IO.File.ReadAllBytesAsync(filename);
    }

    [HttpGet("api/cloudstorage/user/{accountId}")]
    public object CloudStorageUser() => new object[0];

    [HttpGet("api/cloudstorage/user/{accountId}/{filename}")]
    public IActionResult CloudStorageUserFile() => NoContent();
    
    [HttpPut("api/cloudstorage/user/{accountId}/{filename}")]
    public IActionResult CloudStorageUserFilePut() => NoContent();
}

public class CloudStorageFile {
    static CloudStorageFile() {
        if (!Directory.Exists(CloudStorageLocation)) {
            Directory.CreateDirectory(CloudStorageLocation);
        }
    }

    public static CloudStorageFile[] LoadAll() => Directory.GetFiles(CloudStorageLocation, "*.ini").Aggregate(new List<CloudStorageFile>(), (r, x) => {
        r.Add(Load(x));
        return r;
    }).ToArray();

    public static CloudStorageFile Load(string filename) {
        var filepath = Path.Combine(CloudStorageLocation, filename);
        if (!File.Exists(filepath)) throw new FileNotFoundException();
        var file = File.OpenRead(filepath);
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
}