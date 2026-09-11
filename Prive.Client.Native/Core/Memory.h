#pragma once

#include <cstdint>
#include <string_view>

// Patterns are IDA-style: hex bytes separated by spaces, "??" for a wildcard byte.
namespace Memory {
    uintptr_t ImageBase();
    size_t ImageSize();

    inline uintptr_t Rva(uint32_t rva) { return ImageBase() + rva; }

    // Whether the bytes at `address` match `pattern`.
    bool Matches(uintptr_t address, std::string_view pattern);

    // First match of `pattern` anywhere in the game image, or 0.
    uintptr_t FindPattern(std::string_view pattern);

    // Target of a rip-relative operand: `displacementOffset` bytes into the instruction at
    // `instruction`, whose total length is `instructionLength`.
    uintptr_t ResolveRelative(uintptr_t instruction, int displacementOffset, int instructionLength);

    // A readable, non-guard page holds `address`.
    bool IsReadable(const void* address, size_t size = sizeof(void*));

    // `address` points into the game image (a vtable, a function).
    inline bool IsInImage(uintptr_t address) { return address >= ImageBase() && address < ImageBase() + ImageSize(); }

    // A code location in the one build Prive targets. With an Rva, the bytes there are checked
    // against Bytes (a mismatch means a different executable, and resolves to 0 rather than to a
    // wrong address); with Rva 0, Bytes is scanned for instead.
    struct Signature {
        const char* Name;
        uint32_t Rva;
        const char* Bytes;
    };

    // The signature's address, or 0 (logged) when it does not check out.
    uintptr_t Resolve(const Signature& signature);
}
