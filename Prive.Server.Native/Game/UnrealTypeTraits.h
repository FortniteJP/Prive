#pragma once

#include "IsEnum.h"
#include "IsPointer.h"
#include "IsArithmetic.h"
#include "AndOrNot.h"
#include "IsPODType.h"
#include "IsTriviallyCopyConstructible.h"

template <typename T, bool TypeIsSmall>
struct TCallTraitsParamTypeHelper {
    typedef const T& ParamType;
    typedef const T& ConstParamType;
};

template <typename T>
struct TCallTraitsParamTypeHelper<T, true> {
    typedef const T ParamType;
    typedef const T ConstParamType;
};

template <typename T>
struct TCallTraitsParamTypeHelper<T*, true> {
    typedef T* ParamType;
    typedef const T* ConstParamType;
};

template <typename T>
struct TCallTraitsBase {
    private:
        enum { PassByValue = TOr<TAndValue<(sizeof(T) <= sizeof(void*)), TIsPODType<T>>, TIsArithmetic<T>, TIsPointer<T>>::Value };
    public:
        typedef T ValueType;
        typedef T& Reference;
        typedef const T& ConstReference;
        typedef typename TCallTraitsParamTypeHelper<T, PassByValue>::ParamType ParamType;
        typedef typename TCallTraitsParamTypeHelper<T, PassByValue>::ConstParamType ConstParamType;
};

template <typename T>
struct TCallTraits : public TCallTraitsBase<T> {};

template <typename T>
struct TCallTraits<T&> {
    typedef T& ValueType;
    typedef T& Reference;
    typedef const T& ConstReference;
    typedef T& ParamType;
    typedef T& ConstParamType;
};

template <typename T, size_t N>
struct TCallTraits<T[N]> {
    private:
        typedef T ArrayType[N];
    public:
        typedef const T* ValueType;
        typedef ArrayType& Reference;
        typedef const ArrayType& ConstReference;
        typedef const T* ParamType;
        typedef const T* ConstParamType;
};

template <typename T>
struct TTypeTraitsBase {
    typedef typename TCallTraits<T>::ParamType ConstInitType;
    typedef typename TCallTraits<T>::ConstReference ConstPointerType;

    enum { IsBytewiseComparable = TOr<TIsEnum<T>, TIsArithmetic<T>, TIsPointer<T>>::Value };
};

template <typename T> struct TTypeTraits : public TTypeTraitsBase<T> {};

template <typename T, typename Arg>
struct TIsBitwiseConstructible {
    enum { Value = false };
};

template <typename T>
struct TIsBitwiseConstructible<T, T> {
    enum { Value = TIsTriviallyCopyConstructible<T>::Value };
};

template <typename T, typename U>
struct TIsBitwiseConstructible<const T, U> {
    // ?
};

template <typename T>
struct TIsBitwiseConstructible<const T*, T*> {
    enum { Value = true };
};

template <> struct TIsBitwiseConstructible<uint8, char> { enum { Value = true }; };
template <> struct TIsBitwiseConstructible< char, uint8> { enum { Value = true }; };
template <> struct TIsBitwiseConstructible<uint16, short> { enum { Value = true }; };
template <> struct TIsBitwiseConstructible< short, uint16> { enum { Value = true }; };
template <> struct TIsBitwiseConstructible<uint32, int32> { enum { Value = true }; };
template <> struct TIsBitwiseConstructible< int32, uint32> { enum { Value = true }; };
template <> struct TIsBitwiseConstructible<uint64, int64> { enum { Value = true }; };
template <> struct TIsBitwiseConstructible< int64, uint64> { enum { Value = true }; };