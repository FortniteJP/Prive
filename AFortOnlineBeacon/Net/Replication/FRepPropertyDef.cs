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
    ///     AActor::ReplicatedMovement specifically - a StructAtomic that DOES have a serializer.
    ///     See Core.Math.FRepMovement for the format and why it needs one of its own.
    /// </summary>
    RepMovement,

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
    ///     An FVector_NetQuantize leaf - the UNSUFFIXED one, WritePackedVector&lt;1, 20&gt;: whole
    ///     units, twenty bits. Distinct from the 10 and 100 kinds beside it, and not interchangeable
    ///     with them - a vector written at the wrong scale is not slightly off, it misreads every
    ///     later handle in the bunch.
    ///
    ///     AFortPawn::PushMomentum is one of these.
    /// </summary>
    VectorQuantize,

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
    EmptyDynamicArray,

    /// <summary>
    ///     An FVector_NetQuantizeNormal leaf - one handle, SerializeFixedVector&lt;1, 16&gt;: three
    ///     16-bit fixed-point components over [-1, 1], with no bit-count header. RepLayout
    ///     special-cases the struct by name (RepLayout.cpp:4464) so it never recurses into X/Y/Z.
    ///
    ///     Not one of the packed VectorQuantize kinds and not interchangeable with them - those
    ///     write a length header and a variable width. `AFortPickup::PickupLocationData.StartDirection`
    ///     is the one this project sends.
    /// </summary>
    VectorNormal,

    /// <summary>
    ///     A plain FVector leaf - ONE handle, three raw 32-bit floats.
    ///
    ///     One handle and not three: RepLayout.cpp:4444 special-cases a struct named `Vector` into
    ///     ERepLayoutCmdType::PropertyVector before the recursion that would otherwise split it into
    ///     X/Y/Z, exactly as it does for Rotator and the quantized vectors. Getting that wrong
    ///     inside an array element would be invisible in the value and fatal in the numbering -
    ///     every later element's handles would be off by two per vector.
    ///
    ///     Distinct from <see cref="VectorQuantize10"/>/<see cref="VectorQuantize100"/>, which are
    ///     the PACKED encodings; this is the uncompressed one an ordinary FVector member gets.
    /// </summary>
    Vector,

    /// <summary>
    ///     An FText leaf - ONE handle, and no value serializer here.
    ///
    ///     UTextProperty is not one of the types AddPropertyCmd names (RepLayout.cpp:4424-4521), so
    ///     it falls through to the generic `ERepLayoutCmdType::Property` and travels as
    ///     UProperty::NetSerializeItem, i.e. FText's own history-based archive format. That format
    ///     is versioned, variable-shape (an FText can be a literal, a namespace/key lookup, or one
    ///     of a dozen generators) and nothing here has ever produced or verified a byte of it.
    ///
    ///     So this kind exists to RESERVE the handle, never to fill it. That is enough for every
    ///     use so far: an FText inside a replicated struct is configured by the Blueprint, the
    ///     client already holds it, and a server that never names it leaves it alone. See
    ///     <see cref="StructArray"/> for why leaving a member alone is a real option rather than a
    ///     gap.
    /// </summary>
    Text,

    /// <summary>
    ///     A TArray of non-atomic STRUCTS, sent as a partial update: the array's own handle, the
    ///     element count, and then only the members this server actually models, addressed by
    ///     element.
    ///
    ///     THE PARTIAL UPDATE IS THE POINT, and it is what makes replicating a configured array
    ///     possible at all. `PrepReceivedArray` (RepLayout.cpp:2682) resizes the client's array to
    ///     the count on the wire and does nothing else - and `FScriptArrayHelper::Resize` to the
    ///     size it already has is a no-op. So sending the count the client already has leaves every
    ///     element's data exactly as the Blueprint configured it, and the handles that follow
    ///     overwrite only the members named. A server does not have to know how to serialize an
    ///     FText to change who is sitting in a seat.
    ///
    ///     WIRE FORMAT, from SendProperties_r's DynamicArray branch (RepLayout.cpp:2020):
    ///     [array handle(packed)][ArrayNum(raw uint16)] then, in ASCENDING handle order,
    ///     [handle(packed)][value] for each member sent, then [0(packed)]. The handle for member j
    ///     (1-based, in <see cref="FRepPropertyDef.Children"/> order) of element i is
    ///     `i * Children.Length + j` - FRepHandleIterator::NextHandle (RepLayout.cpp:1568) inverts
    ///     exactly that division, and the receive side counts through the same space by walking
    ///     every element's cmds in one continuous handle counter.
    ///
    ///     Which is why <see cref="FRepPropertyDef.Children"/> has to list ALL of the struct's
    ///     replicated members, including the ones with no getter: they are what makes the divisor
    ///     right. A missing child does not lose a member, it corrupts every element after the first.
    /// </summary>
    StructArray
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

    /// <summary>
    ///     Elements for <see cref="ERepPropertyKind.StructArray"/> - each one is then read through
    ///     <see cref="Children"/>, so the element type is whatever those children's getters expect.
    /// </summary>
    public Func<object, IReadOnlyList<object>>? GetStructArrayValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.Name"/>.</summary>
    public Func<object, FName>? GetNameValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.String"/>.</summary>
    public Func<object, string>? GetStringValue { get; init; }

    /// <summary>Leaf value getter for <see cref="ERepPropertyKind.RepMovement"/>.</summary>
    public Func<object, Core.Math.FRepMovement>? GetRepMovementValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.NetId"/>.</summary>
    public Func<object, FUniqueNetIdRepl?>? GetNetIdValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.Int32"/> and <see cref="ERepPropertyKind.Int16"/>.</summary>
    public Func<object, int>? GetIntValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.Float"/>.</summary>
    public Func<object, float>? GetFloatValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.VectorQuantize10"/>, <see cref="ERepPropertyKind.VectorQuantize100"/> and <see cref="ERepPropertyKind.Vector"/>.</summary>
    public Func<object, FVector>? GetVectorValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.Rotator"/>.</summary>
    public Func<object, FRotator>? GetRotatorValue { get; init; }

    /// <summary>Only meaningful for <see cref="ERepPropertyKind.ByteEnum"/> - the enum's highest raw value (e.g. ENetRole.ROLE_MAX=4), matching UByteProperty::NetSerializeItem's CeilLogTwo(Enum-&gt;GetMaxEnumValue()).</summary>
    public int EnumMaxValue { get; init; }

    /// <summary>
    ///     For <see cref="ERepPropertyKind.StructRecurse"/> and <see cref="ERepPropertyKind.StructArray"/>,
    ///     in the struct's own offset order (name as tie-break, RepSkip members omitted) - the order
    ///     FCompareUFieldOffsets sorts by in InitFromProperty_r.
    /// </summary>
    public FRepPropertyDef[]? Children { get; init; }

    /// <summary>
    ///     Whether this server has a REAL value for this leaf, read off the instance - which is the
    ///     rule that decides what a <see cref="ERepPropertyKind.StructArray"/> element puts on the
    ///     wire and what it leaves exactly as the client configured it.
    ///
    ///     NOT "can this Kind be written". <see cref="ERepPropertyKind.EmptyDynamicArray"/> can
    ///     always be written - it is a constant - and it is FALSE here anyway, because at top level
    ///     it means "reserve this handle, this project cannot produce the real contents" and inside
    ///     an array element writing it would send an EMPTY array over data the client already holds
    ///     correctly. FAthenaCarPlayerSlot::ExitSockets is the case: the first version of this
    ///     property said true, which would have blanked every seat's exit sockets - the exact
    ///     destruction the partial-update design exists to avoid.
    /// </summary>
    public bool IsModelled => Kind switch {
        ERepPropertyKind.Bool or ERepPropertyKind.ByteEnum => GetByteValue != null,
        ERepPropertyKind.Int32 or ERepPropertyKind.Int16 => GetIntValue != null,
        ERepPropertyKind.Float or ERepPropertyKind.QuantizedBuildingAttribute => GetFloatValue != null,
        ERepPropertyKind.String => GetStringValue != null,
        ERepPropertyKind.Name => GetNameValue != null,
        ERepPropertyKind.NetId => GetNetIdValue != null,
        ERepPropertyKind.ObjectRef => GetObjectValue != null,
        ERepPropertyKind.ObjectRefArray => GetObjectArrayValue != null,
        ERepPropertyKind.StructArray => GetStructArrayValue != null,
        ERepPropertyKind.Rotator => GetRotatorValue != null,
        ERepPropertyKind.RepMovement => GetRepMovementValue != null,
        ERepPropertyKind.Vector or ERepPropertyKind.VectorQuantize10 or ERepPropertyKind.VectorQuantize100
            or ERepPropertyKind.VectorNormal => GetVectorValue != null,

        // Everything else is unmodelled by construction: EmptyDynamicArray for the reason above,
        // and StructAtomic/Text/StructRecurse have no value writer at all - see their own remarks.
        _ => false
    };
}
