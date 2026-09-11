#pragma once

#include <cstdint>
#include <optional>

// Adds Prive's own On/Off rows to the game's Settings > Game tab - by editing DATA only.
//
// 10.40's options menu is data-driven (/Game/UI/Frontend/Settings/OptionsMenuData) and every row
// is identified by a native ESettingType. A new enum value would mean nothing to the native code,
// so each Prive row BORROWS a row the PC build hides (a touch/mobile setting): once the frontend is
// up, the row is marked visible on PC, relabelled and moved to the end of the tab. Nothing is
// hooked - the protector crashes the game on code patches - so the row keeps driving the game's
// own setting behind it (harmless on PC), and Prive reads its value from there. The game shows,
// changes and saves it like any other setting.
namespace SettingsMenu {
    // A Game-tab row that is an OptionsMenuRotator_C (generic Off/On names), hidden on PC, present
    // exactly once, whose native ShouldShowSetting check is only the PC flag - and the bool it
    // edits in UFortClientSettingsRecord (UFortLocalPlayer +0x348).
    struct Borrowed {
        uint8_t Type;          // ESettingType
        uint32_t RecordOffset; // its bool in UFortClientSettingsRecord
    };
    namespace Borrowable {
        constexpr Borrowed TouchEdit{ 80, 0x3BA };            // bTouchEditEnabled, default On
        constexpr Borrowed EditConfirmOnRelease{ 81, 0x3BB }; // bEditConfirmOnReleaseEnabled, default Off
        // "Motion Invert Turn": bInvertedYawForMotion, default Off. Only ever copied to and from
        // UFortPlayerInput::bInvertedYawMobile (+0x84D), which nothing else reads - inert on PC.
        constexpr Borrowed InvertYawForMotion{ 48, 0x3AF };
    }

    struct Row {
        Borrowed Setting;
        const wchar_t* Label;
        const wchar_t* Description;
    };

    // Before Install. Rows appear in the order they were added.
    void Add(const Row& row);

    bool Install(); // after Engine::Init

    // Called periodically from the watcher thread: patches the menu data once it is reachable.
    void Poll();

    // The row's current value (the game's own setting), or nothing before the frontend is up.
    std::optional<bool> Value(const Row& row);
}
