using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("")]
public class MatchMakingController : ControllerBase {
    public static MatchMakingManager MatchMakingManagerSolo { get; } = new("Playlist_DefaultSolo", TimeSpan.FromMinutes(5));
    public static MatchMakingManager MatchMakingManagerLateGameSolo { get; } = new("Playlist_Auto_Solo", TimeSpan.FromMinutes(3));
    public static Dictionary<string, string> SessionIds { get; } = new();

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
        
        using var client = await HttpContext.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext() { KeepAliveInterval = TimeSpan.FromSeconds(5) });

        if (playlistId.Equals("Playlist_DefaultSolo", StringComparison.InvariantCultureIgnoreCase)) {
            await MatchMakingManagerSolo.HandleClient(client);
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Finished", CancellationToken.None);
        } else if (playlistId.Equals("Playlist_Auto_Solo", StringComparison.InvariantCultureIgnoreCase)) {
            await MatchMakingManagerLateGameSolo.HandleClient(client);
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Finished", CancellationToken.None);
        } else {
            await client.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid playlist!", CancellationToken.None);
            return null;
        }
        return null;
    }

    [HttpGet("fortnite/api/matchmaking/session/{sessionId}")]
    public object MatchMakingSession() {
        var sessionId = Request.RouteValues["sessionId"]?.ToString() ?? "TEST_SESSION_ID";
        var playlistId = SessionIds.ContainsKey(sessionId) ? SessionIds[sessionId] : "Playlist_DefaultSolo";
        var buildUniqueId = Request.Cookies["NetCL"];
        SessionIds.Remove(sessionId);

        var r = new {
            id = sessionId,
            ownerId = "Prive",
            ownerName = "Prive",
            serverName = "PriveAsia",
            #if DEBUG
            serverAddress = "127.0.0.1",
            serverPort = playlistId.Equals("Playlist_DefaultSolo", StringComparison.InvariantCultureIgnoreCase) ? 20000 : 20001,
            #else
            serverAddress = "180.52.134.178",
            serverPort = playlistId.Equals("Playlist_DefaultSolo", StringComparison.InvariantCultureIgnoreCase) ? 20000 : 20001,
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
                ["PLAYLISTNAME_s"] = playlistId,
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

    // this doesnt work well, im looking for the correct way
    public static async Task<bool> Disconnected(System.Net.WebSockets.WebSocket client) {
        using var timeoutCTS = new CancellationTokenSource();
        var result = client.ReceiveAsync(new byte[0], timeoutCTS.Token);
        var timeout = Task.Delay(5000, timeoutCTS.Token);
        var completed = await Task.WhenAny(result, timeout);
        if (completed == timeout) {
            // timeoutCTS.Cancel(); // throws System.Net.WebSockets.WebSocketException `The WebSocket is in an invalid state ('Aborted') for this operation. Valid states are: 'Open, CloseReceived'`
            return false;
        } else return client.State != System.Net.WebSockets.WebSocketState.Open;
    }
}