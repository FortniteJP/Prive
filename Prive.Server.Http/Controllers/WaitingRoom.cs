using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("waitingroom")]
public class WaitingRoomController : ControllerBase {
    [HttpGet("api/waitingroom")] [NoAuth]
    public IActionResult Get() => NoContent();
}