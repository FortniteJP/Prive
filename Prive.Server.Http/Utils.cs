using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Prive.Server.Http;

public static class Utils {
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

    public static void SuspendThreads(Process proc) {
        foreach (ProcessThread thread in proc.Threads) {
            var pOpenThread = OpenThread(0x0002, false, thread.Id);
            if (pOpenThread == IntPtr.Zero) continue;
            SuspendThread(pOpenThread);
        }
    }

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
}