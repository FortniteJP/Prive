#include <iostream>
#include "Main.h"

void Main() {
    if (true) {
        AllocConsole();
        FILE* pFile;
        freopen_s(&pFile, "CONOUT$", "w", stdout);
    }

    auto easyFind = FindPattern(
        "\x89\x54\x24\x10\x4C\x89\x44\x24\x18\x4C\x89\x4C\x24\x20\x48\x83\xEC\x28\x48\x85\xC9\x75\x08\x8D\x41\x2B\x48\x83\xC4\x28\xC3\x4C",
        0x20
    );
    auto setFind = FindPattern(
        "\x48\x89\x5C\x24\x08\x48\x89\x6C\x24\x10\x48\x89\x74\x24\x18\x57\x48\x83\xEC\x30\x33\xED\x49\x8B\xF0\x48\x8B\xD9",
        0x1c
    );

    OCurlEasySetOpt = (decltype(OCurlEasySetOpt))easyFind;
    OCurlSetOpt = (decltype(OCurlSetOpt))setFind;

    EnableHook(OCurlEasySetOpt, CurlEasySetOptDetour);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    switch (ul_reason_for_call) {
        case DLL_PROCESS_ATTACH:
            Main();
            break;
        case DLL_THREAD_ATTACH:
        case DLL_THREAD_DETACH:
        case DLL_PROCESS_DETACH:
            break;
    }
    return TRUE;
}
