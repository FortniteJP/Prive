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
    public int TickInterval { get; init; }

    public Dictionary<WebSocket, (TaskCompletionSource<object?>, DateTime)> Clients { get; } = new();
    public Timer TickTimer { get; init; }
    public Timer WatchTimer { get; init; }
    public ServerInstance Instance { get; internal set; }
    public ServerInstanceCommunicator Communicator { get; internal set; }
    // public Timer CheckConnectionTimer { get; init; }

    public MatchMakingManager(string playlistId, TimeSpan? matchMakingTimeout = null, int tickInterval = 5000) {
        PlaylistId = playlistId;
        MatchMakingTimeout = matchMakingTimeout ?? TimeSpan.FromMinutes(5);
        TickInterval = tickInterval;
        TickTimer = new(new(Tick), null, 0, tickInterval);
        WatchTimer = new(new(Watch), null, Timeout.Infinite, Timeout.Infinite);
        // CheckConnectionTimer = new(new(CheckConnection), null, 0, 1000);
        Instance = new(Controllers.ServerApiController.ShippingLocation);
        Communicator = new("[::1]", 12345 + (playlistId.ToLower().Equals("Playlist_Auto_Solo", StringComparison.InvariantCultureIgnoreCase) ? 1 : 0));
    }

    public async Task HandleClient(WebSocket client) {
        await SendInitialThings(client);
        var tcs = new TaskCompletionSource<object?>();
        Clients.Add(client, (tcs, DateTime.Now));
        await tcs.Task;
        Clients.Remove(client);
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
                queuedPlayers = UseEstimatedWaitTime ? 0 : Clients.Count,
                estimatedWaitSec = 0,
                status = new {}
            },
            name = "StatusUpdate"
        }));
        await Task.Delay(500);
    }

    public async void Tick(object? state) {
        Console.WriteLine($"MatchMakingManager[{PlaylistId}].Tick: {Clients.Count}");
        if (Clients.Count == 0 || IsListening) MatchMakingStartedAt = DateTime.Now;
        if (DateTime.Now - MatchMakingStartedAt > MatchMakingTimeout) {
            TickTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (Clients.Count > 0) await Finish();
            Console.WriteLine($"MatchMakingManager[{PlaylistId}].Tick: Finished");
            TickTimer.Change(1000, TickInterval);
            return;
        }
        for (int i = 0; i < Clients.Count; i++) {
            var client = Clients.Keys.ElementAt(i);
            var tcs = Clients.Values.ElementAt(i).Item1;
            var startTime = Clients.Values.ElementAt(i).Item2;
            var estimatedWaitSec = IsListening ? 0 : (int)(MatchMakingTimeout + TimeSpan.FromMinutes(1) - (startTime - MatchMakingStartedAt)).TotalSeconds;
            if (estimatedWaitSec % 60 == 0) estimatedWaitSec += 1; // because if moduled by 60 is 0, it will shown as Message.ETANotAvailable (N/A)

            try {
                await client.SendAsync(JsonSerializer.Serialize(new {
                    payload = new {
                        state = "Queued",
                        ticketId = "TEST_TICKET_ID",
                        queuedPlayers = UseEstimatedWaitTime ? 0 : Clients.Count, // set it 0 will make it use Message.FindingMatch (Finding match...\nElapsed: {0}, ETA: {1}) otherwise it will use Message.PlayersInQueue (Queued players: {0}\nElapsed: {1})
                        estimatedWaitSec = UseEstimatedWaitTime ? estimatedWaitSec : 0,
                        status = new {}
                    },
                    name = "StatusUpdate"
                }));
            } catch (Exception e) {
                Console.WriteLine($"MatchMakingManager[{PlaylistId}].Tick: {e}");
                tcs.SetResult(null);
                Clients.Remove(client);
                i--;
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
                }
            }
        } catch {
            Console.WriteLine("Sending outfits timed out!");
        }
        await Communicator.InfiniteAmmo(true);
        await Communicator.InfiniteMaterials(true);
        for (int i = 0; i < Clients.Count; i++) {
            var client = Clients.Keys.ElementAt(i);
            var tcs = Clients.Values.ElementAt(i).Item1;
            var sessionId = Guid.NewGuid().ToString().Replace("-", "");
            Controllers.MatchMakingController.SessionIds.Add(sessionId, PlaylistId);
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
                Console.WriteLine($"MatchMakingManager[{PlaylistId}].Tick: {e}");
            } finally {
                tcs.SetResult(null);
                Clients.Remove(client);
                i--;
            }
        }
        await Task.Delay(1000 * 50);
        await Communicator.StartBus();
        WatchTimer.Change(0, 1000);
        await Communicator.InfiniteAmmo(false);
        await Communicator.InfiniteMaterials(false);
    }

    public async Task LaunchGameServer() {
        Console.WriteLine($"MatchMakingManager[{PlaylistId}].LaunchGameServer");
        if (!Instance.ShippingProcess?.HasExited ?? false) Instance.Kill();
        Instance.Launch();
        if (!Instance.InjectDll(Controllers.ServerApiController.ClientNativeDllLocation)) {
            Console.WriteLine("Failed to inject dll!");
            Instance.Launch();
            if (!Instance.InjectDll(Controllers.ServerApiController.ClientNativeDllLocation)) {
                Console.WriteLine("Failed to inject dll TWICE!");
                return;
            }
        }
        await Instance.WaitForLogAndInjectDll(line => line.Contains("LogHotfixManager: Display: Update State CheckingForPatch -> CheckingForHotfix"), PlaylistId.ToLower().Equals("Playlist_Auto_Solo", StringComparison.InvariantCultureIgnoreCase) ? Controllers.ServerApiController.ServerNativeDllLocation.Replace(".dll", ".lg.dll") : Controllers.ServerApiController.ServerNativeDllLocation);
        while (true) {
            try {
                if (await Communicator.IsListening()) break;
            } catch (Exception e) {
                Console.WriteLine($"MatchMakingManager[{PlaylistId}].LaunchGameServer: {e}");
            }
            await Task.Delay(1000);
        }
        IsListening = true;
    }

    public async void Watch(object? state) {
        Console.WriteLine($"MatchMakingManager[{PlaylistId}].Watch");
        var playersLeft = await Communicator.GetPlayersLeft();
        if (playersLeft == 0) {
            WatchTimer.Change(Timeout.Infinite, Timeout.Infinite);
            Console.WriteLine($"MatchMakingManager[{PlaylistId}].Watch: No players left!");
            IsListening = false;
            await Task.Delay(1000 * 15);
            await Communicator.Restart();
            await Task.Delay(1000);
            Instance.Kill();
            return;
        }
    }

    public async void CheckConnection(object? state) {
        for (int i = 0; i < Clients.Count; i++) {
            var client = Clients.Keys.ElementAt(i);
            var tcs = Clients.Values.ElementAt(i).Item1;
            try {
                // what is the correct implementation?
                await client.ReceiveAsync(ArraySegment<byte>.Empty, CancellationToken.None);
            } catch (Exception e) {
                Console.WriteLine($"MatchMakingManager[{PlaylistId}].CheckConnection: {e}");
                tcs.SetResult(null);
                Clients.Remove(client);
                i--;
            }
        }
    }
}