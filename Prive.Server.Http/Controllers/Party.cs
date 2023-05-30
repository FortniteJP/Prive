using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("party")]
public class PartyController : ControllerBase {
    [HttpPost("api/v1/Fortnite/user/{accountId}")]
    public object FortniteUser() {
        return new {
            // WHAT
        };
    }

    [HttpPost("api/v1/Fortnite/parties")]
    public async Task<object> FortniteParties() {
        using var reader = new StreamReader(Request.Body);
        Console.WriteLine(await reader.ReadToEndAsync());

        return new {
        };
    }
}

public class PartyRequest {
    [K("config")] public required object Config { get; init; }
    [K("meta")] public required object Meta { get; init; }
    [K("join_info")] public required object JoinInfo { get; init; }
}