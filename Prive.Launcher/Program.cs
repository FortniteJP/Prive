using Terminal.Gui;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

#pragma warning disable CA1416 // Validate platform compatibility

namespace Prive.Launcher {
    public class Program {
        public static readonly string ExecutingPath = Process.GetCurrentProcess().MainModule!.FileName;
        public static readonly string ExecutingAssemblyPath = Assembly.GetExecutingAssembly().Location.Replace(".dll", ".exe");
        public static string ExecutablePath { get => ExecutingPath.EndsWith("dotnet.exe") ? ExecutingAssemblyPath : ExecutingPath; }

        public static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static readonly bool IsServer = !IsWindows; // Run server on Linux
        public static readonly int RefreshIntervalMS = IsWindows ? 10 : 100;

        public static void Main(string[] args) {
            if (IsWindows &&  !args.Contains("/conhost")) {
                // To avoid running in Windows Terminal, because ui is broken there
                Process.Start("conhost.exe", $"\"{ExecutablePath}\" /conhost");
                Environment.Exit(0);
            }

            Application.Init();
            Console.Title = "Prive";
            if (IsWindows) {
                Utils.DisableConsoleMode(Utils.ENABLE_QUICK_EDIT);
                Utils.EnableConsoleMode(Utils.ENABLE_VIRTUAL_TERMINAL_PROCESSING);
                Utils.DeleteConsoleMenu(Utils.SC_SIZE);

                #if DEBUG
                // Restart on maximize in debug environment
                Application.Resized += e => {
                    if (Utils.IsMaximized()) Restart();
                };
                #else
                Utils.DeleteConsoleMenu(Utils.SC_MAXIMIZE);
                #endif

                Console.SetWindowSize(55, 15);
                // Set Icon? https://stackoverflow.com/a/59897483
            }
            
            Application.Run<MainWindow>();
            Console.ReadKey(true);
        }

        public class MainWindow : Window {
            public MainWindow() : base("Prive Launcher") {
                ColorScheme.Normal = new(Color.BrightMagenta, Color.Black);
                Add(
                    new Label() {
                        Text = "Hello!",
                        X = 1
                    }
                );
            }
        }

        public static void Restart() {
            Application.RequestStop();
            var path = string.Join('\\', ExecutablePath.Replace('/', '\\').Split('\\')[..^4]);
            Process.Start("conhost.exe", $"dotnet run \"{path}\" -- /conhost");
            Environment.Exit(0);
        }
    }
}