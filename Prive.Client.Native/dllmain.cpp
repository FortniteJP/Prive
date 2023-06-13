#include <iostream>
#include "Main.h"
#include "CommunicateServer.h"
#include <thread>

CommunicateServer server;
bool RunServer = false;

void Main() {
    if (true) {
        AllocConsole();
        FILE* pFile;
        freopen_s(&pFile, "CONOUT$", "w", stdout);
        // printf("Prive.Client.Native injected\n");
    }
    WSADATA wsaData;
    if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0) {
        std::cout << "Failed to initialize Winsock" << std::endl;
        return;
    }
    if (RunServer && !server.Start()) {
        std::cout << "Failed to start server" << std::endl;
        WSACleanup();
        return;
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

BOOL APIENTRY DllMain(HMODULE hModule, DWORD dwReason, LPVOID lpReserved) {
    switch (dwReason) {
        case DLL_PROCESS_ATTACH:
            Main();
            break;
        case DLL_PROCESS_DETACH:
            if (RunServer) server.Stop();
            WSACleanup();
        case DLL_THREAD_ATTACH:
        case DLL_THREAD_DETACH:
            break;
    }
    return TRUE;
}
