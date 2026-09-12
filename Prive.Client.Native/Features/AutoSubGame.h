#pragma once

// Picks Battle Royale on the subgame-select screen, so Prive's players go straight to the lobby.
//
// 10.40 offers no way to ask for this: the screen is part of the native login flow and is shown
// whatever the account can access (FortSubGameSelectBase::IsSubGameOptionVisible returns true for
// Campaign and Athena unconditionally), the UI state machine has no Blueprint caller to override,
// and `bSkipSubgameSelect` / `bForceBRMode` are later versions' runtime options that this build's
// UFortRuntimeOptions does not have. So this does what the tile does: once the UI manager reports
// the SubgameSelect state, call UFortGlobalUIContext::SetSubGame(Athena) - the same single call the
// widget's own SafeSetSubGame makes, plus the screen's own SubGameSelected() broadcast, which is
// what actually moves the UI on. Data reads, a per-object vtable copy and two calls; no code patched.
//
// OFF unless `AutoBattleRoyale=1` is in ClientSettings.ini. It has worked live (UI state
// SubgameSelect -> FrontEnd) but it is not yet dependable enough to be on for everyone: it has to
// find the live screen by scanning for its vtable and then wait for the game thread to come through
// the hooked ProcessEvent, and when either misses, the player is simply left on the select screen.
namespace AutoSubGame {
    // Call every watcher tick. Does nothing until the subgame-select screen is up, then acts once.
    void Poll();
}
