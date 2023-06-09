#pragma once

#include "Inc.h"

struct FNameEntryId {
    uint32 Value;

    FNameEntryId() : Value(0) {}

    FNameEntryId(uint32 value) : Value(value) {}

    bool operator<(FNameEntryId rhs) const { return Value < rhs.Value; }
    bool operator>(FNameEntryId rhs) const { return Value > rhs.Value; }
    bool operator==(FNameEntryId rhs) const { return Value == rhs.Value; }
    bool operator!=(FNameEntryId rhs) const { return Value != rhs.Value; }
};

struct FName {
    FNameEntryId ComparisonIndex;
    uint32 Number;

    FORCEINLINE int32 GetNumber() const { return Number; }
    FORCEINLINE FNameEntryId GetComparisonIndexFast() const { return ComparisonIndex; }

    std::string ToString() const;
    std::string ToString();

    FName() : ComparisonIndex(0), Number(0) {}

    FName(uint32 value) : ComparisonIndex(value), Number(0) {}

    bool IsValid() const { return ComparisonIndex.Value > 0; }

    FORCEINLINE bool operator==(FName rhs) const {
        return GetComparisonIndexFast() == rhs.GetComparisonIndexFast() && GetNumber() == rhs.GetNumber();
    }

    int32 Compare(const FName& other) const;

    FORCEINLINE bool operator<(const FName& rhs) const {
        return this->ComparisonIndex == rhs.ComparisonIndex ? Number < rhs.Number : this->ComparisonIndex < rhs.ComparisonIndex;
    }
};