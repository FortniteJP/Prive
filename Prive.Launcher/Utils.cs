using System.Runtime.InteropServices;
using ConsoleGUI.Space;
using Microsoft.Win32;

namespace Prive.Launcher {
    public static class Utils {
        public static void Resize(in Size size) {
            Console.SetWindowSize(size.Width, size.Height);
            Console.Write($"\x1b[8;{size.Height};{size.Width}t");

            IntPtr handle = GetConsoleWindow();

            if (handle != IntPtr.Zero) {
                SetConsoleWindowInfo(handle, true, new SmallRect() {
                    Left = 0,
                    Top = 0,
                    Right = (short)size.Width,
                    Bottom = (short)size.Height
                });
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SmallRect {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleWindowInfo(IntPtr hConsoleOutput, bool bAbsolute, in SmallRect lpConsoleWindow);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        public static uint ForceV2 { get => GetForceV2(); set => SetForceV2(value); }

        public static uint GetForceV2() {
            var key = Registry.CurrentUser.OpenSubKey("Console", true);
            if (key is null) return (uint)0;
            var result = uint.Parse((string)key.GetValue("ForceV2"));
            key.Close();
            return result;
        }

        public static void SetForceV2(uint value) {
            var key = Registry.CurrentUser.OpenSubKey("Console", true);
            if (key is null) return;
            key.SetValue("ForceV2", value, RegistryValueKind.DWord);
            key.Close();
        }

        public const int MF_BYCOMMAND = 0x00000000;
        public const int SC_CLOSE = 0xF060;
        public const int SC_MINIMIZE = 0xF020;
        public const int SC_MAXIMIZE = 0xF030;
        public const int SC_SIZE = 0xF000;

        [DllImport("user32.dll")]
        public static extern int DeleteMenu(IntPtr hMenu, int nPosition, int wFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        public static void Delete(int nPosition) {
            IntPtr handle = GetConsoleWindow();
            IntPtr sysMenu = GetSystemMenu(handle, false);

            if (handle != IntPtr.Zero) DeleteMenu(sysMenu, nPosition, MF_BYCOMMAND);
        }
    }
}