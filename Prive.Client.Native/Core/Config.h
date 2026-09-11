#pragma once

#include <string_view>

// Prive's own client settings: %LOCALAPPDATA%\Prive.Launcher\ClientSettings.ini, one `Key=Value`
// per line. Deliberately not GameUserSettings.ini - the settings menu rows that edit these borrow
// the ESettingType of a real (mobile-only) setting, and must never write that setting's value.
namespace Config {
    void Load();

    bool GetBool(std::string_view key, bool fallback);
    void SetBool(std::string_view key, bool value); // saves immediately
}
