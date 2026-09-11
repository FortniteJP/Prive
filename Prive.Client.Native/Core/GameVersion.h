#pragma once

// The Fortnite version of the process we are in, e.g. 10.40, read from the engine's branch string
// ("++Fortnite+Release-10.40") in the image - the same string Project Reboot 3.0 falls back to. It
// sits in .rdata, which is plaintext, so it is readable the moment the DLL is injected.
namespace GameVersion {
    struct Version {
        int Major = 0;
        int Minor = 0;
        bool Known = false;
    };

    const Version& Get(); // scanned once, on first use

    // Known and strictly older than major.minor.
    inline bool IsBelow(int major, int minor = 0) {
        auto& v = Get();
        return v.Known && (v.Major < major || (v.Major == major && v.Minor < minor));
    }
}
