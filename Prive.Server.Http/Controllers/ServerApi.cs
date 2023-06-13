using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using System.Text.Json;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("serverapi")]
public class ServerApiController : ControllerBase {
    public static readonly string BaseDllLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher");
    public static readonly string ClientNativeDllLocation = Path.Combine(BaseDllLocation, "Prive.Client.Native.dll");
    public static readonly string ServerNativeDllLocation = Path.Combine(BaseDllLocation, "Prive.Server.Native.dll");
    public static readonly string ShippingLocation = @"D:\Documents\Fortnite\10.4\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe" is var p && System.IO.File.Exists(p) ? p : @"C:\Users\User\Documents\Fortnite\v10.40\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"; // Hardcoded

    private static ServerInstance? Instance { get => Program.Instance; set => Program.Instance = value; }
    private static CommunicateClient CClient { get => Program.CClient; }
    [HttpPost("start")] [NoAuth]
    public IActionResult Start() {
        Console.WriteLine("Start posted");
        Instance?.Kill();
        Instance = new ServerInstance(ShippingLocation);
        Instance.Launch();
        Instance.InjectDll(ClientNativeDllLocation);
        // have to wait because required things does not load instantly
        Task.Run(async () => await Instance.WaitForLogAndInjectDll(line => line.Contains("LogHotfixManager: Verbose: Using default hotfix"), ServerNativeDllLocation));
        return NoContent();
    }

    [HttpPost("stop")] [NoAuth]
    public IActionResult Stop() {
        Console.WriteLine("Stop posted");
        Program.Instance?.Kill();
        return NoContent();
    }

    [HttpPost("shutdown")] [NoAuth]
    public IActionResult Shutdown() {
        Console.WriteLine("Shutdown posted");
        CClient.Shutdown();
        return NoContent();
    }

    [HttpPost("timetogotrue")] [NoAuth]
    public async Task<IActionResult> TimeToGoTrue() {
        var users = (await DB.Users.Find(Builders<User>.Filter.Empty).ToListAsync());
        foreach (var user in users) {
            await CClient.SendOutfit(user.AccountId, (await DB.GetAthenaProfile(user.AccountId)).CharacterId);
        }
        MatchMakingController.TimeToGo = true;
        return NoContent();
    }

    [HttpPost("timetogofalse")] [NoAuth]
    public IActionResult TimeToGoFalse() {
        MatchMakingController.TimeToGo = false;
        return NoContent();
    }

    [HttpPost("createuser")] [NoAuth]
    public async Task<IActionResult> CreateUser() {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? throw new Exception("Failed to deserialize body");
        var username = d["username"];
        var password = d["password"];
        if ((await DB.GetUser($"{username}@fortnite.day", "Email")) is not null) return Conflict();
        var user = new User() {
            Email = $"{username}@fortnite.day",
            Password = password,
            DisplayName = username,
            AccountId = Guid.NewGuid().ToString().Replace("-", ""),
        };
        await DB.Users.InsertOneAsync(user);
        return NoContent();
    }

    [HttpPost("setport")] [NoAuth]
    public async Task<IActionResult> SetPort() {
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
        Console.WriteLine("Restart posted");
        CClient.Send("restart;");
        return NoContent();
    }

    [HttpPost("startbus")] [NoAuth]
    public IActionResult StartBus() {
        Console.WriteLine("StartBus posted");
        CClient.StartBus();
        return NoContent();
    }

    [HttpPost("infiniteammotrue")] [NoAuth]
    public IActionResult InfiniteAmmoTrue() {
        Console.WriteLine("InfiniteAmmoTrue posted");
        CClient.Send("infiniteammo;true");
        return NoContent();
    }

    [HttpPost("infiniteammofalse")] [NoAuth]
    public IActionResult InfiniteAmmoFalse() {
        Console.WriteLine("InfiniteAmmoFalse posted");
        CClient.Send("infiniteammo;false");
        return NoContent();
    }

    [HttpPost("infinitematerialstrue")] [NoAuth]
    public IActionResult InfiniteMaterialsTrue() {
        Console.WriteLine("InfiniteMaterialsTrue posted");
        CClient.Send("infinitematerials;true");
        return NoContent();
    }

    [HttpPost("infinitematerialsfalse")] [NoAuth]
    public IActionResult InfiniteMaterialsFalse() {
        Console.WriteLine("InfiniteMaterialsFalse posted");
        CClient.Send("infinitematerials;false");
        return NoContent();
    }

    [HttpPost("startsafezone")] [NoAuth]
    public IActionResult StartSafeZone() {
        Console.WriteLine("StartSafeZone posted");
        CClient.Send("startsafezone;");
        return NoContent();
    }

    [HttpPost("stopsafezone")] [NoAuth]
    public IActionResult StopSafeZone() {
        Console.WriteLine("StopSafeZone posted");
        CClient.Send("stopsafezone;");
        return NoContent();
    }

    [HttpPost("skipsafezone")] [NoAuth]
    public IActionResult SkipSafeZone() {
        Console.WriteLine("SkipSafeZone posted");
        CClient.Send("skipsafezone;");
        return NoContent();
    }

    [HttpPost("startshrinksafezone")] [NoAuth]
    public IActionResult StartShrinkSafeZone() {
        Console.WriteLine("StartShrinkSafeZone posted");
        CClient.Send("startshrinksafezone;");
        return NoContent();
    }

    [HttpPost("skipshrinksafezone")] [NoAuth]
    public IActionResult SkipShrinkSafeZone() {
        Console.WriteLine("SkipShrinkSafeZone posted");
        CClient.Send("skipshrinksafezone;");
        return NoContent();
    }
}