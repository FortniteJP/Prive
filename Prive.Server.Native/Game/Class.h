#pragma once

#include "Object.h"

#include "Addresses.h"
#include "UnrealString.h"
#include "Map.h"
#include <xstring>

struct UField : UObject {
    UField* Next;
};

class UStruct : public UField {
    public:
        int GetPropertiesSize();
        UStruct* GetSuperStruct() { return *(UStruct**)(__int64(this) + Offsets::SuperStruct); }
        TArray<uint8_t> GetScript() { return *(TArray<uint8_t>*)(__int64(this) + Offsets::Script); }
};

class UClass : public UStruct {
    public:
        UObject* CreateDefaultObject();
};

class UFunction : public UStruct {
    public:
        void*& GetFunc() { return *(void**)(__int64(this) + Offsets::Func); }
};

class UEnum : public UField {
    public:
        int64 GetValue(const std::string& enumMemberName) {
            auto names = (TArray<TPair<FName, __int64>>*)(__int64(this) + sizeof(UField) + sizeof(FString));

            for (int i = 0; i < names->Num(); i++) {
                auto& pair = names->At(i);
                auto& name = pair.Key();
                auto value = pair.Value();
                // why no std::string::contains ?
                if (name.ComparisonIndex.Value && name.ToString().find(enumMemberName) != std::string::npos) return value;
            }

            return -1;
        }
};