using ConsoleGUI;
using ConsoleGUI.Api;
using ConsoleGUI.Controls;
using ConsoleGUI.Data;
using ConsoleGUI.Input;
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
                Utils.DisableQuickEdit();
                Utils.Delete(Utils.SC_SIZE);
                Utils.Delete(Utils.SC_MAXIMIZE);
                ConsoleManager.Resize(new(40, 10));
            }
            // Set Icon? https://stackoverflow.com/a/59897483
            ConsoleManager.Console = new SimplifiedConsole();
            ConsoleManager.Setup();
            if (IsWindows) MouseHandler.Initialize();
            
            MainUI();
        }

        public static void MainUI() {
            var exitButton = new Button() {
                Content = new Margin() {
                    Offset = new(1, 1, 1, 1),
                    Content = new TextBlock() {
                        Text = "Exit"
                    }
                },
                MouseOverColor = new(255, 0, 0),
                MouseDownColor = new(100, 100, 100)
            };
            var content = new Background() {
                Color = Color.Black,
                Content = new Margin() {
                    Offset = new(5, 1, 5, 1),
                    Content = new VerticalStackPanel() {
                        Children = new IControl[] {
                            new TextBlock() {
                                Text = "Prive Launcher!"
                            },
                            new HorizontalSeparator(),
                            exitButton
                        }
                    }
                }
            };

            ConsoleManager.Content = content;

            var input = new IInputListener[] {
                new InputController(exitButton)
            };
            
            while (Running) {
                Thread.Sleep(RefreshIntervalMS);
                if (IsWindows) MouseHandler.ReadMouseEvents();
                ConsoleManager.ReadInput(input);
                ConsoleManager.AdjustBufferSize(); // Handle resizing
            }
        }

        public static async void ObserveInAsync() {
            await Task.Delay(1);
            await Console.In.ReadLineAsync();
            Running = false;
        }
    }

    public class InputController : IInputListener {
        public Button ExitButton { get; }
        
        public InputController(Button exitButton) {
            ExitButton = exitButton;
            ExitButton.Clicked += ExitButton_Clicked;
        }

        void IInputListener.OnInput(InputEvent inputEvent) {}

        private void ExitButton_Clicked(object? sender, EventArgs e) {
            Program.Running = false;
        }
    }
}