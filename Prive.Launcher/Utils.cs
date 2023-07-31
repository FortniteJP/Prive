using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

#pragma warning disable CA1416 // Validate platform compatibility

namespace Prive.Launcher;

public static partial class Utils {
    public const string ShippingExecutableName = "FortniteClient-Win64-Shipping.exe";

    // https://stackoverflow.com/questions/281640/how-do-i-get-a-human-readable-file-size-in-bytes-abbreviation-using-net, https://stackoverflow.com/a/4975942
    public static string BytesToString(long byteCount) {
        string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; // Longs run out around EB
        if (byteCount == 0) return "0" + suf[0];
        long bytes = Math.Abs(byteCount);
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
        double num = Math.Round(bytes / Math.Pow(1024, place), 1);
        return (Math.Sign(byteCount) * num).ToString() + suf[place];
    }

    public static void DeleteConsoleMenu(int nPosition) {
        IntPtr handle = GetConsoleWindow();
        IntPtr sysMenu = GetSystemMenu(handle, false);

        if (handle != IntPtr.Zero) DeleteMenu(sysMenu, nPosition, MF_BYCOMMAND);
    }

    public static bool DrawMenu() => DrawMenuBar(GetConsoleWindow());

    public static bool EnableConsoleMode(uint m) {
        var consoleHandle = GetStdHandle(-10);
        if (!GetConsoleMode(consoleHandle, out uint mode)) {
            return false;
        }
        mode |= m;
        return SetConsoleMode(consoleHandle, mode);
    }

    public static bool DisableConsoleMode(uint m) {
        var consoleHandle = GetStdHandle(-10);
        if (!GetConsoleMode(consoleHandle, out var mode)) {
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

        var foregroundId = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var targetId = GetWindowThreadProcessId(handle, out _);

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

    public static void SuspendThreads(Process proc) {
        foreach (ProcessThread thread in proc.Threads) {
            var pOpenThread = OpenThread(0x0002, false, thread.Id);
            if (pOpenThread == IntPtr.Zero) continue;
            SuspendThread(pOpenThread);
        }
    }

    public const int MF_BYCOMMAND = 0x00000000;
    public const int SC_CLOSE = 0xF060;
    public const int SC_MINIMIZE = 0xF020;
    public const int SC_MAXIMIZE = 0xF030;
    public const int SC_SIZE = 0xF000;

    public const uint ENABLE_QUICK_EDIT = 0x0040;
    public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(IntPtr hWnd);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteMenu(IntPtr hMenu, int nPosition, int wFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawMenuBar(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetSystemMenu(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bRevert);

    [LibraryImport("kernel32.dll")]
    private static partial Int32 SuspendThread(IntPtr hThread);

    [LibraryImport("kernel32.dll")]
    private static partial int ResumeThread(IntPtr hThread);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr OpenThread(int dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwThreadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hHandle);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr OpenProcess(int dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32", SetLastError = true, StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(System.Runtime.InteropServices.Marshalling.AnsiStringMarshaller))]
    private static partial IntPtr GetProcAddress(IntPtr hModule, string procName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string lpModuleName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, int options);

    [DllImport("Comdlg32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool GetOpenFileName([In, Out] ref OpenFileName ofn);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct OpenFileName {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int flagsEx;
    }
}