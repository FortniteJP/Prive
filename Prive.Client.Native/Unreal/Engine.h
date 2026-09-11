#pragma once

#include "Types.h"

// Read-only access to the running engine from our own thread: GEngine, GObjects and the first local
// player. Nothing here calls into the game or writes to it.
namespace Engine {
    bool Init(); // resolves GEngine and GObjects from their code references

    UObject* GEngine();
    UObject* GameViewport();
    UObject* LocalPlayer();      // the first local player, or null
    UObject* PlayerController(); // non-null once the frontend world is running

    // The object a TWeakObjectPtr/FWeakObjectPtr ({int32 ObjectIndex; int32 SerialNumber}) names,
    // or null when it is unset or stale.
    UObject* ResolveWeak(const void* weakObjectPtr);

    template <typename T>
    T& Member(void* object, uint32_t offset) { return *(T*)((uint8_t*)object + offset); }
}
