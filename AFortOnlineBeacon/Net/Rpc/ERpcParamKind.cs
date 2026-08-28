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
    AbilityRpcBatch
}
