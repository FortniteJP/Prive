#pragma once

#include <unordered_map>

#include "ObjectMacros.h"
#include "NameTypes.h"

#include "Addresses.h"

class UClass;
class UFunction;

struct FGuid {
    unsigned int A;
    unsigned int B;
    unsigned int C;
    unsigned int D;

    bool operator==(const FGuid& other) {
        return A == other.A && B == other.B && C == other.C && D == other.D;
    }

    bool operator!=(const FGuid& other) {
        return !(*this == other);
    }
};

class UObject {
    public:
        void** VFTable;
        int32 ObjectFlags; // EObjectFlags
        UClass* ClassPrivate;
        FName NamePrivate;
        UObject* OuterPrivate;

        static inline void (*ProcessEventOriginal)(const UObject*, UFunction*, void*);

        void ProcessEvent(UFunction* function, void* parms = nullptr) {
            ProcessEventOriginal(this, function, parms);
        }

        void ProcessEvent(UFunction* function, void* parms = nullptr) const {
            ProcessEventOriginal(this, function, parms);
        }

        std::string GetName() { return NamePrivate.ToString(); }
        std::string GetPathName();
        std::string GetFullName();
        UObject* GetOuter() const { return OuterPrivate; }
        FName GetFName() const { return NamePrivate; }

        class UPackage* GetOutermost() const;
        bool IsA(class UStruct* other);
        class UFunction* FindFunction(const std::string& shortFunctionName);

        void* GetProperty(const std::string& childName, bool bWarnIfNotFound = true);
        void* GetProperty(const std::string& childName, bool bWarnIfNotFound = true) const;
        int GetOffset(const std::string& childName, bool bWarnIfNotFound = true);
        int GetOffset(const std::string& childName, bool bWarnIfNotFound = true) const;

        template <typename T = UObject*>
        T& Get(int offset) const { return *(T*)(__int64(this) + offset); }

        void* GetInterfaceAddress(UClass* interfaceClass);

        bool ReadBitfieldValue(int offset, uint8_t fieldMask);
        bool ReadBitfieldValue(const std::string& childName, uint8_t fieldMask) { return ReadBitfieldValue(GetOffset(childName), fieldMask); }

        void SetBitfieldValue(int offset, uint8_t fieldMask, bool newValue);
        void SetBitfieldValue(const std::string& childName, uint8_t fieldMask, bool newValue) { SetBitfieldValue(GetOffset(childName), fieldMask, newValue); }

        template <typename T = UObject*>
        T& Get(const std::string& childName) { return Get<T>(GetOffset(childName)); }

        template <typename T = UObject*>
        T* GetPtr(int offset) { return (T*)(__int64(this) + offset); }

        template <typename T = UObject*>
        T* GetPtr(const std::string& childName) { return GetPtr<T>(GetOffset(childName)); }

        void AddToRoot();
        bool IsValidLowLevel();
        FORCEINLINE bool IsPendingKill() const;
};

FORCEINLINE bool IsValidChecked (const UObject* test) {
    return true;
}