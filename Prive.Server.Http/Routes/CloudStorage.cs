using System.Security.Cryptography;
using Prive.Server.Http.CloudStorage;

namespace Prive.Server.Http.Routes;

public static class CloudStorageRoutes {
    public static List<CloudStorageFile> CloudStorageFiles = typeof(CloudStorageFile).Assembly.GetTypes().Where(x => x.IsSubclassOf(typeof(CloudStorageFile)) && x.Name.StartsWith("Default")).Select(x => (CloudStorageFile)Activator.CreateInstance(x)!).ToList();

    public static void Map(IEndpointRouteBuilder app) {
        var fortnite = app.MapGroup("/fortnite");

        // The two system routes are left open in Debug so the ini files can be diffed with curl.
        fortnite.MapGet("/api/cloudstorage/system", CloudStorageSystem).NoAuthInDebug();
        fortnite.MapGet("/api/cloudstorage/system/{filename}", CloudStorageSystemFile).NoAuthInDebug();
        fortnite.MapGet("/api/cloudstorage/user/{accountId}", CloudStorageUser);
        fortnite.MapGet("/api/cloudstorage/user/{accountId}/{filename}", CloudStorageUserFile);
        fortnite.MapPut("/api/cloudstorage/user/{accountId}/{filename}", CloudStorageUserFilePut);
    }

    static object[] CloudStorageSystem(HttpContext ctx) {
        // NoAuth doesn't store the token to the context, so read the header directly
        var token = ctx.Request.Headers["Authorization"].FirstOrDefault()?.Split(" ").LastOrDefault();
        var authToken = token is null ? null : AuthTokens.GetValueOrDefault(token);
        var result = new List<object>();

        foreach (var file in CloudStorageFiles) {
            if (file is DefaultGame && authToken is not null && (authToken.AccountId == "42fc8fa6481f4cb4b75f97a514540304" || authToken.AccountId == "454404e79a6b4038a8f04485b41795eb")) {
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
        return result.ToArray();
    }

    static object CloudStorageSystemFile(HttpContext ctx, string filename) {
        if (filename == "config") return new object();
        if (!CloudStorageFiles.Any(x => x.Filename == filename)) {
            ctx.Response.StatusCode = 404;
            return EpicError.Create(
                "errors.com.epicgames.cloudstorage.file_not_found", 12004,
                $"Sorry, we couldn't find a system file for {filename}",
                "fortnite", "prod-live", new[] { filename }
            );
        }
        var token = ctx.Request.Headers["Authorization"].FirstOrDefault()?.Split(" ").LastOrDefault();
        var authToken = token is null ? null : AuthTokens.GetValueOrDefault(token);
        if (filename == "DefaultGame.ini" && authToken is not null && (authToken.AccountId == "42fc8fa6481f4cb4b75f97a514540304" || authToken.AccountId == "454404e79a6b4038a8f04485b41795eb")) {
            var newFile = new ModifiedDefaultGame();
            return Results.File(newFile.Data, "application/octet-stream", filename);
        }
        return Results.File(CloudStorageFiles.First(x => x.Filename == filename).Data, "application/octet-stream", filename);
    }

    static object CloudStorageUser(HttpContext ctx, string accountId) {
        if (ctx.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"cloudstorage", "ALL", "fortnite");
        }
        return GetCloudStorageFiles(accountId);
    }

    static object CloudStorageUserFile(HttpContext ctx, string accountId, string filename) {
        if (ctx.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"cloudstorage", "ALL", "fortnite");
        }
        var filepath = Path.Combine(CloudStorageLocation, $"{accountId}_{filename.ToLower()}");
        if (!File.Exists(filepath)) {
            ctx.Response.StatusCode = 404;
            return EpicError.Create(
                "errors.com.epicgames.cloudstorage.file_not_found", 12004,
                $"Sorry, we couldn't find a file for {filename}",
                "fortnite", "prod-live", new[] { filename }
            );
        }
        return Results.File(File.OpenRead(filepath), "application/octet-stream", filename);
    }

    static async Task<object> CloudStorageUserFilePut(HttpContext ctx, string accountId, string filename) {
        if (ctx.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"cloudstorage", "ALL", "fortnite");
        }
        if (filename.ToLower() != "clientsettings.sav") return Results.NoContent();
        var filepath = Path.Combine(CloudStorageLocation, $"{accountId}_{filename.ToLower()}");
        using var file = File.OpenWrite(filepath);
        await ctx.Request.Body.CopyToAsync(file);
        return Results.NoContent();
    }

    static object GetCloudStorageFiles(string accountId) {
        var prefix = $"{accountId}_";
        var files = Directory.GetFiles(CloudStorageLocation).Where(x => Path.GetFileName(x).StartsWith(prefix));
        return files.Select(x => new {
            uniqueFilename = Path.GetFileName(x).Replace(prefix, ""),
            filename = Path.GetFileName(x).Replace(prefix, ""),
            hash = ComputeSHA1(x),
            hash256 = ComputeSHA256(x),
            length = new FileInfo(x).Length,
            contentType = "application/octet-stream",
            uploaded = File.GetLastWriteTime(x),
            storageType = "S3",
            doNotCache = false
        }).ToArray();
    }

    static string ComputeSHA1(string filePath) => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(filePath)));

    static string ComputeSHA256(string filePath) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath)));
}
