#pragma once

#include <string>

// The game's per-user cloud storage (in practice ClientSettings.Sav - the Settings > Game values)
// served from local disk, so the settings need neither the backend nor the launcher.
//
// A small HTTP server on 127.0.0.1:<free port>, on its own threads inside the game process.
// CurlRedirect sends every /fortnite/api/cloudstorage/user/... request here; everything else still
// goes to the backend. It answers like Prive.Server.Http's cloudstorage user routes (JSON file list,
// file, PUT to store) and keeps the files in %LOCALAPPDATA%\Prive.Launcher\CloudStorage\<account>\.
// An account's folder is seeded on first use from the game's own local copy
// (FortniteGame\Saved\Cloud\<account>, written next to every upload), so the settings the backend
// had carry over.
//
// `LocalCloudStorage=0` in ClientSettings.ini leaves user cloud storage on the backend.
namespace LocalCloudStorage {
    bool Start();

    // The port it listens on, or empty when it is not running.
    const std::string& Port();
}
