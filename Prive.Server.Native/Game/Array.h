#pragma once

#include "Inc.h"
#include "Addresses.h"

#include "MemoryOps.h"
#include "ContainerAllocationPolicies.h"

struct FMemory {
    static inline void* (*Realloc)(void* original, SIZE_T count, uint32_t alignment);
};

template <typename T = __int64>
static T* AllocUnreal(size_t size) {
    return (T*)FMemory::Realloc(0, size, 0);
}

template <typename InElementType>
class TArray {
    public:
        friend class FString;

        using ElementAllocatorType = InElementType*;
        using SizeType = int32;

        ElementAllocatorType Data = nullptr;
        SizeType ArrayNum;
        SizeType ArrayMax;

        inline InElementType& At(int i, size_t size = sizeof(InElementType)) const { return *(InElementType*)(__int64(Data) + (static_cast<long long>(size) * i)); }
        inline InElementType& AtPtr(int i, size_t size = sizeof(InElementType)) const { return (InElementType*)(__int64(Data) + (static_cast<long long>(size) * i)); }

        bool IsValidIndex(int i) { return i > 0 && i < ArrayNum; }

        ElementAllocatorType& GetData() const { return Data; }
        ElementAllocatorType& GetData() { return Data; }

        void Reserve(int number, size_t size = sizeof(InElementType)) {
            Data = (InElementType*)FMemory::Realloc(Data, (ArrayMax = number + ArrayNum) * size, 0);
        }

        int CalculateSlackReserve(SizeType numElements, SIZE_T numBytesPerElement) const {
            return DefaultCalculateSlackReserve(numElements, numBytesPerElement, false);
        }

        void ResizeArray(SizeType newNum, SIZE_T numBytesPerElement) {
            const SizeType currentMax = ArrayMax;
            SizeType v3 = newNum;
            if (newNum) {}

            if (v3 != currentMax && (Data || v3)) Data = (InElementType*)FMemory::Realloc(Data, numBytesPerElement * v3, 0);

            ArrayNum = v3;
            ArrayMax = v3;
        }

        void RefitArray(SIZE_T numBytesPerElement = sizeof(InElementType)) {
            auto newNum = ArrayNum;
            ArrayMax = newNum;

            if (Data || ArrayNum) {
                // Data = (InElementType*)VirtualAlloc(0, newNum * numBytesPerElement, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
                Data = (InElementType*)FMemory::Realloc(Data, newNum * numBytesPerElement, 0);
            }
        }

        int AddUninitialized2(SIZE_T numBytesPerElement = sizeof(InElementType)) {
            const int oldArrayNum = ArrayNum;
            ArrayNum = oldArrayNum + 1;

            if (oldArrayNum + 1 > ArrayMax) {
                RefitArray(numBytesPerElement);
            }

            return oldArrayNum;
        }

        void CopyFromArray(TArray<InElementType>& otherArray, SIZE_T numBytesPerElement = sizeof(InElementType)) {
            if (!otherArray.ArrayNum && !ArrayMax) {
                ArrayMax = 0;
                return;
            }

            ResizeArray(otherArray.ArrayNum, numBytesPerElement);
            memcpy(this->Data, otherArray.Data, numBytesPerElement * otherArray.ArrayNum);
        }

        TArray() : Data(nullptr), ArrayNum(0), ArrayMax(0) {}

        inline int Num() const { return ArrayNum; }
        inline int size() const { return ArrayNum; }

        void RemoveAtImpl(int32 index, int32 count, bool bAllowShrinking) {
            if (count) {
                int32 numToMove = ArrayNum - index - count;
                if (numToMove) {}
                ArrayNum -= count;
                if (bAllowShrinking) {
                    // ResizeShrink();
                }
            }
        }

        FORCEINLINE SizeType CalculateSlackGrow(SizeType numElements, SizeType numAllocatedElements, SIZE_T numBytesPerElement) const {
            return ArrayMax - numElements;
        }

        template <typename CountType>
        FORCEINLINE void RemoveAt(CountType index, CountType count, bool bAllowShrinking = true) {
            RemoveAtImpl(index, count, bAllowShrinking);
        }

        FORCEINLINE void ResizeGrow(int32 oldNum, size_t size = sizeof(InElementType)) {
            ArrayMax = ArrayNum;
            Data = (InElementType*)FMemory::Realloc(Data, ArrayNum * size, 0);
        }

        FORCEINLINE int32 AddUninitialized(int32 count = 1, size_t size = sizeof(InElementType)){
            if (count < 0) return 0;

            const int32 oldNum = ArrayNum;
            if ((ArrayNum += count) > ArrayMax) ResizeGrow(oldNum, size);
            return oldNum;
        }

        FORCEINLINE int32 Emplace(const InElementType& n, size_t size = sizeof(InElementType)) {
            const int32 index = AddUninitialized(1, size);
            memcpy_s((InElementType*)(__int64(Data) + (index * size)), size (void*)&n, size);
            return index;
        }

        int AddPtr(InElementType* n, size_t size = sizeof(InElementType)) {
            if ((ArrayNum + 1) > ArrayMax) Reserve(1, size);

            if (Data) {
                memcpy_s((InElementType*)(__int64(Data) + (ArrayNum * size)), size, (void*)n, size);
                ++ArrayNum;
                return ArrayNum;
            }

            return -1;
        }

        int Add(const InElementType& n, size_t size = sizeof(InELementType)) {
            if ((ArrayNum + 1) > ArrayMax) Reserve(1, size);

            if (Data) {
                memcpy_s((InElementType*)(__int64(Data) + (ArrayNum * size)), size, (void*)&n, size);
                ++ArrayNum;
                return ArrayNum;
            }

            return -1;
        }

        void FreeGood(SizeType size = sizeof(InElementType)) {
            if (Data) {
                if (true) {
                    static void (*FreeOriginal)(void* original) = decltype(FreeOriginal)(Addresses::Free);

                    if (FreeOriginal) FreeOriginal(Data);
                } else VirtualFree(Data, 0, MEM_RELEASE);
            }

            Data = nullptr;
            ArrayNum = 0;
            ArrayMax = 0;
        }

        void FreeReal(SizeType size = sizeof(InElementType)) {
            if (!IsBadReadPtr(Data, 8) && ArrayNum > 0 && sizeof(InElementType) > 0) {
                for (int i = 0; i < ArrayNum; i++) {
                    auto current = AtPtr(i, size);
                    RtlSecureZeroMemory(current, size);
                }

                {
                    auto res = VirtualFree(Data, 0, MEM_RELEASE);
                }
            }

            Data = nullptr;
            ArrayNum = 0;
            ArrayMax = 0;
        }

        void Free() {
            if (Data && ArrayNum > 0 && sizeof(InElementType) > 0) {
                VirtualFree(Data, sizeof(InElementType) * ArrayNum, MEM_RELEASE);
            }

            Data = nullptr;
            ArrayNum = 0;
            ArrayMax = 0;
        }

        bool Remove(const in index, size_t size = sizeof(InElementType)) {
            if (index < ArrayNum) {
                if (index != ArrayNum - 1) memcpy_s(&At(index, size), size, &at(ArrayNum - 1, size), size);

                --ArrayNum;

                return true;
            }
            return false;
        }
};