#include "GuardHook.h"
#include "Log.h"

#include <windows.h>

namespace {
    uintptr_t Target = 0;
    uintptr_t Detour = 0;
    uintptr_t PageStart = 0;
    uintptr_t PageEnd = 0;
    DWORD PageProtect = 0; // the page's own protection, without PAGE_GUARD

    // Set when this thread's trap flag was ours, so a single-step we did not cause passes through.
    thread_local bool Rearm = false;

    void Arm() {
        DWORD old;
        VirtualProtect((void*)Target, 1, PageProtect | PAGE_GUARD, &old);
    }

    LONG CALLBACK Handler(EXCEPTION_POINTERS* info) {
        auto* record = info->ExceptionRecord;
        auto* context = info->ContextRecord;

        if (record->ExceptionCode == STATUS_GUARD_PAGE_VIOLATION) {
            uintptr_t accessed = record->NumberParameters >= 2 ? record->ExceptionInformation[1] : 0;
            if (accessed < PageStart || accessed >= PageEnd) return EXCEPTION_CONTINUE_SEARCH;
            // The guard is one-shot and is gone now; re-arm after one instruction.
            if (context->Rip == Target) context->Rip = Detour;
            context->EFlags |= 0x100;
            Rearm = true;
            return EXCEPTION_CONTINUE_EXECUTION;
        }
        if (record->ExceptionCode == STATUS_SINGLE_STEP && Rearm) {
            Rearm = false;
            Arm();
            return EXCEPTION_CONTINUE_EXECUTION;
        }
        return EXCEPTION_CONTINUE_SEARCH;
    }
}

namespace GuardHook {
    bool Install(const char* name, void* target, void* detour) {
        if (Target) {
            Log::Error("GuardHook %s: only one hook is supported (already hooking %p)", name, (void*)Target);
            return false;
        }
        MEMORY_BASIC_INFORMATION region;
        if (!target || !VirtualQuery(target, &region, sizeof(region))) {
            Log::Error("GuardHook %s: cannot query %p", name, target);
            return false;
        }
        SYSTEM_INFO system;
        GetSystemInfo(&system);
        Target = (uintptr_t)target;
        Detour = (uintptr_t)detour;
        PageStart = Target & ~((uintptr_t)system.dwPageSize - 1);
        PageEnd = PageStart + system.dwPageSize;
        PageProtect = region.Protect & ~PAGE_GUARD;

        if (!AddVectoredExceptionHandler(1, Handler)) {
            Log::Error("GuardHook %s: AddVectoredExceptionHandler failed", name);
            Target = 0;
            return false;
        }
        Arm();
        Log::Info("GuardHook %s at %p (page protection 0x%lX)", name, target, PageProtect);
        return true;
    }
}
