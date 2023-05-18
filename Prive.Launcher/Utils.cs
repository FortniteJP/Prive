using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

#pragma warning disable CA1416 // Validate platform compatibility

namespace Prive.Launcher {
    public static class Utils {
        public static void DeleteConsoleMenu(int nPosition) {
            IntPtr handle = GetConsoleWindow();
            IntPtr sysMenu = GetSystemMenu(handle, false);

            if (handle != IntPtr.Zero) DeleteMenu(sysMenu, nPosition, MF_BYCOMMAND);
        }

        public static bool EnableConsoleMode(uint m) {
            var consoleHandle = GetStdHandle(-10);
            var mode = 0u;
            if (!GetConsoleMode(consoleHandle, out mode)) {
                return false;
            }
            mode |= m;
            return SetConsoleMode(consoleHandle, mode);
        }

        public static bool DisableConsoleMode(uint m) {
            var consoleHandle = GetStdHandle(-10);
            var mode = 0u;
            if (!GetConsoleMode(consoleHandle, out mode)) {
                return false;
            }
            mode &= ~m;
            return SetConsoleMode(consoleHandle, mode);
        }

        public static void InjectDll(Process process, string filePath) => InjectDll(process.Id, filePath);

        public static void InjectDll(int processId, string filePath) {
            if (!File.Exists(filePath)) throw new FileNotFoundException("File not found", filePath);

            var handle = OpenProcess(0x1F0FFF, false, processId);
            var loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryA");
            var size = (uint)((filePath.Length + 1) * Marshal.SizeOf(typeof(char)));
            var address = VirtualAllocEx(handle, IntPtr.Zero, size, 0x1000 | 0x2000, 4);
            
            WriteProcessMemory(handle, address, Encoding.Default.GetBytes(filePath), size, out _);
            CreateRemoteThread(handle, IntPtr.Zero, 0, loadLibrary, address, 0, IntPtr.Zero);
        }

        public static bool IsMaximized() => IsZoomed(GetConsoleWindow());

        public static bool SetForegroundWindow(Process process) {
            var handle = process.MainWindowHandle;
            // if (SetTopMostWindow(handle, true)) SetTopMostWindow(handle, false);
            SetTopMostWindow(handle, true);
            if (handle == IntPtr.Zero) return false;
            if (IsIconic(handle)) ShowWindowAsync(handle, 9); // SW_RESTORE

            var foregroundId = GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
            var targetId = GetWindowThreadProcessId(handle, out processId);

            AttachThreadInput((uint)targetId, (uint)foregroundId, true);
            var ret = SetForegroundWindow(handle);
            AttachThreadInput((uint)targetId, (uint)foregroundId, false);
            SetTopMostWindow(handle, false);
            return ret;
        }

        public static bool SetTopMostWindow(IntPtr handle, bool isTopMost) => SetWindowPos(handle, isTopMost ? -1 : -2, 0, 0, 0, 0, (uint)(isTopMost ? 0x0001 | 0x0002 : 0x0001 | 0x0002 | 0x0040));

        public static bool PatchForegroundLockTimeout() {
            var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
            if (key == null) return false;
            key.SetValue("ForegroundLockTimeout", 0);
            key.Close();
            return true;
        }

        public static int MessageBox(string text, string caption = "Prive", int type = 0) => MessageBox(IntPtr.Zero, text, caption, type);

        public const int MF_BYCOMMAND = 0x00000000;
        public const int SC_CLOSE = 0xF060;
        public const int SC_MINIMIZE = 0xF020;
        public const int SC_MAXIMIZE = 0xF030;
        public const int SC_SIZE = 0xF000;

        public const uint ENABLE_QUICK_EDIT = 0x0040;
        public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("user32.dll")]
        private static extern int DeleteMenu(IntPtr hMenu, int nPosition, int wFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("kernel32.dll")]
        private static extern Int32 SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        private static extern int ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenThread(int dwDesiredAccess, bool bInheritHandle, int dwThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hHandle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, int options);
    }
}