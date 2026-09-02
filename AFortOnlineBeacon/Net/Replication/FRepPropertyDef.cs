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
    ///     A TArray of object references, sent for real rather than as an empty stub. Wire format is
    ///     FRepLayout::SendProperties_r's DynamicArray branch (RepLayout.cpp:1998): the array's own
    ///     handle, a uint16 element count, then each element as its own relative handle (1-based)
    ///     plus the value, closed by a 0 handle.
    /// </summary>
    ObjectRefArray,

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
    ///     FVector_NetQuantize100 - the same packed encoding as
    ///     <see cref="ERepPropertyKind.VectorQuantize10"/> with a finer scale factor (100/30 rather
    ///     than 10/24), and atomic for the same reason. `ABuildingSMActor::ReplicatedDrawScale3D` is
    ///     the one this project sends; see NativeRepLayouts handle 58 for why a building's scale has
    ///     to travel as a property and not only in its spawn bunch.
    /// </summary>
    VectorQuantize100,

    /// <summary>
    ///     An FRotator leaf with its own NetSerialize - three optionally-present compressed shorts,
    ///     exactly what SerializeNewActor already writes for a spawned actor's rotation. Atomic for
    ///     the same reason the quantized vectors are: RepLayout never recurses into a struct that has
    ///     a native NetSerialize. `AFortAthenaAircraft::FlightInfo.FlightStartRotation` is the one
    ///     this project sends - the bus's heading, i.e. the entire flight path.
    /// </summary>
    Rotator,

    /// <summary>
    ///     A UInt16Property / UInt16 leaf (e.g. FFortItemEntry::OrderIndex) - no NetSerializeItem
    ///     override, so UProperty's default SerializeItem runs: 16 raw little-endian bits.
    /// </summary>
    Int16,

    /// <summary>
    ///     An FQuantizedBuildingAttribute (FortniteGame) - a struct that IS
    ///     STRUCT_NetSerializeNative, so it occupies ONE handle and carries a hand-written wire
    ///     format that is emphatically NOT the 32-bit float its single `Value` member looks like.
    ///
    ///     Decoded from the real 10.40 client via the CppStructOps vtable route (slot 12
    ///     HasNetSerializer is `mov al,1; ret`; slot 14's loading branch reads):
    ///
    ///         SerializeInt(&amp;tmp, 0x10000)          -&gt; exactly 16 bits
    ///         Value = (tmp - 0x8000) * (1.0f/546)
    ///
    ///     so writing it is the inverse: round(Value * 546) + 0x8000, in 16 bits. For a power-of-two
    ///     ValueMax, FBitWriter::SerializeInt is bit-for-bit identical to writing the value LSB-first
    ///     in CeilLogTwo(ValueMax) bits, which is what this does.
    ///
    ///     Getting the WIDTH right is the load-bearing part. Sending this as a plain 32-bit float put
    ///     16 extra bits into the stream, the client read them as the next property handle, and every
    ///     property after it in the same push - Health and MaxHealth - never arrived at all, leaving
    ///     MaxHealth at its class default of 0. A building with 0 max health reads as already
    ///     destroyed, which is exactly how it presented in-game: the destruction animation firing the
    ///     instant a piece was placed, and the piece then sitting there transparent.
    /// </summary>
    QuantizedBuildingAttribute,

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

    /// <summary>Elements for <see cref="ERepPropertyKind.ObjectRefArray"/>.</summary>
    public Func<object, IReadOnlyList<UObject>>? GetObjectArrayValue { get; init; }

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

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.Rotator"/>.</summary>
    public Func<object, FRotator>? GetRotatorValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.ByteEnum"/> - the enum's highest raw value (e.g. ENetRole.ROLE_MAX=4), matching UByteProperty::NetSerializeItem's CeilLogTwo(Enum-&gt;GetMaxEnumValue()).</summary>
    public int EnumMaxValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.StructRecurse"/>, in the struct's own offset order.</summary>
    public FRepPropertyDef[]? Children { get; init; }
}
