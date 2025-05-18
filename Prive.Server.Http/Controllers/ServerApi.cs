using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using System.Net;
using System.Text.Json;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("serverapi")]
public class ServerApiController : ControllerBase {
    public static readonly string BaseDllLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher");
    public static readonly string ClientNativeDllLocation = Path.Combine(BaseDllLocation, "Prive.Client.Native.dll");
    public static readonly string ServerNativeDllLocation = Path.Combine(BaseDllLocation, "Prive.Server.Native.dll");
    public static readonly string ShippingLocation = @"D:\Documents\Fortnite\10.4\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe" is var p && System.IO.File.Exists(p) ? p : @"C:\Users\User\AppData\Local\Prive.Launcher\v10.40\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"; // Hardcoded

    public static ServerInstance? Instance { get => Program.Instance; set => Program.Instance = value; }
    public static ServerInstanceCommunicator CClient { get => Program.CClient; }

    public static string IP = Dns.GetHostEntry("xthe.org").AddressList.First().ToString();
    public bool IsFromAuthorized() => !Request.Headers.ContainsKey("X-Forwarded-For") || Request.Headers["X-Forwarded-For"].Select(x => x?.Split(", ")).Any(x => x?.Any(x => x == IP) ?? false);
    
    [HttpGet("ip")] [NoAuth]
    public string GetIP() => IP;

    [HttpGet("activeplayers")] [NoAuth]
    public async Task<int> GetActivePlayers() {
        var activePlayers = 0;
        activePlayers += MatchMakingController.MatchMakingManagerLateGameSolo.Clients.Count;
        activePlayers += MatchMakingController.MatchMakingManagerSolo.Clients.Count;
        try {
            activePlayers += await MatchMakingController.MatchMakingManagerLateGameSolo.Communicator.GetPlayersLeft();
        } catch {}
        try {
            activePlayers += await MatchMakingController.MatchMakingManagerSolo.Communicator.GetPlayersLeft();
        } catch {}
        return activePlayers;
    }

    [HttpPost("start")] [NoAuth]
    public IActionResult Start() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("Start posted");
        Instance?.Kill();
        Instance = new ServerInstance(ShippingLocation);
        Instance.Launch();
        Instance.InjectDll(ClientNativeDllLocation);
        // have to wait because required things does not load instantly
        Task.Run(async () => await Instance.WaitForLogAndInjectDll(line => line.Contains("LogHotfixManager: Display: Update State CheckingForPatch -> CheckingForHotfix"), ServerNativeDllLocation));
        return NoContent();
    }

    [HttpPost("stop")] [NoAuth]
    public IActionResult Stop() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("Stop posted");
        Program.Instance?.Kill();
        return NoContent();
    }

    [HttpPost("shutdown")] [NoAuth]
    public IActionResult Shutdown() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("Shutdown posted");
        CClient.Client.GetAsync("shutdown");
        return NoContent();
    }

    [HttpPost("timetogotrue")] [NoAuth]
    public async Task<IActionResult> TimeToGoTrue() {
        if (!IsFromAuthorized()) return Unauthorized();
        var users = (await DB.Users.Find(Builders<User>.Filter.Empty).ToListAsync());
        foreach (var user in users) {
            if ((await DB.GetAthenaProfile(user.AccountId))?.CharacterId is var cid && cid is string) {
                var splited = cid.Split(":");
                Console.WriteLine($"Send outfit to {user.DisplayName} ({cid})");
                if (string.IsNullOrWhiteSpace(cid)) continue;
                await CClient.SendOutfit(user.DisplayName, splited[1]);
            }
        }
        return NoContent();
    }

    [HttpPost("timetogofalse")] [NoAuth]
    public IActionResult TimeToGoFalse() {
        if (!IsFromAuthorized()) return Unauthorized();
        return NoContent();
    }

    [HttpPost("createuser")] [NoAuth]
    public async Task<IActionResult> CreateUser() {
        if (!IsFromAuthorized()) return Unauthorized();
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? throw new Exception("Failed to deserialize body");
        var username = d["username"];
        var password = d["password"];
        var discord = d["discord"];
        if ((await DB.GetUser($"{username}@fortnite.day", "Email")) is not null) return Conflict();
        var user = new User() {
            Email = $"{username}@fortnite.day",
            Password = password,
            DisplayName = username,
            AccountId = Guid.NewGuid().ToString().Replace("-", ""),
            DiscordAccountId = discord
        };
        await DB.Users.InsertOneAsync(user);
        return NoContent();
    }

    [HttpGet("discord/{discordAccountId}")] [NoAuth]
    public async Task<IActionResult> DiscordAccountId() {
        if (!IsFromAuthorized()) return Unauthorized();
        var discordAccountId = RouteData.Values["discordAccountId"] as string ?? throw new Exception("Failed to get discordAccountId");
        Response.ContentType = "application/json";
        try {
            var user = await DB.Users.Find(Builders<User>.Filter.Eq("DiscordAccountId", discordAccountId)).FirstOrDefaultAsync();
            if (user is null) return NotFound();
            else return Ok(user);
        } catch {}
        return NotFound();
    }

    [HttpPost("setport")] [NoAuth]
    public async Task<IActionResult> SetPort() {
        if (!IsFromAuthorized()) return Unauthorized();
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        var d = JsonSerializer.Deserialize<Dictionary<string, int>>(body) ?? throw new Exception("Failed to deserialize body");
        var port = d["port"];
        await CClient.SetPort(port);
        Port = port;
        Console.WriteLine($"Set port to {port}");
        return NoContent();
    }

    [HttpPost("restart")] [NoAuth]
    public IActionResult Restart() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("Restart posted");
        CClient.Restart();
        return NoContent();
    }

    [HttpPost("startbus")] [NoAuth]
    public IActionResult StartBus() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("StartBus posted");
        CClient.StartBus();
        return NoContent();
    }

    [HttpPost("startlategame")] [NoAuth]
    public IActionResult StartLateGame() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("StartLateGame posted");
        CClient.StartBus();
        return NoContent();
    }

    [HttpPost("infiniteammotrue")] [NoAuth]
    public IActionResult InfiniteAmmoTrue() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("InfiniteAmmoTrue posted");
        CClient.InfiniteAmmo(true);
        return NoContent();
    }

    [HttpPost("infiniteammofalse")] [NoAuth]
    public IActionResult InfiniteAmmoFalse() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("InfiniteAmmoFalse posted");
        CClient.InfiniteAmmo(false);
        return NoContent();
    }

    [HttpPost("infinitematerialstrue")] [NoAuth]
    public IActionResult InfiniteMaterialsTrue() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("InfiniteMaterialsTrue posted");
        CClient.InfiniteMaterials(true);
        return NoContent();
    }

    [HttpPost("infinitematerialsfalse")] [NoAuth]
    public IActionResult InfiniteMaterialsFalse() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("InfiniteMaterialsFalse posted");
        CClient.InfiniteMaterials(false);
        return NoContent();
    }

    [HttpPost("startsafezone")] [NoAuth]
    public IActionResult StartSafeZone() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("StartSafeZone posted");
        CClient.StartSafeZone();
        return NoContent();
    }

    [HttpPost("stopsafezone")] [NoAuth]
    public IActionResult StopSafeZone() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("StopSafeZone posted");
        CClient.StopSafeZone();
        return NoContent();
    }

    [HttpPost("skipsafezone")] [NoAuth]
    public IActionResult SkipSafeZone() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("SkipSafeZone posted");
        CClient.SkipSafeZone();
        return NoContent();
    }

    [HttpPost("startshrinksafezone")] [NoAuth]
    public IActionResult StartShrinkSafeZone() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("StartShrinkSafeZone posted");
        CClient.StartShrinkSafeZone();
        return NoContent();
    }

    [HttpPost("skipshrinksafezone")] [NoAuth]
    public IActionResult SkipShrinkSafeZone() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("SkipShrinkSafeZone posted");
        CClient.SkipShrinkSafeZone();
        return NoContent();
    }

    [HttpPost("executetest")] [NoAuth]
    public IActionResult ExecuteTest() {
        if (!IsFromAuthorized()) return Unauthorized();
        Console.WriteLine("ExecuteTest posted");
        CClient.Client.GetAsync("executetest");
        return NoContent();
    }
}