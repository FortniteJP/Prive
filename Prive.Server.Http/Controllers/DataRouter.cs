using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("datarouter")]
public class DataRouterController : ControllerBase {
    [HttpPost("api/v1/public/data")] [NoAuth]
    public async Task<object> PublicData() {
        Console.WriteLine("DataRouter posted");
        using var reader = new StreamReader(Request.Body);
        Console.WriteLine(await reader.ReadToEndAsync());
        return new object();
    }
}