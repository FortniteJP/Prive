#include "CrashLog.h"
#include "Log.h"
#include "Memory.h"

#include <windows.h>
#include <atomic>
#include <cstdio>

namespace {
    // First-chance: an exception the game goes on to handle itself is reported too, so keep a few.
    constexpr int MaxReports = 5;
    constexpr int StackSlots = 512;
    std::atomic<int> Reports = 0;

    // "FortniteClient-Win64-Shipping.exe+0x22FA300", or the raw address outside any module.
    void Describe(uintptr_t address, char* out, size_t size) {
        HMODULE module = nullptr;
        wchar_t path[MAX_PATH];
        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)address, &module)
            && GetModuleFileNameW(module, path, MAX_PATH)) {
            const wchar_t* name = wcsrchr(path, L'\\');
            snprintf(out, size, "%ls+0x%llX", name ? name + 1 : path, (unsigned long long)(address - (uintptr_t)module));
        } else {
            snprintf(out, size, "0x%llX (no module)", (unsigned long long)address);
        }
    }

    bool IsCode(uintptr_t address) {
        MEMORY_BASIC_INFORMATION region;
        if (!VirtualQuery((void*)address, &region, sizeof(region)) || region.State != MEM_COMMIT) return false;
        return region.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY);
    }

    LONG CALLBACK Handler(EXCEPTION_POINTERS* info) {
        DWORD code = info->ExceptionRecord->ExceptionCode;
        if (code != EXCEPTION_ACCESS_VIOLATION && code != EXCEPTION_ILLEGAL_INSTRUCTION && code != EXCEPTION_PRIV_INSTRUCTION) {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        if (Reports.fetch_add(1) >= MaxReports) return EXCEPTION_CONTINUE_SEARCH;

        CONTEXT* context = info->ContextRecord;
        char where[300];
        Describe(context->Rip, where, sizeof(where));
        if (code == EXCEPTION_ACCESS_VIOLATION && info->ExceptionRecord->NumberParameters >= 2) {
            auto kind = info->ExceptionRecord->ExceptionInformation[0];
            Log::Error("CrashLog: (first-chance) access violation at %s (thread %lu): %s 0x%llX", where, GetCurrentThreadId(),
                kind == 0 ? "read of" : kind == 1 ? "write to" : "execute at",
                (unsigned long long)info->ExceptionRecord->ExceptionInformation[1]);
        } else {
            Log::Error("CrashLog: (first-chance) exception 0x%08lX at %s (thread %lu)", code, where, GetCurrentThreadId());
        }
        // The summary above is shown everywhere; registers and the stack go to the file always and
        // to the window only in Debug builds (Log::Detail).
        Log::Detail("CrashLog: rax=%llX rcx=%llX rdx=%llX r8=%llX r9=%llX rsp=%llX",
            context->Rax, context->Rcx, context->Rdx, context->R8, context->R9, context->Rsp);

        // Every stack slot that points into executable memory: a superset of the return addresses,
        // which is enough to name the call chain without symbols.
        auto* stack = (const uintptr_t*)context->Rsp;
        for (int i = 0; i < StackSlots; i++) {
            if (!Memory::IsReadable(stack + i, sizeof(uintptr_t))) break;
            uintptr_t value = stack[i];
            if (value < 0x10000 || !IsCode(value)) continue;
            Describe(value, where, sizeof(where));
            Log::Detail("CrashLog:   [rsp+0x%03X] %s", i * 8, where);
        }
        return EXCEPTION_CONTINUE_SEARCH;
    }
}

namespace CrashLog {
    void Install() {
        AddVectoredExceptionHandler(1, Handler);
    }
}
