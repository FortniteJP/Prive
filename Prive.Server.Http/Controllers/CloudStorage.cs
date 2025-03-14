using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Prive.Server.Http.CloudStorage;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("fortnite")]
public class CloudStorageController : ControllerBase {
    public static List<CloudStorageFile> CloudStorageFiles = typeof(CloudStorageFile).Assembly.GetTypes().Where(x => x.IsSubclassOf(typeof(CloudStorageFile)) && x.Name.StartsWith("Default")).Select(x => (CloudStorageFile)Activator.CreateInstance(x)!).ToList();
    
    #if DEBUG
    [NoAuth]
    #endif
    [HttpGet("api/cloudstorage/system")]
    public object[] CloudStorageSystem() {
        // NoAuthAttribute doesn't store the token to context
        var token = Request.Headers["Authorization"].FirstOrDefault()?.Split(" ").LastOrDefault();
        // if (AuthTokens.FirstOrDefault(x => x.TokenString == token) is var authToken && authToken is not null) Console.WriteLine($"\nGET FROM {System.Text.Json.JsonSerializer.Serialize(authToken)}\n");
        // else if (ClientTokens.FirstOrDefault(x => x.TokenString == token) is var clientToken && clientToken is not null) Console.WriteLine($"\nGET FROM {System.Text.Json.JsonSerializer.Serialize(clientToken)}\n");
        // else Console.WriteLine($"\nGET FROM UNKNOWN {System.Text.Json.JsonSerializer.Serialize(HttpContext.Items)}\n");
        var authToken = AuthTokens.FirstOrDefault(x => x.TokenString == token);
        // Console.WriteLine($"AuthToken is {authToken?.AccountId ?? "null"}");
        var result = new List<object>();

        foreach (var file in CloudStorageFiles) {
            if (file is DefaultGame && authToken is not null && (authToken.AccountId == "42fc8fa6481f4cb4b75f97a514540304" || authToken.AccountId == "454404e79a6b4038a8f04485b41795eb")) {
                // Console.WriteLine("\nGET FROM MODIFIED DEFAULT GAME\n");
                var newFile = new ModifiedDefaultGame();
                result.Add(new {
                    uniqueFilename = newFile.Filename,
                    filename = newFile.Filename,
                    hash = newFile.ComputeSHA1(),
                    hash256 = newFile.ComputeSHA256(),
                    length = newFile.Length,
                    contentType = "application/octet-stream",
                    uploaded = newFile.LastModified,
                    storageType = "S3",
                    doNotCache = false
                });
            } else result.Add(new {
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
        // Console.WriteLine($"Result is {System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })}");
        return result.ToArray();
    }

    #if DEBUG
    [NoAuth]
    #endif
    [HttpGet("api/cloudstorage/system/{filename}")]
    public object CloudStorageSystemFile() {
        var filename = Request.RouteValues["filename"] as string;
        if (filename == "config") return new object();
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
        var token = Request.Headers["Authorization"].FirstOrDefault()?.Split(" ").LastOrDefault();
        var authToken = AuthTokens.FirstOrDefault(x => x.TokenString == token);
        // Console.WriteLine($"AuthToken is {authToken?.AccountId ?? "null"}");
        if (filename == "DefaultGame.ini" && authToken is not null && (authToken.AccountId == "42fc8fa6481f4cb4b75f97a514540304" || authToken.AccountId == "454404e79a6b4038a8f04485b41795eb")) {
            var newFile = new ModifiedDefaultGame();
            return File(newFile.Data, "application/octet-stream", filename);
        }
        return File(CloudStorageFiles.First(x => x.Filename == filename).Data, "application/octet-stream", filename);
    }

    [HttpGet("api/cloudstorage/user/{accountId}")]
    public object CloudStorageUser() {
        var accountId = Request.RouteValues["accountId"] as string;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"cloudstorage", "ALL", "fortnite");
        }
        if (string.IsNullOrEmpty(accountId)) {
            Response.StatusCode = 404;
            return EpicError.Create(
                "errors.com.epicgames.cloudstorage.account_not_found", 12004,
                $"Sorry, we couldn't find an account for {accountId}",
                "fortnite", "prod-live", new[] { accountId ?? "" }
            );
        }
        return GetCloudStorageFiles(accountId);
    }

    [HttpGet("api/cloudstorage/user/{accountId}/{filename}")]
    public object CloudStorageUserFile() {
        var accountId = Request.RouteValues["accountId"] as string;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"cloudstorage", "ALL", "fortnite");
        }
        if (string.IsNullOrEmpty(accountId)) {
            Response.StatusCode = 404;
            return EpicError.Create(
                "errors.com.epicgames.cloudstorage.account_not_found", 12004,
                $"Sorry, we couldn't find an account for {accountId}",
                "fortnite", "prod-live", new[] { accountId ?? "" }
            );
        }
        var filename = Request.RouteValues["filename"] as string;
        var filepath = Path.Combine(CloudStorageLocation, $"{accountId}_{filename?.ToLower() ?? ""}");
        if (!System.IO.File.Exists(filepath)) {
            Response.StatusCode = 404;
            return EpicError.Create(
                "errors.com.epicgames.cloudstorage.file_not_found", 12004,
                $"Sorry, we couldn't find a file for {filename}",
                "fortnite", "prod-live", new[] { filename ?? "" }
            );
        }
        return File(System.IO.File.OpenRead(filepath), "application/octet-stream", filename);
    }
    
    [HttpPut("api/cloudstorage/user/{accountId}/{filename}")]
    public async Task<object?> CloudStorageUserFilePut() {
        var accountId = Request.RouteValues["accountId"] as string;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"cloudstorage", "ALL", "fortnite");
        }
        if (string.IsNullOrEmpty(accountId)) {
            Response.StatusCode = 404;
            return EpicError.Create(
                "errors.com.epicgames.cloudstorage.account_not_found", 12004,
                $"Sorry, we couldn't find an account for {accountId}",
                "fortnite", "prod-live", new[] { accountId ?? "" }
            );
        }
        Response.StatusCode = 204;
        var filename = Request.RouteValues["filename"] as string;
        if (filename?.ToLower() != "clientsettings.sav") return null;
        var filepath = Path.Combine(CloudStorageLocation, $"{accountId}_{filename.ToLower() ?? ""}");
        using var file = System.IO.File.OpenWrite(filepath);
        await Request.Body.CopyToAsync(file);
        return null;
    }

    public object GetCloudStorageFiles(string accountId) {
        var prefix = $"{accountId}_";
        var files = Directory.GetFiles(CloudStorageLocation).Where(x => Path.GetFileName(x).StartsWith(prefix));
        return files.Select(x => new {
            uniqueFilename = Path.GetFileName(x).Replace(prefix, ""),
            filename = Path.GetFileName(x).Replace(prefix, ""),
            hash = ComputeSHA1(x),
            hash256 = ComputeSHA256(x),
            length = new FileInfo(x).Length,
            contentType = "application/octet-stream",
            uploaded = System.IO.File.GetLastWriteTime(x),
            storageType = "S3",
            doNotCache = false
        }).ToArray();
    }

    public string ComputeSHA1(string filePath) {
        return Convert.ToHexString(SHA1.HashData(System.IO.File.ReadAllBytes(filePath)));
    }

    public string ComputeSHA256(string filePath) {
        return Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(filePath)));
    }
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