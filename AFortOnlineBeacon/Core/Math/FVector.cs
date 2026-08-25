namespace AFortOnlineBeacon.Core.Math;

/// <summary>Minimal port of FVector - just enough state and wire format to read RPC parameters.</summary>
public class FVector {
    public float X;
    public float Y;
    public float Z;

    /// <summary>
    ///     Matches vanilla FVector::NetSerialize (UnrealMath.cpp): three raw, uncompressed floats,
    ///     no presence bits or quantization (that's what the FVector_NetQuantize* variants add).
    /// </summary>
    public static FVector NetSerializeRead(FArchive ar) => new() {
        X = ar.ReadFloat(),
        Y = ar.ReadFloat(),
        Z = ar.ReadFloat()
    };

    /// <summary>
    ///     Matches ReadPackedVector&lt;ScaleFactor, MaxBitsPerComponent&gt; (NetSerialization.h) - used
    ///     by FVector_NetQuantize/10/100 (scaleFactor/maxBitsPerComponent = 1/20, 10/24, 100/30
    ///     respectively). Format: a bounded-int bit-count header, then each component as a bounded
    ///     int biased so it can represent negative values, descaled back to a float on the way out.
    /// </summary>
    public static FVector NetSerializeReadQuantized(FArchive ar, uint scaleFactor, uint maxBitsPerComponent) {
        var bits = ar.ReadInt(maxBitsPerComponent);
        var bias = 1 << (int) (bits + 1);
        var max = (uint) (1 << (int) (bits + 2));

        var dx = ar.ReadInt(max);
        var dy = ar.ReadInt(max);
        var dz = ar.ReadInt(max);

        return new FVector {
            X = ((int) dx - bias) / (float) scaleFactor,
            Y = ((int) dy - bias) / (float) scaleFactor,
            Z = ((int) dz - bias) / (float) scaleFactor
        };
    }

    public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
}
