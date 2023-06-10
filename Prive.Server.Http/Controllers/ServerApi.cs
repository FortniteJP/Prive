using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("serverapi")]
public class ServerApiController : ControllerBase {
    public static readonly string ClientNativeDllLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher/Prive.Client.Native.dll");
    public static readonly string ServerNativeDllLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher/Prive.Server.Native.dll");
    public static readonly string ShippingLocation = @"C:\Users\User\Documents\Fortnite\v10.40\FortniteGame\Binaries\Win64\FortniteServer-Win64-Shipping.exe"; // Hardcoded

    private static ServerInstance? Instance { get => Program.Instance; set => Program.Instance = value; }
    private static CommunicateClient CClient { get => Program.CClient; }
    [HttpPost("startinstance")] [NoAuth]
    public IActionResult StartInstance() {
        Console.WriteLine("StartInstance posted");
        Instance?.Kill();
        Instance = new ServerInstance(ShippingLocation);
        Instance.Launch();
        Instance.InjectDll(ClientNativeDllLocation);
        Instance.InjectDll(ServerNativeDllLocation);
        return NoContent();
    }

    [HttpPost("stopinstance")] [NoAuth]
    public IActionResult StopInstance() {
        Console.WriteLine("StopInstance posted");
        Program.Instance?.Kill();
        return NoContent();
    }

    [HttpPost("shutdown")] [NoAuth]
    public IActionResult Shutdown() {
        Console.WriteLine("Shutdown posted");
        CClient.Shutdown();
        return NoContent();
    }
}