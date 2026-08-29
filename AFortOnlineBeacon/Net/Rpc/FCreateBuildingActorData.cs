namespace AFortOnlineBeacon.Net.Rpc;

/// <summary>
///     FortniteGame.CreateBuildingActorData - the single parameter of
///     AFortPlayerController::ServerCreateBuildingActor, and the thing that decides where a placed
///     building actually goes.
///
///     This struct is STRUCT_NetSerializeNative, so RepLayout does NOT walk its members: InitFromProperty_r
///     (RepLayout.cpp) takes the NetSerializeNative early-out and emits ONE generic cmd for the whole
///     struct, which SerializeProperties_r then hands to a hand-written native NetSerialize. That is
///     why neither a flat member dump nor a RepLayout-handle sequence ever decoded it, and why the
///     wire order below matches neither declaration order nor memory order.
///
///     Confirmed two independent ways rather than guessed:
///
///     1. Both PR3.0 logs contain exactly one line -
///        `AddPropertyCmd: Falling back to default type for property [StructProperty
///        /Script/FortniteGame.FortPlayerController:ServerCreateBuildingActor.CreateBuildingData]` -
///        naming the STRUCT itself and none of its members. AddPropertyCmd only ever receives a
///        UStructProperty from InitFromProperty_r's NetSerializeNative branch, so that line alone
///        proves the flag is set.
///
///     2. Disassembled from a full client memory dump (Tools/BinXref/dumpxref.py; the exe's .text is
///        encrypted on disk but decrypted in a live process). Route: the generated FStructParams for
///        "CreateBuildingActorData" -> its StructOpsFunc -> the TCppStructOps vtable -> slot 12
///        HasNetSerializer returns 1 -> slot 14 tail-calls FCreateBuildingActorData::NetSerialize.
///        The wire order below is read straight off that function.
///
///     Wire order (total 177 or 178 bits, depending only on BuildingClassHandle's bounded-int width):
///
///         BuildingClassHandle                Ar.SerializeInt(&v, 0x1FF)   8 or 9 bits
///         bMirrored                          Ar.Serialize(&v, 4)          32 bits (bool as legacy UBOOL)
///         BuildLoc                           Ar &lt;&lt; FVector              96 bits (three RAW floats)
///         BuildingClassData.UpgradeLevel     Ar.Serialize(&v, 1)          8 bits
///         SyncKey                            Ar.SerializeInt(&v, 1&lt;&lt;24)  24 bits, quantized
///         BuildRot.Yaw                       Ar.Serialize(&v, 1)          8 bits, 4-value enum
///
///     Three details matter and none of them are guessable from the SDK's declaration:
///
///     - BuildLoc is declared FVector_NetQuantize10 but is NOT quantized on the wire. The native
///       NetSerialize just does `Ar &lt;&lt; BuildLoc`, which picks vanilla FVector's operator&lt;&lt; (three
///       raw float32s) and never reaches FVector_NetQuantize10::NetSerialize at all. Every earlier
///       attempt to read it as SerializePackedVector&lt;10,24&gt; failed for this reason.
///     - Only BuildRot.YAW is transmitted, as a byte holding one of four codes. Pitch and Roll are
///       never written, so they are whatever ReceivePropertiesForRPC zero-initialised them to: 0.
///     - BuildingClassData.BuildingClass and .PreviousBuildingLevel are never written either. The
///       building CLASS does not come from this RPC - it comes from the player's
///       BroadcastRemoteClientInfo.RemoteBuildableClass, set by an earlier
///       ServerSetPlayerBuildableClass. Project-Reboot-3.0 reads it the same way
///       (FortPlayerController.cpp's ServerCreateBuildingActorHook, the Fortnite_Version >= 8.30 branch).
///
///     Validated against 940 real captured samples from this project's own live traffic: every one
///     consumes exactly its declared 178 bits with zero left over, every BuildLoc lands on the
///     building grid (X/Y multiples of 256, Z of 128), every yaw is one of the four legal values,
///     and SyncKey increases monotonically across a session.
/// </summary>
public class FCreateBuildingActorData {
    /// <summary>Index into the player's own buildable-class list. Not resolvable server-side without that list; the class comes from RemoteBuildableClass instead - see the class doc comment.</summary>
    public uint BuildingClassHandle;

    /// <summary>Where the client's own ghost was standing. Already grid-snapped by the client - use it verbatim, exactly as a real server does.</summary>
    public FVector BuildLoc = new();

    /// <summary>Yaw only, and only ever 180 / 90 / 0 / -90. Pitch and Roll are never sent and are always 0.</summary>
    public FRotator BuildRot = new();

    public bool bMirrored;

    /// <summary>Client-side placement timestamp, quantized to 1/13 of a unit and biased by 2^23.</summary>
    public float SyncKey;

    /// <summary>The only member of BuildingClassData that reaches the wire.</summary>
    public byte UpgradeLevel;

    /// <summary>The four yaw values a building piece can be placed at, indexed by the byte code on the wire.</summary>
    private static readonly float[] YawForCode = { 180f, 90f, 0f, -90f };

    public static unsafe FCreateBuildingActorData NetSerializeRead(FArchive ar) {
        var data = new FCreateBuildingActorData {
            BuildingClassHandle = ar.ReadInt(0x1FF),
            // `Ar << bool` serializes a bool as a 32-bit legacy UBOOL, not as one bit.
            bMirrored = ar.ReadUInt32() != 0,
            BuildLoc = FVector.NetSerializeRead(ar),
            UpgradeLevel = ar.ReadByte()
        };

        // The saving side is round(SyncKey * 13) + 2^23 clamped to [0, 2^24); this is its exact inverse.
        data.SyncKey = ((int) ar.ReadInt(0x1000000) - 0x800000) / 13f;

        // The native reader is a switch on 0/1/2 with everything else falling through to the last
        // case, so a code above 3 means -90 rather than an error.
        var yawCode = ar.ReadByte();
        data.BuildRot = new FRotator { Yaw = YawForCode[System.Math.Min(yawCode, YawForCode.Length - 1)] };

        return data;
    }

    public override string ToString() =>
        $"BuildLoc={BuildLoc} Yaw={BuildRot.Yaw} Mirrored={bMirrored} Handle={BuildingClassHandle} " +
        $"UpgradeLevel={UpgradeLevel} SyncKey={SyncKey:F2}";
}
