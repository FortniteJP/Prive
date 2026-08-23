using AFortOnlineBeacon.Serialization;

namespace AFortOnlineBeacon.Net.Replication;

/// <summary>One flattened, numbered leaf of an FRepLayout - mirrors FRepLayoutCmd (RepLayout.h).</summary>
public sealed class FRepLayoutCmd {
    public required FRepPropertyDef Def { get; init; }
    public required uint RelativeHandle { get; init; }
}

/// <summary>
///     Minimal C# port of FRepLayout, scoped to exactly what this project currently needs: turning
///     a declarative, offset-ordered property table (see NativeRepLayouts) into the same 1-based
///     wire "handle" numbering real UE assigns via FRepLayout::InitFromClass/InitFromProperty_r
///     (RepLayout.cpp) - sequential handles across all top-level properties, recursing into any
///     struct property that lacks a native NetSerialize instead of giving it a handle of its own -
///     and writing out a sparse [handle(packed)][value bits]...[handle 0] payload for a caller-
///     chosen subset of properties.
///
///     Deliberately NOT implemented: dirty-property tracking (the caller decides what changed),
///     arrays, NetGUID/object-reference values, and non-Bool/ByteEnum value types. Properties this
///     project doesn't have a real C# getter for yet still get a correctly-numbered handle (via
///     FRepPropertyDef with no GetByteValue) - they just can't be named in a changed set.
/// </summary>
public sealed class FRepLayout {
    private readonly List<FRepLayoutCmd> _cmds = new();

    public FRepLayout(IEnumerable<FRepPropertyDef> topLevelProps) {
        uint handle = 0;
        foreach (var prop in topLevelProps) Visit(prop, ref handle);
        return;

        void Visit(FRepPropertyDef def, ref uint relativeHandle) {
            if (def.Kind == ERepPropertyKind.StructRecurse) {
                foreach (var child in def.Children!) Visit(child, ref relativeHandle);
                return;
            }

            relativeHandle++;
            _cmds.Add(new FRepLayoutCmd { Def = def, RelativeHandle = relativeHandle });
        }
    }

    /// <summary>
    ///     Writes [handle(packed)][value bits] for every leaf named in <paramref name="changedNames"/>,
    ///     in ascending handle order, followed by the handle-0 terminator - the same minimal wire
    ///     shape as FRepLayout::SendProperties for a set of always-active, non-array properties.
    /// </summary>
    public unsafe void WriteChangedProperties(FNetBitWriter payload, object instance, IReadOnlySet<string> changedNames) {
        // Real UE's FRepLayout::SendProperties (RepLayout.cpp) writes a leading bDoChecksum bit
        // before the first property handle whenever the engine is built with
        // ENABLE_PROPERTY_CHECKSUMS - the matching read side (FRepLayout::ReceiveProperties)
        // reads it back symmetrically. A real Fortnite 10.40 client's overflow when parsing our
        // RemoteRole push (FBitReader::SetOverflowed - ReadLen: 8, Remaining: 1, Max: 18, then
        // ReadHandle=2 instead of the intended 5) matches, bit-for-bit, what happens when this one
        // leading bit is missing: every handle read afterward is off by one bit. That means
        // Fortnite's client build has ENABLE_PROPERTY_CHECKSUMS on, so we need to write it too,
        // even though we never do anything with checksums ourselves (always false/0).
        payload.WriteBit(false);

        foreach (var cmd in _cmds) {
            if (!changedNames.Contains(cmd.Def.Name)) continue;

            if (cmd.Def.GetByteValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{cmd.Def.Name}' has no value serializer yet, can't be in a changed set.");
            }

            var handle = cmd.RelativeHandle;
            payload.SerializeIntPacked(&handle);

            var value = cmd.Def.GetByteValue(instance);
            var bits = cmd.Def.Kind == ERepPropertyKind.Bool ? 1 : (int) Math.Ceiling(Math.Log2(cmd.Def.EnumMaxValue));
            payload.SerializeBits(&value, bits);
        }

        uint terminator = 0;
        payload.SerializeIntPacked(&terminator);
    }
}
