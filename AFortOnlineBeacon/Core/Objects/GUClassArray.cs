namespace AFortOnlineBeacon.Core.Objects;

public class GUClassArray {
    private static readonly Dictionary<Type, UClass> Classes = new();

    // Maps our C# actor classes to a REAL, always-loaded native UE class path the real client can
    // resolve via StaticFindObject (e.g. "/Script/Engine.PlayerController"). We don't have Fortnite's
    // own gameplay class paths yet (needs SDK dumps), so only classes with a native engine equivalent
    // can currently be replicated to a real client - see UClass.NativePackagePath.
    private static readonly Dictionary<Type, string> NativePackagePaths = new() {
        // Real PR3.0 traffic capture (2026-08-24, packets_ProjectReboot3.0.log packet #24) shows
        // the actual class exported to the client is a BLUEPRINT subclass of
        // FortPlayerControllerAthena - "/Game/Athena/Athena_PlayerController.Athena_PlayerController_C"
        // (with default-object export name "Default__Athena_PlayerController_C") - not the bare
        // native class this used to point at. The Blueprint's own client-side BeginPlay/
        // construction script (which only runs if the CLIENT actually spawns an instance of that
        // exact Blueprint class, not just its native parent) is a strong candidate for whatever
        // client-side setup the loading screen dismissal has been silently waiting on all session -
        // this project already relies on the same reasoning for the Pawn mapping below. All the
        // native FortPlayerControllerAthena properties/RPCs this project already sends still exist
        // at the same handles on the Blueprint subclass (a Blueprint only ever APPENDS properties
        // after its native parent's), so none of NativeRepLayouts/NativeClassNetCache's hard-won
        // handle numbering needs to change for this switch.
        [typeof(APlayerController)] = "/Game/Athena/Athena_PlayerController.Athena_PlayerController_C",
        // The real Athena playable character Blueprint (confirmed via a static SDK dump,
        // PriveDev/raider3.5: "BlueprintGeneratedClass PlayerPawn_Athena.PlayerPawn_Athena_C",
        // hierarchy APlayerPawn_Athena_C -> ... -> AFortPlayerPawnAthena -> AFortPlayerPawn ->
        // AFortPawn -> ACharacter -> APawn) rather than the bare native APawn - the client needs
        // the real class to have a mesh/animation blueprint/CharacterMovementComponent at all;
        // "/Script/Engine.Pawn" alone spawns something with nothing to see or move. This assumes
        // the client already has the class loaded (it's the actual gameplay pawn for the current
        // mode) - if not, SerializeNewActor's Archetype reference will fail to resolve client-side
        // the same way an unloaded class always does.
        [typeof(APawn)] = "/Game/Athena/PlayerPawn_Athena.PlayerPawn_Athena_C",
        // Same PR3.0 capture (packet #205) shows GameState is ALSO spawned as a Blueprint
        // subclass - "/Game/Athena/Athena_GameState.Athena_GameState_C" (default-object export
        // "Default__Athena_GameState_C") - not the bare native FortGameStateAthena this used to
        // point at. See the PlayerController mapping above for why this likely matters for
        // loading-screen dismissal, and why it doesn't disturb existing handle numbering.
        [typeof(AGameState)] = "/Game/Athena/Athena_GameState.Athena_GameState_C",
        // The other half of the loading-screen-dismissal gate (bHasStartedPlaying). Chain
        // confirmed via the same SDK dump: AFortPlayerStateAthena -> AFortPlayerStatePvP ->
        // AFortPlayerStateZone -> AFortPlayerState -> APlayerState -> AInfo.
        [typeof(APlayerState)] = "/Script/FortniteGame.FortPlayerStateAthena",
        // AFortPlayerController::WorldInventory's real engine type - confirmed via the same PR3.0
        // capture's multi-shift string scan (packet #213 contains "Default__FortInventory") plus
        // ObjectsDump.txt listing "/Script/FortniteGame.FortInventory" as a real native class. A
        // live client confirmed this is really an ACTOR class ("Sub-object cannot be actor class"
        // rejecting the sub-object-content-block approach) - see AFortInventory's doc comment.
        [typeof(AFortInventory)] = "/Script/FortniteGame.FortInventory",
        // The native TimeOfDayManager. Override with TODM_CLASS - see below for why the Battle
        // Royale Blueprint is not the default yet.
        //
        // /Game/TimeOfDay/TODM/BR/TODM_BR.TODM_BR_C is the "right" class (its CDO carries the
        // SkyboxFog*/day-phase settings) and was tried first, but the client does not preload it,
        // and a path export only NAMES an asset - it does not stream one. The client's log:
        //
        //   GetObjectFromNetGUID: Async loading package. Path: /Game/TimeOfDay/TODM/BR/TODM_BR
        //   Error: UPackageMapClient::SerializeNewActor. Unresolved Archetype GUID.
        //           Path: Default__TODM_BR_C, NetGUID: 3.
        //   Error: UPackageMapClient::SerializeNewActor Unable to read Archetype for NetGUID 2 / 3
        //
        // i.e. the actor was never spawned, because the archetype was still loading when the spawn
        // header arrived. The missing piece was on THIS side, not the client's: the client already
        // has UActorChannel::ProcessQueuedBunches, but it only queues a channel's bunches when the
        // bunch announces the GUIDs it is waiting on, and this server never announced any. It does
        // now - see UChannel.AppendMustBeMappedGuids - so the race should be survivable.
        //
        // Still defaulting to the native class because that combination is live-confirmed working
        // and the announcement path is not yet: a native class is always resident, so it cannot
        // lose the race at all. Lighting is native defaults rather than Athena's; the loading
        // screen only needs the GameState's FortTimeOfDayManager to be non-null. To try the real
        // one: TODM_CLASS=/Game/TimeOfDay/TODM/BR/TODM_BR.TODM_BR_C
        [typeof(AFortTimeOfDayManager)] = Environment.GetEnvironmentVariable("TODM_CLASS") is { Length: > 0 } todm
            ? todm
            : "/Script/FortniteGame.FortTimeOfDayManager"
    };

    public static UClass StaticClass<T>() => StaticClass(typeof(T));

    public static UClass StaticClass(Type type) {
        if (Classes.TryGetValue(type, out var existing)) return existing;

        var uClass = new UClass(type);
        if (NativePackagePaths.TryGetValue(type, out var nativePath)) uClass.NativePackagePath = nativePath;

        Classes[type] = uClass;
        return uClass;
    }
}
