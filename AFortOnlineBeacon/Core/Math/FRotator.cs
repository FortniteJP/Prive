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
