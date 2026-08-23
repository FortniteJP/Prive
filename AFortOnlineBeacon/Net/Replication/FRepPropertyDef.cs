namespace AFortOnlineBeacon.Net.Replication;

/// <summary>
///     How a replicated property occupies handles in an FRepLayout - mirrors the cases
///     FRepLayout::InitFromProperty_r (RepLayout.cpp) distinguishes.
/// </summary>
public enum ERepPropertyKind {
    /// <summary>A UBoolProperty leaf - serializes as exactly 1 bit.</summary>
    Bool,

    /// <summary>A TEnumAsByte&lt;T&gt; leaf - serializes as CeilLogTwo(EnumMaxValue) bits (UByteProperty::NetSerializeItem).</summary>
    ByteEnum,

    /// <summary>
    ///     A struct property whose UScriptStruct has a native NetSerialize (StructFlags &amp;
    ///     STRUCT_NetSerializeNative) - occupies exactly 1 handle, same as a plain leaf, but has no
    ///     value serializer here yet (see FRepPropertyDef.GetByteValue).
    /// </summary>
    StructAtomic,

    /// <summary>
    ///     A struct property with no native NetSerialize - FRepLayout doesn't assign it a handle of
    ///     its own; it recurses into <see cref="FRepPropertyDef.Children"/> instead, each of which
    ///     consumes its own handle(s).
    /// </summary>
    StructRecurse
}

/// <summary>
///     Declarative description of one native, already-compiled Unreal property, used to compute
///     FRepLayout-equivalent wire handles without needing UE's real reflection data (which this
///     project doesn't have for engine-internal classes like AActor). See NativeRepLayouts for the
///     actual per-class tables and FRepLayout for how these get turned into handle numbers.
///
///     A property this codebase doesn't have a real C# field for yet - most of AActor's own
///     properties, at the moment - is declared with <see cref="GetByteValue"/> left null. It still
///     reserves its correct handle slot (so later properties number correctly), but FRepLayout
///     will refuse to actually serialize it if it's ever named in a changed-property set.
/// </summary>
public sealed class FRepPropertyDef {
    public required string Name { get; init; }
    public required ERepPropertyKind Kind { get; init; }

    /// <summary>Leaf value getter (Bool: 0/1, ByteEnum: the raw enum byte). Null for reserved/unimplemented properties.</summary>
    public Func<object, byte>? GetByteValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.ByteEnum"/> - the enum's highest raw value (e.g. ENetRole.ROLE_MAX=4), matching UByteProperty::NetSerializeItem's CeilLogTwo(Enum-&gt;GetMaxEnumValue()).</summary>
    public int EnumMaxValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.StructRecurse"/>, in the struct's own offset order.</summary>
    public FRepPropertyDef[]? Children { get; init; }
}
