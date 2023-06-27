using System.Runtime.CompilerServices;

namespace AFortOnlineBeacon.Serialization;

public abstract class FArchive : IDisposable {
    public bool _ArIsLoading;
    public bool _ArIsSaving;
    public bool _ArIsTransacting;
    public bool _ArIsTextFormat;
    public bool _ArWantBinaryPropertySerialization;
    public bool _ArForceUnicode;
    public bool _ArIsPersistent;
    public bool _ArIsError;
    public bool _ArIsCriticalError;
    public bool _ArContainsCode;
    public bool _ArContainsMap;
    public bool _ArRequiresLocalizationGather;
    public bool _ArForceByteSwapping;
    public bool _ArIgnoreArchetypeRef;
    public bool _ArNoDelta;
    public bool _ArIgnoreOuterRef;
    public bool _ArIgnoreClassGeneratedByRef;
    public bool _ArIgnoreClassRef;
    public bool _ArAllowLazyLoading;
    public bool _ArIsObjectReferenceCollector;
    public bool _ArIsModifyingWeakAndStrongReferences;
    public bool _ArIsCountingMemory;
    public bool _ArShouldSkipBulkData;
    public bool _ArIsFilterEditorOnly;
    public bool _ArIsSaveGame;
    public bool _ArIsNetArchive;
    public bool _ArUseCustomPropertyList;
    public int _ArSerializingDefaults;
    public uint _ArPortFlags;
    public long _ArMaxSerializeSize;
    protected int _ArUE4Ver;
    protected int _ArLicenseeUE4Ver;
    protected FEngineVersion _ArEngineVer;
    protected EEngineNetworkVersionHistory _ArEngineNetVer;
    protected uint _ArGameNetVer;

    public FArchive() {}

    public FArchive(FArchive archive) {
        _ArIsLoading = archive._ArIsLoading;
        _ArIsSaving = archive._ArIsSaving;
        _ArIsTransacting = archive._ArIsTransacting;
        _ArIsTextFormat = archive._ArIsTextFormat;
        _ArWantBinaryPropertySerialization = archive._ArWantBinaryPropertySerialization;
        _ArForceUnicode = archive._ArForceUnicode;
        _ArIsPersistent = archive._ArIsPersistent;
        _ArIsError = archive._ArIsError;
        _ArIsCriticalError = archive._ArIsCriticalError;
        _ArContainsCode = archive._ArContainsCode;
        _ArContainsMap = archive._ArContainsMap;
        _ArRequiresLocalizationGather = archive._ArRequiresLocalizationGather;
        _ArForceByteSwapping = archive._ArForceByteSwapping;
        _ArIgnoreArchetypeRef = archive._ArIgnoreArchetypeRef;
        _ArNoDelta = archive._ArNoDelta;
        _ArIgnoreOuterRef = archive._ArIgnoreOuterRef;
        _ArIgnoreClassGeneratedByRef = archive._ArIgnoreClassGeneratedByRef;
        _ArIgnoreClassRef = archive._ArIgnoreClassRef;
        _ArAllowLazyLoading = archive._ArAllowLazyLoading;
        _ArIsObjectReferenceCollector = archive._ArIsObjectReferenceCollector;
        _ArIsModifyingWeakAndStrongReferences = archive._ArIsModifyingWeakAndStrongReferences;
        _ArIsCountingMemory = archive._ArIsCountingMemory;
        _ArShouldSkipBulkData = archive._ArShouldSkipBulkData;
        _ArIsFilterEditorOnly = archive._ArIsFilterEditorOnly;
        _ArIsSaveGame = archive._ArIsSaveGame;
        _ArIsNetArchive = archive._ArIsNetArchive;
        _ArUseCustomPropertyList = archive._ArUseCustomPropertyList;
        _ArSerializingDefaults = archive._ArSerializingDefaults;
        _ArPortFlags = archive._ArPortFlags;
        _ArMaxSerializeSize = archive._ArMaxSerializeSize;
        _ArUE4Ver = archive._ArUE4Ver;
        _ArLicenseeUE4Ver = archive._ArLicenseeUE4Ver;
        _ArEngineVer = archive._ArEngineVer;
        _ArEngineNetVer = archive._ArEngineNetVer;
        _ArGameNetVer = archive._ArGameNetVer;
    }
    
    public virtual bool ReadBit() => throw new NotImplementedException();
    
    public virtual unsafe byte ReadByte() {
        byte value;
        Serialize(&value, 1);
        return value;
    }
    
    public virtual unsafe void WriteByte(byte value) => Serialize(&value, 1);

    public virtual unsafe byte[] ReadBytes(long amount) {
        byte[] value = new byte[amount];

        fixed (byte* pValue = value) Serialize(pValue, amount);

        return value;
    }

    public virtual unsafe ushort ReadUInt16() {
        ushort value;
        ByteOrderSerialize(&value, sizeof(ushort));
        return value;
    }

    public virtual unsafe short ReadInt16() {
        short value;
        ByteOrderSerialize(&value, sizeof(short));
        return value;
    }

    public virtual unsafe uint ReadUInt32() {
        uint value;
        ByteOrderSerialize(&value, sizeof(uint));
        return value;
    }

    public virtual unsafe void WriteUInt32(uint value) => ByteOrderSerialize(&value, sizeof(uint));

    public virtual unsafe int ReadInt32() {
        int value;
        ByteOrderSerialize(&value, sizeof(int));
        return value;
    }

    public unsafe void WriteInt32(int value) => ByteOrderSerialize((uint*)&value, sizeof(uint));

    public virtual unsafe uint ReadInt(uint max) => throw new NotImplementedException();

    public virtual unsafe uint ReadUInt32Packed() {
        uint value;
        SerializeIntPacked(&value);
        return value;
    }

    public virtual unsafe ulong ReadUInt64() {
        ulong value;
        ByteOrderSerialize(&value, sizeof(ulong));
        return value;
    }

    public virtual unsafe long ReadInt64() {
        long value;
        ByteOrderSerialize(&value, sizeof(long));
        return value;
    }

    public virtual unsafe float ReadFloat() {
        float value;
        ByteOrderSerialize(&value, sizeof(float));
        return value;
    }

    public virtual unsafe double ReadDouble() {
        double value;
        ByteOrderSerialize(&value, sizeof(double));
        return value;
    }

    public unsafe void WriteFloat(float value) => ByteOrderSerialize((ulong*)&value, sizeof(ulong)); // is this correct ?

    public unsafe void WriteDouble(double value) => ByteOrderSerialize((ulong*)&value, sizeof(ulong));

    public string ReadString() => FString.Deserialize(this);
    
    public void WriteString(string value) => FString.Serialize(this, value);

    public unsafe void Serialize(Span<byte> value, long num) {
        fixed (byte* pBuffer = value) Serialize(pBuffer, num);
    }

    public unsafe void Serialize(byte[] value, long num) {
        fixed (byte* pBuffer = value) Serialize(pBuffer, num);
    }

    public virtual unsafe void Serialize(void* value, long num) {
        throw new NotImplementedException();
    }

    public unsafe void SerializeBits(Span<byte> value, long lengthBits) {
        fixed (byte* pBuffer = value) SerializeBits(pBuffer, lengthBits);
    }

    public unsafe void SerializeBits(byte[] value, long lengthBits) {
        fixed (byte* pBuffer = value) SerializeBits(pBuffer, lengthBits);
    }

    public virtual unsafe void SerializeBits(void* value, long lengthBits) {
        Serialize(value, (lengthBits + 7) / 8);

        if (IsLoading() && (lengthBits % 8) != 0) ((byte*) value)[lengthBits / 8] &= (byte) ((1 << (int) (lengthBits & 7)) - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void SerializeInt(uint value, uint valueMax) => SerializeInt(&value, valueMax);

    public virtual unsafe void SerializeInt(uint* value, uint valueMax) => throw new NotImplementedException();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void SerializeIntPacked(uint value) => SerializeIntPacked(&value);

    public virtual unsafe void SerializeIntPacked(uint* value) {
        if (IsLoading()) {
            byte count = 0;
            byte more = 1;

            while (more != 0) {
                byte nextByte;
                Serialize(&nextByte, 1);                     // Read next byte

                more = (byte)(nextByte & 1);                 // Check 1 bit to see if theres more after this
                nextByte = (byte)(nextByte >> 1);            // Shift to get actual 7 bit value
                *value += (uint)nextByte << (7 * count++);   // Add to total value
            }
        } else throw new NotImplementedException();
    }
    
    public virtual string GetArchiveName() => "FArchive";

    public virtual long Tell() => -1;

    public virtual long TotalSize() => -1;

    public virtual bool AtEnd() {
        var pos = Tell();

        return pos != -1 && pos >= TotalSize();
    }

    public virtual void Seek(long inPos) => throw new NotImplementedException();

    public virtual void Flush() => throw new NotImplementedException();

    public virtual bool Close() => !_ArIsError;

    public virtual bool GetError() => _ArIsError;

    public void SetError() => _ArIsError = true;

    public bool IsByteSwapping() => BitConverter.IsLittleEndian ? _ArForceByteSwapping : _ArIsPersistent;

    public unsafe void ByteSwap(void* v, int length) {
        byte* ptr = (byte*) v;
        int top = length - 1;
        int bottom = 0;

        while (bottom < top) {
            var aPos = top--;
            var bPos = bottom++;

            (ptr[aPos], ptr[bPos]) = (ptr[bPos], ptr[aPos]);
        }
    }

    public unsafe void ByteOrderSerialize(void* v, int length) {
        if (!IsByteSwapping()) {
            Serialize(v, length);
            return;
        }

        SerializeByteOrderSwapped(v, length);
    }

    private unsafe void SerializeByteOrderSwapped(void* v, int length) {
        if (IsLoading()) {
            Serialize(v, length);
            ByteSwap(v, length);
        } else {
            ByteSwap(v, length);
            Serialize(v, length);
            ByteSwap(v, length);
        }
    }

    public void ThisContainsCode() => _ArContainsCode = true;

    public void ThisContainsMap() => _ArContainsMap = true;

    public void ThisRequiresLocalizationGather() => _ArRequiresLocalizationGather = true;

    public void StartSerializingDefaults() => _ArSerializingDefaults++;

    public void StopSerializingDefaults() => _ArSerializingDefaults--;

    public int UE4Ver() => _ArUE4Ver;

    public int LicenseeUE4Ver() => _ArLicenseeUE4Ver;

    public FEngineVersion EngineVer() => _ArEngineVer;

    public EEngineNetworkVersionHistory EngineNetVer() => _ArEngineNetVer;

    public uint GameNetVer() => _ArGameNetVer;

    public bool IsLoading() => _ArIsLoading;

    public bool IsSaving() => _ArIsSaving;

    // Misses FPlatformProperties::HasEditorOnlyData.
    public bool IsTransacting() => _ArIsTransacting;

    public bool IsTextFormat() => _ArIsTextFormat;

    public bool WantBinaryPropertySerialization() => _ArWantBinaryPropertySerialization;

    public bool IsForcingUnicode() => _ArForceUnicode;

    public bool IsPersistent() => _ArIsPersistent;

    public bool IsError() => _ArIsError;

    public bool IsCriticalError() => _ArIsCriticalError;

    public bool ContainsCode() => _ArContainsCode;

    public bool ContainsMap() => _ArContainsMap;

    public bool RequiresLocalizationGather() => _ArRequiresLocalizationGather;

    public bool ForceByteSwapping() => _ArForceByteSwapping;

    public bool IsSerializingDefaults() => _ArSerializingDefaults > 0;

    public bool IsIgnoringArchetypeRef() => _ArIgnoreArchetypeRef;

    public bool DoDelta() => !_ArNoDelta;

    public bool IsIgnoringOuterRef() => _ArIgnoreOuterRef;

    public bool IsIgnoringClassGeneratedByRef() => _ArIgnoreClassGeneratedByRef;

    public bool IsIgnoringClassRef() => _ArIgnoreClassRef;

    public bool IsAllowingLazyLoading() => _ArAllowLazyLoading;

    public bool IsObjectReferenceCollector() => _ArIsObjectReferenceCollector;

    public bool IsModifyingWeakAndStrongReferences() => _ArIsModifyingWeakAndStrongReferences;

    public bool IsCountingMemory() => _ArIsCountingMemory;

    public uint GetPortFlags() => _ArPortFlags;

    public bool HasAnyPortFlags(uint flags) => (_ArPortFlags & flags) != 0;

    public bool HasAllPortFlags(uint flags) => (_ArPortFlags & flags) == flags;

    public bool ShouldSkipBulkData() => _ArShouldSkipBulkData;

    public long GetMaxSerializeSize() => _ArMaxSerializeSize;

    public void SetUE4Ver(int inVer) => _ArUE4Ver = inVer;

    public void SetLicenseeUE4Ver(int inVer) => _ArLicenseeUE4Ver = inVer;

    public void SetEngineVer(FEngineVersion inVer) => _ArEngineVer = inVer;

    public void SetEngineNetVer(EEngineNetworkVersionHistory inEngineNetVer) => _ArEngineNetVer = inEngineNetVer;

    public void SetGameNetVer(uint inGameNetVer) => _ArGameNetVer = inGameNetVer;

    public void SetForceUnicode(bool enabled) => _ArForceUnicode = enabled;

    public virtual bool IsFilterEditorOnly() => _ArIsFilterEditorOnly;

    public virtual void SetFilterEditorOnly(bool inFilterEditorOnly) => _ArIsFilterEditorOnly = inFilterEditorOnly;

    public virtual void Reset() {}

    public abstract void Dispose();
}