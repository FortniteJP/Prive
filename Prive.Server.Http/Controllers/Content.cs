using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("content")]
public class ContentController : ControllerBase {
    [HttpGet("api/pages/fortnite-game")] [NoAuth]
    public object PagesFortniteGame() => new object();
}