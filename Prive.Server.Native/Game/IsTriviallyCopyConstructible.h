#pragma once

#include "Inc.h"

template <typename T>
struct TIsTriviallyCopyConstructible {
    enum { Value = __has_trivial_copy(T) };
};