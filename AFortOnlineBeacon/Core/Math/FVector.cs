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

    /// <summary>
    ///     Matches SerializeFixedVector&lt;MaxValue, NumBits&gt; (NetSerialization.h) - a completely
    ///     different encoding from the packed one above: no bit-count header, every component a
    ///     fixed-width biased int. FVector_NetQuantizeNormal is 1/16, i.e. three flat 16-bit fields,
    ///     which is what an FHitResult's Normal and ImpactNormal use.
    /// </summary>
    public static FVector NetSerializeReadFixed(FArchive ar, int maxValue, int numBits) => new() {
        X = ReadFixedCompressedFloat(ar, maxValue, numBits),
        Y = ReadFixedCompressedFloat(ar, maxValue, numBits),
        Z = ReadFixedCompressedFloat(ar, maxValue, numBits)
    };

    /// <summary>ReadFixedCompressedFloat&lt;MaxValue, NumBits&gt; (NetSerialization.h:1820).</summary>
    private static float ReadFixedCompressedFloat(FArchive ar, int maxValue, int numBits) {
        var maxBitValue = (1 << (numBits - 1)) - 1;
        var bias = 1 << (numBits - 1);
        var serIntMax = (uint) (1 << numBits);

        var unscaled = (float) ((int) ar.ReadInt(serIntMax) - bias);

        // Scaling up (MaxValue <= MaxBitValue) is the normal case - a unit normal at 16 bits has
        // 32767 steps per unit. Scaling DOWN only happens for a range wider than the bit budget.
        return maxValue > maxBitValue
            ? unscaled * (maxValue / (float) maxBitValue)
            : unscaled / (maxBitValue / maxValue);
    }

    /// <summary>
    ///     Matches SerializeFixedVector&lt;MaxValue, NumBits&gt; on the WRITE side - the send half of
    ///     <see cref="NetSerializeReadFixed"/>, and nothing like the packed encoding below it: no
    ///     bit-count header, three flat biased ints of NumBits each.
    ///
    ///     FVector_NetQuantizeNormal is 1/16, i.e. three 16-bit fields - the shape an
    ///     FAthenaBatchedDamageGameplayCues_Shared::Normal and an FHitResult's normals use.
    /// </summary>
    public void NetSerializeWriteFixed(FBitWriter ar, int maxValue, int numBits) {
        WriteFixedCompressedFloat(ar, X, maxValue, numBits);
        WriteFixedCompressedFloat(ar, Y, maxValue, numBits);
        WriteFixedCompressedFloat(ar, Z, maxValue, numBits);
    }

    /// <summary>
    ///     WriteFixedCompressedFloat&lt;MaxValue, NumBits&gt; (NetSerialization.h:1782). Note the
    ///     asymmetry with the read: the clamp saturates at MaxDelta = (1&lt;&lt;NumBits)-1 while the
    ///     value itself is written with SerializeInt(SerIntMax = 1&lt;&lt;NumBits), so the widest
    ///     legal value is one below the int bound.
    /// </summary>
    private static void WriteFixedCompressedFloat(FBitWriter ar, float value, int maxValue, int numBits) {
        var maxBitValue = (1 << (numBits - 1)) - 1;
        var bias = 1 << (numBits - 1);
        var serIntMax = (uint) (1 << numBits);
        var maxDelta = (uint) ((1 << numBits) - 1);

        // Scaling UP (MaxValue <= MaxBitValue) is the normal case and rounds; scaling down
        // truncates. Both branches are the engine's, including which one rounds.
        var scaled = maxValue > maxBitValue
            ? (int) (value * (maxBitValue / (float) maxValue))
            : (int) MathF.Round(value * (maxBitValue / maxValue));

        var delta = (uint) (scaled + bias);
        if (delta > maxDelta) delta = (int) delta > 0 ? maxDelta : 0;

        ar.WriteIntWrapped(delta, serIntMax);
    }

    /// <summary>
    ///     Matches WritePackedVector&lt;ScaleFactor, MaxBitsPerComponent&gt; (NetSerialization.h) - the
    ///     send half of NetSerializeReadQuantized above, and the encoding
    ///     UPackageMapClient::SerializeNewActor uses for a spawned actor's Location (FVector_NetQuantize10,
    ///     i.e. 10/24).
    ///
    ///     The bit-count header is CeilLogTwo(1 + max|component|) clamped to [1, MaxBitsPerComponent]
    ///     then minus one, so the three components that follow are each bounded ints of the same
    ///     width, biased by 1&lt;&lt;(Bits+1) so negatives fit.
    /// </summary>
    public void NetSerializeWriteQuantized(FBitWriter ar, uint scaleFactor, uint maxBitsPerComponent) {
        // Real UE clamps to these before rounding, because some platforms' RoundToInt effectively
        // caps the usable input range at 2^31.
        const float minV = -1073741824.0f;
        const float maxV = 1073741760.0f;

        var intX = (int) MathF.Round(System.Math.Clamp(X * scaleFactor, minV, maxV));
        var intY = (int) MathF.Round(System.Math.Clamp(Y * scaleFactor, minV, maxV));
        var intZ = (int) MathF.Round(System.Math.Clamp(Z * scaleFactor, minV, maxV));

        var largest = System.Math.Max(System.Math.Max(System.Math.Abs(intX), System.Math.Abs(intY)), System.Math.Abs(intZ));
        var bits = System.Math.Clamp(CeilLogTwo(1u + (uint) largest), 1u, maxBitsPerComponent) - 1u;

        ar.WriteIntWrapped(bits, maxBitsPerComponent);

        var bias = 1 << (int) (bits + 1);
        var max = (uint) (1 << (int) (bits + 2));

        WriteComponent(ar, intX, bias, max);
        WriteComponent(ar, intY, bias, max);
        WriteComponent(ar, intZ, bias, max);
    }

    private static void WriteComponent(FBitWriter ar, int value, int bias, uint max) {
        var d = (uint) (value + bias);
        if (d >= max) d = value + bias > 0 ? max - 1 : 0;
        ar.WriteIntWrapped(d, max);
    }

    /// <summary>FMath::CeilLogTwo - 0 for 0 or 1, otherwise the number of bits needed to hold value-1.</summary>
    private static uint CeilLogTwo(uint value) {
        if (value <= 1) return 0;
        return 32u - (uint) System.Numerics.BitOperations.LeadingZeroCount(value - 1u);
    }

    /// <summary>Matches FVector::Equals(FVector::ZeroVector, epsilon) - SerializeNewActor's own test.</summary>
    public bool IsNearlyZero(float epsilon = 0.001f) =>
        MathF.Abs(X) <= epsilon && MathF.Abs(Y) <= epsilon && MathF.Abs(Z) <= epsilon;

    /// <summary>
    ///     Matches FVector::Equals(Other, epsilon). SerializeNewActor tests the scale against
    ///     (1,1,1) rather than zero, which is what this exists for.
    /// </summary>
    public bool EqualsNearly(float x, float y, float z, float epsilon = 0.001f) =>
        MathF.Abs(X - x) <= epsilon && MathF.Abs(Y - y) <= epsilon && MathF.Abs(Z - z) <= epsilon;

    public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
}
