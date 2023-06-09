// #include <Psapi.h>
#include "CommunicateServer.h"

#include "Game/UObjectGlobals.h"

CommunicateServer server;

void Main() {
    if (true) {
        AllocConsole();
        FILE* pFile;
        freopen_s(&pFile, "CONOUT$", "w", stdout);
        // printf("Prive.Server.Native injected\n");
    }
    WSADATA wsaData;
    if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0) {
        std::cout << "Failed to initialize Winsock" << std::endl;
        return;
    }
    if (!server.Start()) {
        std::cout << "Failed to start server" << std::endl;
        WSACleanup();
        return;
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD dwReason, LPVOID lpReserved) {
    switch (dwReason) {
        case DLL_PROCESS_ATTACH:
            Main();
            break;
        case DLL_PROCESS_DETACH:
            server.Stop();
            WSACleanup();
            break;
        case DLL_THREAD_ATTACH:
        case DLL_THREAD_DETACH:
            break;
    }
    return TRUE;
}