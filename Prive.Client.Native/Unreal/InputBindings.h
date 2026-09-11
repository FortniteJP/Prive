#pragma once

#include "Types.h"

// Repoints one native action binding of the local PlayerController at our function - DATA only.
//
// A binding's handler is reached through a delegate on the HEAP (UInputComponent +0x110 ->
// FInputActionBinding -> delegate -> {vtable, weak object, method}), so swapping that one method
// pointer changes what the key does without touching the game's code. The replacement is called by
// the game, on the game thread, as (controller->*method)() - `this` is the controller - and
// normally calls the original handler itself.
namespace InputBindings {
    struct Redirect {
        const char* Name;
        uint32_t ComponentOffset; // the controller's UInputComponent* member holding the binding
        uint8_t KeyEvent;         // EInputEvent: 0 pressed, 1 released
        void* Original;           // the handler the game bound - identifies the binding
        void* Replacement;
    };

    enum class Outcome { NotFound, AlreadyRepointed, Repointed };

    // Watcher thread. Each match creates a new PlayerController with new bindings, so call it
    // periodically; it is safe while the controller is being torn down (map travel) and logs once
    // per controller when it repoints.
    Outcome Apply(const Redirect& redirect);
}
