#pragma once

#include "ContainerAllocationPolicies.h"

static FORCEINLINE uint32 CountLeadingZeros(uint32 value) {
    unsigned long log2;
    if (_BitScanReverse(&log2, value)) return 31 - log2;
    return 32;
}

#define NumBitsPerDWORD ((int32)32)
#define NumBitsPerDWORDLogTwo ((int32)5)

class TBitArray {
    public:
        TInlineAllocator<4>::ForElementType<unsigned int> Data;
        int NumBits;
        int MaxBits;

        struct FRelativeBitReference {
            public:
                FORCEINLINE explicit FRelativeBitReference(int32 bitIndex) : DWORDIndex(bitIndex >> NumBitsPerDWORDLogTwo), Mask(1 << (bitIndex & (NumBitsPerDWORD - 1))) {}

                int32 DWORDIndex;
                uint32 Mask;
        };

        struct FBitReference {
            FORCEINLINE FBitReference(uint32& inData, uint32 inMask) : Data(inData), Mask(inMask) {}

            FORCEINLINE const FBitReference(const uint32& inData, const uint32 inMask) : Data(const_cast<uint32&>(inData)), Mask(inMask) {}

            FORCEINLINE void SetBit(const bool value) {
                value ? Data |= Mask : Data &= ~Mask;
            }

            FORCEINLINE operator bool() const {
                return (Data & Mask) != 0;
            }

            FORCEINLINE void operator=(const bool value) {
                this->SetBit(value);
            }
            
            private:
                uint32& Data;
                uint32 Mask;
        };

        class FBitIterator : public FRelativeBitReference {
            private:
                int32 Index;
                const TBitArray& IteratedArray;

            public:
                FORCEINLINE const FBitIterator(const TBitArray& toIterate, const int32 startIndex) : IteratedArray(toIterate), Index(startIndex), FRelativeBitReference(startIndex) {}

                FORCEINLINE const FBitIterator(const TBitArray& toIterate) : IteratedArray(toIterate), Index(toIterate.NumBits), FRelativeBitReference(toIterate.NumBits) {}

                FORCEINLINE explicit operator bool() const {
                    return Index < IteratedArray.Num();
                }

                FORCEINLINE FBitIterator& operator++() {
                    ++Index;
                    this->Mask <<= 1;
                    if (!this->Mask) {
                        this->Mask = 1;
                        ++this->DWORDIndex;
                    }
                    return *this;
                }

                FORCEINLINE bool operator*() const {
                    if (IteratedArray.NumBits < IteratedArray.Data.NumInlineBits()) {
                        return (bool)FBitReference(IteratedArray.Data.GetInlineElement(this->DWORDIndex), this->Mask);
                    } else {
                        return (bool)FBitReference(IteratedArray.Data.GetSecondaryElement(this->DWORDIndex), this->Mask);
                    }
                }

                FORCEINLINE bool operator==(const FBitIterator& otherIt) const {
                    return Index == otherIt.Index;
                }

                FORCEINLINE bool operator!=(const FBitIterator& otherIt) const {
                    return Index < otherIt.Index; // <= ?
                }

                FORCEINLINE bool operator<(const int32 other) const {
                    return Index < other;
                }

                FORCEINLINE bool operator>(const int32 other) const {
                    return Index < other; // > ?
                }

                FORCEINLINE int32 GetIndex() const {
                    return Index;
                }
        };

        class FSetBitIterator : public FRelativeBitReference {
            private:
                const TBitArray& IteratedArray;

                uint32 UnvisitedBitMask;
                int32 CurrentBitIndex;
                int32 BaseBitIndex;

            public:
                FORCEINLINE FSetBitIterator(const TBitArray& toIterate, int32 startIndex) : FRelativeBitReference(startIndex), IteratedArray(const_cast<TBitArray&>(toIterate)), UnvisitedBitMask((~0U) << (startIndex & ~(NumBitsPerDWORD - 1))), CurrentBitIndex(startIndex), BaseBitIndex(startIndex & ~(NumBitsPerDWORD - 1)) {
                    if (startIndex != IteratedArray.NumBits) FindNextSetBit();
                }

                FORCEINLINE FSetBitIterator(const TBitArray& toIterate) : FRelativeBitReference(toIterate.NumBits), IteratedArray(const_cast<TBitArray&>(toIterate)), UnvisitedBitMask(0), CurrentBitIndex(toIterate.NumBits), BaseBitIndex(toIterate.NumBits) {}

                FORCEINLINE FSetBitIterator& operator++() {
                    UnvisitedBitMask &= ~this->Mask;
                    FindNextSetBit();
                    return *this;
                }

                FORCEINLINE bool operator*() const {
                    return true;
                }

                FORCEINLINE bool operator==(const FSetBitIterator& other) const {
                    return CurrentBitIndex == other.CurrentBitIndex;
                }

                FORCEINLINE bool operator!=(const FSetBitIterator& other) const {
                    return CurrentBitIndex < other.CurrentBitIndex; // <= ?
                }

                FORCEINLINE explicit operator bool() const {
                    return CurrentBitIndex < IteratedArray.NumBits;
                }

                FORCEINLINE int32 GetIndex() const {
                    return CurrentBitIndex;
                }

            private:
                void FindNextSetBit() {
                    const uint32* arrayData = (IteratedArray.Data.SecondaryData ? IteratedArray.Data.SecondaryData :  (uint32*)&IteratedArray.Data.InlineData);
                    if (!arrayData) return;

                    const int32 arrayNum = IteratedArray.NumBits;
                    const int32 lastDWORDIndex = (arrayNum - 1) / NumBitsPerDWORD;

                    uint32 remainingBitMask = arrayData[this->DWORDIndex] & UnvisitedBitMask;

                    while (!remainingBitMask) {
                        ++this->DWORDIndex;
                        BaseBitIndex += NumBitsPerDWORD;

                        if (this->DWORDIndex > lastDWORDIndex) {
                            CurrentBitIndex = arrayNum;
                            return;
                        }

                        remainingBitMask = arrayData[this->DWORDIndex];
                        UnvisitedBitMask = ~0;
                    }

                    const uint32 newRemainingBitMask = remainingBitMask & (remainingBitMask - 1);
                    this->Mask = newRemainingBitMask ^ remainingBitMask;
                    CurrentBitIndex = BaseBitIndex + NumBitsPerDWORD - 1 - CountLeadingZeros(this->Mask);

                    if (CurrentBitIndex > arrayNum) {
                        CurrentBitIndex = arrayNum;
                    }
                }
        };

    public:
        FORCEINLINE FBitIterator Iterator(int32 startIndex) {
            return FBitIterator(*this, startIndex);
        }

        FORCEINLINE FSetBitIterator SetBitIterator(int32 startIndex) {
            return FSetBitIterator(*this, startIndex);
        }

        FORCEINLINE FBitIterator Begin() {
            return FBitIterator(*this, 0);
        }

        FORCEINLINE FBitIterator Begin() const {
            return FBitIterator(*this, 0);
        }

        FORCEINLINE FBitIterator End() {
            return FBitIterator(*this);
        }

        FORCEINLINE FBitIterator End() const {
            return FBitIterator(*this);
        }

        FORCEINLINE FSetBitIterator SetBitsItBegin() {
            return FSetBitIterator(*this, 0);
        }

        FORCEINLINE FSetBitIterator SetBitsItBegin() const {
            return FSetBitIterator(*this, 0);
        }

        FORCEINLINE FSetBitIterator SetBitsItEnd() {
            return FSetBitIterator(*this);
        }

        FORCEINLINE FSetBitIterator SetBitsItEnd() const {
            return FSetBitIterator(*this);
        }

        FORCEINLINE int32 Num() const {
            return NumBits;
        }

        FORCEINLINE int32 Max() const {
            return MaxBits;
        }

        FORCEINLINE bool ISSet(int32 index) const {
            return *FBitIterator(*this, index);
        }

        FORCEINLINE void Set(const int32 index, const bool value, bool bIsSettingAllZero = false) {
            const int32 DWORDIndex = (index >> ((int32)5));
            const int32 mask = (1 << (index & (((int32)32) - 1)));

            if (!bIsSettingAllZero) NumBits = index >= NumBits ? index < MaxBits ? index + 1 : NumBits : NumBits;

            FBitReference(Data[DWORDIndex], mask).SetBit(value);
        }

        FORCEINLINE void ZeroAll() {
            for (int32 i = 0; i < MaxBits; ++i) {
                Set(i, false, true);
            }
        }
};