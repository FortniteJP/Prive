#pragma once

// Redirects calls to one function WITHOUT writing a byte of the game's code.
//
// 10.40's protector checks the game's code: an inline hook (MinHook) made it jump to a garbage
// address from its OnPostEngineInit callback, every time (2026-09-11, two live rounds - see
// CrashLog). So, like the original client DLL: the target's page is marked PAGE_GUARD, and a
// vectored handler moves Rip to the detour when the guard fires at the target's first byte. Any
// other touch of the page (other code on it, the protector reading it) just re-arms the guard one
// instruction later via the trap flag.
//
// Every entry into that page costs an exception, so this is only for rarely-run code (libcurl's
// setopt). One hook per process.
namespace GuardHook {
    bool Install(const char* name, void* target, void* detour);
}
