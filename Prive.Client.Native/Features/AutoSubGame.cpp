#include "AutoSubGame.h"

#include "../Core/Config.h"
#include "../Core/Log.h"
#include "../Core/Memory.h"
#include "../Unreal/Addresses.h"
#include "../Unreal/Engine.h"
#include "../Unreal/Types.h"

#include <windows.h>

namespace {
    using Engine::Member;

    using GetUIManagerFn = UObject*(__fastcall*)(UObject* worldContextObject);
    using SetSubGameFn = void(__fastcall*)(UObject* globalUIContext, uint8_t subGame);
    using SubGameSelectedFn = void(__fastcall*)(UObject* subGameSelectWidget);
    using ProcessEventFn = void(__fastcall*)(UObject* self, void* function, void* params);

    constexpr uint32_t RF_ClassDefaultObject = 0x10;

    // OFF by default: the pick works (live: UI state 3 -> 4) but it is a hooked ProcessEvent plus a
    // heavy engine call on a screen the player only sees once a session, and a run where it does not
    // fire leaves them on the select screen rather than breaking anything. `AutoBattleRoyale=1` in
    // ClientSettings.ini turns it on.
    constexpr const char* EnabledKey = "AutoBattleRoyale";

    constexpr uint8_t SubGameAthena = 1;        // ESubGame::Athena
    constexpr uint8_t UIStateSubgameSelect = 3; // EFortUIState::SubgameSelect

    // TMap<UClass*, USubsystem*>: { UClass* Key; USubsystem* Value; int32 HashNextId; int32 HashIndex }.
    constexpr uint32_t SubsystemElementSize = 0x18;
    constexpr uint32_t SubsystemValueOffset = 0x08;
    // A sane upper bound on a local player's subsystems (there are three on this build) - a wrong
    // offset reads a huge Num rather than a plausible one, so this is the guard against walking it.
    constexpr int32_t MaxSubsystems = 256;

    // How much of the object's vtable to copy. UFortUIManagerWidget_NUI's is 174 entries; .rdata
    // holds one vtable after another, so copying past the end just carries the neighbour's along and
    // nothing ever calls those slots. Generous on purpose: a copy SHORTER than the real vtable would
    // turn a high-slot call into a read off the end of our allocation.
    constexpr size_t VTableSlots = 1024;

    // SetSubGame no-ops when the subgame is already the current one, so re-arm a few times rather
    // than firing once into the void - but stop instead of fighting a player who went back on purpose,
    // and leave the transition several seconds to happen before deciding it did not take.
    constexpr int MaxAttempts = 8;
    constexpr uint64_t RearmDelayMs = 3000;

    GetUIManagerFn GetUIManagerWidget = nullptr;
    SetSubGameFn SetSubGame = nullptr;
    SubGameSelectedFn SubGameSelected = nullptr;

    bool Resolved = false;
    bool Done = false;
    int Attempts = 0;
    uint8_t LastState = 0xFF;

    // Written by the watcher, read by the game thread inside the detour.
    UObject* HookedWidget = nullptr;
    void** OriginalVTable = nullptr;
    ProcessEventFn OriginalProcessEvent = nullptr;
    UObject* PendingContext = nullptr;
    UObject* PendingWidget = nullptr;
    volatile LONG WorkPending = 0;
    volatile uint64_t FiredAt = 0;

    // The context is a ULocalPlayerSubsystem, so it is in the local player's subsystem map. Matching
    // the VTABLE rather than the map's UClass key keeps this to image-relative constants: no UClass
    // pointer to find, no GObjects walk, no FName.
    UObject* FindGlobalUIContext(UObject* localPlayer) {
        uintptr_t vtable = Memory::Rva(Addresses::FortGlobalUIContextVTable);
        auto& map = Member<FSparseSet>(localPlayer, Offsets::LocalPlayer_SubsystemMap);
        if (!map.Elements || map.NumElements <= 0 || map.NumElements > MaxSubsystems) return nullptr;

        for (int32_t i = 0; i < map.NumElements; i++) {
            if (!map.IsAllocated(i)) continue;
            auto* subsystem = *(UObject**)(map.Element(i, SubsystemElementSize) + SubsystemValueOffset);
            if (Memory::IsReadable(subsystem) && *(uintptr_t*)subsystem == vtable) return subsystem;
        }
        return nullptr;
    }

    // The live subgame-select screen, found the way Features/CustomPaks finds the pak platform file:
    // by its vtable, in committed memory. Its Blueprint class adds no virtuals, so the pointer is
    // exact; the class default object carries the same vtable and is skipped by its flags.
    UObject* ScanRegion(uint8_t* base, size_t size, uintptr_t vtable) {
        __try {
            for (size_t off = 0; off + sizeof(uintptr_t) <= size; off += sizeof(uintptr_t)) {
                auto* candidate = base + off;
                if (*(uintptr_t*)candidate != vtable) continue;
                if (*(uint32_t*)(candidate + Offsets::Object_Flags) & RF_ClassDefaultObject) continue;
                return (UObject*)candidate;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        return nullptr;
    }

    UObject* FindSubGameSelectWidget() {
        uintptr_t vtable = Memory::Rva(Addresses::FortSubGameSelectBaseVTable);
        SYSTEM_INFO si{}; GetSystemInfo(&si);
        auto* addr = (uint8_t*)si.lpMinimumApplicationAddress;
        auto* end = (uint8_t*)si.lpMaximumApplicationAddress;
        MEMORY_BASIC_INFORMATION mbi{};
        while (addr < end && VirtualQuery(addr, &mbi, sizeof(mbi))) {
            DWORD prot = mbi.Protect & 0xFF;
            // MEM_PRIVATE and writable only: a UObject lives in the engine's own heap, never in the
            // image, a mapped file or executable memory. Scanning everything committed instead took
            // eight seconds of the watcher's time (measured live), and most of it was those.
            bool readable = mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE &&
                !(mbi.Protect & PAGE_GUARD) && (prot == PAGE_READWRITE || prot == PAGE_WRITECOPY);
            if (readable)
                if (UObject* found = ScanRegion((uint8_t*)mbi.BaseAddress, mbi.RegionSize, vtable)) return found;
            addr = (uint8_t*)mbi.BaseAddress + mbi.RegionSize;
        }
        return nullptr;
    }

    // The scan runs on its own thread: it is only reads, and it must not stall the watcher (every
    // other feature polls from there).
    volatile LONG ScanState = 0; // 0 not started, 1 running, 2 finished

    DWORD WINAPI ScanThread(LPVOID) {
        uint64_t started = GetTickCount64();
        UObject* found = FindSubGameSelectWidget();
        PendingWidget = found;
        InterlockedExchange(&ScanState, 2);
        Log::Info("AutoSubGame: subgame-select screen %p (scan took %llu ms)", found, GetTickCount64() - started);
        return 0;
    }

    // Put the object back on the game's own vtable. The copy is deliberately never freed: another
    // thread can be dispatching through it, and one 8 KB page is cheaper than that race.
    void Unhook() {
        if (HookedWidget && OriginalVTable) *(void***)HookedWidget = OriginalVTable;
        HookedWidget = nullptr;
    }

    // Runs ON THE GAME THREAD, at a Blueprint-event boundary - which is exactly where the
    // subgame-select tile's own SafeSetSubGame runs, so SetSubGame sees the state it expects.
    void __fastcall ProcessEventDetour(UObject* self, void* function, void* params) {
        ProcessEventFn original = OriginalProcessEvent;
        bool ours = InterlockedCompareExchange(&WorkPending, 0, 1) == 1;
        UObject* context = PendingContext;

        // Unhook BEFORE doing anything else: whatever we call re-enters ProcessEvent constantly, and
        // this must not turn into recursion.
        if (ours) Unhook();

        if (original) original(self, function, params);

        // After the game's own call, and touching nothing of `self` afterwards.
        if (ours && context && SetSubGame) {
            FiredAt = GetTickCount64();
            Log::Info("AutoSubGame: selecting Battle Royale on the game thread (context %p)", context);
            SetSubGame(context, SubGameAthena);
            // The same order the tile's SafeSetSubGame uses: set the subgame, then tell the screen.
            if (PendingWidget && SubGameSelected) {
                Log::Info("AutoSubGame: SubGameSelected on the select screen (%p)", PendingWidget);
                SubGameSelected(PendingWidget);
            }
            Log::Info("AutoSubGame: done");
        }
    }

    // Swap in a copy of the object's vtable with ProcessEvent replaced. Data only - the game's own
    // vtable in .rdata is never written, so the protector's code/image checks see nothing.
    bool Hook(UObject* widget) {
        if (HookedWidget == widget) return true;

        auto** vtable = *(void***)widget;
        constexpr size_t slot = Addresses::UObject_ProcessEventSlot / sizeof(void*);
        if (!Memory::IsReadable(vtable, (slot + 1) * sizeof(void*))) return false;

        auto** copy = (void**)VirtualAlloc(nullptr, VTableSlots * sizeof(void*), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!copy) return false;

        size_t copied = 0;
        for (; copied < VTableSlots; copied++) {
            if (!Memory::IsReadable(&vtable[copied])) break;
            copy[copied] = vtable[copied];
        }
        if (copied <= slot) { VirtualFree(copy, 0, MEM_RELEASE); return false; }

        OriginalProcessEvent = (ProcessEventFn)vtable[slot];
        if (!Memory::IsInImage((uintptr_t)OriginalProcessEvent)) { VirtualFree(copy, 0, MEM_RELEASE); return false; }

        copy[slot] = (void*)&ProcessEventDetour;
        OriginalVTable = vtable;
        HookedWidget = widget;
        *(void***)widget = copy;
        return true;
    }
}

namespace AutoSubGame {
    void Poll() {
        if (Done) return;

        if (!Resolved) {
            Resolved = true;
            if (!Config::GetBool(EnabledKey, false)) {
                Log::Info("AutoSubGame: off (%s=0) - the subgame select screen is left alone", EnabledKey);
                Done = true;
                return;
            }
            GetUIManagerWidget = (GetUIManagerFn)Memory::Resolve(Addresses::GetUIManagerWidget);
            SetSubGame = (SetSubGameFn)Memory::Resolve(Addresses::SetSubGame);
            SubGameSelected = (SubGameSelectedFn)Memory::Resolve(Addresses::SubGameSelected);
        }
        if (!GetUIManagerWidget || !SetSubGame) { Done = true; return; } // wrong build - stop

        UObject* localPlayer = Engine::LocalPlayer();
        if (!localPlayer) return; // no frontend yet

        UObject* manager = GetUIManagerWidget(localPlayer);
        if (!manager || !Memory::IsReadable(manager, Offsets::UIManagerWidget_CurrentState + 1)) return;

        // The state is the one thing that says whether anything we did landed, so report every change.
        uint8_t state = Member<uint8_t>(manager, Offsets::UIManagerWidget_CurrentState);
        if (state != LastState) {
            Log::Info("AutoSubGame: UI state %u -> %u (1 Login, 2 JoinServer, 3 SubgameSelect, 4 FrontEnd)",
                      LastState, state);
            LastState = state;
        }

        if (state != UIStateSubgameSelect) {
            // Left the screen after we picked: done. Before that, keep waiting - login comes first.
            if (Attempts > 0) {
                Unhook();
                Log::Info("AutoSubGame: Battle Royale selected");
                Done = true;
            }
            return;
        }
        if (WorkPending) return; // armed; waiting for the game thread to come through ProcessEvent
        // Give a fired attempt time to land before deciding it did not take.
        if (FiredAt && GetTickCount64() - FiredAt < RearmDelayMs) return;

        // Find the select screen first - it is both what SubGameSelected needs and the better object
        // to hook: it animates and runs timers on its own, so its ProcessEvent comes round in
        // milliseconds, while the UI manager's waited a full minute for the player to touch something.
        // BEFORE the attempt counter, not after: counting the polls that merely WAIT for the scan
        // burned all eight attempts in the two seconds it takes, and the feature gave up having never
        // armed once. An attempt means a call that was actually made.
        if (ScanState == 0) {
            InterlockedExchange(&ScanState, 1);
            if (HANDLE thread = CreateThread(nullptr, 0, ScanThread, nullptr, 0, nullptr)) CloseHandle(thread);
            else InterlockedExchange(&ScanState, 2);
            return;
        }
        if (ScanState != 2) return; // still scanning

        UObject* context = FindGlobalUIContext(localPlayer);
        if (!context) { Log::Error("AutoSubGame: FortGlobalUIContext not found - skipped"); Done = true; return; }

        if (++Attempts > MaxAttempts) {
            Unhook();
            Log::Error("AutoSubGame: still on subgame select after %d attempts - giving up", MaxAttempts);
            Done = true;
            return;
        }

        PendingContext = context;
        UObject* target = PendingWidget ? PendingWidget : manager;
        if (!Hook(target)) {
            Log::Error("AutoSubGame: could not hook ProcessEvent on %p - skipped", target);
            Done = true;
            return;
        }
        InterlockedExchange(&WorkPending, 1);
        Log::Info("AutoSubGame: armed (attempt %d, hooked %p%s, context %p)",
                  Attempts, target, PendingWidget ? "" : " [UI manager - screen not found]", context);
    }
}
