#include "SettingsMenu.h"
#include "../Core/Log.h"
#include "../Unreal/Addresses.h"
#include "../Unreal/Engine.h"
#include "../Unreal/Text.h"

#include <array>
#include <cstring>
#include <vector>

namespace {
    using Engine::Member;

    std::vector<SettingsMenu::Row> Rows;
    const void* ConfigurationWeakPtr = nullptr; // static TWeakObjectPtr<UFortUIDataConfiguration>
    bool Installed = false;
    bool Prepared = false;

    // Returns false to be tried again later (not reachable yet); true once handled either way.
    bool Prepare() {
        UObject* configuration = Engine::ResolveWeak(ConfigurationWeakPtr);
        UObject* menuData = configuration ? Member<UObject*>(configuration, Offsets::UIDataConfiguration_GameOptionsMenuData) : nullptr;
        if (!menuData) return false;

        auto* rows = Member<FSettingTabMap>(menuData, Offsets::OptionsMenuData_TabDatas).FindSettingDatas((uint8_t)ESettingTab::Game);
        if (!rows || !rows->Data || rows->Num <= 0) {
            Log::Error("SettingsMenu: OptionsMenuData has no Game tab");
            return true;
        }

        // Which rows to borrow: a type must occur exactly once, or which row is ours is ambiguous.
        std::array<int, 256> occurrences{};
        for (int32_t i = 0; i < rows->Num; i++) occurrences[rows->Data[i].SettingType()]++;
        std::array<const SettingsMenu::Row*, 256> borrowing{};
        for (auto& row : Rows) {
            uint8_t type = row.Setting.Type;
            if (occurrences[type] != 1 || borrowing[type]) {
                Log::Error("SettingsMenu: \"%ls\" borrows type %u, which is in the Game tab %d times or taken - skipped",
                    row.Label, type, occurrences[type]);
                continue;
            }
            borrowing[type] = &row;
        }

        // Everything else keeps its order; the borrowed rows go last, in registration order.
        std::vector<FSettingData> kept, borrowed;
        for (int32_t i = 0; i < rows->Num; i++) {
            if (!borrowing[rows->Data[i].SettingType()]) kept.push_back(rows->Data[i]);
        }
        for (auto& row : Rows) {
            if (borrowing[row.Setting.Type] != &row) continue;
            for (int32_t i = 0; i < rows->Num; i++) {
                if (rows->Data[i].SettingType() != row.Setting.Type) continue;
                auto& data = borrowed.emplace_back(rows->Data[i]);
                // The texts being replaced keep one reference each, on purpose: the asset lives
                // as long as the process does.
                Text::Make(row.Label, data.DisplayText());
                Text::Make(row.Description, data.HoverText());
                data.DisplayOnPC() = true;
            }
        }

        // Same count, same buffer: the rows are only moved, so no allocation is involved. Done once,
        // as soon as the frontend is up - long before the menu can be opened and read it.
        memcpy(rows->Data, kept.data(), kept.size() * sizeof(FSettingData));
        memcpy(rows->Data + kept.size(), borrowed.data(), borrowed.size() * sizeof(FSettingData));
        Log::Info("SettingsMenu: %zu Prive row(s) added to the Game tab", borrowed.size());
        return true;
    }
}

namespace SettingsMenu {
    void Add(const Row& row) { Rows.push_back(row); }

    bool Install() {
        auto getConfiguration = Memory::Resolve(Addresses::GetUIDataConfiguration);
        if (!getConfiguration || !Text::Init()) {
            Log::Error("SettingsMenu: not installed");
            return false;
        }
        ConfigurationWeakPtr = (const void*)Memory::ResolveRelative(getConfiguration, 17, 21);
        Installed = true;
        return true;
    }

    void Poll() {
        if (!Installed || Prepared || Rows.empty() || !Engine::PlayerController()) return;
        Prepared = Prepare();
    }

    std::optional<bool> Value(const Row& row) {
        UObject* localPlayer = Engine::LocalPlayer();
        UObject* record = localPlayer ? Member<UObject*>(localPlayer, Offsets::LocalPlayer_ClientSettingsRecord) : nullptr;
        if (!record) return std::nullopt;
        return Member<bool>(record, row.Setting.RecordOffset);
    }
}
