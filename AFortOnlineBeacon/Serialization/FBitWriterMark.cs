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

    /// <summary>
    ///     FBitWriterMark::Pop (BitWriter.cpp) - rewind a writer to this mark AND undo everything
    ///     written past it, including the error flag.
    ///
    ///     Three things, and all three matter:
    ///
    ///       * the partial byte AT the mark is masked, so bits written into its high end are gone;
    ///       * every whole byte after it is zeroed - real UE's FBitWriter backs onto a zeroed
    ///         TArray and its bit writes only OR in 1 bits, and this port rents from an ArrayPool
    ///         that does NOT zero, so leaving stale bytes behind is how a later write leaks garbage;
    ///       * the ERROR FLAG is restored to what it was at the mark. That is the whole point at the
    ///         one call site that needs it: UPackageMapClient.ExportNetGUID writes an object,
    ///         discovers the bunch overflowed, and has to hand back a bunch that is both the right
    ///         length and no longer marked failed so it can be finished and sent.
    ///
    ///     PopWithoutClear above does only the rewind, which is right for patching a header in
    ///     place and wrong for undoing a failed write.
    /// </summary>
    public void Pop(FBitWriter writer) {
        if ((_Num & 7) != 0) writer.Data[_Num >> 3] &= FBitUtil.GMask[_Num & 7];

        var start = (int) ((_Num + 7) >> 3);
        var end = (int) ((writer.Num + 7) >> 3);
        if (end > start) Array.Clear(writer.Data, start, end - start);

        writer._ArIsError = _Overflowed;
        writer.Num = _Num;
    }
}