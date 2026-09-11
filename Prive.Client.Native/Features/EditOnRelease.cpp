#include "EditOnRelease.h"
#include "../Core/Log.h"
#include "../Unreal/Addresses.h"
#include "../Unreal/InputBindings.h"

namespace {
    constexpr uint8_t IE_Released = 1;

    using HandlerFn = void(__fastcall*)(UObject* controller);
    HandlerFn EditSelectReleased = nullptr;
    HandlerFn CompleteBuildingEditInteraction = nullptr;

    std::function<bool()> Enabled;
    InputBindings::Redirect Binding{};
    bool Installed = false;

    // Called by the game, on the game thread, in place of the EditSelect-released handler.
    void __fastcall EditSelectReleasedDetour(UObject* controller) {
        EditSelectReleased(controller);
        if (Enabled()) CompleteBuildingEditInteraction(controller);
    }
}

namespace EditOnRelease {
    bool Install(std::function<bool()> enabled) {
        EditSelectReleased = (HandlerFn)Memory::Resolve(Addresses::EditSelectReleased);
        CompleteBuildingEditInteraction = (HandlerFn)Memory::Resolve(Addresses::CompleteBuildingEditInteraction);
        if (!EditSelectReleased || !CompleteBuildingEditInteraction) {
            Log::Error("EditOnRelease: not installed");
            return false;
        }
        Enabled = std::move(enabled);
        Binding = { "EditSelect-released binding", Offsets::PlayerController_EditModeInputComponent, IE_Released,
            (void*)EditSelectReleased, (void*)&EditSelectReleasedDetour };
        Installed = true;
        return true;
    }

    void Poll() {
        if (Installed) InputBindings::Apply(Binding);
    }
}
