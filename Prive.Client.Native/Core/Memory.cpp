#include "Memory.h"
#include "Log.h"

#include <windows.h>
#include <psapi.h>
#include <optional>
#include <string>
#include <vector>

namespace {
    struct Image {
        uintptr_t Base = 0;
        size_t Size = 0;
    };

    const Image& GameImage() {
        static const Image image = [] {
            Image result;
            MODULEINFO info{};
            if (GetModuleInformation(GetCurrentProcess(), GetModuleHandleW(nullptr), &info, sizeof(info))) {
                result.Base = (uintptr_t)info.lpBaseOfDll;
                result.Size = info.SizeOfImage;
            }
            return result;
        }();
        return image;
    }

    // nullopt = wildcard
    std::vector<std::optional<uint8_t>> Parse(std::string_view pattern) {
        std::vector<std::optional<uint8_t>> bytes;
        size_t i = 0;
        while (i < pattern.size()) {
            if (pattern[i] == ' ') { i++; continue; }
            if (pattern[i] == '?') {
                while (i < pattern.size() && pattern[i] == '?') i++;
                bytes.push_back(std::nullopt);
                continue;
            }
            bytes.push_back((uint8_t)strtoul(std::string(pattern.substr(i, 2)).c_str(), nullptr, 16));
            i += 2;
        }
        return bytes;
    }

    bool MatchesAt(const uint8_t* at, const std::vector<std::optional<uint8_t>>& bytes) {
        for (size_t j = 0; j < bytes.size(); j++) {
            if (bytes[j] && at[j] != *bytes[j]) return false;
        }
        return true;
    }
}

namespace Memory {
    uintptr_t ImageBase() { return GameImage().Base; }
    size_t ImageSize() { return GameImage().Size; }

    bool Matches(uintptr_t address, std::string_view pattern) {
        auto bytes = Parse(pattern);
        if (!IsReadable((const void*)address, bytes.size())) return false;
        return MatchesAt((const uint8_t*)address, bytes);
    }

    uintptr_t FindPattern(std::string_view pattern) {
        auto bytes = Parse(pattern);
        if (bytes.empty() || !bytes[0]) return 0; // the scan below keys on a concrete first byte

        // Walk committed, readable regions only - the image has guard pages and gaps.
        uintptr_t address = ImageBase();
        const uintptr_t end = ImageBase() + ImageSize();
        while (address < end) {
            MEMORY_BASIC_INFORMATION region;
            if (!VirtualQuery((void*)address, &region, sizeof(region))) break;
            auto regionStart = (uintptr_t)region.BaseAddress;
            auto regionEnd = regionStart + region.RegionSize;
            bool readable = region.State == MEM_COMMIT && !(region.Protect & (PAGE_GUARD | PAGE_NOACCESS));
            if (readable && regionEnd - regionStart >= bytes.size()) {
                auto* data = (const uint8_t*)regionStart;
                size_t last = regionEnd - regionStart - bytes.size();
                for (size_t i = 0; i <= last; i++) {
                    if (data[i] == *bytes[0] && MatchesAt(data + i, bytes)) return regionStart + i;
                }
            }
            address = regionEnd;
        }
        return 0;
    }

    uintptr_t ResolveRelative(uintptr_t instruction, int displacementOffset, int instructionLength) {
        return instruction + instructionLength + *(const int32_t*)(instruction + displacementOffset);
    }

    uintptr_t Resolve(const Signature& signature) {
        if (signature.Rva == 0) {
            auto found = FindPattern(signature.Bytes);
            if (!found) Log::Error("%s: pattern not found", signature.Name);
            return found;
        }
        auto address = Rva(signature.Rva);
        if (!Matches(address, signature.Bytes)) {
            Log::Error("%s: bytes at rva 0x%X do not match - not the 10.40 (CL 9380822) executable?", signature.Name, signature.Rva);
            return 0;
        }
        return address;
    }

    bool IsReadable(const void* address, size_t size) {
        MEMORY_BASIC_INFORMATION region;
        if (!address || !VirtualQuery(address, &region, sizeof(region))) return false;
        if (region.State != MEM_COMMIT || (region.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
        return (uintptr_t)address + size <= (uintptr_t)region.BaseAddress + region.RegionSize;
    }
}
