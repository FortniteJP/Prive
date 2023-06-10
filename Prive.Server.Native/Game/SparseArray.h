#pragma once

#include "Array.h"
#include "BitArray.h"

#define INDEX_NONE -1

template <typename ElementType>
union TSparseArrayElementOrListLink {
    TSparseArrayElementOrListLink(ElementType& inElement) : ElementData(inElement) {}

    TSparseArrayElementOrListLink(ElementType&& inElement) : ElementData(inElement) {}

    TSparseArrayElementOrListLink(int32 inPrevFree, int32 inNextFree)  : PrevFreeIndex(inPrevFree), NextFreeIndex(inNextFree) {}

    ElementType ElementData;

    struct {
        int PrevFreeIndex;
        int NextFreeIndex;
    };
};

template <typename ArrayType>
class TSparseArray {
    public:
        typedef TSparseArrayElementOrListLink<ArrayType> FSparseArrayElement;

        TArray<FSparseArrayElement> Data;
        TBitArray AllocationFlags;
        int32 FirstFreeIndex;
        int32 NumFreeIndices;

        FORCEINLINE int32 Num() const {
            return Data.Num() - NumFreeIndices;
        }

        class FBaseIterator {
            private:
                TSparseArray<ArrayType>& IteratedArray;
                TBitArray::FSetBitIterator BitArrayIt;

            public:
                FORCEINLINE FBaseIterator(const TSparseArray<ArrayType>& array, const TBitArray::FSetBitIterator bitIterator) : IteratedArray(const_cast<TSparseArray<ArrayType>&>(array)), BitArrayIt(const_cast<TBitArray::FSetBitIterator&>(bitIterator)) {}

                FORCEINLINE explicit operator bool() const {
                    return (bool)BitArrayIt;
                }

                FORCEINLINE TSparseArray<ArrayType>::FBaseIterator& operator++() {
                    ++BitArrayIt;
                    return *this;
                }

                FORCEINLINE ArrayType& operator*() {
                    return IteratedArray[BitArrayIt.GetIndex()].ElementData;
                }

                FORCEINLINE const ArrayType& operator*() const {
                    return IteratedArray[BitArrayIt.GetIndex()].ElementData;
                }
                FORCEINLINE ArrayType* operator->() {
                    return &IteratedArray[BitArrayIt.GetIndex()].ElementData;
                }

                FORCEINLINE const ArrayType* operator->() const {
                    return &IteratedArray[BitArrayIt.GetIndex()].ElementData;
                }
                FORCEINLINE bool operator==(const TSparseArray<ArrayType>::FBaseIterator& other) const {
                    return BitArrayIt == other.BitArrayIt;
                }

                FORCEINLINE bool operator!=(const TSparseArray<ArrayType>::FBaseIterator& other) const {
                    return BitArrayIt != other.BitArrayIt;
                }

                FORCEINLINE int32 GetIndex() const {
                    return BitArrayIt.GetIndex();
                }

                FORCEINLINE bool IsElementValid() const {
                    return *BitArrayIt;
                }
        };

    public:
        FORCEINLINE TSparseArray<ArrayType>::FBaseIterator Begin() {
            return TSparseArray<ArrayType>::FBaseIterator(*this, TBitArray::FSetBitIterator(AllocationFlags, 0));
        }

        FORCEINLINE const TSparseArray<ArrayType>::FBaseIterator Begin() const {
            return TSparseArray<ArrayType>::FBaseIterator(*this, TBitArray::FSetBitIterator(AllocationFlags, 0));
        }
        FORCEINLINE TSparseArray<ArrayType>::FBaseIterator End() {
            return TSparseArray<ArrayType>::FBaseIterator(*this, TBitArray::FSetBitIterator(AllocationFlags));
        }

        FORCEINLINE const TSparseArray<ArrayType>::FBaseIterator End() const {
            return TSparseArray<ArrayType>::FBaseIterator(*this, TBitArray::FSetBitIterator(AllocationFlags));
        }

        FORCEINLINE FSparseArrayElement& operator[](uint32 index) {
            return *(FSparseArrayElement*)&Data.at(index).ElementData;
        }

        FORCEINLINE const FSparseArrayElement& operator[](uint32 index) const {
            return *(const FSparseArrayElement*)&Data.at(index).ElementData;
        }

        FORCEINLINE int32 GetNumFreeIndices() const {
            return NumFreeIndices;
        }

        FORCEINLINE int32 GetFirstFreeIndex() const {
            return FirstFreeIndex;
        }

        FORCEINLINE const TArray<FSparseArrayElement>& GetData() const {
            return Data;
        }

        FSparseArrayElement& GetData(int32 index) {
            return *(FSparseArrayElement*)&Data.at(index).ElementData;
            // return ((FSparseArrayElement*)Data.Data)[index];
        }

        const FSparseArrayElement& GetData(int32 index) const {
            return *(const FSparseArrayElement*)&Data.at(index).ElementData;
            // return ((FSparseArrayElement*)Data.Data)[Index];
        }

        FORCEINLINE const TBitArray& GetAllocationFlags() const {
            return AllocationFlags;
        }

        FORCEINLINE bool IsIndexValid(int32 indexToCheck) const {
            return AllocationFlags.IsSet(indexToCheck);
        }

        void RemoveAt(int32 index, int32 count = 1) {
            RemoveAtUninitialized(index, count);
        }

        void RemoveAtUninitialized(int32 index, int32 count = 1) {
            for (; count; --count) {
                if (NumFreeIndices) GetData(FirstFreeIndex).PrevFreeIndex = index;
                auto& IndexData = GetData(index);
                IndexData.PrevFreeIndex = -1;
                IndexData.NextFreeIndex = NumFreeIndices > 0 ? FirstFreeIndex : INDEX_NONE;
                FirstFreeIndex = index;
                ++NumFreeIndices;
                AllocationFlags.Set(index, false);
                // AllocationFlags[index] = false;

                ++Index;
            }
        }
};