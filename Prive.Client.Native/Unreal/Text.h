#pragma once

#include "Types.h"

namespace Text {
    bool Init();

    // A culture-invariant FText built by the game itself (so its allocator owns the memory).
    // The caller owns the one reference: store it somewhere the game keeps alive, never destroy it.
    bool Make(const wchar_t* string, FText& out);
}
