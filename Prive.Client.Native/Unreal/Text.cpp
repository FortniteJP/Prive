#include "Text.h"
#include "Addresses.h"

#include <cwchar>

namespace {
    using ConvStringToTextFn = FText*(__fastcall*)(FText* out, const FString* string);
    ConvStringToTextFn ConvStringToText = nullptr;
}

namespace Text {
    bool Init() {
        ConvStringToText = (ConvStringToTextFn)Memory::Resolve(Addresses::ConvStringToText);
        return ConvStringToText != nullptr;
    }

    bool Make(const wchar_t* string, FText& out) {
        if (!ConvStringToText) return false;
        int32_t length = (int32_t)wcslen(string) + 1;
        FString input{ string, length, length };
        ConvStringToText(&out, &input);
        return true;
    }
}
