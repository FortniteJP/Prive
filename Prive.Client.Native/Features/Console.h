#pragma once

#include <functional>

// The UE developer console (the ` key). Shipping builds never create one, so this does what
// UGameViewportClient::SetupInitialLocalPlayer does in a development build: construct
// GEngine->ConsoleClass with the game viewport as its outer and store it in ViewportConsole.
//
// Replaces the separately injected FortniteConsole.dll (UniversalFNConsole) and does it the same
// proven way: from our own thread, once the frontend is running. No log-line wait and no hook.
namespace Console {
    bool Install();

    // Whether the console should exist. Default: `DeveloperConsole` in ClientSettings.ini (on).
    void SetEnabledSource(std::function<bool()> source);

    // Called periodically from the watcher thread: creates or removes the console to match.
    void Poll();
}
