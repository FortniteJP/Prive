namespace AFortOnlineBeacon.Core.Math;

/// <summary>Minimal port of FRotator - just enough state and wire format to read RPC parameters.</summary>
public class FRotator {
    public float Pitch;
    public float Yaw;
    public float Roll;

    /// <summary>
    ///     Matches vanilla FRotator::NetSerialize -> SerializeCompressedShort (UnrealMath.cpp):
    ///     per axis, a presence bit (set only when the compressed value is nonzero) followed by an
    ///     optional 16-bit compressed angle, decompressed as Angle * 360 / 65536.
    /// </summary>
    public static FRotator NetSerializeRead(FArchive ar) => new() {
        Pitch = ReadAxis(ar),
        Yaw = ReadAxis(ar),
        Roll = ReadAxis(ar)
    };

    private static float ReadAxis(FArchive ar) => ar.ReadBit() ? ar.ReadUInt16() * 360f / 65536f : 0f;

    private static float DecompressAxisFromShort(uint compressed) => compressed * 360f / 65536f;

    /// <summary>
    ///     Send half of <see cref="NetSerializeRead"/> - FRotator::SerializeCompressedShort's saving
    ///     branch. Note the presence bit is computed from the COMPRESSED value, not the float: a
    ///     rotation that rounds to 0/65536 (an exact multiple of 360) sends a clear bit even though
    ///     the float is nonzero, which is what the reader above expects.
    /// </summary>
    public void NetSerializeWrite(FBitWriter ar) {
        WriteAxis(ar, Pitch);
        WriteAxis(ar, Yaw);
        WriteAxis(ar, Roll);
    }

    /// <summary>
    ///     FRotator::SerializeCompressed - the BYTE-per-axis form, which is what FRepMovement uses
    ///     (ERotatorQuantization::ByteComponents is FRepMovement's constructor default, EngineTypes.cpp:268).
    ///     Same shape as the short form above - a presence bit per axis computed from the COMPRESSED
    ///     value, then the byte - and the same trap: an angle that rounds to 0 or 256 sends a clear
    ///     bit however non-zero the float was.
    /// </summary>
    public void NetSerializeWriteCompressedByte(FBitWriter ar) {
        WriteAxisByte(ar, Pitch);
        WriteAxisByte(ar, Yaw);
        WriteAxisByte(ar, Roll);
    }

    private static void WriteAxisByte(FBitWriter ar, float angle) {
        var compressed = CompressAxisToByte(angle);
        ar.WriteBit(compressed != 0);
        if (compressed != 0) ar.WriteByte(compressed);
    }

    /// <summary>FRotator::CompressAxisToByte - map [0,360) onto [0,256) and mask off winding.</summary>
    private static byte CompressAxisToByte(float angle) =>
        (byte) ((int) MathF.Round(angle * 256f / 360f) & 0xFF);

    private static void WriteAxis(FBitWriter ar, float angle) {
        var compressed = CompressAxisToShort(angle);
        ar.WriteBit(compressed != 0);
        if (compressed != 0) ar.WriteUInt16(compressed);
    }

    /// <summary>FRotator::CompressAxisToShort - map [0,360) onto [0,65536) and mask off winding.</summary>
    private static ushort CompressAxisToShort(float angle) =>
        (ushort) ((int) MathF.Round(angle * 65536f / 360f) & 0xFFFF);

    /// <summary>Matches FRotator::Equals(FRotator::ZeroRotator, epsilon) - SerializeNewActor's own test.</summary>
    public bool IsNearlyZero(float epsilon = 0.001f) =>
        MathF.Abs(NormalizeAxis(Pitch)) <= epsilon &&
        MathF.Abs(NormalizeAxis(Yaw)) <= epsilon &&
        MathF.Abs(NormalizeAxis(Roll)) <= epsilon;

    /// <summary>FRotator::NormalizeAxis - fold an angle into (-180, 180].</summary>
    private static float NormalizeAxis(float angle) {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        else if (angle <= -180f) angle += 360f;
        return angle;
    }

    /// <summary>
    ///     Inverse of UCharacterMovementComponent::PackYawAndPitchTo32 (CharacterMovementComponent.h):
    ///     the "View" uint32 ServerMove-family RPCs carry is just Yaw and Pitch, each independently
    ///     run through FRotator::CompressAxisToShort and packed as (YawShort &lt;&lt; 16) | PitchShort -
    ///     no presence bits like SerializeCompressedShort uses, both axes are always present. Roll
    ///     isn't part of this packing (the RPCs carry it separately as ClientRoll).
    /// </summary>
    public static FRotator FromPackedView(uint view) => new() {
        Yaw = DecompressAxisFromShort(view >> 16),
        Pitch = DecompressAxisFromShort(view & 0xFFFF)
    };

    public override string ToString() => $"(P={Pitch:F2}, Y={Yaw:F2}, R={Roll:F2})";
}
