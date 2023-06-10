#pragma once

#include "NumericLimits.h"

template <int NumElements>
class TInlineAllocator {
    private:
        template <int Size, int Alignment>
        struct alignas(Alignment) TAlignedBytes {
            unsigned char Pad[Size];
        };

        template <typename ElementType>
        struct TTypeCompatibleBytes : public TAlignedBytes<sizeof(ElementType), alignof(ElementType)> {};

    public:
        template <typename ElementType>
        class ForElementType {
            friend class TBitArray;

            private:
                TTypeCompatibleBytes<ElementType> InlineData[NumElements];

                ElementType* SecondaryData;

            public:
                FORCEINLINE int32 NumInlineBytes() const {
                    return sizeof(ElementType) * NumElements;
                }

                FORCEINLINE int32 NumInlineBits() const {
                    return NumInlineBytes() * 8;
                }

                FORCEINLINE ElementType& operator[](int32 index) {
                    return *(ElementType*)(&InlineData[index]);
                }

                FORCEINLINE const ElementType& operator[](int32 index) const {
                    return *(ElementType*)(&InlineData[index]);
                }

                FORCEINLINE void operator=(void* inElements) {
                    SecondaryData = inElements;
                }

                FORCEINLINE ElementType& GetInlineElement(int32 index) {
                    return *(ElementType*)(&InlineData[index]);
                }

                FORCEINLINE const ElementType& GetInlineElement(int32 index) const {
                    return *(ElementType*)(&InlineData[index]);
                }

                FORCEINLINE ElementType& GetSecondaryElement(int32 index) {
                    return SecondaryData[index];
                }

                FORCEINLINE const ElementType& GetSecondaryElement(int32 index) const {
                    return SecondaryData[index];
                }

                ElementType* GetInlineElements() {
                    return (ElementType*)InlineData;
                }

                FORCEINLINE ElementType* GetAllocation() const {
                    return IfAThenAElseB(SecondaryData, GetInlineElements());
                }
        };
};

FORCEINLINE size_t QuantizeSize(SIZE_T count, uint32 alignment) {
    return count;
}

enum {
    DEFAULT_ALIGNMENT = 0
};

template <typename SizeType>
FORCEINLINE SizeType DefaultCalculateSlackReserve(SizeType numElements, SIZE_T bytesPerElement, bool bAllowQuantize, uint32 alignment = DEFAULT_ALIGNMENT) {
    SizeType retval = numElements;

    if (bAllowQuantize) {
        auto count = SIZE_T(retval) * SIZE_T(bytesPerElement);
        retval = (SizeType)(QuantizeSize(count, alignment) / bytesPerElement);
        if (numElements > retval) {
            retval = TNumericLimits<SizeType>::Max();
        }
    }

    return retval;
}