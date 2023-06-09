#pragma once

#include <Windows.h>
#include <iostream>
#include <format>
#include <string>

typedef unsigned short uint16;
typedef unsigned char uint8;
typedef char int8;
typedef short int16;
typedef int int32;
typedef __int64 int64;
typedef unsigned int uint32;
typedef char ANSICHAR;
typedef uint32_t CodeSkipSizeType;
typedef unsigned __int64 uint64;

extern inline int EngineVersion = 0;
extern inline double FortniteVersion = 0;
extern inline int FortniteCL = 0;

struct PlaceholderBitfield {
    uint8_t First : 1;
    uint8_t Second : 1;
    uint8_t Third : 1;
    uint8_t Fourth : 1;
    uint8_t Fifth : 1;
    uint8_t Sixth : 1;
    uint8_t Seventh : 1;
    uint8_t Eighth : 1;
};

#define MS_ALIGN(n) __declspec(align(n))
#define FORCENOINLINE __declspec(noinline)

#define ENUM_CLASS_FLAGS(Enum) \
    inline           Enum& operator|=(Enum& lhs, Enum rhs) { return lhs = (Enum)((__underlying_type(Enum))lhs | (__underlying_type(Enum))rhs); } \
    inline           Enum& operator&=(Enum& lhs, Enum rhs) { return lhs = (Enum)((__underlying_type(Enum))lhs & (__underlying_type(Enum))rhs); } \
    inline           Enum& operator^=(Enum& lhs, Enum rhs) { return lhs = (Enum)((__underlying_type(Enum))lhs ^ (__underlying_type(Enum))rhs); } \
    inline constexpr Enum  operator| (Enum  lhs, Enum rhs) { return (Enum)((__underlying_type(Enum))lhs | (__underlying_type(Enum))rhs); } \
    inline constexpr Enum  operator& (Enum  lhs, Enum rhs) { return (Enum)((__underlying_type(Enum))lhs & (__underlying_type(Enum))rhs); } \
    inline constexpr Enum  operator^ (Enum  lhs, Enum rhs) { return (Enum)((__underlying_type(Enum))lhs ^ (__underlying_type(Enum))rhs); } \
    inline constexpr bool  operator! (Enum  rhs)           { return !(__underlying_type(Enum))rhs; } \
    inline constexpr Enum  operator~ (Enum  rhs)           { return (Enum)~(__underlying_type(Enum))rhs; }

#define UNLIKELY(x) (x)

inline bool AreVehicleWeaponsEnabled() { return FortniteVersion > 6; }

inline bool IsRestartingSupported() { return EngineVersion >= 429 && EngineVersion < 424; }