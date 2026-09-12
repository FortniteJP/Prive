#include "CustomPaks.h"

#include "../Core/Log.h"
#include "../Core/Memory.h"
#include "../Core/Paths.h"
#include "../Unreal/Addresses.h"

#include <cwchar>
#include <set>
#include <string>
#include <windows.h>

namespace {
    using MountFn = bool(__fastcall*)(void* self, const wchar_t* filename, uint32_t order, const wchar_t* path);
    using GetNameFn = const wchar_t*(__fastcall*)(void* self);
    constexpr int GetNameSlot = 13; // FPakPlatformFile vtable: GetName() -> "PakFile"

    MountFn Mount = nullptr;
    void* Singleton = nullptr;
    bool Done = false;      // both phases complete
    bool Mounted = false;   // phase 1 (bSigned clear + mount) complete
    std::set<std::wstring> MountedPaks;

    // Higher ReadOrder wins in FindFileInPakFiles (shipped content paks are 3-4).
    constexpr uint32_t OverrideOrder = 100000;

    bool IsFPakPlatformFile(void* candidate, uintptr_t vtable) {
        if (!Memory::IsReadable(candidate) || *(uintptr_t*)candidate != vtable) return false;
        auto getName = (GetNameFn)(*(void***)candidate)[GetNameSlot];
        if (!Memory::IsInImage((uintptr_t)getName)) return false;
        const wchar_t* name = getName(candidate);
        return Memory::IsReadable(name, 16) && wcscmp(name, L"PakFile") == 0;
    }

    void* ScanRegion(uint8_t* base, size_t size, uintptr_t vtable) {
        __try {
            for (size_t off = 0; off + sizeof(uintptr_t) <= size; off += sizeof(uintptr_t))
                if (*(uintptr_t*)(base + off) == vtable && IsFPakPlatformFile(base + off, vtable))
                    return base + off;
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        return nullptr;
    }

    // Locate the FPakPlatformFile by its (fixed) vtable pointer in committed memory.
    void* FindSingleton() {
        uintptr_t vtable = Memory::Rva(Addresses::PakPlatformFileVTable);
        SYSTEM_INFO si{}; GetSystemInfo(&si);
        auto* addr = (uint8_t*)si.lpMinimumApplicationAddress;
        auto* end = (uint8_t*)si.lpMaximumApplicationAddress;
        MEMORY_BASIC_INFORMATION mbi{};
        while (addr < end && VirtualQuery(addr, &mbi, sizeof(mbi))) {
            DWORD prot = mbi.Protect & 0xFF;
            bool readable = mbi.State == MEM_COMMIT && !(mbi.Protect & PAGE_GUARD) &&
                (prot == PAGE_READWRITE || prot == PAGE_READONLY || prot == PAGE_WRITECOPY ||
                 prot == PAGE_EXECUTE_READ || prot == PAGE_EXECUTE_READWRITE);
            if (readable)
                if (void* found = ScanRegion((uint8_t*)mbi.BaseAddress, mbi.RegionSize, vtable)) return found;
            addr = (uint8_t*)mbi.BaseAddress + mbi.RegionSize;
        }
        return nullptr;
    }

    // Clear the precacher's sig-check enable (a pointer, non-null = on). Returns true once the precacher
    // exists and has been handled - it is created LATER than the FPakPlatformFile (not until
    // InitializeNewAsyncIO), so this is retried each tick until it appears, which is still well before
    // the in-match emote read. Keeping the precacher active (only skipping the sig check) means Epic's
    // streaming is unaffected.
    bool ClearPrecacherSigCheck() {
        void* precacher = *(void**)Memory::Rva(Addresses::PakPrecacherSingleton);
        if (!precacher) return false; // not created yet - retry next tick
        if (Memory::IsReadable((uint8_t*)precacher + Addresses::PakPrecacher_SigCheckEnable, sizeof(void*))) {
            auto& sig = *(void**)((uint8_t*)precacher + Addresses::PakPrecacher_SigCheckEnable);
            if (sig) sig = nullptr;
            Log::Info("CustomPaks: cleared precacher sig-check (%p)", precacher);
        }
        return true;
    }

    void MountAll() {
        std::wstring dir = Paths::LocalAppData() + L"\\FortniteGame\\Saved\\Paks\\";
        WIN32_FIND_DATAW fd{};
        HANDLE h = FindFirstFileW((dir + L"*.pak").c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) { Log::Info("CustomPaks: no *.pak in %ls", dir.c_str()); return; }
        do {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            std::wstring path = dir + fd.cFileName;
            if (!MountedPaks.insert(path).second) continue;
            bool ok = Mount(Singleton, path.c_str(), OverrideOrder, nullptr);
            Log::Info("CustomPaks: Mount(%ls) -> %s", fd.cFileName, ok ? "OK" : "FAILED");
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
}

namespace CustomPaks {
    // Two phases, both retried each watcher tick until done (no frontend/PlayerController needed):
    //  1. Once the FPakPlatformFile exists: clear bSigned + Mount our pak (it exists from PreInit).
    //  2. Once the FPakPrecacher exists (created later, at InitializeNewAsyncIO): clear its sig-check,
    //     so the in-match emote read doesn't null-deref in DoSignatureCheck.
    void Poll() {
        if (Done) return;
        if (!Mounted) {
            if (!Mount) {
                Mount = (MountFn)Memory::Resolve(Addresses::PakMount); // .text is decrypted by now
                if (!Mount) { Done = true; return; }                    // wrong build - stop
            }
            void* s = FindSingleton();
            if (!s) return; // FPakPlatformFile not created yet - retry
            Singleton = s;
            auto& bSigned = *(uint8_t*)((uint8_t*)Singleton + Addresses::PakPlatformFile_bSigned);
            if (bSigned) { bSigned = 0; Log::Info("CustomPaks: cleared FPakPlatformFile::bSigned"); }
            Log::Info("CustomPaks: FPakPlatformFile %p, Mount %p", Singleton, (void*)Mount);
            MountAll();
            Mounted = true;
        }
        if (ClearPrecacherSigCheck()) Done = true; // retries until the precacher exists
    }
}
