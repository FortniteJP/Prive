namespace AFortOnlineBeacon.Net.Rpc;

/// <summary>
///     One server-RPC's declared parameter list and the handler to run once they're decoded.
///     <paramref name="values"/> is positional, matching <see cref="Params"/>: an entry is null if
///     its presence bit was 0 on the wire (real UE leaves the parameter at its zero-constructed
///     default in that case - callers should treat null the same way).
/// </summary>
public class FRpcDef {
    public FRpcDef(string name, FRpcParamDef[] paramDefs, Action<AActor, object?[]> invoke) {
        Name = name;
        Params = paramDefs;
        Invoke = invoke;
    }

    public string Name { get; }
    public FRpcParamDef[] Params { get; }
    public Action<AActor, object?[]> Invoke { get; }
}
