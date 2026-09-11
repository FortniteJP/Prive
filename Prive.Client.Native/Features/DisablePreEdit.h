#pragma once

#include <functional>

// Disable Pre-Edit: pressing Edit while aiming at your own build preview (the ghost piece in build
// mode) does nothing, instead of opening edit mode on the unplaced piece - what ErbiumClient does by
// hooking PerformBuildingEditInteraction.
//
// Same mechanism as EditOnRelease, no code patched: the PlayerController's own InputComponent
// (AActor +0xF8) binds PerformBuildingEditInteraction/pressed to a handler through a heap delegate,
// and the watcher repoints it at ours. The target is a preview when TargetedBuilding is one of the
// controller's two preview markers - the same comparison the game's confirm handler makes.
// Keyboard and mouse only: the gamepad's combined edit/vehicle action is bound separately.
namespace DisablePreEdit {
    bool Install(std::function<bool()> enabled); // after Engine::Init

    // Called periodically from the watcher thread: repoints the current PlayerController's binding.
    void Poll();
}
