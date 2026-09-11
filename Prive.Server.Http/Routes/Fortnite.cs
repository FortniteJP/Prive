namespace Prive.Server.Http.Routes;

public static class FortniteRoutes {
    public static void Map(IEndpointRouteBuilder app) {
        var fortnite = app.MapGroup("/fortnite");

        fortnite.MapGet("/api/v2/versioncheck/Windows", VersionCheckWindows).NoAuth();
        fortnite.MapPost("/api/game/v2/tryPlayOnPlatform/account/{accountId}", TryPlayOnPlatform);
        fortnite.MapGet("/api/game/v2/enabled_features", EnabledFeatures);
        fortnite.MapGet("/api/storefront/v2/keychain", StorefrontKeychain);
        fortnite.MapGet("/api/game/v2/matchmakingservice/ticket/player/{accountId}", MatchMakingServiceTicket);
        fortnite.MapGet("/api/game/v2/privacy/account/{accountId}", PrivacyAccount);
        fortnite.MapGet("/api/game/v2/world/info", WorldInfo);
        fortnite.MapPost("/api/game/v2/grant_access", GrantAccess);
        fortnite.MapGet("/api/storefront/v2/catalog", StorefrontCatalog);
        fortnite.MapGet("/api/receipts/v1/account/{accountId}/receipts", AccountReceipts);
        fortnite.MapPost("/api/game/v2/profileToken/verify/{accountId}", ProfileTokenVerify);
    }

    static object VersionCheckWindows() => new {
        type = "NO_UPDATE"
    };

    static object TryPlayOnPlatform(HttpContext ctx) {
        ctx.Response.Headers.ContentType = "text/plain";
        return "true";
    }

    static object EnabledFeatures() => new object[0];

    static object StorefrontKeychain() => KeyChain;

    static object MatchMakingServiceTicket(HttpContext ctx, string accountId) {
        var netCL = "";
        var region = "";
        var playlist = "";
        var hotfixVersion = -1;

        var splitted = ctx.Request.Query["bucketId"].First()!.Split(':');
        Console.WriteLine($"Bucket: {string.Join(", ", splitted)}");
        netCL = splitted[0];
        hotfixVersion = int.Parse(splitted[1]);
        region = splitted[2];
        playlist = splitted[3];

        bool isPlaylistSupported(string playlistId) => (new string[] {
            "Playlist_DefaultSolo",
            "Playlist_Auto_Solo",
        }).Any(x => x.Equals(playlistId, StringComparison.InvariantCultureIgnoreCase));

        if (!isPlaylistSupported(playlist)) {
            ctx.Response.StatusCode = 406;
            return EpicError.Create(
                "errors.prive.server.unsupported_playlist", 1,
                "This playlist is not supported for now.",
                "Fortnite"
            );
        }

        ctx.Response.Cookies.Append("NetCL", netCL);

        var data = new {
            playerId = accountId,
            partyPlayerIds = new[] { accountId },
            bucketId = $"FN:Live:{netCL}:{hotfixVersion}:{region}:{playlist}:PC:public:1",
            attributes = new Dictionary<string, string>() {
                ["player.userAgent"] = ctx.Request.Headers.UserAgent.ToString(),
                ["player.preferredSubregion"] = "None",
                ["player.option.spectator"] = "false",
                ["player.inputTypes"] = "",
                ["playlist.revision"] = "1",
                ["player.teamFormat"] = "unknown"
            },
            expireAt = DateTime.UtcNow.AddHours(1).ToString(DateTimeFormat),
            nonce = GenerateToken()
        };

        return new {
            serviceUrl = $"{(ctx.Request.Host.Value == "api.fortnite.day" ? "wss" : "ws")}://{ctx.Request.Host.Value}/matchmaking",
            ticketType = "mms-player",
            payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(data)))
        };
    }

    static object PrivacyAccount() => new {
        acceptInvites = "public"
    };

    static object WorldInfo() => new();

    static IResult GrantAccess() => Results.NoContent();

    static object StorefrontCatalog() => ItemShop;

    static object AccountReceipts() => new object[0];

    static IResult ProfileTokenVerify() => Results.NoContent();
}
