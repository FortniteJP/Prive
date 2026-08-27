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
                ERpcParamKind.Float => bunch.ReadFloat(),
                ERpcParamKind.Vector => FVector.NetSerializeRead(bunch),
                ERpcParamKind.VectorQuantize10 => FVector.NetSerializeReadQuantized(bunch, 10, 24),
                ERpcParamKind.VectorQuantize100 => FVector.NetSerializeReadQuantized(bunch, 100, 30),
                ERpcParamKind.Rotator => FRotator.NetSerializeRead(bunch),
                ERpcParamKind.String => bunch.ReadString(),
                ERpcParamKind.PredictionKey => ReadPredictionKey(bunch),
                _ => throw new NotSupportedException($"FRpcReader: unhandled param kind {def.Kind}")
            };
        }

        return values;
    }

    /// <summary>
    ///     FPredictionKey::NetSerialize (GameplayPrediction.h). Note the middle bit is CONDITIONAL:
    ///     HasBaseKey is only on the wire when the key is valid for this connection, so reading it
    ///     unconditionally would shift everything after it.
    /// </summary>
    private static FPredictionKey ReadPredictionKey(FArchive bunch) {
        var key = new FPredictionKey { bValidKeyForConnection = bunch.ReadBit() };

        var hasBaseKey = false;
        if (key.bValidKeyForConnection) hasBaseKey = bunch.ReadBit();

        key.bIsServerInitiated = bunch.ReadBit();

        if (key.bValidKeyForConnection) key.Current = (short) bunch.ReadUInt16();
        if (hasBaseKey) key.Base = (short) bunch.ReadUInt16();

        return key;
    }

    /// <summary>
    ///     Reads a packed NetGUID and resolves it. Returns null when the archive carries no package
    ///     map or the id names nothing - the handler must treat that the same way real UE treats an
    ///     unmapped object reference, i.e. as "the caller meant nothing I can act on".
    /// </summary>
    private static unsafe UObject? ReadObject(FArchive bunch) {
        var netGuid = new FNetworkGUID();
        netGuid.NetSerialize(bunch);

        if (bunch.IsError() || bunch is not FNetBitReader { PackageMap: UPackageMapClient packageMap }) return null;

        return packageMap.GuidCache?.GetObjectFromNetGUID(netGuid);
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
