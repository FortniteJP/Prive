using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("fortnite")]
public class FortniteController : ControllerBase {
    [HttpGet("api/v2/versioncheck/Windows")] [NoAuth]
    public object VersionCheckWindows() => new {
        type = "NO_UPDATE"
    };

    [HttpPost("api/game/v2/tryPlayOnPlatform/account/{accountId}")]
    public object TryPlayOnPlatform() {
        Response.Headers.ContentType = "text/plain";
        return "true";
    }

    [HttpGet("api/game/v2/enabled_features")]
    public object EnabledFeatures() => new object[0];

    [HttpGet("api/storefront/v2/keychain")]
    public object StorefrontKeychain() => KeyChain;

    [HttpGet("api/game/v2/matchmakingservice/ticket/player/{accountId}")]
    public object MatchMakingServiceTicket() {
        var accountId = Request.RouteValues["accountId"]?.ToString() ?? "";
        var netCL = "";
        var region = "";
        var playlist = "";
        var hotfixVersion = -1;

        var splitted = Request.Query["bucketId"].First()!.Split(':');
        Console.WriteLine($"Bucket: {string.Join(", ", splitted)}");
        netCL = splitted[0];
        hotfixVersion = int.Parse(splitted[1]);
        region = splitted[2];
        playlist = splitted[3];
        // wtf
        // playlist = splitted[2];
        // region = splitted[3];

        // Console.WriteLine($"{playlist.ToLower()} != Playlist_DefaultSolo.ToLower() => {playlist.ToLower() != "Playlist_DefaultSolo".ToLower()}");

        bool isPlaylistSupported(string playlistId) => (new string[] {
            "Playlist_DefaultSolo",
            "Playlist_Auto_Solo",
        }).Any(x => x.Equals(playlistId, StringComparison.InvariantCultureIgnoreCase));
        
        if (!isPlaylistSupported(playlist)) {
            Response.StatusCode = 406;
            return EpicError.Create(
                "errors.prive.server.unsupported_playlist", 1,
                "This playlist is not supported for now.",
                "Fortnite"
            );
        }

        Response.Cookies.Append("NetCL", netCL);

        var data = new {
            playerId = accountId,
            partyPlayerIds = new[] { accountId },
            bucketId = $"FN:Live:{netCL}:{hotfixVersion}:{region}:{playlist}:PC:public:1",
            attributes = new Dictionary<string, string>() {
                ["player.userAgent"] = Request.Headers.UserAgent.ToString(),
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
            serviceUrl = $"{(Request.Host.Value == "api.fortnite.day" ? "wss" : "ws")}://{Request.Host.Value}/matchmaking",
            ticketType = "mms-player",
            payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(data)))
        };
    }

    [HttpGet("api/game/v2/privacy/account/{accountId}")]
    public object PrivacyAccount() => new {
        acceptInvites = "public"
    };

    [HttpGet("api/game/v2/world/info")]
    public object WorldInfo() => new();

    [HttpPost("api/game/v2/grant_access")]
    public IActionResult GrantAccess() => NoContent();
    
    [HttpGet("api/storefront/v2/catalog")]
    public object StorefrontCatalog() => ItemShop;

    [HttpGet("api/receipts/v1/account/{accountId}/receipts")]
    public object AccountReceipts() => new object[0];

    [HttpPost("api/game/v2/profileToken/verify/{accountId}")]
    public IActionResult ProfileTokenVerify() => NoContent();
}