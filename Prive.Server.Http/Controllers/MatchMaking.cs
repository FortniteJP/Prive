using Microsoft.AspNetCore.Mvc;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("")]
public class MatchMakingController : ControllerBase {
    private static List<WebSocket> Connections { get; } = new();
    private object ConnectionsLock { get; } = new();
    public static bool TimeToGo { get; internal set; } = false;
    public static bool AutoMatchMaking { get; internal set; } = true;
    public static DateTime LastMatchTime { get; internal set; } = new();
    public static DateTime MatchMakingStartTime { get; internal set; } = new();
    public static bool MatchMakingStarted { get; internal set; } = false;
    public static bool Starting { get; internal set; } = false;

    [Route("matchmaking")] [NoAuth]
    public async Task<object?> MatchMaking() {
        if (!HttpContext.WebSockets.IsWebSocketRequest) {
            Response.StatusCode = 400;
            return null;
        }

        // Console.WriteLine(Request.Headers.Authorization.ToString());
        // Console.WriteLine(Request.Headers.Authorization.ToString().Split(" ")[2]);
        // Console.WriteLine(Encoding.UTF8.GetString(Convert.FromBase64String(Request.Headers.Authorization.ToString().Split(" ")[2])));

        var obj = JsonSerializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(Request.Headers.Authorization.ToString().Split(" ")[2]))) ?? throw new Exception("Invalid payload");
        var bucketId = obj["bucketId"].ToString()!;
        var playlistId = bucketId.Split(":")[5];
        Console.WriteLine($"MatchMaking: {playlistId} ({bucketId})");
        
        using var client = await HttpContext.WebSockets.AcceptWebSocketAsync();

        try {
            lock (ConnectionsLock) Connections.Add(client);
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
            #if DEBUG
            await client.SendAsync(JsonSerializer.Serialize(new {
                    payload = new {
                        state = "Queued",
                        ticketId = "TEST_TICKET_ID",
                        queuedPlayers = Connections.Count,
                        estimatedWaitSec = 0,
                        status = new {}
                    },
                    name = "StatusUpdate"
                }));
            #else
            while (!TimeToGo) {
                OnNewConnection();
                // Console.WriteLine($"{u}, {client.State.ToString()}, {Connections}");
                // if (client.State != System.Net.WebSockets.WebSocketState.Open) {
                await client.SendAsync(JsonSerializer.Serialize(new {
                    payload = new {
                        state = "Queued",
                        ticketId = "TEST_TICKET_ID",
                        queuedPlayers = Connections.Count,
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
            #endif
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
        } catch (Exception e) {
            Console.WriteLine(e);
        } finally {
            lock (ConnectionsLock) Connections.Remove(client);
        }
        return null;
    }

    public async void OnNewConnection() {
        if (!AutoMatchMaking) return;
        if (!MatchMakingStarted) {
            MatchMakingStarted = true;
            MatchMakingStartTime = DateTime.Now;
        }
        Console.WriteLine($"Connections.Count < 10: {Connections.Count < 10}");
        Console.WriteLine($"2: {DateTime.Now - MatchMakingStartTime < TimeSpan.FromMinutes(10)} => {(long)(DateTime.Now - MatchMakingStartTime).TotalSeconds} < {TimeSpan.FromMinutes(10)}");
        
        // prevent if connections count is less than 10 and MatchMakingStartTime is less than 10 minutes
        if (Connections.Count < 10 && DateTime.Now - MatchMakingStartTime < TimeSpan.FromMinutes(10)) return;
        // prevent if match is started in 20 minutes
        if (DateTime.Now - LastMatchTime < TimeSpan.FromMinutes(20)) return;
        if (Starting) return;
        Starting = true;
        Program.Instance?.Kill();
        Program.Instance = new ServerInstance(ServerApiController.ShippingLocation);
        Console.WriteLine("Launching server...");
        Program.Instance.Launch();
        Console.WriteLine("Injecting dll...");
        Program.Instance.InjectDll(ServerApiController.ClientNativeDllLocation);
        Console.WriteLine("Waiting for log...");
        await Program.Instance.WaitForLogAndInjectDll(line => line.Contains("LogHotfixManager: Verbose: Using default hotfix"), ServerApiController.ServerNativeDllLocation);
        Console.WriteLine("Waiting for server to be ready...");
        await Task.Delay(60 * 1000);
        LastMatchTime = DateTime.Now;
        Console.WriteLine("Starting match...");
        TimeToGo = true;
        await Task.Delay(60 * 1000);
        Console.WriteLine("Resetting...");
        TimeToGo = false;
        Console.WriteLine("Starting bus...");
        try {
            await ServerApiController.CClient.StartBus();
        } catch (TimeoutException) {
            Console.WriteLine("StartBus() timed out!");
            Program.Instance?.Kill();
        }
        MatchMakingStarted = false;
        Starting = false;
        Console.WriteLine("Done");
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