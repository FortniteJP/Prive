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
    StructRecurse,

    /// <summary>
    ///     A UObjectProperty leaf (e.g. AController::PlayerState/Pawn) - NetSerializeItem defers to
    ///     UPackageMap::SerializeObject, i.e. the same NetGUID-reference write SerializeNewActor
    ///     already uses for Archetype/Level, occupies exactly 1 handle like a plain leaf.
    /// </summary>
    ObjectRef,

    /// <summary>
    ///     An FName leaf (e.g. AGameStateBase::MatchState) - NetSerializeItem defers to
    ///     UPackageMap::StaticSerializeName, occupies exactly 1 handle like a plain leaf.
    /// </summary>
    Name,

    /// <summary>
    ///     TEMP diagnostic (2026-08-25): a TArray-typed leaf, sent as an always-empty array - the
    ///     minimal, unambiguous DynamicArray Cmd encoding per RepLayout.cpp's SendProperties_r
    ///     (line ~1998-2033): [handle(packed)][ArrayNum=0 (raw uint16, NOT packed)]
    ///     [terminator=0 (packed)]. Used to test whether handle 50/51 (previously assumed to be a
    ///     plain ObjectRef for WorldInventory) is actually a DynamicArray Cmd on the real client -
    ///     a plain ObjectRef write there produced "Invalid property terminator handle" regardless of
    ///     which object was referenced (even AController.PlayerState, already proven-good
    ///     elsewhere), which rules out the referenced object/class and points at a Cmd-type
    ///     mismatch instead. See NativeRepLayouts.PlayerControllerProps.
    /// </summary>
    EmptyDynamicArrayProbe
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

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.ObjectRef"/> - the referenced object, or null.</summary>
    public Func<object, UObject?>? GetObjectValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.Name"/>.</summary>
    public Func<object, FName>? GetNameValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.ByteEnum"/> - the enum's highest raw value (e.g. ENetRole.ROLE_MAX=4), matching UByteProperty::NetSerializeItem's CeilLogTwo(Enum-&gt;GetMaxEnumValue()).</summary>
    public int EnumMaxValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.StructRecurse"/>, in the struct's own offset order.</summary>
    public FRepPropertyDef[]? Children { get; init; }
}
