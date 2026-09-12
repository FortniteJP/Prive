#pragma once

#include "../Core/Memory.h"

// Every code address Prive.Client.Native uses, for FortniteClient-Win64-Shipping.exe 10.40
// (CL 9380822) - the only build Prive runs.
//
// RVAs come from the decrypted memory dump (PriveDev\FortniteClient-Win64-Shipping.DMP) and the
// Dumper-7 SDK of the same build; Bytes are the dump's bytes at that RVA with rel32 displacements
// wildcarded. PriveDev\dumpwork\addresses_check.py checks them all against the dump. The .exe's
// .text is encrypted on disk, so none of this can be read from the file.
//
// These are only ever CALLED or READ - never patched. The protector crashes the game when its
// code is modified (see Core/GuardHook.h); the one redirect is a PAGE_GUARD, not a code write.
namespace Addresses {
    using Memory::Signature;

    // --- libcurl (statically linked) -----------------------------------------------------------

    // curl_easy_setopt sits on the one page missing from the dump (the old PAGE_GUARD hook made it
    // unreadable to the dumper), so it has no RVA and is scanned for instead.
    inline constexpr Signature CurlEasySetOpt{ "curl_easy_setopt", 0,
        "89 54 24 10 4C 89 44 24 18 4C 89 4C 24 20 48 83 EC 28 48 85 C9 75 08 8D 41 2B 48 83 C4 28 C3 4C" };
    // CURLcode Curl_vsetopt(Curl_easy*, CURLoption, va_list)
    inline constexpr Signature CurlVSetOpt{ "Curl_vsetopt", 0x40B4710,
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 30 33 ED 49 8B F0 48 8B D9" };

    // --- CoreUObject / Engine ------------------------------------------------------------------

    // UObject* StaticConstructObject_Internal(UClass*, UObject* Outer, FName, EObjectFlags,
    //     EInternalObjectFlags, UObject* Template, bool bCopyTransientsFromClassDefaults,
    //     FObjectInstancingGraph*, bool bAssumeTemplateIsArchetype)
    inline constexpr Signature StaticConstructObject{ "StaticConstructObject_Internal", 0x22FA300,
        "48 89 5C 24 18 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 40 FF FF FF 48 81 EC C0 01 00 00" };
    // The `mov [rip+GEngine], rax` that publishes the engine object: displacement at +16, the
    // instruction ends at +20. GEngine itself is rva 0x65A40A0.
    inline constexpr Signature GEngineStore{ "GEngine store", 0x3831A3,
        "48 8B D3 E8 ?? ?? ?? ?? 48 8B 4C 24 40 48 89 05 ?? ?? ?? ?? 48 85 C9" };
    // bool FWeakObjectPtr::IsValid(): `cmp eax, [rip+NumElements]` at +15 (displacement +17, ends
    // +21) reads GUObjectArray.ObjObjects.NumElements, which sits 0x14 into ObjObjects - so this
    // locates GObjects (rva 0x64A0090, = Dumper-7's Offsets::GObjects). Chunks of 65536 items.
    inline constexpr Signature WeakObjectIsValid{ "FWeakObjectPtr::IsValid", 0x2309910,
        "44 8B 41 04 45 85 C0 74 ?? 8B 01 85 C0 78 ?? 3B 05 ?? ?? ?? ?? 7D ?? 99 0F B7 D2 03 C2" };

    // FText UKismetTextLibrary::Conv_StringToText(const FString&) - copies its input
    // (FText::AsCultureInvariant(const FString&)). Native form: FText* (FText* Out, const FString*).
    inline constexpr Signature ConvStringToText{ "Conv_StringToText", 0x31243E0,
        "40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8B C3 48 83 C4 20 5B C3" };

    // --- Pak signing bypass (custom content) ---------------------------------------------------
    // Deterministic RUNTIME approach (Features/CustomPaks): at the frontend, find the FPakPlatformFile
    // singleton, clear its bSigned, clear the precacher's sig-check, then mount our pak ourselves.
    // (The earlier delegate-unbind race - PakSigningKeysDelegateInstance 0x657CBA0 - was flaky: it won
    // the bSigned read some runs and lost others, leaving "pak is invalid". Dropped.)

    // bool FPakPlatformFile::Mount(const TCHAR* filename, uint32 order, const TCHAR* path). rcx/rdx/r8d/r9.
    inline constexpr Signature PakMount{ "FPakPlatformFile::Mount", 0x2DD06A0,
        "48 8B C4 44 89 40 18 48 89 48 08 55 53 41 57 48 8D 68 A9 48 81 EC E0 00 00 00 48 89 70 10 45 33" };
    // FPakPlatformFile vtable base (RTTI stripped; GetName slot 13 -> "PakFile"). The singleton is found
    // at runtime by scanning committed memory for this vtable pointer (one heap object carries it).
    inline constexpr uint32_t PakPlatformFileVTable = 0x51AF688; // static 0x1451AF688 - 0x140000000
    // FPakPlatformFile::bSigned - clear it, then Mount() constructs an unsigned FPakFile that mounts.
    inline constexpr uint32_t PakPlatformFile_bSigned = 0x30;

    // The FPakPrecacher singleton pointer (a .data global). Read *(void**)Rva(...) to get the instance.
    inline constexpr uint32_t PakPrecacherSingleton = 0x657CB58; // static 0x14657CB58 - 0x140000000
    // Within the precacher: the signature-check ENABLE. On this build it is a POINTER, non-null = checks
    // on; the read-completion gate is `if (*(precacher+0x290) != 0 && perRequestDoCheck) StartSignatureCheck`
    // (found live: cmp qword [precacher+0x290],0 at 0x142DC1983). Zeroing it makes every read skip the
    // precacher's DoSignatureCheck (which null-derefs on our unsigned pak) while the precacher stays
    // active - so Epic's streaming is unaffected, unlike disabling GPakCache_Enable.
    inline constexpr uint32_t PakPrecacher_SigCheckEnable = 0x290;
    // GPakCache_Enable (int32, default 1). FPakPlatformFile::OpenAsyncRead checks it PER CALL: when 0,
    // the async read goes direct (lower level) instead of through the precacher (IPlatformFilePak.cpp:3788).
    // The precacher's DoSignatureCheck null-derefs on an unsigned pak, and unbinding can't win the race
    // for the precacher's own init read - but keeping this 0 from startup routes every read past the
    // precacher deterministically; the direct read then skips signatures because our pak mounted bSigned=0.
    inline constexpr uint32_t GPakCache_Enable = 0x6076980; // static 0x146076980 - 0x140000000

    // --- Frontend: skip the subgame select (Features/AutoSubGame) -------------------------------
    // 10.40 has no option for this: the screen is shown unconditionally (FortSubGameSelectBase::
    // IsSubGameOptionVisible returns true for Campaign and Athena whatever the account can access),
    // and both the UI state machine and HasAccesstoMultipleSubGames are native with no Blueprint
    // caller. bSkipSubgameSelect/bForceBRMode are LATER versions' FortRuntimeOptions keys and do not
    // exist on this build. So Prive picks Battle Royale the way the tile does.

    // void UFortGlobalUIContext::SetSubGame(ESubGame). This IS the selection: SubgameSelect_v2's
    // `SafeSetSubGame` is GetContext(FortGlobalUIContext)->CanPlay(sg) then ->SetSubGame(sg), and
    // everything after it in that graph is the spinner, an analytics event and a delegate broadcast.
    // No-ops when the subgame is already the current one (it early-outs on GetSubGame() == argument).
    inline constexpr Signature SetSubGame{ "UFortGlobalUIContext::SetSubGame", 0x39D3310,
        "88 54 24 10 48 89 4C 24 08 55 56 41 55 41 57 48 8D AC 24 78 FE FF FF 48 81 EC 88 02 00 00" };
    // UFortUIManagerWidget_NUI* UFortUIManagerWidget_NUI::GetUIManagerWidget(UObject* WorldContext) -
    // a static UFUNCTION; it calls GetWorld() (UObject vtable +0x140) on whatever it is handed, so a
    // ULocalPlayer works. Read its CurrentState to know the frontend is actually on the select screen.
    inline constexpr Signature GetUIManagerWidget{ "UFortUIManagerWidget_NUI::GetUIManagerWidget", 0x3D39DC0,
        "48 83 EC 28 48 85 C9 74 15 48 8B 01 FF 90 40 01 00 00 48 8B C8 48 83 C4 28 E9 ?? ?? ?? ?? 33 C0" };
    // void UFortSubGameSelectBase::SubGameSelected() - the OTHER half of what the tile does:
    // GetTypedOuter(...) then Broadcast() on a native (non-UPROPERTY) delegate at +0x788. Live, calling
    // SetSubGame alone DID set the subgame - 2.4s of real work, and every later call early-outed - and
    // still left the screen exactly where it was, so this broadcast is what moves the UI on.
    inline constexpr Signature SubGameSelected{ "UFortSubGameSelectBase::SubGameSelected", 0x3D5C9A0,
        "40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8B D0 48 8B CB E8 ?? ?? ?? ?? 48 8D 88 88 07 00 00" };
    // The subgame-select screen's vtable. Its Blueprint subclass (SubgameSelect_v2_C) adds no virtuals
    // of its own, so the live widget carries this exact pointer and can be found by scanning for it;
    // the class default object carries it too and is told apart by RF_ClassDefaultObject.
    inline constexpr uint32_t FortSubGameSelectBaseVTable = 0x5584180; // static 0x145584180 - 0x140000000

    // UFortGlobalUIContext's vtable. The context is a ULocalPlayerSubsystem, so it is found by
    // matching this in the local player's subsystem map (Offsets::LocalPlayer_SubsystemMap) - no
    // UClass pointer and no GObjects walk needed.
    inline constexpr uint32_t FortGlobalUIContextVTable = 0x5626F80; // static 0x145626F80 - 0x140000000

    // UObject::ProcessEvent's vtable slot. SetSubGame must run on the GAME thread - called from the
    // watcher it spent 0.68s inside itself and died on a null UFunction::Func, and the crash frame
    // (0x1422F2C75) sits inside the function at 0x1422F2990, which is exactly what this slot holds
    // in every CDO vtable. Hooking it on ONE object (a copy of that object's vtable, so no image
    // memory is touched) gives a callback at a Blueprint-event boundary - the same place the real
    // SafeSetSubGame runs from.
    inline constexpr uint32_t UObject_ProcessEventSlot = 0x200;

    // --- FortniteUI options menu ---------------------------------------------------------------

    // UFortUIDataConfiguration* Get(): its `lea rcx, [rip+X]` at +14 (displacement +17, ends +21)
    // is the static TWeakObjectPtr<UFortUIDataConfiguration> it caches (rva 0x65DA3A0). 286 call
    // sites across the UI, so it is set by the time the frontend shows. Read, never called.
    inline constexpr Signature GetUIDataConfiguration{ "UFortUIDataConfiguration::Get", 0x3AC8C50,
        "40 55 48 8D 6C 24 A9 48 81 EC C0 00 00 00 48 8D 0D ?? ?? ?? ?? E8" };

    // --- Building edit mode --------------------------------------------------------------------
    // AFortPlayerController's EditModeInputComponent handlers, all `void (AFortPlayerController*)`,
    // bound by the function that creates "EditModeInputComponent0" (its BindAction calls at
    // 0x1419CF6B5..0x1419CF853): EditSelect pressed 0x19B1360 / released 0x19B1320,
    // BuildingEditReset pressed 0x19B1340, CompleteBuildingEditInteraction pressed (and
    // GamepadEditBuilding released) 0x19B50A0.

    // EditSelect released: EditBuildingActor(+0x1358)->EditModeSupport(+0x960)->vtable +0x270,
    // which only ends the tile selection (clears bEditActionInProgress). Never confirms anything -
    // on PC nothing reads bEditConfirmOnReleaseEnabled.
    inline constexpr Signature EditSelectReleased{ "AFortPlayerController EditSelect released", 0x19B1320,
        "48 8B 89 58 13 00 00 48 85 C9 74 0A 48 8B 01 48 FF A0 A0 0B 00 00 C3" };
    // PerformBuildingEditInteraction pressed (the Edit key), bound on the controller's own
    // InputComponent (AActor +0xF8) by the same setup function (0x1419CC4C1): a thunk to the virtual
    // at vtable +0x18B0. Neighbours there: ToggleBuildingEditInteractionWithReset 0x1419D60B0,
    // GamepadPerformBuildingEditInteractionOrUsePersonalVehicle 0x14198B758 (vtable +0x18B8).
    inline constexpr Signature PerformBuildingEditInteraction{ "AFortPlayerController PerformBuildingEditInteraction", 0x122C9E0,
        "48 8B 01 FF A0 B0 18 00 00" };
    // CompleteBuildingEditInteraction pressed: confirms the edit (the same input check as the other
    // handlers first, via vtable +0x15B8).
    inline constexpr Signature CompleteBuildingEditInteraction{ "AFortPlayerController CompleteBuildingEditInteraction", 0x19B50A0,
        "48 89 5C 24 10 56 48 83 EC 30 48 8B 19 48 8B F1 48 81 C3 B8 15 00 00 E8 ?? ?? ?? ?? 48 8B CE 48 8D 90 F0 05 00 00 FF 13" };
}
