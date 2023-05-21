using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
public class AccountController : ControllerBase {
    [HttpPost("/api/oauth/token")] [NoAuth]
    public async Task<object> OAuth_Token() {
        return "OAUTH_TOKEN";
    }
}
