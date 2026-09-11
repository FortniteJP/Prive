#pragma once

#include <string>

// Where *.epicgames.com requests are sent instead. Read once from
// %LOCALAPPDATA%\Prive.Launcher\hostconfig.txt ("scheme:host:port"), falling back to the public
// Prive backend when the file is missing or malformed.
namespace HostConfig {
    void Load();

    const std::string& Scheme();
    const std::string& Host();
    const std::string& Port();
}
