#include "Config.h"
#include "Log.h"
#include "Paths.h"

#include <fstream>
#include <map>
#include <mutex>
#include <string>

namespace {
    std::mutex Mutex;
    std::map<std::string, std::string, std::less<>> Values;

    std::wstring FilePath() { return Paths::LauncherDirectory() + L"\\ClientSettings.ini"; }

    std::string Trim(const std::string& value) {
        auto first = value.find_first_not_of(" \t\r\n");
        if (first == std::string::npos) return {};
        auto last = value.find_last_not_of(" \t\r\n");
        return value.substr(first, last - first + 1);
    }

    void Save() {
        std::ofstream file(FilePath(), std::ios::trunc);
        if (!file) {
            Log::Warn("Config: cannot write ClientSettings.ini");
            return;
        }
        for (auto& [key, value] : Values) file << key << '=' << value << '\n';
    }
}

namespace Config {
    void Load() {
        std::lock_guard lock(Mutex);
        std::ifstream file(FilePath());
        std::string line;
        while (std::getline(file, line)) {
            auto separator = line.find('=');
            if (separator == std::string::npos) continue;
            auto key = Trim(line.substr(0, separator));
            if (!key.empty()) Values[key] = Trim(line.substr(separator + 1));
        }
    }

    bool GetBool(std::string_view key, bool fallback) {
        std::lock_guard lock(Mutex);
        auto it = Values.find(key);
        if (it == Values.end()) return fallback;
        return it->second == "1" || it->second == "true" || it->second == "True";
    }

    void SetBool(std::string_view key, bool value) {
        std::lock_guard lock(Mutex);
        Values[std::string(key)] = value ? "1" : "0";
        Save();
    }
}
