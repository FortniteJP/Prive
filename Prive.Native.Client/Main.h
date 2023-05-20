#include "CurlHook.h"
#include <Psapi.h>

void* FindPattern(const char* pattern, size_t patternSize) {
    MODULEINFO moduleInfo;
    GetModuleInformation(GetCurrentProcess(), GetModuleHandle(NULL), &moduleInfo, sizeof(MODULEINFO));

    auto moduleBase = (size_t)moduleInfo.lpBaseOfDll;

    for (size_t i = 0; i < moduleInfo.SizeOfImage - patternSize; i++) {
        bool found = true;
        for (size_t j = 0; j < patternSize; j++) {
            if (pattern[j] != *(char*)(moduleBase + i + j)) {
                found = false;
                break;
            }
        }
        if (found) {
            return (void*)(moduleBase + i);
        }
    }
    return NULL;
}

LPCVOID RedirFuncPtr;
LPCVOID OrigFuncPtr;

LONG ExcHandler(EXCEPTION_POINTERS* ExceptionInfo) {
    if (ExceptionInfo->ExceptionRecord->ExceptionCode == EXCEPTION_GUARD_PAGE) {
        if (ExceptionInfo->ContextRecord->Rip == (DWORD64)OrigFuncPtr) ExceptionInfo->ContextRecord->Rip = (DWORD64)RedirFuncPtr;
        ExceptionInfo->ContextRecord->EFlags |= 0x100;
        return EXCEPTION_CONTINUE_EXECUTION;
    } else if (ExceptionInfo->ExceptionRecord->ExceptionCode == EXCEPTION_SINGLE_STEP) {
        DWORD oldFlags;
        VirtualProtect((LPVOID)OrigFuncPtr, 1, PAGE_GUARD | PAGE_EXECUTE_READ, &oldFlags);
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

void EnableHook(const void* origFuncPtr, const void* redirFuncPtr) {
    MEMORY_BASIC_INFORMATION redirMemInfo;
    MEMORY_BASIC_INFORMATION origMemInfo;

    RedirFuncPtr = redirFuncPtr;
    OrigFuncPtr = origFuncPtr;
    if (VirtualQuery(OrigFuncPtr, &origMemInfo, sizeof(origMemInfo)) && VirtualQuery(RedirFuncPtr, &redirMemInfo, sizeof(redirMemInfo))) {
        if (origMemInfo.BaseAddress != redirMemInfo.BaseAddress) {
            if (AddVectoredExceptionHandler(1, ExcHandler)) {
                DWORD oldFlags;
                VirtualProtect((LPVOID)OrigFuncPtr, 1, PAGE_GUARD | PAGE_EXECUTE_READ, &oldFlags);
            }
        }
    }
}