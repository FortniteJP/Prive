#include "Engine.h"
#include "Addresses.h"
#include "../Core/Log.h"

namespace {
    UObject** GEngineAddress = nullptr;

    // FChunkedFixedUObjectArray (4.23): the head of GUObjectArray.ObjObjects
    struct FUObjectItem {
        UObject* Object;
        int32_t Flags;
        int32_t ClusterRootIndex;
        int32_t SerialNumber;
        int32_t Padding;
    };
    static_assert(sizeof(FUObjectItem) == 0x18);

    struct FChunkedObjectArray {
        FUObjectItem** Objects;
        FUObjectItem* PreAllocatedObjects;
        int32_t MaxElements;
        int32_t NumElements;
        int32_t MaxChunks;
        int32_t NumChunks;
    };
    constexpr int32_t ElementsPerChunk = 65536;
    const FChunkedObjectArray* GObjects = nullptr;
}

namespace Engine {
    bool Init() {
        auto store = Memory::Resolve(Addresses::GEngineStore);
        auto isValid = Memory::Resolve(Addresses::WeakObjectIsValid);
        if (!store || !isValid) return false;
        GEngineAddress = (UObject**)Memory::ResolveRelative(store, 16, 20);
        GObjects = (const FChunkedObjectArray*)(Memory::ResolveRelative(isValid, 17, 21) - 0x14);
        return true;
    }

    UObject* GEngine() { return GEngineAddress ? *GEngineAddress : nullptr; }

    UObject* GameViewport() {
        UObject* engine = GEngine();
        return engine ? Member<UObject*>(engine, Offsets::Engine_GameViewport) : nullptr;
    }

    UObject* LocalPlayer() {
        UObject* viewport = GameViewport();
        UObject* gameInstance = viewport ? Member<UObject*>(viewport, Offsets::GameViewportClient_GameInstance) : nullptr;
        if (!gameInstance) return nullptr;
        auto& localPlayers = Member<TArray<UObject*>>(gameInstance, Offsets::GameInstance_LocalPlayers);
        return localPlayers.Num >= 1 && localPlayers.Data ? localPlayers.Data[0] : nullptr;
    }

    UObject* PlayerController() {
        UObject* localPlayer = LocalPlayer();
        return localPlayer ? Member<UObject*>(localPlayer, Offsets::Player_PlayerController) : nullptr;
    }

    UObject* ResolveWeak(const void* weakObjectPtr) {
        if (!GObjects || !weakObjectPtr) return nullptr;
        auto index = ((const int32_t*)weakObjectPtr)[0];
        auto serial = ((const int32_t*)weakObjectPtr)[1];
        if (serial == 0 || index < 0 || index >= GObjects->NumElements || !GObjects->Objects) return nullptr;
        if (index / ElementsPerChunk >= GObjects->NumChunks) return nullptr;
        FUObjectItem* chunk = GObjects->Objects[index / ElementsPerChunk];
        if (!chunk) return nullptr;
        const FUObjectItem& item = chunk[index % ElementsPerChunk];
        return item.SerialNumber == serial ? item.Object : nullptr;
    }
}
