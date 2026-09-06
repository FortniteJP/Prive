namespace AFortOnlineBeacon.Net.Rpc;

/// <summary>
///     One server-RPC's declared parameter list and the handler to run once they're decoded.
///     <paramref name="values"/> is positional, matching <see cref="Params"/>: an entry is null if
///     its presence bit was 0 on the wire (real UE leaves the parameter at its zero-constructed
///     default in that case - callers should treat null the same way).
/// </summary>
public class FRpcDef {
    public FRpcDef(string name, FRpcParamDef[] paramDefs, Action<AActor, object?[]> invoke,
                   bool expectsFullDecode = false, bool dumpRawAlways = false) {
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
        ExpectsFullDecode = expectsFullDecode;
        DumpRawAlways = dumpRawAlways;
    }

    /// <summary>
    ///     Capture this RPC's raw bytes on every arrival, without RPC_DUMP being set.
    ///
    ///     For an RPC that is KNOWN to arrive but whose parameter layout has not been pinned down
    ///     yet. RPC_DUMP already covers "I suspect this one", and a failed decode already dumps
    ///     itself - this covers the case in between, where there is nothing to fail because no
    ///     layout has been declared, and the bytes are the only thing that can settle it. Remove the
    ///     flag once the layout is real: from then on the leftover check is the better signal.
    /// </summary>
    public bool DumpRawAlways { get; }

    public string Name { get; }
    public FRpcParamDef[] Params { get; }
    public Action<AActor, object?[]> Invoke { get; }

    /// <summary>
    ///     True when <see cref="Params"/> is claimed to describe the WHOLE call, so that anything
    ///     left over in the field is a decode bug worth hearing about.
    ///
    ///     Most definitions here deliberately stop early (ServerMove reads a timestamp and abandons
    ///     a tail containing types this reader has no support for), and the field's own declared bit
    ///     count resynchronises the bunch either way - so leftover bits are normally not interesting.
    ///     For the ability RPCs they are the ONLY check available on a layout derived on paper: the
    ///     wire carries no per-member framing, so a target data decode that is off by a few bits
    ///     produces plausible numbers rather than an error. The leftover count says how far off.
    /// </summary>
    public bool ExpectsFullDecode { get; }
}
