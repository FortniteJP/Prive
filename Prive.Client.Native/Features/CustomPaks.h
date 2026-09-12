#pragma once

// Mounts Prive's own (unsigned) .pak files at runtime so custom content - starting with ported
// emote animations - overrides the shipped assets. Fully deterministic (no races, no early injection):
// once the frontend is up, find the FPakPlatformFile singleton, clear its bSigned (so an unsigned pak
// mounts) and the precacher's sig-check enable (so reads don't null-deref in DoSignatureCheck), then
// call the game's own Mount. Data writes + one function call, no code patch. The emote assets load on
// demand in-match, after this has run.
namespace CustomPaks {
    // Call every watcher tick; does the one-time mount when the frontend is up. No-op afterwards.
    void Poll();
}
