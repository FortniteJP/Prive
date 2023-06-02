using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("fortnite")]
public class FortniteController : ControllerBase {
    [HttpGet("api/v2/versioncheck/Windows")]
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
        Console.WriteLine("MatchMakingServiceTicket");
        Response.StatusCode = 401; // 501
        return EpicError.Create(
            "errors.not_implemented", 0,
            "Not Implemented",
            "fortnite", "prod-live"
        );
    }

    [HttpGet("api/game/v2/privacy/account/{accountId}")]
    public object PrivacyAccount() => new {
        acceptInvites = "public"
    };

    [HttpGet("api/game/v2/world/info")]
    public object WorldInfo() => new();

    [HttpGet("api/matchmaking/session/findPlayer/{accountId}")]
    public IActionResult MatchMakingSessionFindPlayer() {
        Console.WriteLine("MatchMakingSessionFindPlayer");
        return NoContent();
    }

    [HttpPost("api/game/v2/grant_access")]
    public IActionResult GrantAccess() => NoContent();
    
    [HttpGet("api/storefront/v2/catalog")]
    public object StorefrontCatalog() => ItemShop;

    [HttpGet("api/receipts/v1/account/{accountId}/receipts")]
    public object AccountReceipts() => new object[0];

    [HttpPost("api/game/v2/profileToken/verify/{accountId}")]
    public IActionResult ProfileTokenVerify() => NoContent();
}