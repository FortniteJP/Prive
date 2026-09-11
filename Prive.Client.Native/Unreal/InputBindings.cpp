#include "InputBindings.h"
#include "Engine.h"
#include "../Core/Log.h"
#include "../Core/Memory.h"

#include <windows.h>

namespace {
    using Engine::Member;
    using InputBindings::Outcome;

    constexpr uint8_t BoundToFuncDelegate = 1;

    // Everything read here can be mid-destruction. While a match is being joined, the local
    // player's PlayerController is torn down and replaced; the first version read its +0x1350
    // blindly and died on a non-canonical pointer (2026-09-11). So: every pointer is checked
    // before it is followed, the input component must name this controller as its Outer (which
    // also rules out a controller class without the member), and the store only happens if the
    // slot still holds the game's handler at that instant.
    Outcome TryApply(const InputBindings::Redirect& redirect, UObject*& controllerOut) {
        UObject* controller = Engine::PlayerController();
        controllerOut = controller;
        if (!controller || !Memory::IsReadable((uint8_t*)controller + redirect.ComponentOffset, sizeof(void*))) return Outcome::NotFound;
        UObject* input = Member<UObject*>(controller, redirect.ComponentOffset);
        if (!Memory::IsReadable(input, Offsets::InputComponent_ActionBindings + sizeof(TArray<FSharedActionBinding>))) return Outcome::NotFound;
        if (Member<UObject*>(input, Offsets::Object_Outer) != controller) return Outcome::NotFound;

        auto& bindings = Member<TArray<FSharedActionBinding>>(input, Offsets::InputComponent_ActionBindings);
        if (!bindings.Data || bindings.Num <= 0 || bindings.Num > 4096) return Outcome::NotFound;
        if (!Memory::IsReadable(bindings.Data, bindings.Num * sizeof(FSharedActionBinding))) return Outcome::NotFound;

        for (int32_t i = 0; i < bindings.Num; i++) {
            FInputActionBinding* binding = bindings.Data[i].Object;
            if (!Memory::IsReadable(binding, sizeof(FInputActionBinding))) continue;
            if (binding->KeyEvent != redirect.KeyEvent || binding->BoundDelegateType != BoundToFuncDelegate) continue;
            if (!Memory::IsReadable(binding->FuncDelegate, sizeof(FNativeDelegate))) continue;
            FMethodDelegateInstance* instance = binding->FuncDelegate->Instance;
            if (!Memory::IsReadable(instance, sizeof(FMethodDelegateInstance))) continue;

            // Identified by what it calls, not by its FName (which cannot be resolved from here):
            // the handlers used are each bound once per component, and to this controller.
            if (instance->Method != redirect.Replacement && instance->Method != redirect.Original) continue;
            if (instance->ThisAdjustment != 0 || Engine::ResolveWeak(instance->UserObject) != controller) continue;
            if (instance->Method == redirect.Replacement) return Outcome::AlreadyRepointed;

            // The game thread sees either the old handler or ours - and nothing is written unless
            // the slot still holds the game's handler right now.
            void* previous = InterlockedCompareExchangePointer((void* volatile*)&instance->Method, redirect.Replacement, redirect.Original);
            return previous == redirect.Original ? Outcome::Repointed : Outcome::NotFound;
        }
        return Outcome::NotFound;
    }

    // The checks above narrow the window; this closes it. Memory freed between a check and the
    // read that follows it is an access violation, which here just means "not this time" (CrashLog
    // still reports it as first-chance).
    Outcome TryApplyGuarded(const InputBindings::Redirect& redirect, UObject*& controller) {
        __try {
            return TryApply(redirect, controller);
        } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
            return Outcome::NotFound;
        }
    }
}

namespace InputBindings {
    Outcome Apply(const Redirect& redirect) {
        UObject* controller = nullptr;
        Outcome outcome = TryApplyGuarded(redirect, controller);
        if (outcome == Outcome::Repointed) Log::Info("InputBindings: %s of %p repointed", redirect.Name, controller);
        return outcome;
    }
}
