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
                ERpcParamKind.Float => bunch.ReadFloat(),
                ERpcParamKind.Vector => FVector.NetSerializeRead(bunch),
                ERpcParamKind.VectorQuantize10 => FVector.NetSerializeReadQuantized(bunch, 10, 24),
                ERpcParamKind.VectorQuantize100 => FVector.NetSerializeReadQuantized(bunch, 100, 30),
                ERpcParamKind.Rotator => FRotator.NetSerializeRead(bunch),
                ERpcParamKind.String => bunch.ReadString(),
                _ => throw new NotSupportedException($"FRpcReader: unhandled param kind {def.Kind}")
            };
        }

        return values;
    }
}
