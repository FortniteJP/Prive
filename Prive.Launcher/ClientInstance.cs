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
    private string Arguments = "-epicapp=Fortnite -epicenv=Prod -EpicPortal -noeac -nobe -fromfl=eac -fltoken=h1cdhchd10150221h130eB56";
    
    public ClientInstance(string shippingPath) {
        ShippingPath = shippingPath;
    }

    public ClientInstance(string shippingPath, string? username = null, string? password = null) {
        ShippingPath = shippingPath;
        if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password)) {
            Arguments += $" -AUTH_LOGIN={username} -AUTH_PASSWORD={password} -AUTH_TYPE=epic";
        }
    }

    public void Launch() {
        LauncherProcess = Process.Start(new ProcessStartInfo(LauncherPath, Arguments))!;
        Utils.SuspendThreads(LauncherProcess);
        EACProcess = Process.Start(new ProcessStartInfo(EACPath, Arguments))!;
        Utils.SuspendThreads(EACProcess);

        ShippingProcess = Process.Start(new ProcessStartInfo(ShippingPath, Arguments) {
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

    public void Kill() {
        ShippingProcess?.Kill();
        LauncherProcess?.Kill();
        EACProcess?.Kill();
    }

    public void WaitForExit() {
        ShippingProcess?.WaitForExit();
    }
}