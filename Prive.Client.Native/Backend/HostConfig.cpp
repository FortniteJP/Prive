#include "HostConfig.h"
#include "../Core/Log.h"
#include "../Core/Paths.h"

#include <fstream>
#include <sstream>
#include <vector>

namespace {
    std::string SchemeValue = "https";
    std::string HostValue = "api-prive.xthe.org";
    std::string PortValue = "443";
}

namespace HostConfig {
    void Load() {
        auto path = Paths::LauncherDirectory() + L"\\hostconfig.txt";
        std::ifstream file(path);
        if (!file) {
            Log::Info("HostConfig: no hostconfig.txt, using %s://%s:%s", SchemeValue.c_str(), HostValue.c_str(), PortValue.c_str());
            return;
        }

        std::string content((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
        content.erase(content.find_last_not_of(" \t\r\n") + 1);

        std::vector<std::string> parts;
        std::stringstream stream(content);
        for (std::string part; std::getline(stream, part, ':');) parts.push_back(part);
        if (parts.size() != 3) {
            Log::Warn("HostConfig: expected scheme:host:port, got \"%s\" - using %s://%s:%s", content.c_str(), SchemeValue.c_str(), HostValue.c_str(), PortValue.c_str());
            return;
        }

        SchemeValue = parts[0];
        HostValue = parts[1];
        PortValue = parts[2];
        Log::Info("HostConfig: %s://%s:%s", SchemeValue.c_str(), HostValue.c_str(), PortValue.c_str());
    }

    const std::string& Scheme() { return SchemeValue; }
    const std::string& Host() { return HostValue; }
    const std::string& Port() { return PortValue; }
}
