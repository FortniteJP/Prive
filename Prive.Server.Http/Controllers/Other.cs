using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("")]
public class OtherController : ControllerBase {
    [HttpPost("api/v1/user/setting")]
    public object UserSetting() {
        return new {};
    }
}