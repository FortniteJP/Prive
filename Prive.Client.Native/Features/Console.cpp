#include "Console.h"
#include "../Core/Config.h"
#include "../Core/Log.h"
#include "../Unreal/Addresses.h"
#include "../Unreal/Engine.h"

namespace {
    using Engine::Member;

    using StaticConstructObjectFn = UObject*(__fastcall*)(UClass* objectClass, UObject* outer, uint64_t name,
        uint32_t flags, uint32_t internalFlags, UObject* objectTemplate, bool copyTransientsFromClassDefaults,
        void* instanceGraph, bool assumeTemplateIsArchetype);
    StaticConstructObjectFn StaticConstructObject = nullptr;

    std::function<bool()> EnabledSource;
    bool Installed = false;
}

namespace Console {
    bool Install() { // only after Engine::Init succeeded
        StaticConstructObject = (StaticConstructObjectFn)Memory::Resolve(Addresses::StaticConstructObject);
        if (!StaticConstructObject) return false;
        if (!EnabledSource) EnabledSource = [] { return Config::GetBool("DeveloperConsole", true); };
        Installed = true;
        return true;
    }

    void SetEnabledSource(std::function<bool()> source) { EnabledSource = std::move(source); }

    void Poll() {
        // The frontend world is running. The console used to be created right at the end of
        // engine init; that is not what crashed the game, but UniversalFNConsole, which always
        // worked, only ever ran this late.
        if (!Installed || !Engine::PlayerController()) return;
        UObject* viewport = Engine::GameViewport();
        auto& console = Member<UObject*>(viewport, Offsets::GameViewportClient_ViewportConsole);
        bool enabled = EnabledSource();

        if (!enabled) {
            if (console) {
                // The engine's own DetachViewportClient does exactly this; the collector frees it.
                console = nullptr;
                Log::Info("Console: removed");
            }
            return;
        }
        if (console) return;

        auto* consoleClass = Member<UClass*>(Engine::GEngine(), Offsets::Engine_ConsoleClass);
        if (!consoleClass) return;
        console = StaticConstructObject(consoleClass, viewport, 0 /* NAME_None */, 0, 0, nullptr, false, nullptr, false);
        Log::Info("Console: created %p", console);
    }
}
