using System.Buffers;
using System.Collections;

namespace AFortOnlineBeacon.Serialization;

// Real UE's FBitWriter backs onto a zero-initialized TArray (Buffer.AddZeroed), so its bit-write
// helpers only ever OR in 1 bits and rely on 0 bits already being there. This port rents from
// ArrayPool<byte> instead - Rent() does NOT zero the array, so every single-bit write below must
// explicitly clear as well as set, or stale bits from a previous rental of the same backing array
// leak through as garbage.
public class FBitWriter : FArchive {
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create();

    private readonly bool _UsesPool;
    
    public FBitWriter() {
        Num = 0;
        Max = 0;
        Data = Array.Empty<byte>();
        AllowResize = false;
        AllowOverflow = false;

        _UsesPool = false;
        _ArIsSaving = true;
        _ArIsPersistent = true;
        _ArIsNetArchive = true;
    }
    
    public FBitWriter(long inMaxBits, bool inAllowResize = false, bool usePool = true) {
        Num = 0;
        Max = inMaxBits;
        AllowResize = inAllowResize;

        var byteCount = (int)((inMaxBits + 7) >> 3);
        if (usePool) {
            Data = Pool.Rent(byteCount);
            _UsesPool = true;
        } else {
            Data = new byte[byteCount];
            _UsesPool = false;
        }

        _ArIsSaving = true;
        _ArIsPersistent = true;
        _ArIsNetArchive = true;
    }

    public FBitWriter(FBitWriter writer) : base(writer) {
        Num = writer.Num;
        Max = writer.Max;
        AllowOverflow = writer.AllowOverflow;
        AllowResize = writer.AllowResize;

        if (writer.Data.Length > 0) {
            _UsesPool = writer._UsesPool;

            // Sized from Max (the logical cap this copy inherits) rather than from the source's
            // physical array, so the copy keeps the invariant AllowAppend relies on: the buffer is
            // never bigger than Max implies. Copying the source's physical length instead would
            // hand the copy a buffer its own Max cannot account for - and would drag along the
            // slack past Num, which on a pooled array is some other writer's leftovers.
            var usedBytes = (int) ((writer.Num + 7) >> 3);
            var byteCount = Math.Max(usedBytes, (int) ((writer.Max + 7) >> 3));

            Data = _UsesPool ? Pool.Rent(byteCount) : new byte[byteCount];

            Buffer.BlockCopy(writer.Data, 0, Data, 0, usedBytes);
        } else Data = Array.Empty<byte>();
    }

    public byte[] Data { get; set; }
    internal long Num { get; set; }
    private long Max { get; set; }
    private bool AllowResize { get; set; }
    private bool AllowOverflow { get; set; }

    /// <summary>
    ///     FBitWriter::SetAllowResize. FNetBitWriter - and so every FOutBunch - is created RESIZABLE,
    ///     which is what real UE does too: an oversized content bunch is meant to grow and then be
    ///     split into partial bunches by UChannel::SendBunch.
    ///
    ///     A GUID-EXPORT BUNCH IS THE EXCEPTION AND MUST TURN THIS OFF, because nothing ever splits
    ///     one - UPackageMapClient::ExportNetGUID handles a full bunch itself, by closing it and
    ///     starting another. Real UE calls SetAllowResize(false) on it explicitly for that reason;
    ///     this port inherited the resizable default and not the two calls that override it, and the
    ///     consequences were invisible until an export batch got big enough to matter:
    ///
    ///         ExportNetGUIDHeader: finished export bunch - 14 guid(s), 9297/7476 bits
    ///
    ///     A 9297-bit bunch never reports IsError(), so the overflow-and-spill path in ExportNetGUID
    ///     could not fire. Worse, the bunch header's length is written as
    ///     WriteIntWrapped(GetNumBits(), MaxPacket * 8) - THIRTEEN BITS, which cannot hold 9297 at
    ///     all. The client therefore reads a length that stops well short of the data, treats the
    ///     rest of the packet as more bunches, and every symptom after that is garbage: a nonsense
    ///     NumGUIDsInBunch, a bunch claiming more bits than the packet holds, an impossible channel
    ///     index. One cause, several unrelated-looking errors.
    ///
    ///     Turned off with <see cref="SetAllowResize"/>, which existed all along and which nothing
    ///     had ever called.
    /// </summary>
    public void SetAllowOverflow(bool allow) => AllowOverflow = allow;

    public byte[] GetData() {
        if (IsError()) {
            // Logger.Error("Retrieved data from a BitWriter that had an error");    
        }
        
        return Data;
    }

    public long GetNumBytes() => (Num + 7) >> 3;
    
    public long GetNumBits() => Num;

    public long GetMaxBits() => Max;

    public override unsafe void Serialize(void* src, long lengthBytes) {
        var lengthBits = lengthBytes * 8;
        if (AllowAppend(lengthBits)) {
            fixed (byte* pBuffer = Data) FBitUtil.AppBitsCpy(pBuffer, (int)Num, (byte*)src, 0, (int)lengthBits);

            Num += lengthBits;
        } else SetOverflowed(lengthBits);
        //ShowStack();
    }

    public override unsafe void SerializeBits(void* value, long lengthBits) {
        if (AllowAppend(lengthBits)) {
            if (lengthBits == 1) {
                if ((((byte*)value)[0] & 0x01) != 0) Data[Num >> 3] |= FBitUtil.GShift[Num & 7];
                else Data[Num >> 3] &= (byte) ~FBitUtil.GShift[Num & 7];

                Num++;
            } else {
                fixed (byte* pBuffer = Data) {
                    FBitUtil.AppBitsCpy(pBuffer, (int)Num, (byte*)value, 0, (int)lengthBits);
                    Num += lengthBits;
                }
            }
        } else SetOverflowed(lengthBits);
    }

    public void SerializeBits(BitArray bits, int lengthBits) {
        if (AllowAppend(lengthBits)) {
            for (var i = 0; i < lengthBits; i++) WriteBit((byte)(bits.Get(i) ? 1 : 0));
        } else SetOverflowed(lengthBits);
    }

    public override unsafe void SerializeInt(uint* value, uint valueMax) {
        if (valueMax < 2) throw new NotSupportedException();

        var lengthBits = (int) Math.Ceiling(Math.Log2(valueMax));
        var writeValue = *value;
        if (writeValue >= valueMax) {
            // Logger.Error("SerializeInt(): Value out of bounds (Value: {Value}, ValueMax: {ValueMax})", writeValue, valueMax);

            writeValue = valueMax - 1;
        }

        if (AllowAppend(lengthBits)) {
            uint newValue = 0;
            var localNum = Num;

            for (uint mask = 1; (newValue + mask) < valueMax && (mask != 0); mask *= 2, localNum++) {
                if ((writeValue & mask) != 0) {
                    Data[localNum >> 3] |= FBitUtil.GShift[localNum & 7];
                    newValue += mask;
                } else {
                    Data[localNum >> 3] &= (byte) ~FBitUtil.GShift[localNum & 7];
                }
            }

            Num = localNum;
        } else SetOverflowed(lengthBits);
    }

    public override unsafe void SerializeIntPacked(uint* inValue) {
        uint value = *inValue;
        Span<uint> bytesAsWords = stackalloc uint[5];
        uint byteCount = 0;

        for (uint It = 0; (It == 0) | (value != 0); ++It, value = value >> 7) {
            if ((value & ~0x7F) != 0) bytesAsWords[(int)byteCount++] = ((value & 0x7FU) << 1) | 1;
            else bytesAsWords[(int)byteCount++] = ((value & 0x7FU) << 1);
        }

        var lengthBits = byteCount * 8;
        if (!AllowAppend(lengthBits)) {
            SetOverflowed(lengthBits);
            return;
        }
        
        int BitCountUsedInByte = (int)(Num & 7);
        int BitCountLeftInByte = (int)(8 - (Num & 7));
        byte DestMaskByte0 = (byte)((1U << BitCountUsedInByte) - 1U);
        byte DestMaskByte1 = (byte)(0xFF ^ DestMaskByte0);
        bool bStraddlesTwoBytes = (BitCountUsedInByte != 0);

        fixed (byte* pData = Data) {
            var Dest = pData + (Num >> 3);
            
            Num += lengthBits;
            for (var ByteIt = 0; ByteIt != byteCount; ++ByteIt) {
                uint ByteAsWord = bytesAsWords[ByteIt];

                *Dest = (byte)((*Dest & DestMaskByte0) | (byte)(ByteAsWord << BitCountUsedInByte));
                ++Dest;
                if (bStraddlesTwoBytes) *Dest = (byte)((*Dest & DestMaskByte1) | (byte)(ByteAsWord >> BitCountLeftInByte));
            }
        }
    }

    public void WriteIntWrapped(uint value, uint valueMax) {
        var lengthBits = (int) Math.Ceiling(Math.Log2(valueMax));

        if (AllowAppend(lengthBits)) {
            uint newValue = 0;

            for (uint mask = 1; newValue + mask < valueMax && (mask != 0); mask *= 2, Num++) {
                if ((value & mask) != 0) {
                    Data[Num >> 3] |= FBitUtil.GShift[Num & 7];
                    newValue += mask;
                } else {
                    Data[Num >> 3] &= (byte) ~FBitUtil.GShift[Num & 7];
                }
            }
        } else SetOverflowed(lengthBits);
    }

    public void WriteBit(bool value) {
        if (value) WriteBit(1);
        else WriteBit(0);
    }

    public void WriteBit(byte value) {
        if (AllowAppend(1)) {
            if (value != 0) Data[Num >> 3] |= FBitUtil.GShift[Num & 7];
            else Data[Num >> 3] &= (byte) ~FBitUtil.GShift[Num & 7];

            Num++;
        } else SetOverflowed(1);
    }

    protected void SetOverflowed(long lengthBits) {
        if (!AllowOverflow) {
            // Logger.Error("FBitWriter overflowed (WriteLen: {Len}, Remaining: {Remaining}, Max: {Max})", lengthBits, (Max - Num), Max);
        }
        
        SetError();
    }

    public bool AllowAppend(long lengthBits) {
        if (Num + lengthBits > Max) {
            if (AllowResize) {
                // Resize our buffer. The common case for resizing bitwriters is hitting the max and continuing to add a lot of small segments of data
                // Though we could just allow the TArray buffer to handle the slack and resizing, we would still constantly hit the FBitWriter's max
                // and cause this block to be executed, as well as constantly zeroing out memory inside AddZeroes (though the memory would be allocated
                // in chunks).
                Max = Math.Max(Max << 1, Num + lengthBits);

                // Never shrink. Max is the LOGICAL cap and Data.Length the PHYSICAL one, and
                // they are not the same number: ArrayPool rounds every rental up to a bucket
                // size, so a writer created with 64 bits is handed 16 bytes, not 8. Sizing the
                // new buffer from Max alone can therefore ask for LESS than we already hold,
                // and the copy below then has nowhere to put it - which is exactly how this
                // threw "Offset and length were out of bounds for the array".
                var byteMax = Math.Max((Max + 7) >> 3, Data.Length);

                // Copy only the bytes actually in use. The old code copied Data.Length, i.e.
                // the whole physical array including the slack past Num - bytes that belong to
                // nothing, and on a pooled array are another writer's leftovers.
                var usedBytes = (int) ((Num + 7) >> 3);
                
                if (!_UsesPool) {
                    var dataTemp = Data;
                    Array.Resize(ref dataTemp, (int) byteMax);
                    Data = dataTemp;
                } else {
                    var newData = Pool.Rent((int) byteMax);

                    // The condition the old code crashed on. It should now be unreachable -
                    // byteMax is floored at Data.Length above - so if it ever fires, something
                    // handed this writer a buffer inconsistent with its Max, and the stack is
                    // the only way to find out who.
                    if (newData.Length < Data.Length) {
                        Console.WriteLine($"FBitWriter.AllowAppend: shrinking buffer! Num={Num} Max={Max} " +
                                          $"Data.Length={Data.Length} byteMax={byteMax} newData.Length={newData.Length}" +
                                          Environment.NewLine + Environment.StackTrace);
                    }

                    Buffer.BlockCopy(Data, 0, newData, 0, usedBytes);
                    Pool.Return(Data, true);
                    Data = newData;
                }
                
                return true;
            } else return false;
        }

        return true;
    }

    public void SetAllowResize(bool newResize) => AllowResize = newResize;

    public override void Reset() {
        base.Reset();
        Num = 0;
        Array.Clear(Data);
        _ArIsSaving = true;
        _ArIsPersistent = true;
        _ArIsNetArchive = true;
    }
    
    public override void Dispose() {
        if (_UsesPool) Pool.Return(Data, true);
    }
}