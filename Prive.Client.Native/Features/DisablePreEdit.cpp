#include "DisablePreEdit.h"
#include "../Core/Log.h"
#include "../Unreal/Addresses.h"
#include "../Unreal/Engine.h"
#include "../Unreal/InputBindings.h"

namespace {
    using Engine::Member;

    constexpr uint8_t IE_Pressed = 0;

    using HandlerFn = void(__fastcall*)(UObject* controller);
    HandlerFn PerformBuildingEditInteraction = nullptr;

    std::function<bool()> Enabled;
    InputBindings::Redirect Binding{};
    bool Installed = false;

    bool TargetsBuildPreview(UObject* controller) {
        UObject* target = Member<UObject*>(controller, Offsets::PlayerController_TargetedBuilding);
        return target
            && (target == Member<UObject*>(controller, Offsets::PlayerController_BuildPreviewMarker)
                || target == Member<UObject*>(controller, Offsets::PlayerController_BuildPreviewMarkerExtraPiece));
    }

    // Called by the game, on the game thread, in place of the PerformBuildingEditInteraction handler.
    void __fastcall PerformBuildingEditInteractionDetour(UObject* controller) {
        if (Enabled() && TargetsBuildPreview(controller)) return;
        PerformBuildingEditInteraction(controller);
    }
}

namespace DisablePreEdit {
    bool Install(std::function<bool()> enabled) {
        PerformBuildingEditInteraction = (HandlerFn)Memory::Resolve(Addresses::PerformBuildingEditInteraction);
        if (!PerformBuildingEditInteraction) {
            Log::Error("DisablePreEdit: not installed");
            return false;
        }
        Enabled = std::move(enabled);
        Binding = { "PerformBuildingEditInteraction-pressed binding", Offsets::Actor_InputComponent, IE_Pressed,
            (void*)PerformBuildingEditInteraction, (void*)&PerformBuildingEditInteractionDetour };
        Installed = true;
        return true;
    }

    void Poll() {
        if (Installed) InputBindings::Apply(Binding);
    }
}
