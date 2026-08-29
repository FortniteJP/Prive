namespace AFortOnlineBeacon.Net.Rpc;

/// <summary>
///     Reads RPC call parameters off the wire. Real UE's non-InternalAck RPC receive path
///     (FRepLayout::ReceivePropertiesForRPC, RepLayout.cpp) walks a UFunction's parameters in
///     declaration order and, for every parameter that ISN'T a bool, reads one presence bit first -
///     bools skip the presence bit entirely and are always read directly, since a bool is already
///     only 1 bit and a separate presence flag would just double its cost for nothing. A 0 presence
///     bit means the caller left that parameter at its zero-constructed default and nothing follows
///     on the wire for it.
/// </summary>
public static class FRpcReader {
    public static unsafe object?[] ReadParams(FArchive bunch, FRpcParamDef[] paramDefs) {
        var values = new object?[paramDefs.Length];

        for (var i = 0; i < paramDefs.Length; i++) {
            var def = paramDefs[i];
            if (def.Kind != ERpcParamKind.Bool && !bunch.ReadBit()) continue;

            values[i] = def.Kind switch {
                ERpcParamKind.Bool => bunch.ReadBit(),
                ERpcParamKind.Byte => bunch.ReadByte(),
                ERpcParamKind.UInt32 => bunch.ReadUInt32(),
                ERpcParamKind.Int32 => bunch.ReadInt32(),
                ERpcParamKind.Guid => ReadGuid(bunch),
                ERpcParamKind.Object => ReadObject(bunch),
                ERpcParamKind.ObjectPath => ReadObjectPath(bunch),
                ERpcParamKind.Float => bunch.ReadFloat(),
                ERpcParamKind.Vector => FVector.NetSerializeRead(bunch),
                ERpcParamKind.VectorQuantize10 => FVector.NetSerializeReadQuantized(bunch, 10, 24),
                ERpcParamKind.VectorQuantize100 => FVector.NetSerializeReadQuantized(bunch, 100, 30),
                ERpcParamKind.Rotator => FRotator.NetSerializeRead(bunch),
                ERpcParamKind.String => bunch.ReadString(),
                ERpcParamKind.PredictionKey => FPredictionKey.NetSerializeRead(bunch),
                ERpcParamKind.TargetDataHandle => FGameplayAbilityTargetDataHandle.NetSerializeRead(bunch),
                ERpcParamKind.AbilityRpcBatch => FServerAbilityRPCBatch.NetSerializeRead(bunch),
                ERpcParamKind.CreateBuildingActorData => FCreateBuildingActorData.NetSerializeRead(bunch),
                _ => throw new NotSupportedException($"FRpcReader: unhandled param kind {def.Kind}")
            };
        }

        return values;
    }

    /// <summary>
    ///     Reads an object reference and resolves it. Returns null when the id names nothing this
    ///     server handed out - the handler must treat that the same way real UE treats an unmapped
    ///     object reference, i.e. as "the caller meant nothing I can act on".
    ///
    ///     This used to read the packed NetGUID and stop there, which is only correct for a guid the
    ///     SERVER assigned. A client naming anything else sends the default guid followed by an
    ///     export block (flags, outer chain, path, checksum); consuming just the guid left all of
    ///     that in the stream. UPackageMapClient.ReadObjectRef handles both shapes.
    /// </summary>
    private static UObject? ReadObject(FArchive bunch) => UPackageMapClient.ReadObjectRef(bunch, out _);

    /// <summary>See <see cref="ERpcParamKind.ObjectPath"/> - the path, when the reference carried one; null otherwise.</summary>
    private static string? ReadObjectPath(FArchive bunch) {
        UPackageMapClient.ReadObjectRef(bunch, out var path);
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>Mirrors FFastArraySerializerWriter's GuidToAbcd - four int32s in A/B/C/D order.</summary>
    private static Guid ReadGuid(FArchive bunch) {
        var bytes = new byte[16];

        for (var part = 0; part < 4; part++) {
            BitConverter.GetBytes(bunch.ReadInt32()).CopyTo(bytes, part * 4);
        }

        return new Guid(bytes);
    }
}
