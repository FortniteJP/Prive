#pragma once

// Rewrites every *.epicgames.com URL the game hands to libcurl so it reaches the Prive backend
// (HostConfig), and turns off peer verification. Must be installed before the game's first HTTP
// request, which is why it is the first thing Init does.
namespace CurlRedirect {
    bool Install();
}
