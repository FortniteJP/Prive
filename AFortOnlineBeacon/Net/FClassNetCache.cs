namespace AFortOnlineBeacon.Net;

/// <summary>Describes one net-replicated field (property or RPC) within a class's ClassNetCache.</summary>
public class FFieldNetCache {
    public string Name { get; }
    public int FieldNetIndex { get; }

    public FFieldNetCache(string name, int fieldNetIndex) {
        Name = name;
        FieldNetIndex = fieldNetIndex;
    }
}

/// <summary>
///     Simplified, read-only port of FClassNetCache (see CoreNet.h/.cpp). Real UE builds this
///     from UClass::NetFields - all CPF_Net properties and FUNC_Net functions declared directly
///     on a class, in UClass::Children/TFieldIterator order. That order does not follow a simple
///     rule from source declaration order alone (an earlier attempt to hand-derive it that way -
///     "reversed declaration order" - didn't match a live dump: e.g. AActor's own properties came
///     out in forward declaration order, while APlayerController's functions came out in neither
///     forward nor simply-reversed order). See NativeClassNetCache, which supplies this order from
///     a live reflection dump instead of a guess.
/// </summary>
public class FClassNetCache {
    public FClassNetCache? Super { get; }
    public int FieldsBase { get; }
    private readonly FFieldNetCache[] _fields;

    /// <param name="ownFields">This class's own net fields, in real wire (NetFields) order.</param>
    public FClassNetCache(FClassNetCache? super, string[] ownFields) {
        Super = super;
        FieldsBase = super?.GetMaxIndex() ?? 0;
        _fields = new FFieldNetCache[ownFields.Length];
        for (var i = 0; i < ownFields.Length; i++) _fields[i] = new FFieldNetCache(ownFields[i], FieldsBase + i);
    }

    public int GetMaxIndex() => FieldsBase + _fields.Length;

    public FFieldNetCache? GetFromIndex(int index) {
        for (var c = this; c != null; c = c.Super) {
            if (index >= c.FieldsBase && index < c.FieldsBase + c._fields.Length) return c._fields[index - c.FieldsBase];
        }

        return null;
    }
}
