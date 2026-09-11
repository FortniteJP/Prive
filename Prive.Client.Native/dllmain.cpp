// Prive.Client.Native - injected into FortniteClient-Win64-Shipping.exe 10.40 by Prive.Launcher (and
// by Prive.Server.Http into its game-server process) right after the process starts.
//
//   Backend/   send *.epicgames.com traffic to the Prive backend (must win the race to the first
//              request) - except the game's settings, which a local HTTP server keeps on this machine
//   Features/  the UE developer console (Debug builds), Confirm Edit on Release, Disable Pre-Edit,
//              and their settings-menu rows
//   Unreal/    the 10.40 addresses and engine types those need, input-binding redirects
//   Core/      logging, config, memory, the PAGE_GUARD redirect, crash logging
//
// Rule: never write to the game's code. The protector checks it and crashes the game from its
// OnPostEngineInit callback (two live rounds with MinHook, 2026-09-11). Everything here reads data,
// writes data, or calls functions; the one redirect (curl) is a PAGE_GUARD, like the original DLL.

#include "Backend/CurlRedirect.h"
#include "Backend/HostConfig.h"
#include "Backend/LocalCloudStorage.h"
#include "Core/Config.h"
#include "Core/CrashLog.h"
#include "Core/GameVersion.h"
#include "Core/Log.h"
#include "Core/Memory.h"
#include "Features/Console.h"
#include "Features/DisablePreEdit.h"
#include "Features/EditOnRelease.h"
#include "Features/SettingsMenu.h"
#include "Unreal/Engine.h"

#include <windows.h>

namespace {
    // `LogWindow=0` in ClientSettings.ini hides the log window.
    constexpr const char* LogWindowKey = "LogWindow";
    // `LocalCloudStorage=0` keeps the game's settings (user cloud storage) on the backend.
    constexpr const char* LocalCloudStorageKey = "LocalCloudStorage";
    constexpr DWORD WatchIntervalMs = 250;

    // The developer console, and its settings row, exist in Debug builds only: the Release DLL that
    // players get (CI builds Release) can neither show nor create it.
#ifdef _DEBUG
    constexpr bool DeveloperConsoleAllowed = true;
#else
    constexpr bool DeveloperConsoleAllowed = false;
#endif

    const SettingsMenu::Row ConsoleRow{
        SettingsMenu::Borrowable::TouchEdit,
        L"Prive: Developer Console",
        L"Opens the Unreal console with the ` (tilde) key.",
    };
    // Borrows the game's own (touch-only) setting of the same name, so the value is the real
    // bEditConfirmOnReleaseEnabled - EditOnRelease makes it work for keyboard and mouse too.
    const SettingsMenu::Row EditOnReleaseRow{
        SettingsMenu::Borrowable::EditConfirmOnRelease,
        L"Confirm Edit on Release",
        L"Automatically confirms an edit when you release the select button.",
    };
    // Borrows "Motion Invert Turn", whose value nothing on PC acts on.
    const SettingsMenu::Row DisablePreEditRow{
        SettingsMenu::Borrowable::InvertYawForMotion,
        L"Disable Pre-Edit",
        L"Pressing Edit while aiming at your build preview does nothing, instead of editing the unplaced piece.",
    };

    // The rows borrow rows of 10.40's options menu data and the edit features use 10.40 handler
    // addresses; nothing checks a newer build still has either, so only below 11 (Erbium, too, only
    // redirects EditSelect below 11).
    void InstallVersionFeatures(bool engineReady, bool consoleInstalled) {
        if (!GameVersion::IsBelow(11)) {
            Log::Info("SettingsMenu/edit features: only enabled on a known Fortnite version below 11 - skipped");
            return;
        }
        bool editOnRelease = engineReady
            && EditOnRelease::Install([] { return SettingsMenu::Value(EditOnReleaseRow).value_or(false); });
        bool disablePreEdit = engineReady
            && DisablePreEdit::Install([] { return SettingsMenu::Value(DisablePreEditRow).value_or(false); });

        if (consoleInstalled) SettingsMenu::Add(ConsoleRow);
        if (editOnRelease) SettingsMenu::Add(EditOnReleaseRow);
        if (disablePreEdit) SettingsMenu::Add(DisablePreEditRow);
        if (!consoleInstalled && !editOnRelease && !disablePreEdit) {
            Log::Info("SettingsMenu: no row to show - skipped");
            return;
        }
        if (!SettingsMenu::Install()) return;
        // The row's value decides; until it can be read, keep the console (the old default).
        if (consoleInstalled) Console::SetEnabledSource([] { return SettingsMenu::Value(ConsoleRow).value_or(true); });
    }

    DWORD WINAPI Init(LPVOID) {
        Log::Init();
        CrashLog::Install();
        Config::Load();
        Log::SetWindowVisible(Config::GetBool(LogWindowKey, true));
        Log::Info("Prive.Client.Native loaded (image base %p)", (void*)Memory::ImageBase());

        // First: the game may start its first HTTP request any moment now. The local cloud storage
        // is up before the redirect, so the first settings request can already go to it.
        HostConfig::Load();
        if (Config::GetBool(LocalCloudStorageKey, true)) LocalCloudStorage::Start();
        else Log::Info("LocalCloudStorage: off (%s=0) - the game's settings are stored on the backend", LocalCloudStorageKey);
        CurlRedirect::Install();

        GameVersion::Get();

        // Each feature degrades on its own: a missing address disables that feature, not the DLL.
        bool engineReady = Engine::Init();
        bool consoleInstalled = false;
        if (!DeveloperConsoleAllowed) Log::Info("Console: Debug builds only - not installed");
        else if (!(consoleInstalled = engineReady && Console::Install())) Log::Error("Console: not installed");
        InstallVersionFeatures(engineReady, consoleInstalled);
        Log::Info("Init done");

        // The watcher: everything that waits for the frontend, from this thread (the way the old
        // FortniteConsole.dll worked). A server process may never get a local player; then this
        // is a few pointer reads four times a second.
        while (true) {
            SettingsMenu::Poll(); // before Console::Poll, which reads the row's value
            Console::Poll();
            EditOnRelease::Poll();
            DisablePreEdit::Poll();
            Sleep(WatchIntervalMs);
        }
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        // Keep DllMain trivial (loader lock); everything runs on our own thread.
        if (HANDLE thread = CreateThread(nullptr, 0, Init, nullptr, 0, nullptr)) CloseHandle(thread);
    }
    return TRUE;
}
