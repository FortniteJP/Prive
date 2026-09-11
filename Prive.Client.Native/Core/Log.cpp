#include "Log.h"
#include "Paths.h"

#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <mutex>

namespace {
    std::mutex Mutex;
    FILE* File = nullptr;
    HANDLE Window = nullptr; // console output handle while the window exists

#ifdef _DEBUG
    constexpr bool DetailInWindow = true;
#else
    constexpr bool DetailInWindow = false;
#endif

    void Write(const char* level, const char* format, va_list args, bool toWindow = true) {
        char message[2048];
        vsnprintf(message, sizeof(message), format, args);

        SYSTEMTIME time;
        GetLocalTime(&time);
        char line[2200];
        int length = snprintf(line, sizeof(line), "[%02d:%02d:%02d.%03d] %s%s\n",
            time.wHour, time.wMinute, time.wSecond, time.wMilliseconds, level, message);
        if (length < 0) return;
        if (length >= (int)sizeof(line)) length = sizeof(line) - 1;

        std::lock_guard lock(Mutex);
        if (File) {
            fwrite(line, 1, length, File);
            fflush(File);
        }
        if (Window && toWindow) {
            DWORD written;
            WriteConsoleA(Window, line, length, &written, nullptr);
        }
    }
}

namespace Log {
    void Init() {
        if (Paths::LauncherDirectory().empty()) return;
        auto path = Paths::LauncherDirectory() + L"\\Prive.Client.Native.log";
        std::lock_guard lock(Mutex);
        if (!File) File = _wfsopen(path.c_str(), L"w", _SH_DENYWR);
    }

    void SetWindowVisible(bool visible) {
        std::lock_guard lock(Mutex);
        if (visible && !Window) {
            if (!AllocConsole()) return;
            SetConsoleTitleW(L"Prive.Client.Native");
            // Closing a console window ends the whole process - i.e. the game. Hide it from the
            // settings menu instead.
            if (HWND console = GetConsoleWindow()) DeleteMenu(GetSystemMenu(console, FALSE), SC_CLOSE, MF_BYCOMMAND);
            Window = GetStdHandle(STD_OUTPUT_HANDLE);
        } else if (!visible && Window) {
            Window = nullptr;
            FreeConsole();
        }
    }

    bool IsWindowVisible() {
        std::lock_guard lock(Mutex);
        return Window != nullptr;
    }

    void Info(const char* format, ...) {
        va_list args;
        va_start(args, format);
        Write("", format, args);
        va_end(args);
    }

    void Warn(const char* format, ...) {
        va_list args;
        va_start(args, format);
        Write("WARN ", format, args);
        va_end(args);
    }

    void Error(const char* format, ...) {
        va_list args;
        va_start(args, format);
        Write("ERROR ", format, args);
        va_end(args);
    }

    void Detail(const char* format, ...) {
        va_list args;
        va_start(args, format);
        Write("ERROR ", format, args, DetailInWindow);
        va_end(args);
    }
}
