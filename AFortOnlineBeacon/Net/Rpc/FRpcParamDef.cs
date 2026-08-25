namespace AFortOnlineBeacon.Net.Rpc;

/// <summary>One parameter of an RPC, in the UFunction's own declaration order.</summary>
public class FRpcParamDef {
    public FRpcParamDef(string name, ERpcParamKind kind) {
        Name = name;
        Kind = kind;
    }

    public string Name { get; }
    public ERpcParamKind Kind { get; }
}
