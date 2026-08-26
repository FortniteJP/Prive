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
    ///     An FString leaf (e.g. APlayerState::PlayerNamePrivate) - UStrProperty::NetSerializeItem is
    ///     just `Ar &lt;&lt; String`, i.e. FString's ordinary length-prefixed archive format, and it
    ///     occupies exactly 1 handle like any other leaf. The same FString.Serialize this uses is
    ///     already proven on the wire by UPackageMapClient's NetGUID path exports.
    /// </summary>
    String,

    /// <summary>
    ///     An FUniqueNetIdRepl leaf (APlayerState::UniqueId, PartyOwnerUniqueId, ...). RepLayout
    ///     special-cases this struct by name into ERepLayoutCmdType::PropertyNetId, so it is exactly
    ///     one handle, and the value is FUniqueNetIdRepl::NetSerialize's byte blob.
    /// </summary>
    NetId,

    /// <summary>
    ///     A UIntProperty leaf. UIntProperty has no NetSerializeItem override, so UProperty's
    ///     default runs SerializeItem, i.e. a raw little-endian 32-bit write - one handle.
    /// </summary>
    Int32,

    /// <summary>
    ///     A UFloatProperty leaf - same story as <see cref="Int32"/>: no NetSerializeItem override,
    ///     so a raw 32-bit IEEE-754 write, one handle.
    /// </summary>
    Float,

    /// <summary>
    ///     An FVector_NetQuantize10 leaf - one handle, written by WritePackedVector&lt;10, 24&gt;
    ///     (NetSerialization.h:1688), the same encoding SerializeNewActor already uses for an actor's
    ///     spawn location. RepLayout special-cases this struct as atomic by name, so it never
    ///     recurses into X/Y/Z.
    /// </summary>
    VectorQuantize10,

    /// <summary>
    ///     A UInt16Property / UInt16 leaf (e.g. FFortItemEntry::OrderIndex) - no NetSerializeItem
    ///     override, so UProperty's default SerializeItem runs: 16 raw little-endian bits.
    /// </summary>
    Int16,

    /// <summary>
    ///     A TArray-typed leaf sent as an always-empty array - the minimal, self-terminating
    ///     DynamicArray Cmd encoding from RepLayout.cpp's SendProperties_r (line ~1998-2033):
    ///     [handle(packed)][ArrayNum=0 (raw uint16, NOT packed)][terminator=0 (packed)]. Note the
    ///     element count is deliberately NOT packed while both handles are.
    ///
    ///     Originally added as a diagnostic to prove handle 50/51 on APlayerController really was a
    ///     DynamicArray Cmd rather than a plain ObjectRef (it was), but the encoding is the genuine
    ///     one for an empty array, which is what the three TArray members inside
    ///     AFortPickup::PrimaryPickupItemEntry need.
    /// </summary>
    EmptyDynamicArray
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

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.String"/>.</summary>
    public Func<object, string>? GetStringValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.NetId"/>.</summary>
    public Func<object, FUniqueNetIdRepl?>? GetNetIdValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.Int32"/> and <see cref="ERepPropertyKind.Int16"/>.</summary>
    public Func<object, int>? GetIntValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.Float"/>.</summary>
    public Func<object, float>? GetFloatValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.VectorQuantize10"/>.</summary>
    public Func<object, FVector>? GetVectorValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.ByteEnum"/> - the enum's highest raw value (e.g. ENetRole.ROLE_MAX=4), matching UByteProperty::NetSerializeItem's CeilLogTwo(Enum-&gt;GetMaxEnumValue()).</summary>
    public int EnumMaxValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.StructRecurse"/>, in the struct's own offset order.</summary>
    public FRepPropertyDef[]? Children { get; init; }
}
