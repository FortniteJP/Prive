using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("serverapi")]
public class ServerApiController : ControllerBase {
    public static readonly string ClientNativeDllLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher/Prive.Client.Native.dll");
    public static readonly string ServerNativeDllLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher/Prive.Server.Native.dll");
    public static readonly string ShippingLocation = @"C:\Users\User\Documents\Fortnite\v10.40\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"; // Hardcoded

    private static ServerInstance? Instance { get => Program.Instance; set => Program.Instance = value; }
    private static CommunicateClient CClient { get => Program.CClient; }
    [HttpPost("start")] [NoAuth]
    public IActionResult Start() {
        Console.WriteLine("Start posted");
        Instance?.Kill();
        Instance = new ServerInstance(ShippingLocation);
        Instance.Launch();
        Instance.InjectDll(ClientNativeDllLocation);
        Instance.InjectDll(ServerNativeDllLocation);
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
    public IActionResult TimeToGoTrue() {
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
        var user = new User() {
            Email = $"{username}@fortnite.day",
            Password = password,
            DisplayName = username,
            AccountId = Guid.NewGuid().ToString().Replace("-", ""),
        };
        await DB.Users.InsertOneAsync(user);
        return NoContent();
    }
}