namespace AFortOnlineBeacon.Net.Rpc;

/// <summary>One parameter of an RPC, in the UFunction's own declaration order.</summary>
public class FRpcParamDef {
    public FRpcParamDef(string name, ERpcParamKind kind) {
        Name = name;
        Kind = kind;
    }

    /// <summary>For <see cref="ERpcParamKind.Enum" />: CeilLogTwo(the enum's max value).</summary>
    public FRpcParamDef(string name, ERpcParamKind kind, int bits) : this(name, kind) => Bits = bits;

    public int Bits { get; }

    public string Name { get; }
    public ERpcParamKind Kind { get; }
}
