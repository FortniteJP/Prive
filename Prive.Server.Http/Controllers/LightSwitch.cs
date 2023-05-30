using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("lightswitch")]
public class LightSwitchController : ControllerBase {
    [HttpGet("api/service/bulk/status")]
    public object ServiceBulkStatus() => BulkStatus;
}