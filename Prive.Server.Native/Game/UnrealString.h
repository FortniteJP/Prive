#pragma once

#include <locale>

#include "Array.h"

class FString {
    public:
        TArray<TCHAR> Data;

    public:
        std::string ToString() const {
            auto length = std::wcslen(Data.Data);
            std::string str(length, '\0');
            std::use_facet<std::ctype<wchar_t>>(std::locale()).narrow(Data.Data, Data.Data + length, '?', &str[0]);

            return str;
        }

        void Free() {
            Data.Free();
        }

        bool IsValid() {
            return Data.Data;
        }

        void Set(const wchar_t* newStr) {
            if (!newStr) return;

#ifndef EXPERIMENTAL_FSTRING
            Data.ArrayMax = Data.ArrayNum = *newStr ? (int)std::wcslen(newStr) + 1 : 0;

            if (Data.ArrayNum) Data.Data = const_cast<wchar_t*>(newStr);
#else
            // ...
#endif
        }

        FString() {}

        FString(const wchar_t* str) {
            Set(str);
        }

        ~FString() {
            Data.Data = nullptr;
            Data.ArrayNum = 0;
            Data.ArrayMax = 0;
        }
};