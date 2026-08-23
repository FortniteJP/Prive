#pragma once

#include "Object.h"
#include "Package.h"

#define ANY_PACKAGE (UObject*)-1

extern inline UObject* (*StaticFindObjectOriginal)(UClass* objClass, UObject* inOuter, const TCHAR* name, bool exactClass) = nullptr;

template <typename T = UObject>
static inline T* StaticFindObject(UClass* objClass, UObject* inOuter, const TCHAR* name, bool exactClass = false) {
    return (T*)StaticFindObjectOriginal(objClass, inOuter, name, exactClass);
}

static inline UPackage* GetTransientPackage() {
    return StaticFindObject<UPackage>(nullptr, nullptr, L"/Engine/Transient");
}