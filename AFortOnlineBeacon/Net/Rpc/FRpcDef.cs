namespace AFortOnlineBeacon.Net.Rpc;

/// <summary>
///     One server-RPC's declared parameter list and the handler to run once they're decoded.
///     <paramref name="values"/> is positional, matching <see cref="Params"/>: an entry is null if
///     its presence bit was 0 on the wire (real UE leaves the parameter at its zero-constructed
///     default in that case - callers should treat null the same way).
/// </summary>
public class FRpcDef {
    public FRpcDef(string name, FRpcParamDef[] paramDefs, Action<AActor, object?[]> invoke) {
        // These tables are static readonly dictionaries built from other static readonly fields, and
        // C# runs static field initializers in DECLARATION order - so a shared parameter array
        // declared BELOW the dictionary that uses it is still null when the dictionary is built.
        // That surfaced as a bare NullReferenceException inside FRpcReader on the first call of the
        // affected RPC, minutes into a session, naming neither the RPC nor the real cause.
        Params = paramDefs ?? throw new ArgumentNullException(nameof(paramDefs),
            $"FRpcDef '{name}' was given a null parameter list - a shared FRpcParamDef[] is most " +
            "likely declared after the dictionary that references it.");

        Name = name;
        Invoke = invoke;
    }

    public string Name { get; }
    public FRpcParamDef[] Params { get; }
    public Action<AActor, object?[]> Invoke { get; }
}
