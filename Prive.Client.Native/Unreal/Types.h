#pragma once

#include <cstddef>
#include <cstdint>

// The few engine types and member offsets Prive.Client.Native touches. Offsets are from the
// Dumper-7 SDK of 10.40 (C:\Dumper-7\4.23.0-9380822+++Fortnite+Release-10.40-FortniteGame) and
// the containers follow the UE 4.23 source; nothing here is a full declaration.

struct UObject;
struct UClass;

template <typename T>
struct TArray {
    T* Data;
    int32_t Num;
    int32_t Max;
};

// Only ever built by us to pass a string INTO the game, which copies it - never handed over.
struct FString {
    const wchar_t* Data;
    int32_t Num; // including the terminator
    int32_t Max;
};

// TSharedRef<ITextData, ESPMode::ThreadSafe> TextData; uint32 Flags;
struct FText {
    void* TextData;
    void* ReferenceController;
    uint32_t Flags;
    uint32_t Padding;
};
static_assert(sizeof(FText) == 0x18);

namespace Offsets {
    // UObjectBase
    constexpr uint32_t Object_Outer = 0x0020; // OuterPrivate
    // UEngine
    constexpr uint32_t Engine_ConsoleClass = 0x00F8; // TSubclassOf<UConsole>
    constexpr uint32_t Engine_GameViewport = 0x0750; // UGameViewportClient*
    // UGameViewportClient
    constexpr uint32_t GameViewportClient_ViewportConsole = 0x0040; // UConsole*
    constexpr uint32_t GameViewportClient_GameInstance = 0x0080;    // UGameInstance*
    // UGameInstance
    constexpr uint32_t GameInstance_LocalPlayers = 0x0038;          // TArray<ULocalPlayer*>
    // UPlayer
    constexpr uint32_t Player_PlayerController = 0x0030;            // APlayerController*
    // UFortLocalPlayer
    constexpr uint32_t LocalPlayer_ClientSettingsRecord = 0x0348;   // UFortClientSettingsRecord*
    // AActor
    constexpr uint32_t Actor_InputComponent = 0x00F8;                    // UInputComponent*
    // AFortPlayerController
    constexpr uint32_t PlayerController_BuildPreviewMarker = 0x10A8;           // ABuildingPlayerPrimitivePreview*
    constexpr uint32_t PlayerController_BuildPreviewMarkerExtraPiece = 0x10B0; // ABuildingPlayerPrimitivePreview*
    constexpr uint32_t PlayerController_TargetedBuilding = 0x1198;             // ABuildingActor*
    constexpr uint32_t PlayerController_EditModeInputComponent = 0x1350; // UInputComponent*
    // UInputComponent: the private TArray<TSharedPtr<FInputActionBinding>> ActionBindings, right
    // after the six binding arrays and right before the CachedKeyToActionInfo UPROPERTY (0x120).
    constexpr uint32_t InputComponent_ActionBindings = 0x0110;

    // UFortUIDataConfiguration
    constexpr uint32_t UIDataConfiguration_GameOptionsMenuData = 0x3238; // UFortOptionsMenuData*
    // UFortOptionsMenuData
    constexpr uint32_t OptionsMenuData_TabDatas = 0x0030; // TMap<ESettingTab, FOptionsTabData>
    // UFortOptionsTab
    constexpr uint32_t OptionsTab_TabType = 0x0268; // ESettingTab
}

// One row of the options menu, as authored in /Game/UI/Frontend/Settings/OptionsMenuData.
// Relocatable: moving the bytes moves the row (FText and TArray hold no pointers into themselves).
struct FSettingData {
    uint8_t Bytes[0x198];

    uint8_t SettingType() const { return Bytes[0x000]; }                     // ESettingType
    FText& DisplayText() { return *(FText*)(Bytes + 0x010); }
    FText& HoverText() { return *(FText*)(Bytes + 0x028); }
    bool& DisplayOnPC() { return *(bool*)(Bytes + 0x040); }                  // PlatformPCOverrides.DisplayOnPlatform
    TArray<uint8_t>& HiddenModes() { return *(TArray<uint8_t>*)(Bytes + 0x158); } // TArray<ESubGame>
};
static_assert(sizeof(FSettingData) == 0x198);

// TMap<ESettingTab, FOptionsTabData> as laid out in memory: a TSet of 0x20-byte elements
// { uint8 Key; FOptionsTabData Value @+8 (a TArray<FSettingData>); int32 HashNextId; int32 HashIndex },
// stored in a TSparseArray whose allocation bits say which slots are live.
struct FSettingTabMap {
    uint8_t* Elements;              // +0x00
    int32_t NumElements;            // +0x08 (slots, including free ones)
    int32_t MaxElements;            // +0x0C
    uint32_t AllocationInline[4];   // +0x10
    uint32_t* AllocationSecondary;  // +0x20
    int32_t NumBits;                // +0x28
    int32_t MaxBits;                // +0x2C
    int32_t FirstFreeIndex;         // +0x30
    int32_t NumFreeIndices;         // +0x34
    // hash (+0x38 .. +0x50) not needed

    static constexpr uint32_t ElementSize = 0x20;
    static constexpr uint32_t ElementValue = 0x08;

    bool IsAllocated(int32_t index) const {
        const uint32_t* bits = AllocationSecondary ? AllocationSecondary : AllocationInline;
        return (bits[index / 32] >> (index % 32)) & 1;
    }

    TArray<FSettingData>* FindSettingDatas(uint8_t tab) const {
        for (int32_t i = 0; i < NumElements; i++) {
            if (!IsAllocated(i)) continue;
            uint8_t* element = Elements + i * ElementSize;
            if (element[0] == tab) return (TArray<FSettingData>*)(element + ElementValue);
        }
        return nullptr;
    }
};

// One action binding of a UInputComponent (4.23 FInputActionBinding). Checked against a live
// Athena_PlayerController in the memory dump: its EditModeInputComponent holds 20 of these, and
// entry 1 is EditSelect / released bound to 0x1419B1320 (PriveDev\dumpwork\editbind_check.py).
struct FInputActionBinding {
    uint8_t Flags;              // FInputBinding: bConsumeInput, bExecuteWhenPaused
    uint8_t Paired;             // bPaired
    uint8_t KeyEvent;           // EInputEvent: 0 pressed, 1 released
    uint8_t Padding;
    int32_t ActionName[2];      // FName
    // FInputActionUnifiedDelegate ActionDelegate:
    struct FNativeDelegate* FuncDelegate; // TSharedPtr<FInputActionHandlerSignature>
    void* FuncDelegateController;
    void* WithKeyDelegate[2];
    void* DynamicDelegate[2];
    uint8_t BoundDelegateType;  // 1 = FuncDelegate
};
static_assert(offsetof(FInputActionBinding, FuncDelegate) == 0x10 && offsetof(FInputActionBinding, BoundDelegateType) == 0x40);

// TSharedPtr<FInputActionBinding>
struct FSharedActionBinding {
    FInputActionBinding* Object;
    void* Controller;
};

// FInputActionHandlerSignature (a TBaseDelegate) on 10.40: heap allocator only - the instance lives
// behind a pointer, its size in 16-byte units after it (3 for a UObject method delegate).
struct FNativeDelegate {
    struct FMethodDelegateInstance* Instance;
    int32_t Size;
};

// TBaseUObjectMethodDelegateInstance<false, UserClass, void()>: calls (UserObject->*Method)().
// Method is a 16-byte MSVC member-function pointer {function, this adjustment}.
struct FMethodDelegateInstance {
    void* VTable;
    int32_t UserObject[2];      // TWeakObjectPtr
    void* Method;
    int32_t ThisAdjustment;
    int32_t Padding;
    uint64_t Handle;
};
static_assert(offsetof(FMethodDelegateInstance, Method) == 0x10 && sizeof(FMethodDelegateInstance) == 0x28);

// ESettingTab
enum class ESettingTab : uint8_t {
    Video = 1,
    Game = 2,
};
