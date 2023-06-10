#pragma once

#include "Inc.h"

template <int32 Size, uint32 Alignment>
struct TAlignedBytes;

template <int32 Size>
struct TAlignedBytes<Size, 1> {
    uint8 Pad[Size];
};

#ifndef GCC_PACK
#define GCC_PACK(n)
#endif
#ifndef GCC_ALIGN
#define GCC_ALIGN(n)
#endif
#ifndef MS_ALIGN
#define MS_ALIGN(n)
#endif

#ifdef __cplusplus_cli
#define IMPLEMENT_ALIGNED_STORAGE(Align) \
    template <int32 Size> \
    struct TAlignedBytes<Size, Align> { \
        uint8 Pad[Size]; \
        static_assert(Size % Align == 0, "CLR interop types must not be aligned."); \
    };
#else
#define IMPLEMENT_ALIGNED_STORAGE(Align) \
    template <int32 Size> \
    struct TAlignedBytes<Size, Align> { \
        struct MS_ALIGN(Align) TPadding { \
            uint8 Pad[Size]; \
        } GCC_ALIGN(Align); \
        TPadding Padding; \
    };
#endif

IMPLEMENT_ALIGNED_STORAGE(16);
IMPLEMENT_ALIGNED_STORAGE(8);
IMPLEMENT_ALIGNED_STORAGE(4);
IMPLEMENT_ALIGNED_STORAGE(2);

#undef IMPLEMENT_ALIGNED_STORAGE

template <typename ElementType>
struct TTypeCompatibleBytes : public TAlignedBytes<sizeof(ElementType>, alignof(ElementType)> {};