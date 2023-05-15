using ConsoleGUI;
using ConsoleGUI.Api;
using ConsoleGUI.Controls;
using ConsoleGUI.Space;
using System.Diagnostics;
using System.Reflection;

namespace Prive.Launcher {
    public class Program {
        public static void Main(string[] args) {
            if (!args.Contains("/conhost")) {
                // To avoid running in Windows Terminal, because ui is broken there
                Process.Start("conhost.exe", $"\"{Assembly.GetEntryAssembly()?.Location.Replace(".dll", ".exe")}\" /conhost");
                Environment.Exit(0);
            }

            Utils.Delete(Utils.SC_SIZE);
            Utils.Delete(Utils.SC_MAXIMIZE);
            Console.Title = "Prive";
            // Set Icon? https://stackoverflow.com/a/59897483
            ConsoleManager.Console = new SimplifiedConsole();
            ConsoleManager.Resize(new(25, 5));
            ConsoleManager.Setup();
            MouseHandler.Initialize();
            
            ConsoleManager.Content = new Box() { 
                HorizontalContentPlacement = Box.HorizontalPlacement.Center,
                VerticalContentPlacement = Box.VerticalPlacement.Center,
                Content = new TextBlock() { Text = "Prive Launcher!" }
            };
            
            while (true) {
                Thread.Sleep(10);
                ConsoleManager.AdjustBufferSize(); // Handle resizing
            }
        }
    }
}