#pragma once

#include "Set.h"

template <typename KeyType, typename ValueType>
class TPair {
    public:
        KeyType First;
        ValueType Second;
    
        FORCEINLINE KeyType& Key() {
            return First;
        }

        FORCEINLINE const KeyType& Key() const {
            return First;
        }

        FORCEINLINE ValueType& Value() {
            return Second;
        }

        FORCEINLINE const ValueType& Value() const {
            return Second;
        }
};

template <typename KeyType, typename ValueType>
class TMap {
    public:
        typedef TPair<KeyType, ValueType> ElementType;

    public:
        TSet<ElementType> Pairs;

    public:
        class FBaseIterator {
            private:
                TMap<KeyType, ValueType>& IteratedMap;
                TSet<ElementType>::FBaseIterator SetIt;

            public:
                FBaseIterator(TMap<KeyType, ValueType>& InMap, TSet<ElementType>::FBaseIterator InSet) : IteratedMap(InMap), SetIt(InSet) {}

                FORCEINLINE TMap<KeyType, ValueType>::FBaseIterator operator++() {
                    ++SetIt;
                    return *this;
                }

                FORCEINLINE TMap<KeyType, ValueType>::ElementType& operator*() {
                    return *SetIt;
                }

                FORCEINLINE const TMap<KeyType, ValueType>::ElementType& operator*() const {
                    return *SetIt;
                }

                FORCEINLINE bool operator==(const TMap<KeyType, ValueType>::FBaseIterator& other) const {
                    return SetIt == other.SetIt;
                }

                FORCEINLINE bool operator!=(const TMap<KeyType, ValueType>::FBaseIterator& other) const {
                    return SetIt != other.SetIt;
                }

                FORCEINLINE bool IsElementValid() const {
                    return SetIt.IsElementValid();
                }
        };

        FORCEINLINE TMap<KeyType, ValueType>::FBaseIterator Begin() {
            return TMap<KeyType, ValueType>::FBaseIterator(*this, Pairs.begin());
        }

        FORCEINLINE const TMap<KeyType, ValueType>::FBaseIterator Begin() const {
            return TMap<KeyType, ValueType>::FBaseIterator(*this, Pairs.begin());
        }

        FORCEINLINE TMap<KeyType, ValueType>::FBaseIterator End() {
            return TMap<KeyType, ValueType>::FBaseIterator(*this, Pairs.end());
        }

        FORCEINLINE const TMap<KeyType, ValueType>::FBaseIterator End() const {
            return TMap<KeyType, ValueType>::FBaseIterator(*this, Pairs.end());
        }

        FORCEINLINE ValueType& operator[](const KeyType& key) {
            return this->GetByKey(key);
        }

        FORCEINLINE const ValueType& operator[](const KeyType& key) const {
            return this->GetByKey(key);
        }

        FORCEINLINE int32 Num() const {
            return Pairs.Num();
        }

        FORCEINLINE bool IsValid() const {
            return Pairs.IsValid();
        }

        FORCEINLINE bool IsIndexValid(int32 indexToCheck) const {
            return Pairs.IsIndexValid(indexToCheck);
        }

        FORCEINLINE bool Contains(const KeyType& elementToLookFor) const {
            for (auto& Element : *this) {
                if (Element.Key() == elementToLookFor) return true;
            }
            return false;
        }

        FORCEINLINE ValueType& GetByKey(const KeyType& key, bool* wasSuccessful = nullptr) {
            for (auto& Pair : *this) {
                if (Pair.Key() == key) {
                    if (wasSuccessful) *wasSuccessful = true;

                    return Pair.Value();
                }
            }

            if (wasSuccessful) *wasSuccessful = false;
        }

        FORCEINLINE ValueType& Find(const KeyType& key, bool* wasSuccessful = nullptr) {
            return GetByKey(key, wasSuccessful);
        }

        FORCEINLINE ValueType GetByKeyNoRef(const KeyType& key) {
            for (auto& Pair : *this) {
                if (Pair.Key() == key) return Pair.Value();
            }
        }
};