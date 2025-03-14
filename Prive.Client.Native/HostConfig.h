#include <iostream>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include <windows.h>
#include <shlobj.h>

class HostConfig {
private:
    static std::string Scheme, Host, Port;
    static std::string Path;
    static bool EverFailed;

    static std::string getAppDataPath() {
        char path[MAX_PATH];
        if (SUCCEEDED(SHGetFolderPathA(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, path))) {
            return std::string(path);
        }
        return "";
    }

    static bool readHostConfig(const std::string& filePath) {
        if (EverFailed) return false;
        std::ifstream file(filePath);
        if (!file) {
            std::cerr << "Failed to open file: " << filePath << std::endl;
            EverFailed = true;
            return false;
        }
        std::stringstream buffer;
        buffer << file.rdbuf();
        auto content = buffer.str();
        auto contents = splitString(content, ':');
        if (contents.size() != 3) {
            std::cerr << "Invalid configuration" << std::endl;
            return false;
        }
        Scheme = contents[0];
        Host = contents[1];
        Port = contents[2];
        std::cout << "HostConfig loaded: " << Scheme << "://" << Host << ":" << Port << std::endl;
        return true;
    }

    static std::vector<std::string> splitString(const std::string& str, char delimiter) {
        std::vector<std::string> tokens;
        std::stringstream ss(str);
        std::string token;
        while (std::getline(ss, token, delimiter)) {
            tokens.push_back(token);
        }
        return tokens;
    }

public:
    static std::string GetScheme() {
        if (Scheme.empty() && !readHostConfig(Path)) return "https";
        return Scheme;
    }

    static std::string GetHost() {
        if (Host.empty() && !readHostConfig(Path)) return "prive.xthe.org";
        return Host;
    }

    static std::string GetPort() {
        if (Port.empty() && !readHostConfig(Path)) return "443";
        return Port;
    }
};

std::string HostConfig::Path = HostConfig::getAppDataPath() + "\\Prive.Launcher\\hostconfig.txt";
bool HostConfig::EverFailed = false;
std::string HostConfig::Scheme, HostConfig::Host, HostConfig::Port;