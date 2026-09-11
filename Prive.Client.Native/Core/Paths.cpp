#include "Paths.h"

#include <windows.h>
#include <shlobj.h>

namespace Paths {
    const std::wstring& LocalAppData() {
        static const std::wstring directory = [] {
            std::wstring result;
            PWSTR localAppData = nullptr;
            if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &localAppData))) result = localAppData;
            CoTaskMemFree(localAppData);
            return result;
        }();
        return directory;
    }

    const std::wstring& LauncherDirectory() {
        static const std::wstring directory = [] {
            if (LocalAppData().empty()) return std::wstring();
            std::wstring result = LocalAppData() + L"\\Prive.Launcher";
            CreateDirectoryW(result.c_str(), nullptr);
            return result;
        }();
        return directory;
    }
}
