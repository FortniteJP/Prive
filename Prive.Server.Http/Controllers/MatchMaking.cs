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
            // var u = GenerateToken();
            while (!TimeToGo) {
                // Console.WriteLine($"{u}, {client.State.ToString()}, {Connections}");
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
                    sessionId = Guid.NewGuid().ToString().Replace("-", ""),
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

    [HttpGet("fortnite/api/matchmaking/session/{sessionId}")]
    public object MatchMakingSession() {
        var sessionId = Request.RouteValues["sessionId"]?.ToString() ?? "TEST_SESSION_ID";
        var buildUniqueId = Request.Cookies["NetCL"];
        
        var r = new {
            id = sessionId,
            ownerId = "Prive",
            ownerName = "Prive",
            serverName = "PriveAsia",
            #if DEBUG
            serverAddress = "127.0.0.1",
            serverPort = Port,
            #else
            serverAddress = "180.52.134.178",
            serverPort = Port,
            #endif
            totalPlayers = 45,
            maxPublicPlayers = 220,
            openPublicPlayers = 175,
            maxPrivatePlayers = 0,
            openPrivatePlayers = 0,
            attributes = new Dictionary<string, object>() {
                ["REGION_s"] = "ASIA",
                ["GAMEMODE_s"] = "FORTATHENA",
                ["ALLOWBROADCASTING_b"] = true,
                ["SUBREGION_s"] = "ASIA",
                ["DCID_s"] = "FORTNITE-LIVEASIA0000000000000000000-00000000",
                ["tenant_s"] = "Fortnite",
                ["MATCHMAKINGPOOL_s"] = "Any",
                ["STORMSHIELDDEFENSETYPE_i"] = 0,
                ["HOTFIXVERSION_i"] = 0,
                ["PLAYLISTNAME_s"] = "Playlist_DefaultSolo",
                ["SESSIONKEY_s"] = new Guid().ToString().Replace("-", ""),
                ["TENANT_s"] = "Fortnite",
                ["BEACONPORT_i"] = 20000
            },
            publicPlayers = new object[0],
            privatePlayers = new object[0],
            allowJoinInProgress = false,
            shouldAdvertise = false,
            isDedicated = false,
            usesStats = false,
            allowInvites = false,
            usesPresence = false,
            allowJoinViaPresence = true,
            allowJoinViaPresenceFriendsOnly = false,
            buildUniqueId = buildUniqueId ?? "0",
            lastUpdated = DateTimeOffset.UtcNow.ToString(DateTimeFormat),
            started = false
        };
        Console.WriteLine(JsonSerializer.Serialize(r));
        return r;
    }

    [HttpGet("fortnite/api/game/v2/matchmaking/account/{accountId}/session/{sessionId}")]
    public object MatchMakingAccountSession() {
        var accountId = Request.RouteValues["accountId"]?.ToString() ?? "";
        var sessionId = Request.RouteValues["sessionId"]?.ToString() ?? "";
        return new {
            accountId = accountId,
            sessionId = sessionId,
            key = "none"
        };
    }

    [HttpPost("fortnite/api/matchmaking/session/{sessionId}/join")]
    public IActionResult MatchMakingSessionJoin() => NoContent();

    [HttpGet("fortnite/api/matchmaking/session/findPlayer/{accountId}")]
    public IActionResult MatchMakingSessionFindPlayer() {
        Console.WriteLine("MatchMakingSessionFindPlayer");
        return NoContent();
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