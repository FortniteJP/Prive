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
                return def.GetFloatValue == null ? NotComparable : def.GetFloatValue(instance);
            case ERepPropertyKind.String:
                return def.GetStringValue == null ? NotComparable : def.GetStringValue(instance);
            case ERepPropertyKind.Name:
                return def.GetNameValue == null ? NotComparable : def.GetNameValue(instance).ToString();
            case ERepPropertyKind.NetId:
                return def.GetNetIdValue == null ? NotComparable : def.GetNetIdValue(instance)?.ToDebugString();
            case ERepPropertyKind.ObjectRef:
                return def.GetObjectValue == null ? NotComparable : def.GetObjectValue(instance);
            case ERepPropertyKind.VectorQuantize10:
                // Compared by string: FVector is a mutable reference type here, so holding the
                // instance would compare a value with itself after the actor moved it.
                return def.GetVectorValue == null ? NotComparable : def.GetVectorValue(instance).ToString();
            default:
                return NotComparable;
        }
    }

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

            var handle = cmd.RelativeHandle;

            if (cmd.Def.Kind == ERepPropertyKind.ObjectRef) {
                if (cmd.Def.GetObjectValue == null) {
                    throw new InvalidOperationException($"FRepLayout: '{cmd.Def.Name}' has no object value serializer yet, can't be in a changed set.");
                }

                payload.SerializeIntPacked(&handle);
                ((UPackageMapClient) payload.PackageMap!).SerializeObject(payload, cmd.Def.GetObjectValue(instance));
                continue;
            }

            if (cmd.Def.Kind == ERepPropertyKind.EmptyDynamicArray) {
                payload.SerializeIntPacked(&handle);
                ushort arrayNum = 0;
                payload.SerializeBits(&arrayNum, 16);
                uint arrayTerminator = 0;
                payload.SerializeIntPacked(&arrayTerminator);
                continue;
            }

            if (cmd.Def.Kind == ERepPropertyKind.NetId) {
                if (cmd.Def.GetNetIdValue == null) {
                    throw new InvalidOperationException($"FRepLayout: '{cmd.Def.Name}' has no net-id value serializer yet, can't be in a changed set.");
                }

                payload.SerializeIntPacked(&handle);
                FUniqueNetIdRepl.Write(payload, cmd.Def.GetNetIdValue(instance) ?? new FUniqueNetIdRepl());
                continue;
            }

            if (cmd.Def.Kind == ERepPropertyKind.VectorQuantize10) {
                if (cmd.Def.GetVectorValue == null) {
                    throw new InvalidOperationException($"FRepLayout: '{cmd.Def.Name}' has no vector value serializer yet, can't be in a changed set.");
                }

                payload.SerializeIntPacked(&handle);
                cmd.Def.GetVectorValue(instance).NetSerializeWriteQuantized(payload, 10, 24);
                continue;
            }

            if (cmd.Def.Kind == ERepPropertyKind.Int16) {
                if (cmd.Def.GetIntValue == null) {
                    throw new InvalidOperationException($"FRepLayout: '{cmd.Def.Name}' has no int value serializer yet, can't be in a changed set.");
                }

                payload.SerializeIntPacked(&handle);
                var int16Bits = (ushort) cmd.Def.GetIntValue(instance);
                payload.SerializeBits(&int16Bits, 16);
                continue;
            }

            if (cmd.Def.Kind is ERepPropertyKind.Int32 or ERepPropertyKind.Float) {
                // Neither UIntProperty nor UFloatProperty overrides NetSerializeItem, so UProperty's
                // default SerializeItem runs: a plain 4-byte archive write, which on an FBitWriter is
                // 32 raw bits.
                uint rawBits;
                if (cmd.Def.Kind == ERepPropertyKind.Int32) {
                    if (cmd.Def.GetIntValue == null) {
                        throw new InvalidOperationException($"FRepLayout: '{cmd.Def.Name}' has no int value serializer yet, can't be in a changed set.");
                    }

                    rawBits = (uint) cmd.Def.GetIntValue(instance);
                } else {
                    if (cmd.Def.GetFloatValue == null) {
                        throw new InvalidOperationException($"FRepLayout: '{cmd.Def.Name}' has no float value serializer yet, can't be in a changed set.");
                    }

                    rawBits = BitConverter.SingleToUInt32Bits(cmd.Def.GetFloatValue(instance));
                }

                payload.SerializeIntPacked(&handle);
                payload.SerializeBits(&rawBits, 32);
                continue;
            }

            if (cmd.Def.Kind == ERepPropertyKind.String) {
                if (cmd.Def.GetStringValue == null) {
                    throw new InvalidOperationException($"FRepLayout: '{cmd.Def.Name}' has no string value serializer yet, can't be in a changed set.");
                }

                payload.SerializeIntPacked(&handle);
                payload.WriteString(cmd.Def.GetStringValue(instance));
                continue;
            }

            if (cmd.Def.Kind == ERepPropertyKind.Name) {
                if (cmd.Def.GetNameValue == null) {
                    throw new InvalidOperationException($"FRepLayout: '{cmd.Def.Name}' has no name value serializer yet, can't be in a changed set.");
                }

                payload.SerializeIntPacked(&handle);
                FName? nameValue = cmd.Def.GetNameValue(instance);
                UPackageMap.StaticSerializeName(payload, ref nameValue);
                continue;
            }

            if (cmd.Def.GetByteValue == null) {
                throw new InvalidOperationException($"FRepLayout: '{cmd.Def.Name}' has no value serializer yet, can't be in a changed set.");
            }

            payload.SerializeIntPacked(&handle);

            var value = cmd.Def.GetByteValue(instance);
            var bits = cmd.Def.Kind == ERepPropertyKind.Bool ? 1 : (int) Math.Ceiling(Math.Log2(cmd.Def.EnumMaxValue));
            payload.SerializeBits(&value, bits);
        }

        uint terminator = 0;
        payload.SerializeIntPacked(&terminator);
    }
}
