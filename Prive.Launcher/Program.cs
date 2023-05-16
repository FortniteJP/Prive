using ConsoleGUI;
using ConsoleGUI.Api;
using ConsoleGUI.Controls;
using ConsoleGUI.Space;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Prive.Launcher {
    public class Program {
        public static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static readonly bool IsServer = !IsWindows; // Run server on Linux
        public static readonly int RefreshIntervalMS = IsWindows ? 10 : 100;
        public static bool Running { get; set; } = true;

        public static void Main(string[] args) {
            if (IsWindows &&  !args.Contains("/conhost")) {
                // To avoid running in Windows Terminal, because ui is broken there
                Process.Start("conhost.exe", $"\"{Assembly.GetEntryAssembly()?.Location.Replace(".dll", ".exe")}\" /conhost");
                Environment.Exit(0);
            }

            Console.Title = "Prive";
            if (IsWindows) {
                Utils.Delete(Utils.SC_SIZE);
                Utils.Delete(Utils.SC_MAXIMIZE);
                ConsoleManager.Resize(new(25, 5));
            }
            // Set Icon? https://stackoverflow.com/a/59897483
            ConsoleManager.Console = new SimplifiedConsole();
            ConsoleManager.Setup();
            if (IsWindows) MouseHandler.Initialize();
            
            MainUI();
        }

        public static void MainUI() {
            var canvas = new Canvas();

            // How to move this center
            canvas.Add(
                new Background() { Color = new(10, 40, 10), Content = new Border() { Content = new Box() { Content = new TextBlock() { Text = "Prive Launcher!" } } } }, new(0, 7, 17, 5)
            );

            /* ConsoleManager.Content = new Box() { 
                HorizontalContentPlacement = Box.HorizontalPlacement.Center,
                VerticalContentPlacement = Box.VerticalPlacement.Center,
                Content = canvas
            }; */
            ConsoleManager.Content = canvas;
            
            while (Running) {
                Thread.Sleep(RefreshIntervalMS);
                ConsoleManager.AdjustBufferSize(); // Handle resizing
            }
        }

        public static async void ObserveInAsync() {
            await Task.Delay(1);
            await Console.In.ReadLineAsync();
            Running = false;
        }
    }
}