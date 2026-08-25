namespace AFortOnlineBeacon.Net.Replication;

/// <summary>
///     TEMPORARY diagnostic-only helper (2026-08-24, reintroduced). Identifies an unknown
///     FRepLayout handle on the real Fortnite 10.40 client by deliberately sending a too-narrow
///     (1-bit) probe value at a specific absolute handle, alongside a known-good property prefix -
///     the client's own "ReceiveProperties_r: ... Property=X" error names whatever real property
///     actually occupies that handle. Originally used to find AttachmentReplication's real span
///     (7-12); now reused to find AFortPlayerController::WorldInventory's real handle, since the
///     SDK/NetFields.txt-derived estimate (31) was proven wrong live - a real client named
///     DelayedQuickBarActions (declared several properties AFTER WorldInventory) at handle 49,
///     18 higher than expected, meaning something in between (most likely IntensityGraphInfo/
///     PIDValuesGraphInfo/PIDContributionsGraphInfo - "graph"/curve-data-shaped names) spans many
///     more handles than 1 each. Delete this file and the REPLAYOUT_PROBE_HANDLE hook in
///     UActorChannel.ReplicateActor once WorldInventory's real handle is confirmed and
///     NativeRepLayouts.PlayerControllerProps is corrected to match.
/// </summary>
internal static class HandleProbe {
    /// <summary>
    ///     Builds a PlayerController-shaped probe layout: the confirmed-good prefix through
    ///     bHasServerFinishedLoading (handle 22), then enough 1-bit reserved placeholders to land a
    ///     probe leaf at <paramref name="targetHandle"/>.
    /// </summary>
    public static FRepLayout BuildPlayerControllerProbeLayout(int targetHandle) {
        if (targetHandle < 23) {
            throw new ArgumentOutOfRangeException(nameof(targetHandle), "Handles 1-22 are already confirmed - no need to probe them.");
        }

        var props = new List<FRepPropertyDef> {
            Reserved("bHidden"), Reserved("bReplicateMovement"), Reserved("bTearOff"), Reserved("bCanBeDamaged"),
            new() {
                Name = "RemoteRole", Kind = ERepPropertyKind.ByteEnum, EnumMaxValue = (int) ENetRole.ROLE_MAX,
                GetByteValue = obj => (byte) ((AActor) obj).RemoteRole
            },
            Reserved("ReplicatedMovement"),
            Reserved("AttachmentReplication[0]"), Reserved("AttachmentReplication[1]"), Reserved("AttachmentReplication[2]"),
            Reserved("AttachmentReplication[3]"), Reserved("AttachmentReplication[4]"), Reserved("AttachmentReplication[5]"),
            Reserved("Owner"),
            new() {
                Name = "Role", Kind = ERepPropertyKind.ByteEnum, EnumMaxValue = (int) ENetRole.ROLE_MAX,
                GetByteValue = obj => (byte) ((AActor) obj).Role
            },
            Reserved("Instigator"), // handle 15
            Reserved("PlayerState"), // 16 (ObjectRef in production, Bool here - fine, not sending it)
            Reserved("Pawn"), // 17
            Reserved("TargetViewRotation"), // 18
            Reserved("SpawnLocation"), // 19
            Reserved("bFailedToRespawn"), // 20
            Reserved("bHasInitiallySpawned"), // 21
            Reserved("bHasServerFinishedLoading") // 22
        };

        for (var handle = 23; handle < targetHandle; handle++) {
            props.Add(new FRepPropertyDef { Name = $"__reserved_{handle}", Kind = ERepPropertyKind.Bool });
        }

        // A 1-bit probe (value=1) isn't forceful enough for object-reference (NetGUID) handles:
        // FNetworkGUID's wire format is a self-terminating packed-int (SerializeIntPacked), and our
        // single real bit plus the surrounding zero-padded reserved slots can decode as a
        // perfectly well-formed "value=0" (null reference) with no error at all - confirmed live
        // 2026-08-24 (handles 50 and 51 both came back silent even though *something* real is
        // there). A full byte of all-1 bits is far more disruptive regardless of the real
        // property's type: for a bool it overflows into the next handle's region with garbage;
        // for a packed-int GUID, a 0xFF first byte sets the continuation flag, forcing the reader
        // to keep consuming bytes it doesn't actually have, reliably running off the end.
        props.Add(new FRepPropertyDef {
            Name = "Probe",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = 256,
            GetByteValue = _ => 0xFF
        });

        return new FRepLayout(props);
    }

    private static FRepPropertyDef Reserved(string name) => new() { Name = name, Kind = ERepPropertyKind.Bool };
}
