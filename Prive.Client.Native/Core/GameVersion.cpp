#include "GameVersion.h"
#include "Log.h"
#include "Memory.h"

#include <cstdio>
#include <string>

namespace {
    constexpr char Prefix[] = "++Fortnite+Release-";

    // IDA-style pattern for Prefix, as UTF-16 (TCHAR, the form 10.40 stores) or as plain ASCII.
    std::string PatternFor(bool wide) {
        std::string pattern;
        char hex[4];
        for (const char* c = Prefix; *c; c++) {
            snprintf(hex, sizeof(hex), "%02X ", (unsigned char)*c);
            pattern += hex;
            if (wide) pattern += "00 ";
        }
        return pattern;
    }

    GameVersion::Version Scan() {
        GameVersion::Version version;
        for (bool wide : { true, false }) {
            uintptr_t found = Memory::FindPattern(PatternFor(wide));
            if (!found) continue;

            // "10.40" (possibly followed by "-CL-...") right after the prefix
            std::string digits;
            size_t step = wide ? 2 : 1;
            for (uintptr_t at = found + (sizeof(Prefix) - 1) * step; digits.size() < 16; at += step) {
                if (!Memory::IsReadable((void*)at, step)) break;
                char c = *(const char*)at;
                if ((c < '0' || c > '9') && c != '.') break;
                digits += c;
            }
            if (sscanf_s(digits.c_str(), "%d.%d", &version.Major, &version.Minor) >= 1) {
                version.Known = true;
                Log::Info("GameVersion: Fortnite %s", digits.c_str());
                return version;
            }
        }
        Log::Warn("GameVersion: no \"%s\" string in the image - version unknown", Prefix);
        return version;
    }
}

namespace GameVersion {
    const Version& Get() {
        static const Version version = Scan();
        return version;
    }
}
