using System.Net.WebSockets;
using System.Text.Json;
using MongoDB.Driver;

namespace Prive.Server.Http;

public class MatchMakingManager {
    public bool UseEstimatedWaitTime { get; init; } = true;
    public string PlaylistId { get; init; }
    public TimeSpan MatchMakingTimeout { get; init; }
    public DateTime MatchMakingStartedAt { get; internal set; } = DateTime.Now;
    public DateTime LastMatchStartedAt { get; internal set; } = new();
    public bool IsListening { get; internal set; } = false;
    public int PlayersLeft { get; internal set; } = 0;
    public int TickInterval { get; init; }

    /// <summary>
    ///     Queued clients: the task that releases the request holding each socket open, and when
    ///     they joined. Entries are added from the request's own thread and removed from the tick
    ///     timer, so every access goes through <see cref="ClientsGate"/>.
    /// </summary>
    public Dictionary<WebSocket, (TaskCompletionSource<object?>, DateTime)> Clients { get; } = new();
    readonly object ClientsGate = new();

    public int QueuedCount { get { lock (ClientsGate) return Clients.Count; } }

    public Timer TickTimer { get; init; }
    public Timer WatchTimer { get; init; }
    public ServerInstance Instance { get; internal set; }
    public ServerInstanceCommunicator Communicator { get; internal set; }

    public MatchMakingManager(string playlistId, TimeSpan? matchMakingTimeout = null, int tickInterval = 5000) {
        PlaylistId = playlistId;
        MatchMakingTimeout = matchMakingTimeout ?? TimeSpan.FromMinutes(5);
        TickInterval = tickInterval;
        TickTimer = new(new(Tick), null, 0, tickInterval);
        WatchTimer = new(new(Watch), null, Timeout.Infinite, Timeout.Infinite);
        Instance = new(Routes.ServerApiRoutes.ShippingLocation);
        Communicator = new("[::1]", 12345 + (playlistId.ToLower().Equals("Playlist_Auto_Solo", StringComparison.InvariantCultureIgnoreCase) ? 1 : 0));
        Task.Run(async () => { await Task.Delay(5000); await DiscordRest.UpdateEmbedAsync(this); });
    }

    public async Task HandleClient(WebSocket client) {
        await SendInitialThings(client);
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (ClientsGate) Clients[client] = (tcs, DateTime.Now);
        await DiscordRest.UpdateEmbedAsync(this);

        // Whichever comes first: the match starts and Finish releases the task, or the player gives
        // up and closes the socket. Only the read loop can notice the second one.
        var reading = ReceiveUntilClosed(client);
        await Task.WhenAny(tcs.Task, reading);
        Drop(client);

        if (!reading.IsCompleted) {
            // The player is being sent into a match rather than walking away, so ask for the
            // close from this side. It has to be CloseOutputAsync and the read loop has to be the
            // one that waits: CloseAsync waits for the close reply itself, which means a second
            // receive on a socket that already has one outstanding - measured, that does not throw,
            // it simply never returns. A client that never answers is abandoned after the grace
            // period rather than holding this request open for good.
            try {
                if (client.State == WebSocketState.Open)
                    await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Finished", CancellationToken.None);
            } catch (Exception) { }
            await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(3)));
        }
    }

    /// <summary>
    ///     Reads until the socket closes, and that is the entire point of it.
    ///     <para>
    ///         A WebSocket only finds out the peer went away while a receive is pending, and nothing
    ///         here was ever receiving - HandleClient just awaited a task. That is why
    ///         <c>client.State</c> stayed Open for players who had long since cancelled ("Not
    ///         working as expected"), why they stayed in the queue, and why a match could be started
    ///         for nobody.
    ///     </para>
    ///     <para>
    ///         The old CheckConnection tried to poll this from a timer with
    ///         <c>ReceiveAsync(ArraySegment&lt;byte&gt;.Empty)</c>. Measured, that call blocks until
    ///         a frame arrives rather than reporting anything - so on a live client the very first
    ///         poll never returns, and a second concurrent receive does not throw either, it just
    ///         blocks too. Polling cannot work at all here; one continuous loop is the only shape
    ///         that does.
    ///     </para>
    ///     <para>
    ///         The matchmaker protocol is server-to-client only, so whatever the client sends is
    ///         read and discarded - and for the same reason a message split across frames needs no
    ///         reassembly.
    ///     </para>
    /// </summary>
    static async Task ReceiveUntilClosed(WebSocket client) {
        var buffer = new byte[512];
        try {
            while (client.State == WebSocketState.Open) {
                var result = await client.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
            }
        } catch (Exception) {
            // Reset, aborted, disposed: every one of them means the same thing here.
        }
    }

    /// <summary>
    ///     Takes a client out of the queue and releases the request holding its socket. Safe to
    ///     call twice, and from any thread - the tick timer, Finish and HandleClient all race to it.
    /// </summary>
    void Drop(WebSocket client) {
        TaskCompletionSource<object?> tcs;
        lock (ClientsGate) {
            if (!Clients.TryGetValue(client, out var entry)) return;
            Clients.Remove(client);
            tcs = entry.Item1;
        }
        tcs.TrySetResult(null);
    }

    /// <summary>
    ///     A copy to iterate. The live dictionary cannot be walked by index while the tick timer
    ///     and incoming requests are both changing it.
    /// </summary>
    KeyValuePair<WebSocket, (TaskCompletionSource<object?>, DateTime)>[] Snapshot() {
        lock (ClientsGate) return Clients.ToArray();
    }

    public async Task SendInitialThings(WebSocket client) {
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
                queuedPlayers = UseEstimatedWaitTime ? 0 : QueuedCount,
                estimatedWaitSec = 0,
                status = new {}
            },
            name = "StatusUpdate"
        }));
        await Task.Delay(500);
    }

    public async void Tick(object? state) {
        Console.WriteLine($"MatchMakingManager[{PlaylistId}].Tick: {Clients.Count}");
        if (QueuedCount < 2 || IsListening) MatchMakingStartedAt = DateTime.Now;
        if (DateTime.Now - MatchMakingStartedAt > MatchMakingTimeout) {
            TickTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (QueuedCount > 0) await Finish();
            Console.WriteLine($"MatchMakingManager[{PlaylistId}].Tick: Finished");
            TickTimer.Change(1000, TickInterval);
            return;
        }
        foreach (var (client, entry) in Snapshot()) {
            // Now meaningful: the read loop in HandleClient is what moves a dropped socket out of
            // Open, so this catches one that has gone since the last tick.
            if (client.State != WebSocketState.Open) {
                Drop(client);
                continue;
            }
            var startTime = entry.Item2;
            var estimatedWaitSec = IsListening ? 0 : (int)(MatchMakingTimeout + TimeSpan.FromMinutes(1) - (startTime - MatchMakingStartedAt)).TotalSeconds;
            if (estimatedWaitSec % 60 == 0) estimatedWaitSec += 1; // because if moduled by 60 is 0, it will shown as Message.ETANotAvailable (N/A)

            try {
                await client.SendAsync(JsonSerializer.Serialize(new {
                    payload = new {
                        state = "Queued",
                        ticketId = "TEST_TICKET_ID",
                        queuedPlayers = UseEstimatedWaitTime ? 0 : QueuedCount, // set it 0 will make it use Message.FindingMatch (Finding match...\nElapsed: {0}, ETA: {1}) otherwise it will use Message.PlayersInQueue (Queued players: {0}\nElapsed: {1})
                        estimatedWaitSec = UseEstimatedWaitTime ? estimatedWaitSec : 0,
                        status = new {}
                    },
                    name = "StatusUpdate"
                }));
            } catch (Exception e) {
                Console.WriteLine($"MatchMakingManager[{PlaylistId}].Tick: {e}");
                Drop(client);
            }
        }
    }

    public async Task Finish() {
        await LaunchGameServer();
        LastMatchStartedAt = DateTime.Now;
        Console.WriteLine($"MatchMakingManager[{PlaylistId}].Finish");
        try {
            var users = await DB.Users.Find(Builders<User>.Filter.Empty).ToListAsync();
            foreach (var user in users) {
                if ((await DB.GetAthenaProfile(user.AccountId))?.CharacterId is var cid && cid is string) {
                    var splited = cid.Split(":");
                    // Console.WriteLine($"Send outfit to {user.DisplayName} ({cid})");
                    if (string.IsNullOrWhiteSpace(cid)) continue;
                    await Communicator.SendOutfit(user.DisplayName, splited[1]);
                    await Task.Delay(10);
                }
            }
        } catch {
            Console.WriteLine("Sending outfits timed out!");
        }
        await Communicator.InfiniteAmmo(true);
        await Communicator.InfiniteMaterials(true);
        foreach (var (client, _) in Snapshot()) {
            var sessionId = Guid.NewGuid().ToString().Replace("-", "");
            Routes.MatchMakingRoutes.SessionIds[sessionId] = PlaylistId;
            try {
                await client.SendAsync(JsonSerializer.Serialize(new {
                payload = new {
                    state = "SessionAssignment",
                    matchId = "TEST_MATCH_ID",
                },
                name = "StatusUpdate"
                }));
                await Task.Delay(500);
                await client.SendAsync(JsonSerializer.Serialize(new {
                    payload = new {
                        matchId = "TEST_MATCH_ID",
                        sessionId = sessionId,
                        joinDelaySec = 0
                    },
                    name = "Play"
                }));
            } catch (Exception e) {
                Console.WriteLine($"MatchMakingManager[{PlaylistId}].Finish: {e}");
            } finally {
                // Released even if the Play message failed: the player is out of the queue either
                // way, and leaving the task pending would hold the request open for good.
                Drop(client);
            }
        }
        await DiscordRest.UpdateEmbedAsync(this);
        await Task.Delay(1000 * 50);
        await Communicator.StartBus();
        await DiscordRest.UpdateEmbedAsync(this);
        WatchTimer.Change(0, 1000);
        await Communicator.InfiniteAmmo(false);
        await Communicator.InfiniteMaterials(false);
    }

    public async Task LaunchGameServer() {
        Console.WriteLine($"MatchMakingManager[{PlaylistId}].LaunchGameServer");
        if (!Instance.ShippingProcess?.HasExited ?? false) Instance.Kill();
        Instance.Launch();
        if (!Instance.InjectDll(Routes.ServerApiRoutes.ClientNativeDllLocation)) {
            Console.WriteLine("Failed to inject dll!");
            Instance.Launch();
            if (!Instance.InjectDll(Routes.ServerApiRoutes.ClientNativeDllLocation)) {
                Console.WriteLine("Failed to inject dll TWICE!");
                return;
            }
        }
        await Instance.WaitForLogAndInjectDll(line => line.Contains("LogHotfixManager: Display: Update State CheckingForPatch -> CheckingForHotfix"), PlaylistId.ToLower().Equals("Playlist_Auto_Solo", StringComparison.InvariantCultureIgnoreCase) ? Routes.ServerApiRoutes.ServerNativeDllLocation.Replace(".dll", ".lg.dll") : Routes.ServerApiRoutes.ServerNativeDllLocation);
        await Task.Delay(1000 * 30);
        while (true) {
            try {
                if (await Communicator.IsListening()) break;
            } catch (Exception e) {
                Console.WriteLine($"MatchMakingManager[{PlaylistId}].LaunchGameServer: {e}");
            }
            if (Instance.ShippingProcess?.HasExited ?? false) {
                Console.WriteLine("Game server exited! Restarting...");
                await LaunchGameServer();
                return;
            }
            await Task.Delay(1000);
        }
        IsListening = true;
    }

    public async void Watch(object? state) {
        Console.WriteLine($"MatchMakingManager[{PlaylistId}].Watch");
        try {
            if (Instance.ShippingProcess?.HasExited ?? false) {
                Console.WriteLine("Game server exited!");
                WatchTimer.Change(Timeout.Infinite, Timeout.Infinite);
                IsListening = false;
                Instance.Kill();
                return;
            }
            var playersLeft = await Communicator.GetPlayersLeft();
            if (playersLeft != PlayersLeft) {
                Console.WriteLine($"MatchMakingManager[{PlaylistId}].Watch: Players left changed from {PlayersLeft} to {playersLeft}");
                PlayersLeft = playersLeft;
                await DiscordRest.UpdateEmbedAsync(this);
            }
            if (playersLeft == 0) {
                WatchTimer.Change(Timeout.Infinite, Timeout.Infinite);
                Console.WriteLine($"MatchMakingManager[{PlaylistId}].Watch: No players left!");
                IsListening = false;
                await Task.Delay(1000 * 15);
                await Communicator.Restart();
                await Task.Delay(1000);
                Instance.Kill();
                await DiscordRest.UpdateEmbedAsync(this);
                return;
            }
        } catch (Exception e) {
            Console.WriteLine($"MatchMakingManager[{PlaylistId}].Watch: {e}");
        }
    }
}