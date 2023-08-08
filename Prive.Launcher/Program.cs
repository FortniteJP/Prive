global using static Prive.Launcher.Program;
global using Prive.Launcher;
global using Terminal.Gui;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

#pragma warning disable CA1416 // Validate platform compatibility

namespace Prive.Launcher;

public class Program {
    public static readonly string ExecutingPath = Process.GetCurrentProcess().MainModule!.FileName;
    public static readonly string ExecutingAssemblyPath = Assembly.GetExecutingAssembly().Location.Replace(".dll", ".exe");
    public static string ExecutablePath { get => ExecutingPath.EndsWith("dotnet.exe") ? ExecutingAssemblyPath : ExecutingPath; }

    public static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static readonly bool IsServer = !IsWindows; // Run server on Linux

    public static ClientInstance? Instance { get; set; }

    public static Process? SettingsProcess { get; set; }
    public static Process? ServerProcess { get; set; }

    public static void Main(string[] args) {
        if (IsWindows && !args.Contains("/conhost")) {
            // To avoid running in Windows Terminal, because ui is broken there
            Process.Start("conhost.exe", $"\"{ExecutablePath}\" /conhost");
            Environment.Exit(0);
        }

        Application.Init();
        Console.Title = "Prive";
        if (IsWindows) {
            // Utils.PatchForegroundLockTimeout();
            Utils.DisableConsoleMode(Utils.ENABLE_QUICK_EDIT);
            Utils.EnableConsoleMode(Utils.ENABLE_VIRTUAL_TERMINAL_PROCESSING);
            Utils.DeleteConsoleMenu(Utils.SC_SIZE);

            #if DEBUG
            // Restart on maximize in debug environment
            Application.Resized += e => {
                if (Utils.IsMaximized()) {
                    if (Application.Current.GetType() == typeof(MainWindow)) Restart();
                    else Exit();
                }
            };
            #else
            Utils.DeleteConsoleMenu(Utils.SC_MAXIMIZE);
            #endif

            Console.SetWindowSize(55, 15);
            // Set Icon? https://stackoverflow.com/a/59897483
        }
        
        var cls = typeof(MainWindow);
        if (args.Contains("/w:settings")) cls = typeof(SettingsWindow);
        if (args.Contains("/w:server")) cls = typeof(ServerWindow);
        var errorHistory = new Dictionary<Type, int>();
        try {
            Application.Run((Toplevel)Activator.CreateInstance(cls)!, (e) => {
                if (e is System.ComponentModel.Win32Exception win32e && win32e.NativeErrorCode == 233) Exit();
                Utils.MessageBox(e.ToString(), "Prive - Exception", 0x00000000 | 0x00000010);
                var type = e.GetType();
                if (!errorHistory.ContainsKey(type)) errorHistory[type] = 0;
                errorHistory[type]++;
                if (errorHistory[type] > 2) return false;
                return true;
            });
        } catch (Exception e) {
            Utils.MessageBox(e.ToString(), "Prive - Unhandled Exception!", 0x00000000 | 0x00000010);
            Exit();
        }
    }

    public static void Restart() {
        Application.RequestStop();
        var path = string.Join('\\', ExecutablePath.Replace('/', '\\').Split('\\')[..^4]);
        Process.Start("conhost.exe", $"dotnet run \"{path}\" -- /conhost");
        Exit();
    }

    public static void Exit(int code = 0) {
        Application.RequestStop();
        if (SettingsProcess is not null && !SettingsProcess.HasExited) SettingsProcess.Kill();
        Environment.Exit(code);
    }

    public static Process CreateNewWindow<T>() where T : Window => CreateNewWindow(typeof(T).Name.ToLower().Replace("window", ""));
    
    public static Process CreateNewWindow(string windowType) => Process.Start(new ProcessStartInfo("conhost.exe", $"\"{ExecutablePath}\" /conhost /w:{windowType}") { UseShellExecute = true })!;
}