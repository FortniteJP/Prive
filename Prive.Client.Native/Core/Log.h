#pragma once

// Every line goes to %LOCALAPPDATA%\Prive.Launcher\Prive.Client.Native.log, and to a console window
// while one is shown. The window can be switched on and off at runtime (it is a settings row), so
// nothing here writes through stdout.
namespace Log {
    void Init();

    void SetWindowVisible(bool visible);
    bool IsWindowVisible();

    void Info(const char* format, ...);
    void Warn(const char* format, ...);
    void Error(const char* format, ...);

    // Diagnostic detail (registers, stack dumps): always written to the file, shown in the window
    // only in Debug builds, so a Release window stays readable for players.
    void Detail(const char* format, ...);
}
