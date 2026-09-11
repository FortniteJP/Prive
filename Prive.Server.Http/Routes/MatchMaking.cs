using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Prive.Server.Http.Routes;

public static class MatchMakingRoutes {
    #if DEBUG
    public static MatchMakingManager MatchMakingManagerSolo { get; } = new("Playlist_DefaultSolo", TimeSpan.FromMinutes(1));
    public static MatchMakingManager MatchMakingManagerLateGameSolo { get; } = new("Playlist_Auto_Solo", TimeSpan.FromMinutes(1));
    #else
    public static MatchMakingManager MatchMakingManagerSolo { get; } = new("Playlist_DefaultSolo", TimeSpan.FromMinutes(5));
    public static MatchMakingManager MatchMakingManagerLateGameSolo { get; } = new("Playlist_Auto_Solo", TimeSpan.FromMinutes(3));
    #endif
    public static Dictionary<string, string> SessionIds { get; } = new();

    public static void Map(IEndpointRouteBuilder app) {
        // Any method - the client upgrades this one to a WebSocket.
        app.Map("/matchmaking", (Delegate)MatchMaking).NoAuth();

        app.MapGet("/fortnite/api/matchmaking/session/{sessionId}", MatchMakingSession);
        app.MapGet("/fortnite/api/game/v2/matchmaking/account/{accountId}/session/{sessionId}", MatchMakingAccountSession);
        app.MapPost("/fortnite/api/matchmaking/session/{sessionId}/join", () => Results.NoContent());
        app.MapGet("/fortnite/api/matchmaking/session/findPlayer/{accountId}", MatchMakingSessionFindPlayer);
    }

    static async Task<object?> MatchMaking(HttpContext ctx) {
        if (!ctx.WebSockets.IsWebSocketRequest) return Results.StatusCode(400);

        var obj = JsonSerializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(ctx.Request.Headers.Authorization.ToString().Split(" ")[2]))) ?? throw new Exception("Invalid payload");
        var bucketId = obj["bucketId"].ToString()!;
        var playlistId = bucketId.Split(":")[5];
        Console.WriteLine($"MatchMaking: {playlistId} ({bucketId})");

        using var client = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext() { KeepAliveInterval = TimeSpan.FromSeconds(5), KeepAliveTimeout = TimeSpan.FromHours(1) });

        // HandleClient owns the close now. It has an outstanding receive on this socket, so a
        // CloseAsync here would be a second concurrent receive and throw.
        if (playlistId.Equals("Playlist_DefaultSolo", StringComparison.InvariantCultureIgnoreCase)) {
            await MatchMakingManagerSolo.HandleClient(client);
        } else if (playlistId.Equals("Playlist_Auto_Solo", StringComparison.InvariantCultureIgnoreCase)) {
            await MatchMakingManagerLateGameSolo.HandleClient(client);
        } else {
            await client.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid playlist!", CancellationToken.None);
            return null;
        }
        return null;
    }

    static object MatchMakingSession(HttpContext ctx, string sessionId) {
        var playlistId = SessionIds.ContainsKey(sessionId) ? SessionIds[sessionId] : "Playlist_DefaultSolo";
        var buildUniqueId = ctx.Request.Cookies["NetCL"];
        SessionIds.Remove(sessionId);

        var r = new {
            id = sessionId,
            ownerId = "Prive",
            ownerName = "Prive",
            serverName = "PriveAsia",
            #if DEBUG
            serverAddress = "192.168.11.10",
            serverPort = playlistId.Equals("Playlist_DefaultSolo", StringComparison.InvariantCultureIgnoreCase) ? 20000 : 20001,
            #else
            serverAddress = ServerApiRoutes.IP,
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

    static object MatchMakingAccountSession(string accountId, string sessionId) => new {
        accountId = accountId,
        sessionId = sessionId,
        key = "none"
    };

    static IResult MatchMakingSessionFindPlayer() {
        Console.WriteLine("MatchMakingSessionFindPlayer");
        return Results.NoContent();
    }

    // this doesnt work well, im looking for the correct way
    public static async Task<bool> Disconnected(WebSocket client) {
        using var timeoutCTS = new CancellationTokenSource();
        var result = client.ReceiveAsync(new byte[0], timeoutCTS.Token);
        var timeout = Task.Delay(5000, timeoutCTS.Token);
        var completed = await Task.WhenAny(result, timeout);
        if (completed == timeout) {
            // timeoutCTS.Cancel(); // throws System.Net.WebSockets.WebSocketException `The WebSocket is in an invalid state ('Aborted') for this operation. Valid states are: 'Open, CloseReceived'`
            return false;
        } else return client.State != WebSocketState.Open;
    }
}
