using AFortOnlineBeacon.Runtime;
using AFortOnlineBeacon.Serialization;

namespace AFortOnlineBeacon.Core.Math;

/// <summary>
///     FRepMovement - AActor::ReplicatedMovement, wire handle 6, and THE reason one client could not
///     see another one move.
///
///     Everything else about another player already worked: their pawn spawns, is possessed, holds a
///     weapon and animates. But its POSITION only ever arrived once, in the actor-spawn header, so
///     from every other client's point of view they stood still at the point they were created. This
///     server accepts the owner's own ClientLoc through ServerMoveNoBase and writes it to the pawn -
///     it just never told anybody else.
///
///     The wire format is EngineTypes.h:3074 verbatim:
///
///         2 bits   bSimulatedPhysicSleep, bRepPhysics
///         Location        SerializePackedVector&lt;100, 30&gt;  (EVectorQuantization::RoundTwoDecimals)
///         Rotation        SerializeCompressed              (ERotatorQuantization::ByteComponents)
///         LinearVelocity  SerializePackedVector&lt;1, 24&gt;    (RoundWholeNumber)
///         AngularVelocity ONLY if bRepPhysics
///
///     LOCATION IS NOT THE ENGINE DEFAULT, and assuming it was cost a round. FRepMovement's own
///     constructor (EngineTypes.cpp:259-269) sets all three levels to RoundWholeNumber /
///     RoundWholeNumber / ByteComponents, and PlayerPawn_Athena's cooked archetype carries no
///     ReplicatedMovement override - so both sources said whole numbers. **Fortnite overrides
///     LocationQuantizationLevel in a NATIVE CDO**, which a pak reader cannot see by construction,
///     and the paks were the only place that was checked.
///
///     What settled it was the CLIENT PRINTING ITS OWN SETTINGS. `GetAll FortPlayerPawnAthena
///     ReplicatedMovement` ends with
///     `LocationQuantizationLevel=RoundTwoDecimals, VelocityQuantizationLevel=RoundWholeNumber,
///     RotationQuantizationLevel=ByteComponents` - the three levels, named, from the machine that
///     actually decodes the bytes. The two are DIFFERENT from each other, which is exactly the shape
///     a single shared setting gets wrong.
///
///     The symptom was unmistakable once the same probe showed the value: the remote pawn sat at
///     `(-1169.19, -1212.65, 39.41)` for a server-side `(-116919, -121265, 3941)` - **one hundredth**,
///     so a player walking on the far side of the map moved a few centimetres near the world origin.
///
///     REP_MOVEMENT_SCALE still overrides the location scale, and is now a diagnostic rather than a
///     guess: a wrong scale is visible as exactly this, a remote player at 1/100 or 100x the right
///     distance from the origin.
/// </summary>
public sealed class FRepMovement {
    public FVector Location { get; set; } = new();
    public FRotator Rotation { get; set; } = new();
    public FVector LinearVelocity { get; set; } = new();

    /// <summary>EVectorQuantization - 1/24 is RoundWholeNumber, 10/27 RoundOneDecimal, 100/30 RoundTwoDecimals.</summary>
    private static readonly (uint Scale, uint Bits) DefaultLocationQuantization =
        FBeaconProcess.Options.Get("REP_MOVEMENT_SCALE") switch {
            "1" => (1u, 24u),
            "10" => (10u, 27u),
            _ => (100u, 30u)   // RoundTwoDecimals - what the client reports for AFortPlayerPawn
        };

    /// <summary>RoundWholeNumber, and NOT the same as the location's - see the class comment.</summary>
    private static readonly (uint Scale, uint Bits) VelocityQuantization = (1u, 24u);

    /// <summary>
    ///     FRepMovement::LocationQuantizationLevel - PER ACTOR CLASS, and NOT ON THE WIRE. Both ends
    ///     read it from their own copy of the struct, so a mismatch is not an error anywhere: the
    ///     client simply decodes the bits with the wrong scale and puts the actor somewhere absurd.
    ///
    ///     The 100/30 above is RoundTwoDecimals, confirmed against AFortPlayerPawn - and every actor
    ///     here inherited it. UE's own default is RoundWholeNumber, and a projectile does not override
    ///     it, so a grenade sent at the pawn's scale vanished the instant it was thrown while the
    ///     server went on simulating and damaging correctly.
    /// </summary>
    public (uint Scale, uint Bits) LocationQuantization { get; set; } = DefaultLocationQuantization;

    /// <summary>EVectorQuantization::RoundWholeNumber - the engine default, and what a projectile uses.</summary>
    public static readonly (uint Scale, uint Bits) RoundWholeNumber = (1u, 24u);

    public void NetSerializeWrite(FBitWriter ar) {
        // The two physics flags, always both clear here: this server has no physics simulation, so
        // AngularVelocity is never serialised either (it is written only when bRepPhysics is set).
        ar.WriteBit(false);   // bSimulatedPhysicSleep
        ar.WriteBit(false);   // bRepPhysics

        Location.NetSerializeWriteQuantized(ar, LocationQuantization.Scale, LocationQuantization.Bits);
        Rotation.NetSerializeWriteCompressedByte(ar);
        LinearVelocity.NetSerializeWriteQuantized(ar, VelocityQuantization.Scale, VelocityQuantization.Bits);
    }

    /// <summary>Value form, for the shadow-state comparison - see FRepLayout.SnapshotValue.</summary>
    public override string ToString() => $"{Location}|{Rotation}|{LinearVelocity}";
}
