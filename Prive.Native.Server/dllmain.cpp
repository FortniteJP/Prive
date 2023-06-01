#include <Psapi.h>

void Main() {
    if (true) {
        AllocConsole();
        FILE* pFile;
        freopen_s(&pFile, "CONOUT$", "w", stdout);
        // printf("Prive.Native.Server injected\n");
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD dwReason, LPVOID lpReserved) {
    switch (dwReason) {
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