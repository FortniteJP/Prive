using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http;

[ApiController]
[Route("fortnite")]
public class MCPController : ControllerBase {
    public static object CreateResponse(object changes, string profileId, int rvn = 0) => new {
        profileRevision = rvn + 1,
        profileId = profileId,
        profileChangesBaseRevision = rvn == 0 ? 1 : rvn,
        profileChanges = changes,
        profileCommandRevision = rvn + 1,
        serverTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        responseVersion = 1
    };

    [HttpPost("api/game/v2/profile/{accountId}/client/QueryProfile")]
    public async Task<object> QueryProfile() {
        var accountId = (string)Request.RouteValues["accountId"]!;
        var profileId = Request.Query["profileId"].First()!;
        if (HttpContext.Items["AuthToken"] is AuthToken authToken && authToken.AccountId != accountId) {
            return EpicError.Permission($"fortnite:profile:{accountId}:commands", "ALL", "fortnite");
        }

        Profile profile;
        switch (profileId) {
            case "athena":
                profile = await DB.GetAthenaProfile(accountId);
                break;
            case "profile0":
                profile = await DB.GetAthenaProfile(accountId);
                break;
            case "common_core":
                profile = await DB.GetCommonCoreProfile(accountId);
                break;
            case "creative":
            case "common_public":
            case "collection_book_schematics0":
            case "collection_book_people0":
            case "metadata":
            case "theater0":
            case "outpost0":
                return CreateResponse(new object[0], profileId);
            default:
                Response.StatusCode = 400;
                return EpicError.Create(
                    "errors.com.epicgames.modules.profiles.operation_forbidden", 12813,
                    $"Unable to find template configuration for profile {profileId}",
                    "fortnite", "prod-live", new[] { profileId }
                );
        }

        return CreateResponse(profile, profileId);
    }
}