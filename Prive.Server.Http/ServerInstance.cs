using System.Diagnostics;

namespace Prive.Server.Http;

public class ServerInstance {
    public string ShippingPath { get; }
    public string LauncherPath { get => Path.Combine(Path.GetDirectoryName(ShippingPath)!, "FortniteLauncher.exe"); }
    public string EACPath { get => Path.Combine(Path.GetDirectoryName(ShippingPath)!, "FortniteClient-Win64-Shipping_EAC.exe"); }

    public Process? ShippingProcess { get; private set; }
    public Process? LauncherProcess { get; private set; }
    public Process? EACProcess { get; private set; }

    // What to pass
    private string Arguments = "-epicapp=Fortnite -epicenv=Prod -epiclocale=en-us -skippatchcheck -epicportal -nobe -fromfl=eac -fltoken=h1cdhchd10150221h130eB56 -nullrhi -nosplash -nosound";

    public ServerInstance(string shippingPath) {
        ShippingPath = shippingPath;
        Arguments += " -AUTH_LOGIN=server@fortnite.day -AUTH_PASSWORD=PriveServer -AUTH_TYPE=epic";
    }

    public void Launch() {
        LauncherProcess = Process.Start(new ProcessStartInfo(LauncherPath, Arguments))!;
        Utils.SuspendThreads(LauncherProcess);
        EACProcess = Process.Start(new ProcessStartInfo(EACPath, Arguments))!;
        Utils.SuspendThreads(EACProcess);

        ShippingProcess = Process.Start(new ProcessStartInfo(ShippingPath, Arguments) {
            UseShellExecute = false,
            CreateNoWindow = false
        })!;
    }

    public bool InjectDll(string dllPath) {
        if (ShippingProcess?.HasExited ?? true) return false;
        ShippingProcess.WaitForInputIdle();
        Utils.InjectDll(ShippingProcess, dllPath);
        return true;
    }

    public async Task<bool> WaitForLogAndInjectDll(Func<string, bool> logFunc, string dllPath) {
        if (ShippingProcess?.HasExited ?? true) return false;
        ShippingProcess.WaitForInputIdle();
        using var reader = new StreamReader(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "aved", "Logs", "FortniteGame.log"), new FileStreamOptions {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.ReadWrite
        });
        while (!ShippingProcess.HasExited) {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (logFunc(line)) break;
            await Task.Delay(1);
        }
        Console.WriteLine("WaitForLogAndInjectDll done");
        Utils.InjectDll(ShippingProcess, dllPath);
        return true;
    }

    public void Kill() {
        ShippingProcess?.Kill();
        LauncherProcess?.Kill();
        EACProcess?.Kill();
    }

    public void WaitForExit() {
        ShippingProcess?.WaitForExit();
    }
}

public class ServerInstanceCommunicator {
    public HttpClient Client { get; }

    public ServerInstanceCommunicator(string serverIP, int serverPort) {
        Client = new HttpClient() {
            BaseAddress = new Uri($"http://{serverIP}:{serverPort}/")
        };
    }

    public Task SendOutfit(string id, string cid) => Client.GetAsync($"outfit?id={id}&cid={cid}");

    public Task SetPort(int port) => Client.GetAsync($"setport?port={port}");

    public Task Restart() => Client.GetAsync("restart");

    public Task StartBus() => Client.GetAsync("startbus");

    public async Task<bool> InfiniteAmmo(bool? value = null) => await (await Client.GetAsync($"infiniteammo{(value is bool b ? $"?value={(b ? "true" : "false")}" : "")}")).Content.ReadAsStringAsync() == "true";

    public async Task<bool> InfiniteMaterials(bool? value = null) => await (await Client.GetAsync($"infinitematerials{(value is bool b ? $"?value={(b ? "true" : "false")}" : "")}")).Content.ReadAsStringAsync() == "true";

    public Task StartSafeZone() => Client.GetAsync("startsafezone");

    public Task StopSafeZone() => Client.GetAsync("stopsafezone");

    public Task SkipSafeZone() => Client.GetAsync("skipsafezone");

    public Task StartShrinkSafeZone() => Client.GetAsync("startshrinksafezone");

    public Task SkipShrinkSafeZone() => Client.GetAsync("skipshrinksafezone");

    public async Task<bool> IsListening() => await (await Client.GetAsync("islistening")).Content.ReadAsStringAsync() == "true";

    public async Task<int> GetPlayersLeft() => int.Parse(await (await Client.GetAsync("playersleft")).Content.ReadAsStringAsync());
}