using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http;

[ApiController]
[Route("[controller]")]
public class WaitingRoomController : ControllerBase {
    [HttpGet("api/waitingroom")] [NoAuth]
    public IActionResult Get() => NoContent();
}