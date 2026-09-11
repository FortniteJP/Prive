#pragma once

#include <string>

namespace Paths {
    // %LOCALAPPDATA%
    const std::wstring& LocalAppData();

    // %LOCALAPPDATA%\Prive.Launcher - the launcher's own folder, where it drops this DLL and where
    // hostconfig.txt lives. Created if missing.
    const std::wstring& LauncherDirectory();
}
