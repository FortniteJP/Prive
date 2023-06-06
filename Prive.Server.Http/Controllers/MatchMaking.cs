using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("")]
public class MatchMakingController : ControllerBase {
    [Route("/matchmaking")]
    public async Task<object?> MatchMaking() {
        if (!HttpContext.WebSockets.IsWebSocketRequest) {
            Response.StatusCode = 400;
            return null;
        }
        using var client = await HttpContext.WebSockets.AcceptWebSocketAsync();

        await client.SendAsync(JsonSerializer.Serialize(new {
            payload = new {
                state = "Connecting"
            },
            name = "StatusUpdate"
        }));
        await Task.Delay(500);
        await client.SendAsync(JsonSerializer.Serialize(new {
            payload = new {
                state = "Waiting",
                totalPlayers = 1,
                connectedPlayers = 1
            },
            name = "StatusUpdate"
        }));
        await Task.Delay(500);
        await client.SendAsync(JsonSerializer.Serialize(new {
            payload = new {
                state = "Queued",
                ticketId = "TEST_TICKET_ID",
                queuedPlayers = 1,
                estimatedWaitSec = 0,
                status = new {}
            },
            name = "StatusUpdate"
        }));
        await Task.Delay(500);
        await client.SendAsync(JsonSerializer.Serialize(new {
            payload = new {
                state = "SessionAssignment",
                matchId = "TEST_MATCH_ID"
            },
            name = "StatusUpdate"
        }));
        await Task.Delay(500);
        await client.SendAsync(JsonSerializer.Serialize(new {
            payload = new {
                matchId = "TEST_MATCH_ID",
                sessionId = "TEST_SESSION_ID",
                joinDelaySec = 1
            },
            name = "Play"
        }));
        await Task.Delay(500);
        return null;
    }
}