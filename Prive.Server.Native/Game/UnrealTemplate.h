#pragma once

#include "Inc.h"

#include "EnableIf.h"
#include "RemoveReference.h"
#include "AndOrNot.h"
#include "IsArithmetic.h"
#include "IsPointer.h"
#include "TypeCompatibleBytes.h"

template <typename T> struct TRValueToLValueReference { typedef T Type; };
template <typename T> struct TRValueToLValueReference<T&&> { typedef T& Type; };

template <typename ReferencedType>
FORCEINLINE ReferencedType* IfAThenAElseB(ReferencedType* A, ReferencedType* B) {
    using PTRINT = int64;
    CONST PTRINT IntA = reinterpret_cast<PTRINT>(A);
    CONST PTRINT IntB = reinterpret_cast<PTRINT>(B);

    const PTRINT MaskB = -(!IntA);

    return reinterpret_cast<ReferencedType*>(IntA | (MaskB & IntB));
}

template <typename T>
auto GetData(T&& container) -> decltype(container.GetData()) {
    return container.GetData();
}

template <typename T, SIZE_T N>
auto GetData(T(&container)[N]) {
    return container;
}

template <typename T>
constexpr T* GetData(std::initializer_list<T> list) {
    return list.begin();
}

template <typename T>
SIZE_T GetNum(T&& container) {
    return (SIZE_T)container.Num();
}

template <typename T>
FORCEINLINE T&& Forward(typename TRemoveReference<T>::Type& obj) {
    return (T&&)obj;
}

template <typename T>
FORCEINLINE T&& Forward(typename TRemoveReference<T>::Type&& obj) {
    return (T&&)obj;
}

template <typename T>
FORCEINLINE typename TRemoveReference<T>::Type&& MoveTemp(T&& obj) {
    typedef typename TRemoveReference<T>::Type CastType;

    return (CastType&&)obj;
}

template <typename T>
struct TUseBitwiseSwap {
    enum { Value = !TOrValue<__is_enum(T), TIsPointer<T>, TIsArithmetic<T>>::Value }
};

template <typename T>
inline typename TEnableIf<!TUseBitwiseSwap<T>::Value>::Type Swap(T& a, T& b) {
    T temp = MoveTemp(a);
    a = MoveTemp(b);
    b = MoveTemp(temp);
}

#define LIKELY(x) (x)

template <typename T>
inline typename TEnableIf<TUseBitwiseSwap<T>::Value>::Type Swap(T& a, T& b) {
    if (LIKELY(&a != &b)) {
        TTypeCompatibleBytes<T> temp;

        memcpy(&temp, &a, sizeof(T));
        memcpy(&a, &b, sizeof(T));
        memcpy(&b, &temp, sizeof(T));
    }
}