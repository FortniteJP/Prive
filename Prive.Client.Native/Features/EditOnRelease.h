#pragma once

#include <functional>

// Confirm Edit on Release for keyboard and mouse: releasing EditSelect (the tile-select button)
// confirms the edit, as if CompleteBuildingEditInteraction had been pressed - what ErbiumClient does
// by hooking the EditSelect handlers. 10.40 has the setting itself (bEditConfirmOnReleaseEnabled,
// "Automatically confirms an edit action on input released") but only touch input honours it; the
// PC EditSelect-released handler just ends the selection.
//
// No code is patched: the handler is reached through a delegate that the PlayerController's
// EditModeInputComponent holds on the HEAP, and the watcher repoints that one delegate's method at
// ours - which calls the original handler and then, when enabled, the confirm handler. Both run on
// the game thread, from the game's own input processing.
namespace EditOnRelease {
    bool Install(std::function<bool()> enabled); // after Engine::Init

    // Called periodically from the watcher thread: repoints the current PlayerController's binding.
    void Poll();
}
