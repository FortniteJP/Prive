using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("party")]
public class PartyController : ControllerBase {
    [HttpGet("api/v1/Fortnite/user/{accountId}")]
    public object FortniteUser() {
        var accountId = Request.RouteValues["accountId"] as string;
        return new {
            current = Parties.Where(x => x.Members.Any(y => y.AccountId == accountId)).ToArray() ?? new object[0],
            invites = new object[0],
            pending = new object[0],
            pings = new object[0]
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