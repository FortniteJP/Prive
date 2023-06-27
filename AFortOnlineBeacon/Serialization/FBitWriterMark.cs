namespace AFortOnlineBeacon.Serialization;

public struct FBitWriterMark {
    private bool _Overflowed;
    private long _Num;

    public FBitWriterMark() {
        _Overflowed = false;
        _Num = 0;
    }

    public FBitWriterMark(FBitWriter writer) {
        _Overflowed = writer.IsError();
        _Num = writer.GetNumBits();
    }

    public long GetPos() => _Num;
    
    public void Pop(FBitReader reader) => reader.Pos = _Num;

    public void Init(FBitWriter writer) {
        _Num = writer.Num;
        _Overflowed = writer.IsError();
    }

    public void Reset() {
        _Overflowed = false;
        _Num = 0;
    }

    public void PopWithoutClear(FBitWriter writer) => writer.Num = _Num;
}