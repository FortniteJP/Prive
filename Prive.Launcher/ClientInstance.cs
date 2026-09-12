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
    private List<string> Arguments { get; set; } = [
        "-epicapp=Fortnite",
        "-epicenv=Prod",
        "-EpicPortal",
        "-noeac",
        "-nobe",
        "-fromfl=eac"
    ];
    private string ArgumentsString { get => string.Join(" ", Arguments); }

    public ClientInstance(string shippingPath, string? username = null, string? password = null) {
        Arguments.Add("-fltoken=h1cdhchd10150221h130eB56");
        ShippingPath = shippingPath;
        if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password)) {
            Arguments.AddRange([
                $"-AUTH_LOGIN={username}",
                $"-AUTH_PASSWORD={password}",
                "-AUTH_TYPE=epic"
            ]);
        }
    }

    // earlyInjectDll: injected into the client via an early-bird APC BEFORE its entry point, so
    // Prive.Client.Native can unbind pak signing before the pak system mounts (custom unsigned paks).
    // When null, the DLL is injected later by InjectDll() as before.
    public void Launch(string? earlyInjectDll = null) {
        try {
            if (Directory.Exists(Utils.FortniteSavedPath)) Directory.Move(Utils.FortniteSavedPath, Utils.FortniteSavedOriginalPath);
            if (Directory.Exists(Utils.FortniteSavedPrivePath)) Directory.Move(Utils.FortniteSavedPrivePath, Utils.FortniteSavedPath);
        } catch (Exception ex) {
            #if DEBUG
            var result = Utils.MessageBox($"Failed to move saved folder: {ex.Message}\n\nDo you want to continue anyway?", "Prive", 0x00000000 | 0x00000004 | 0x00000020);
            if (result == 7)
            #endif
            throw;
        }
        LauncherProcess = Process.Start(new ProcessStartInfo(LauncherPath, ArgumentsString))!;
        Utils.SuspendThreads(LauncherProcess);
        EACProcess = Process.Start(new ProcessStartInfo(EACPath, ArgumentsString))!;
        Utils.SuspendThreads(EACProcess);

        if (earlyInjectDll != null) {
            try {
                ShippingProcess = Utils.StartSuspendedWithDll(ShippingPath, ArgumentsString, earlyInjectDll);
                EarlyInjected = true;
                return;
            } catch {
                // Fall back to a normal launch + late injection so we never regress the working path.
                EarlyInjected = false;
            }
        }
        ShippingProcess = Process.Start(new ProcessStartInfo(ShippingPath, ArgumentsString) {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
    }

    // True when Launch already injected Prive.Client.Native (early). Lets the caller skip InjectDll.
    public bool EarlyInjected { get; private set; }

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
        if (Directory.Exists(Utils.FortniteSavedPath) && Directory.Exists(Utils.FortniteSavedOriginalPath)) Directory.Move(Utils.FortniteSavedPath, Utils.FortniteSavedPrivePath);
        if (Directory.Exists(Utils.FortniteSavedOriginalPath)) Directory.Move(Utils.FortniteSavedOriginalPath, Utils.FortniteSavedPath);
    }
}