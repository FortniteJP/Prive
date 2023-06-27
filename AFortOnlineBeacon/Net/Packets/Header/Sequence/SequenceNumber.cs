namespace AFortOnlineBeacon.Net.Packets.Header.Sequence;

public readonly struct SequenceNumber {
    public const int NumBits = FNetPacketNotify.SequenceNumberBits;

    public const int SeqNumberBits = NumBits;
    public const ushort SeqNumberCount = 1 << NumBits;
    public const ushort SeqNumberHalf = 1 << (NumBits - 1);
    public const ushort SeqNumberMax = SeqNumberCount - 1;
    public const ushort SeqNumberMask = SeqNumberMax;
    
    public SequenceNumber(ushort value) => Value = (ushort)(value & SeqNumberMask);
    
    public ushort Value { get; }

    public bool Greater(SequenceNumber other) => (Value != other.Value) && (((Value - other.Value) & SeqNumberMask) < SeqNumberHalf);
    public bool GreaterEq(SequenceNumber other) => ((Value - other.Value) & SeqNumberMask) < SeqNumberHalf;
    public bool Equals(SequenceNumber other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is SequenceNumber other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();

    public static int Diff(SequenceNumber a, SequenceNumber b) {
        const int shiftValue = sizeof(int) * 8 - NumBits;

        var valueA = a.Value;
        var valueB = b.Value;

        return ((valueA - valueB) << shiftValue) >> shiftValue;
    }

    public SequenceNumber IncrementAndGet() => new SequenceNumber((ushort)(Value + 1));

    public readonly struct Difference {
        public Difference(int value) {
            Value = value;
        }
        
        public int Value { get; }
    }
}