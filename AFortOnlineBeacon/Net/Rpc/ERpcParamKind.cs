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
    String
}
