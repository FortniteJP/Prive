using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("")]
public class MatchMakingController : ControllerBase {
    private static int Connections = 0;
    private object ConnectionsLock = new();
    public static bool TimeToGo = false;

    [Route("matchmaking")] [NoAuth]
    public async Task<object?> MatchMaking() {
        if (!HttpContext.WebSockets.IsWebSocketRequest) {
            Response.StatusCode = 400;
            return null;
        }
        using var client = await HttpContext.WebSockets.AcceptWebSocketAsync();
        lock (ConnectionsLock) Connections++;

        try {
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
            var u = GenerateToken();
            while (!TimeToGo) {
                Console.WriteLine($"{u}, {client.State.ToString()}, {Connections}");
                // if (client.State != System.Net.WebSockets.WebSocketState.Open) {
                await client.SendAsync(JsonSerializer.Serialize(new {
                    payload = new {
                        state = "Queued",
                        ticketId = "TEST_TICKET_ID",
                        queuedPlayers = Connections,
                        estimatedWaitSec = 0,
                        status = new {}
                    },
                    name = "StatusUpdate"
                }));
                if (await Disconnected(client)) {
                    throw new Exception("Client disconnected");
                }
                await Task.Delay(5000);
            }
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
            lock (ConnectionsLock) Connections--;
        } catch (Exception e) {
            Console.WriteLine(e);
            lock (ConnectionsLock) Connections--;
        }
        return null;
    }

    public static async Task<bool> Disconnected(System.Net.WebSockets.WebSocket client) {
        using var timeoutCTS = new CancellationTokenSource();
        var result = client.ReceiveAsync(new byte[0], timeoutCTS.Token);
        var timeout = Task.Delay(5000, timeoutCTS.Token);
        var completed = await Task.WhenAny(result, timeout);
        if (completed == timeout) {
            return false;
        } else return client.State != System.Net.WebSockets.WebSocketState.Open;
    }
}