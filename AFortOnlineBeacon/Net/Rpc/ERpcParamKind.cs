namespace AFortOnlineBeacon.Net.Rpc;

/// <summary>The wire encodings this minimal RPC reader knows how to decode.</summary>
public enum ERpcParamKind {
    Bool,
    Byte,
    UInt32,
    Int32,

    /// <summary>
    ///     An FGuid parameter. FGuid is not one of the structs RepLayout special-cases as atomic, so
    ///     SerializeProperties_r walks straight into its four int32 members A/B/C/D - 128 raw bits,
    ///     no handles, no terminator.
    /// </summary>
    Guid,

    /// <summary>
    ///     An object reference parameter (e.g. AFortPlayerPawn::ServerHandlePickup's AFortPickup*).
    ///     UObjectProperty::NetSerializeItem defers to UPackageMap::SerializeObject, which on the
    ///     wire is just the packed NetGUID - the same encoding an ObjectRef property uses. Resolved
    ///     back to the actual object through the reader's own package map, so a GUID this server
    ///     never handed out decodes as null rather than as a wrong object.
    /// </summary>
    Object,

    /// <summary>
    ///     Same wire shape as <see cref="Object"/> (a TSubclassOf&lt;T&gt; is a UObjectPropertyBase
    ///     underneath too), but for when the reference names a CLASS this server never assigned a
    ///     NetGUID to - which is always, for any class the client itself picks (e.g.
    ///     ServerSetPlayerBuildableClass's TSubclassOf&lt;ABuildingSMActor&gt;). That arrives as a path
    ///     export (default guid + flags + outer chain + name), which UPackageMapClient.ReadObjectRef
    ///     consumes correctly but resolves to null (nothing THIS server handed out). The path itself
    ///     is still there and still useful - this kind keeps it instead of discarding it.
    /// </summary>
    ObjectPath,

    Float,
    Vector,
    VectorQuantize10,
    VectorQuantize100,
    Rotator,
    String,

    /// <summary>
    ///     GameplayAbilities' FPredictionKey - a conditional bit layout rather than a fixed-size
    ///     value. See FPredictionKey for the shape and the measurement that confirmed it.
    /// </summary>
    PredictionKey,

    /// <summary>
    ///     An FGameplayAbilityTargetDataHandle - what the client says it hit. A tagged union whose
    ///     tag is a full UScriptStruct path; see FGameplayAbilityTargetDataHandle.
    /// </summary>
    TargetDataHandle,

    /// <summary>
    ///     An FServerAbilityRPCBatch parameter. A struct parameter is still ONE parameter: it takes
    ///     one leading "send" bit and then all of its members back to back, so it must be declared
    ///     as a single param rather than as its five members - declaring the members separately
    ///     would read four presence bits that are not on the wire.
    /// </summary>
    AbilityRpcBatch,

    /// <summary>
    ///     ServerCreateBuildingActor's FCreateBuildingActorData - where a placed building goes. A
    ///     STRUCT_NetSerializeNative struct, so it is ONE parameter with one leading send bit and a
    ///     hand-written body that matches neither declaration nor memory order. See
    ///     <see cref="FCreateBuildingActorData"/> for the layout and how it was derived.
    /// </summary>
    CreateBuildingActorData
}
