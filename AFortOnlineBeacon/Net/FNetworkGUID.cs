namespace AFortOnlineBeacon.Net;

/// <summary>
///     A globally unique identifier for network related use. Static (odd) GUIDs refer to objects the
///     client can resolve by path (classes, CDOs, packages). Dynamic (even, nonzero) GUIDs refer to
///     objects spawned at runtime, which the client re-creates rather than looks up.
/// </summary>
public class FNetworkGUID : IEquatable<FNetworkGUID> {
    public uint Value;

    public FNetworkGUID() {}

    public FNetworkGUID(uint value) => Value = value;

    public bool IsValid() => Value > 0;

    public bool IsStatic() => (Value & 1) != 0;

    public bool IsDynamic() => Value > 0 && (Value & 1) == 0;

    public bool IsDefault() => Value == 1;

    public static FNetworkGUID GetDefault() => new FNetworkGUID(1);

    public unsafe void NetSerialize(FArchive ar) {
        fixed (uint* p = &Value) ar.SerializeIntPacked(p);
    }

    public bool Equals(FNetworkGUID? other) => other != null && Value == other.Value;

    public override bool Equals(object? obj) => Equals(obj as FNetworkGUID);

    public override int GetHashCode() => (int) Value;

    public override string ToString() => Value.ToString();
}
