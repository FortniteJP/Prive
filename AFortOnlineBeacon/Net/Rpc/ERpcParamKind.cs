namespace AFortOnlineBeacon.Net.Rpc;

/// <summary>The wire encodings this minimal RPC reader knows how to decode.</summary>
public enum ERpcParamKind {
    Bool,
    Byte,
    UInt32,
    Float,
    Vector,
    VectorQuantize10,
    VectorQuantize100,
    Rotator,
    String
}
