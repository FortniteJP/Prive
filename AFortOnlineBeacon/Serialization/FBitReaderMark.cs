namespace AFortOnlineBeacon.Serialization;

public readonly struct FBitReaderMark {
    private readonly long _Pos;
    
    public FBitReaderMark(FBitReader reader) => _Pos = reader.Pos;

    public long GetPos() => _Pos;

    public void Pop(FBitReader reader) => reader.Pos = _Pos;
}