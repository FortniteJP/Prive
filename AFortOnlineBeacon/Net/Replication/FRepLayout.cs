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
    /// <summary>
    ///     The two magic numbers in FQuantizedBuildingAttribute's native NetSerialize, read straight
    ///     out of the real client (see ERepPropertyKind.QuantizedBuildingAttribute): the loading
    ///     branch computes `(SerializeInt(0x10000) - 0x8000) * 0.0018315018f`, and 0.0018315018 is
    ///     1/546 to within float precision.
    /// </summary>
    private const float QuantizedBuildingAttributeScale = 546f;

    private const int QuantizedBuildingAttributeBias = 0x8000;

    private readonly List<FRepLayoutCmd> _cmds = new();

    /// <summary>
    ///     Every leaf name this layout knows, in handle order. Exists so a table keyed on property
    ///     NAMES can be checked against reality at startup - UActorChannel's replication-condition
    ///     table is the one that needs it, and a key that matches nothing there would be silently
    ///     useless rather than wrong.
    /// </summary>
    public IEnumerable<string> PropertyNames => _cmds.Select(cmd => cmd.Def.Name);

    /// <summary>String properties whose starting bit offset has already been reported - once each.</summary>
    private static readonly HashSet<string> LoggedStringOffsets = new();

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
    ///     Sentinel returned by <see cref="GetComparableValue"/> for a Cmd whose value this port
    ///     cannot snapshot (a reserved property with no getter, or a Kind with no scalar value such
    ///     as StructAtomic). Such a property is simply never considered changed, so it is sent on
    ///     the initial burst - where the caller names it explicitly - and never again.
    /// </summary>
    public static readonly object NotComparable = new();

    /// <summary>
    ///     Snapshots one Cmd's current value in a form that can be compared with Equals against the
    ///     previously-sent value. This is this port's stand-in for real UE's shadow buffer: UE keeps
    ///     a raw byte copy of the last-sent property block and memcmps it in
    ///     FRepLayout::CompareProperties, which it can do because it has the real UProperty offsets.
    ///     Here the values come from the same getters FRepLayout already uses to serialize them, so
    ///     the two can never disagree about what was sent.
    ///
    ///     Reference types are normalised to something with value equality wherever holding the
    ///     reference would be wrong: an FName or an FUniqueNetIdRepl can be rebuilt with the same
    ///     contents and must still compare equal. An ObjectRef deliberately keeps the reference,
    ///     because a NetGUID reference IS identity - pointing at a different actor with the same
    ///     name is a real change.
    /// </summary>
    public static object? GetComparableValue(FRepLayoutCmd cmd, object instance) {
        var def = cmd.Def;

        switch (def.Kind) {
            case ERepPropertyKind.Bool:
            case ERepPropertyKind.ByteEnum:
                return def.GetByteValue == null ? NotComparable : def.GetByteValue(instance);
            case ERepPropertyKind.Int32:
            case ERepPropertyKind.Int16:
                return def.GetIntValue == null ? NotComparable : def.GetIntValue(instance);
            case ERepPropertyKind.Float:
            case ERepPropertyKind.QuantizedBuildingAttribute:
                return def.GetFloatValue == null ? NotComparable : def.GetFloatValue(instance);
            case ERepPropertyKind.String:
                return def.GetStringValue == null ? NotComparable : def.GetStringValue(instance);
            case ERepPropertyKind.Name:
                return def.GetNameValue == null ? NotComparable : def.GetNameValue(instance).ToString();
            case ERepPropertyKind.NetId:
                return def.GetNetIdValue == null ? NotComparable : def.GetNetIdValue(instance)?.ToDebugString();
            case ERepPropertyKind.ObjectRef:
                return def.GetObjectValue == null ? NotComparable : def.GetObjectValue(instance);
            case ERepPropertyKind.ObjectRefArray:
                // Identity per element, in order - the same rule as a single ObjectRef, since a
                // NetGUID reference IS identity. Flattened to a string so the snapshot has value
                // equality; holding the live List would compare it against itself after a change.
                //
                // Without this case the Kind fell through to NotComparable, which means "never
                // changed" - and a property that is never changed is never sent. SpawnedAttributes
                // silently stayed off the wire.
                return def.GetObjectArrayValue == null
                    ? NotComparable
                    : string.Join('|', def.GetObjectArrayValue(instance)
                        .Select(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode));
            case ERepPropertyKind.StructArray:
                // Only what WriteStructArray would actually SEND, in the same order - the element
                // count and each modelled member. Everything else in an element is the Blueprint's
                // and never changes, so including it would only make the snapshot bigger.
                return def.GetStructArrayValue == null || def.Children == null
                    ? NotComparable
                    : string.Join('|', def.GetStructArrayValue(instance).Select(element =>
                        string.Join(',', def.Children.Where(child => child.IsModelled)
                            .Select(child => SnapshotLeaf(child, element)))));
            case ERepPropertyKind.Rotator:
                // Same reason as the vectors below: FRotator is a mutable reference type here.
                return def.GetRotatorValue == null ? NotComparable : def.GetRotatorValue(instance).ToString();
            case ERepPropertyKind.RepMovement:
                // Same reason again - and FRepMovement.ToString is written to be exactly this
                // snapshot, so a pawn that has not moved compares equal and sends nothing.
                return def.GetRepMovementValue == null ? NotComparable : def.GetRepMovementValue(instance).ToString();
            case ERepPropertyKind.VectorQuantize:
            case ERepPropertyKind.VectorQuantize10:
            case ERepPropertyKind.VectorQuantize100:
            case ERepPropertyKind.VectorNormal:
            case ERepPropertyKind.Vector:
                // Compared by string: FVector is a mutable reference type here, so holding the
                // instance would compare a value with itself after the actor moved it.
                return def.GetVectorValue == null ? NotComparable : def.GetVectorValue(instance).ToString();
            default:
                return NotComparable;
        }
    }

    /// <summary>
    ///     One leaf of an array element as a STRING, for the snapshot above.
    ///
    ///     A string because the snapshot is compared with Equals and has to survive being joined
    ///     with its neighbours; an object reference is folded to its identity hash rather than its
    ///     ToString for the same reason ObjectRefArray does it - a NetGUID reference IS identity,
    ///     and two different pawns can share a name.
    /// </summary>
    private static string SnapshotLeaf(FRepPropertyDef def, object element) => def.Kind switch {
        ERepPropertyKind.ObjectRef =>
            def.GetObjectValue!(element) is { } obj
                ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj).ToString()
                : "null",
        ERepPropertyKind.Bool or ERepPropertyKind.ByteEnum => def.GetByteValue!(element).ToString(),
        ERepPropertyKind.Int32 or ERepPropertyKind.Int16 => def.GetIntValue!(element).ToString(),
        ERepPropertyKind.Float => def.GetFloatValue!(element).ToString("R"),
        ERepPropertyKind.Name => def.GetNameValue!(element).ToString(),
        ERepPropertyKind.String => def.GetStringValue!(element),
        ERepPropertyKind.Vector or ERepPropertyKind.VectorQuantize or ERepPropertyKind.VectorQuantize10
            or ERepPropertyKind.VectorQuantize100 =>
            def.GetVectorValue!(element).ToString(),

        // Deliberately not a fallback value: a Kind that reaches here is one WriteStructArray would
        // happily send and this cannot snapshot, which means it would be sent once and then look
        // unchanged forever. Better to say so at the moment the table gains it.
        _ => throw new InvalidOperationException(
            $"FRepLayout: '{def.Name}' is a {def.Kind} inside a struct array and has no snapshot yet.")
    };

    /// <summary>
    ///     FRepLayout::CompareProperties. Walks the candidate properties, snapshots each one, and
    ///     reports the ones whose value differs from what was last sent on this channel.
    ///
    ///     <paramref name="shadow"/> is NOT updated here. Real UE separates comparison from the
    ///     bookkeeping that says "the client has this now" for the same reason: the send can still
    ///     fail (a saturated channel, a bunch that overflows), and a shadow updated ahead of a send
    ///     that never happened silently drops that property forever - the value would never differ
    ///     again. The caller commits with <see cref="CommitShadowState"/> once the bunch is away.
    /// </summary>
    public List<(string Name, object? Value)> CompareProperties(
        object instance, IReadOnlySet<string> candidateNames, IReadOnlyDictionary<string, object?> shadow) {
        var changed = new List<(string, object?)>();

        foreach (var cmd in _cmds) {
            if (!candidateNames.Contains(cmd.Def.Name)) continue;

            var value = GetComparableValue(cmd, instance);
            if (ReferenceEquals(value, NotComparable)) continue;

            if (shadow.TryGetValue(cmd.Def.Name, out var previous) && Equals(previous, value)) continue;

            changed.Add((cmd.Def.Name, value));
        }

        return changed;
    }

    /// <summary>Records what a successful send just put on the wire, so it is not sent again unchanged.</summary>
    public static void CommitShadowState(IEnumerable<(string Name, object? Value)> sent, Dictionary<string, object?> shadow) {
        foreach (var (name, value) in sent) shadow[name] = value;
    }

    /// <summary>
    ///     Seeds the shadow with everything the initial burst wrote. Uses the same getters, so an
    ///     initially-sent property is only re-sent once it genuinely changes afterwards.
    /// </summary>
    public void SeedShadowState(object instance, IReadOnlySet<string> sentNames, Dictionary<string, object?> shadow) {
        foreach (var cmd in _cmds) {
            if (!sentNames.Contains(cmd.Def.Name)) continue;

            var value = GetComparableValue(cmd, instance);
            if (!ReferenceEquals(value, NotComparable)) shadow[cmd.Def.Name] = value;
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

            WriteCmd(payload, instance, cmd.Def, cmd.RelativeHandle);
        }

        uint terminator = 0;
        payload.SerializeIntPacked(&terminator);
    }

    /// <summary>
    ///     One [handle][value] pair, or - for the container kinds - one handle followed by whatever
    ///     structure that container writes.
    ///
    ///     Split out of <see cref="WriteChangedProperties"/> so an array element's members can be
    ///     written by the same code as a top-level property: a leaf's wire format does not change
    ///     with its depth, and two copies of it would eventually disagree.
    /// </summary>
    private static unsafe void WriteCmd(FNetBitWriter payload, object instance, FRepPropertyDef def, uint handle) {
        if (def.Kind == ERepPropertyKind.ObjectRefArray) {
            if (def.GetObjectArrayValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no object-array serializer yet, can't be in a changed set.");
            }

            var elements = def.GetObjectArrayValue(instance);
            var packageMap = (UPackageMapClient) payload.PackageMap!;

            payload.SerializeIntPacked(&handle);

            var count = (ushort) elements.Count;
            payload.SerializeBits(&count, 16);

            // Handles inside an array are relative and 1-based per element - one handle per
            // element here, since an object reference is a single leaf cmd.
            for (var index = 0; index < elements.Count; index++) {
                var elementHandle = (uint) (index + 1);
                payload.SerializeIntPacked(&elementHandle);
                packageMap.SerializeObject(payload, elements[index]);
            }

            uint arrayEnd = 0;
            payload.SerializeIntPacked(&arrayEnd);
            return;
        }

        if (def.Kind == ERepPropertyKind.StructArray) {
            WriteStructArray(payload, instance, def, handle);
            return;
        }

        if (def.Kind == ERepPropertyKind.EmptyDynamicArray) {
            payload.SerializeIntPacked(&handle);
            ushort arrayNum = 0;
            payload.SerializeBits(&arrayNum, 16);
            uint arrayTerminator = 0;
            payload.SerializeIntPacked(&arrayTerminator);
            return;
        }

        payload.SerializeIntPacked(&handle);
        WriteLeafValue(payload, instance, def);
    }

    /// <summary>
    ///     A TArray of structs, written as a PARTIAL update: the count the client already has,
    ///     then only the members this server models. See
    ///     <see cref="ERepPropertyKind.StructArray"/> for the wire format, and for why sending back
    ///     the same count is exactly what preserves everything the Blueprint configured.
    ///
    ///     <paramref name="def"/>.Children is the DIVISOR as much as it is the field list - the
    ///     handle for member j of element i is `i * Children.Length + j`, so a child missing from
    ///     the table does not drop a member, it renumbers every element after the first.
    /// </summary>
    private static unsafe void WriteStructArray(FNetBitWriter payload, object instance, FRepPropertyDef def, uint handle) {
        if (def.GetStructArrayValue == null || def.Children == null) {
            throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no struct-array serializer yet, can't be in a changed set.");
        }

        var elements = def.GetStructArrayValue(instance);

        payload.SerializeIntPacked(&handle);

        var count = (ushort) elements.Count;
        payload.SerializeBits(&count, 16);

        var handlesPerElement = (uint) def.Children.Length;

        // Ascending handle order, which this ordering gives for free: the receiver walks one
        // continuous handle counter across every element's cmds and only ever moves forward.
        for (var index = 0; index < elements.Count; index++) {
            for (var child = 0; child < def.Children.Length; child++) {
                var childDef = def.Children[child];
                if (!childDef.IsModelled) continue;

                WriteCmd(payload, elements[index], childDef, (uint) index * handlesPerElement + (uint) child + 1);
            }
        }

        uint arrayEnd = 0;
        payload.SerializeIntPacked(&arrayEnd);
    }

    /// <summary>One leaf's VALUE, with its handle already written.</summary>
    private static unsafe void WriteLeafValue(FNetBitWriter payload, object instance, FRepPropertyDef def) {
        if (def.Kind == ERepPropertyKind.ObjectRef) {
            if (def.GetObjectValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no object value serializer yet, can't be in a changed set.");
            }

            ((UPackageMapClient) payload.PackageMap!).SerializeObject(payload, def.GetObjectValue(instance));
            return;
        }

        if (def.Kind == ERepPropertyKind.NetId) {
            if (def.GetNetIdValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no net-id value serializer yet, can't be in a changed set.");
            }

            FUniqueNetIdRepl.Write(payload, def.GetNetIdValue(instance) ?? new FUniqueNetIdRepl());
            return;
        }

        if (def.Kind == ERepPropertyKind.Rotator) {
            if (def.GetRotatorValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no rotator value serializer yet, can't be in a changed set.");
            }

            def.GetRotatorValue(instance).NetSerializeWrite(payload);
            return;
        }

        if (def.Kind == ERepPropertyKind.RepMovement) {
            if (def.GetRepMovementValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no movement value serializer yet, can't be in a changed set.");
            }

            def.GetRepMovementValue(instance).NetSerializeWrite(payload);
            return;
        }

        if (def.Kind is ERepPropertyKind.VectorQuantize or ERepPropertyKind.VectorQuantize10
                     or ERepPropertyKind.VectorQuantize100) {
            if (def.GetVectorValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no vector value serializer yet, can't be in a changed set.");
            }

            // The three differ only in the packed encoding's scale/width - 1/20, 10/24 and 100/30,
            // the engine's own FVector_NetQuantize, _NetQuantize10 and _NetQuantize100 template
            // arguments.
            var (scaleFactor, maxBits) = def.Kind switch {
                ERepPropertyKind.VectorQuantize => (1u, 20u),
                ERepPropertyKind.VectorQuantize10 => (10u, 24u),
                _ => (100u, 30u)
            };

            def.GetVectorValue(instance).NetSerializeWriteQuantized(payload, scaleFactor, maxBits);
            return;
        }

        if (def.Kind == ERepPropertyKind.VectorNormal) {
            if (def.GetVectorValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no vector value serializer yet, can't be in a changed set.");
            }

            // SerializeFixedVector<1, 16> - three flat 16-bit fixed-point fields, no header. See
            // FVector.NetSerializeWriteFixed and ERepPropertyKind.VectorNormal.
            def.GetVectorValue(instance).NetSerializeWriteFixed(payload, 1, 16);
            return;
        }

        if (def.Kind == ERepPropertyKind.Vector) {
            if (def.GetVectorValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no vector value serializer yet, can't be in a changed set.");
            }

            // ERepLayoutCmdType::PropertyVector is FVector's ordinary archive operator - three raw
            // floats, NOT the packed form the quantized kinds use.
            var vector = def.GetVectorValue(instance);
            var x = BitConverter.SingleToUInt32Bits(vector.X);
            var y = BitConverter.SingleToUInt32Bits(vector.Y);
            var z = BitConverter.SingleToUInt32Bits(vector.Z);
            payload.SerializeBits(&x, 32);
            payload.SerializeBits(&y, 32);
            payload.SerializeBits(&z, 32);
            return;
        }

        if (def.Kind == ERepPropertyKind.QuantizedBuildingAttribute) {
            if (def.GetFloatValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no float value serializer yet, can't be in a changed set.");
            }

            var quantized = (ushort) Math.Clamp(
                MathF.Round(def.GetFloatValue(instance) * QuantizedBuildingAttributeScale)
                    + QuantizedBuildingAttributeBias,
                0, ushort.MaxValue);
            payload.SerializeBits(&quantized, 16);
            return;
        }

        if (def.Kind == ERepPropertyKind.Int16) {
            if (def.GetIntValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no int value serializer yet, can't be in a changed set.");
            }

            var int16Bits = (ushort) def.GetIntValue(instance);
            payload.SerializeBits(&int16Bits, 16);
            return;
        }

        if (def.Kind is ERepPropertyKind.Int32 or ERepPropertyKind.Float) {
            // Neither UIntProperty nor UFloatProperty overrides NetSerializeItem, so UProperty's
            // default SerializeItem runs: a plain 4-byte archive write, which on an FBitWriter is
            // 32 raw bits.
            uint rawBits;
            if (def.Kind == ERepPropertyKind.Int32) {
                if (def.GetIntValue == null) {
                    throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no int value serializer yet, can't be in a changed set.");
                }

                rawBits = (uint) def.GetIntValue(instance);
            } else {
                if (def.GetFloatValue == null) {
                    throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no float value serializer yet, can't be in a changed set.");
                }

                rawBits = BitConverter.SingleToUInt32Bits(def.GetFloatValue(instance));
            }

            payload.SerializeBits(&rawBits, 32);
            return;
        }

        if (def.Kind == ERepPropertyKind.String) {
            if (def.GetStringValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no string value serializer yet, can't be in a changed set.");
            }

            // THE BIT OFFSET AT WHICH THE STRING'S BYTES START, logged once per property name.
            //
            // This is a measurement aimed at ONE open question. The client mangles
            // PlayerNamePrivate by delta[j] = (C - 3j) mod 8, j from the end of the string -
            // a rule that four samples fit exactly, with C observed as 4 in some sessions and 0
            // in others (see AGameModeBase.PreCompensateName). Nothing has explained what sets
            // C, which is why the compensation is a hack rather than a fix.
            //
            // The shape of the corruption says what to look at: the delta is always mod 8, so
            // only the LOW THREE BITS of each character change, and it advances by 3 per
            // character - the signature of a stride or alignment error, whose phase would be set
            // by where the string STARTS. If C turns out to track this offset mod 8, the
            // compensation stops being a guess and becomes computable; if it does not, that
            // whole line of thinking is dead and the search moves into the client.
            var stringStartBit = payload.GetNumBits() + 32;   // +32: the FString length prefix

            if (LoggedStringOffsets.Add(def.Name)) {
                // The VALUE is logged too, and that is the point of the second sample. HeroId is
                // a 32-character uppercase GUID sitting in the same bunch as PlayerNamePrivate
                // at a different offset, so comparing what the client ends up holding for BOTH
                // measures the corruption at two known phases against two known inputs - which
                // three characters of "dev" can never do.
                Console.WriteLine($"FRepLayout: '{def.Name}' = \"{def.GetStringValue(instance)}\" - " +
                                  $"string bytes start at bit {stringStartBit} of this payload " +
                                  $"({stringStartBit % 8} mod 8). Compare with what the client reports for it " +
                                  "(console: GetAll <Class> " + def.Name + ").");
            }

            payload.WriteString(def.GetStringValue(instance));
            return;
        }

        if (def.Kind == ERepPropertyKind.Name) {
            if (def.GetNameValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no name value serializer yet, can't be in a changed set.");
            }

            FName? nameValue = def.GetNameValue(instance);
            UPackageMap.StaticSerializeName(payload, ref nameValue);
            return;
        }

        if (def.GetByteValue == null) {
            throw new InvalidOperationException($"FRepLayout: '{def.Name}' has no value serializer yet, can't be in a changed set.");
        }

        var value = def.GetByteValue(instance);
        var bits = def.Kind == ERepPropertyKind.Bool ? 1 : (int) Math.Ceiling(Math.Log2(def.EnumMaxValue));
        payload.SerializeBits(&value, bits);
    }
}
