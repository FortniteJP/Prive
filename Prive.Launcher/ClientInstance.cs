using System.Diagnostics;

namespace Prive.Launcher;

public class ClientInstance {
    public string ShippingPath { get; }
    public string LauncherPath { get => Path.Combine(Path.GetDirectoryName(ShippingPath)!, "FortniteLauncher.exe"); }
    public string EACPath { get => Path.Combine(Path.GetDirectoryName(ShippingPath)!, "FortniteClient-Win64-Shipping_EAC.exe"); }

    public Process? ShippingProcess { get; private set; }
    public Process? LauncherProcess { get; private set; }
    public Process? EACProcess { get; private set; }

    // What to pass
    private string[] Arguments = new[] {
        "-epicapp=Fortnite",
        "-epicenv=Prod",
        "-EpicPortal",
        "-noeac",
        "-nobe",
        "-fromfl=eac",
        "-fltoken=h1cdhchd10150221h130eB56"
    };
    private string ArgumentsString { get => string.Join(" ", Arguments); }

    public ClientInstance(string shippingPath, string? username = null, string? password = null) {
        ShippingPath = shippingPath;
        if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password)) {
            Arguments.Concat(new[] {
                $"-AUTH_LOGIN={username}",
                $"-AUTH_PASSWORD={password}",
                "-AUTH_TYPE=epic"
            });
        }
    }

    public void Launch() {
        LauncherProcess = Process.Start(new ProcessStartInfo(LauncherPath, ArgumentsString))!;
        Utils.SuspendThreads(LauncherProcess);
        EACProcess = Process.Start(new ProcessStartInfo(EACPath, ArgumentsString))!;
        Utils.SuspendThreads(EACProcess);

        ShippingProcess = Process.Start(new ProcessStartInfo(ShippingPath, ArgumentsString) {
            UseShellExecute = false,
            CreateNoWindow = true
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
        using var reader = new StreamReader(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Logs", "FortniteGame.log"), new FileStreamOptions {
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