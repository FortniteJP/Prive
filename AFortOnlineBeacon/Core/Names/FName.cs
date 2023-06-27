using System.Runtime.CompilerServices;

namespace AFortOnlineBeacon.Core.Names;

public readonly struct FName {
    public FName() : this(EName.None, FNameHelper.NAME_NO_NUMBER_INTERNAL) {}
    
    public FName(EName hardcodedName) : this(hardcodedName, FNameHelper.NAME_NO_NUMBER_INTERNAL) {}
    
    public FName(EName hardcodedName, int number) {
        Index = FNamePool.Find(hardcodedName);
        Number = (uint)number;
    }

    public FName(string value, EFindName findType = EFindName.FNAME_Add) {
        var name = FNameHelper.MakeDetectNumber(value, findType);

        Index = name.Index;
        Number = name.Number;
    }

    public FName(string value, int number, EFindName findType = EFindName.FNAME_Add) {
        var name = FNameHelper.MakeWithNumber(value, findType, number);

        Index = name.Index;
        Number = name.Number;
    }

    public FName(FNameEntryId index, int number) {
        Index = index;
        Number = (uint)number;
    }

    public FNameEntryId Index { get; }
    
    public uint Number { get; }
    
    public static implicit operator FName(EName name) => new(name);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetNumber() => (int)Number;

    public string GetPlainNameString() => FNamePool.Resolve(Index);
    
    public EName? ToEName() => FNamePool.FindEName(Index);

    public override string ToString() {
        var name = GetPlainNameString();
        
        if (Number == FNameHelper.NAME_NO_NUMBER_INTERNAL) return name;

        return $"{name}_{FNameHelper.NAME_INTERNAL_TO_EXTERNAL(GetNumber())}";
    }
    
    public static bool operator ==(FName left, EName right) => (left.Index == right) & (left.GetNumber() == 0);
    public static bool operator !=(FName left, EName right) => (left.Index != right) | (left.GetNumber() != 0);
    public static bool operator ==(FName left, FName right) => (left.Index == right.Index) & (left.GetNumber() == right.GetNumber());
    public static bool operator !=(FName left, FName right) => !(left == right);
    public bool Equals(FName other) => this == other;
    public override bool Equals(object? obj) => obj is FName other && Equals(other);
    public override int GetHashCode() => unchecked (Index.GetHashCode() * 397) ^ (int)Number;
}