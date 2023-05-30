using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text.Json;

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
}

public class CloudStorageFile {
    public static string CloudStorageLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Server/CloudStorage");

    static CloudStorageFile() {
        if (!Directory.Exists(CloudStorageLocation)) {
            Directory.CreateDirectory(CloudStorageLocation);
        }
    }

    public static CloudStorageFile[] LoadAll() {
        var files = new List<CloudStorageFile>();
        foreach (var filepath in Directory.GetFiles(CloudStorageLocation, "*.ini")) {
            var filename = Path.GetFileName(filepath);
            var file = File.OpenRead(filepath);
            files.Add(new() {
                Filepath = filepath,
                Filename = filename,
                Length = file.Length,
                LastModified = File.GetLastWriteTime(filepath)
            });
        }
        return files.ToArray();
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