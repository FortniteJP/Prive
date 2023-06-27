namespace AFortOnlineBeacon.Core.Names;

public readonly struct FNameEntryId {
    public FNameEntryId() => Value = 0;
    
    public FNameEntryId(uint value) => Value = value;
    
    public readonly uint Value;
    
    public static bool operator ==(FNameEntryId left, EName right) => left == FNamePool.Find(right);
    public static bool operator !=(FNameEntryId left, EName right) => !(left == right);
    public static bool operator ==(FNameEntryId left, FNameEntryId right) => left.Value == right.Value;
    public static bool operator !=(FNameEntryId left, FNameEntryId right) => left.Value != right.Value;
    public bool Equals(FNameEntryId other) => this == other;
    public override bool Equals(object? obj) => obj is FNameEntryId other && Equals(other);
    public override int GetHashCode() => (int)Value;
}