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
        // AFortPickupAthena : AFortPickup : AActor - the Battle Royale dropped-item actor. Native,
        // so it is always resident on the client; AFortPickupAthena adds no replicated properties
        // of its own, so NativeRepLayouts.PickupProps is really AFortPickup's layout.
        [typeof(AFortPickup)] = "/Script/FortniteGame.FortPickupAthena",
        // Athena's real TimeOfDayManager Blueprint - its CDO carries the SkyboxFog*/day-phase
        // settings, so this is what makes the match look like daytime rather than the native
        // defaults' permanent dark. Override with TODM_CLASS
        // (/Script/FortniteGame.FortTimeOfDayManager is the always-resident fallback).
        //
        // This used to be impossible. A path export NAMES an asset, it does not stream one, and the
        // client does not preload TODM_BR, so the archetype was still loading when the spawn header
        // arrived and the actor was simply dropped:
        //
        //   Error: UPackageMapClient::SerializeNewActor. Unresolved Archetype GUID.
        //           Path: Default__TODM_BR_C, NetGUID: 3.
        //
        // The missing piece was on THIS side. The client has always had
        // UActorChannel::ProcessQueuedBunches, but it only holds a channel's bunches when the bunch
        // announces which GUIDs it is waiting on, and this server announced none. Now that
        // UChannel.AppendMustBeMappedGuids does, the client rides out the load - live-confirmed
        // 2026-08-27, the channel queued one bunch for 74ms and then flushed it:
        //
        //   GetObjectFromNetGUID: Async loading package. Path: /Game/TimeOfDay/TODM/BR/TODM_BR
        //   AFortTimeOfDayManager::PostInitializeComponents: World is "Athena_Terrain",
        //           this is "TODM_BR_C_2147478934"
        //   ProcessQueuedBunches: Flushing queued bunches. ChIndex: 2,
        //           Actor: ...TODM_BR_C_2147478934, Queued: 1
        //   SetupTimeOfDayCallbacks: ... FortTimeOfDayManager is "TODM_BR_C_2147478934"
        //
        // The same now goes for any other Blueprint class the client has not already loaded.
        [typeof(AFortTimeOfDayManager)] = Environment.GetEnvironmentVariable("TODM_CLASS") is { Length: > 0 } todm
            ? todm
            : "/Game/TimeOfDay/TODM/BR/TODM_BR.TODM_BR_C"
    };

    public static UClass StaticClass<T>() => StaticClass(typeof(T));

    /// <summary>
    ///     A UClass for a class path that is NOT fixed per C# type - one entry per distinct path,
    ///     all backed by the same C# type.
    ///
    ///     Everything in NativePackagePaths above is a one-to-one mapping: this project's APawn is
    ///     always PlayerPawn_Athena_C, its AGameState always Athena_GameState_C. Weapons broke that
    ///     assumption - the class to spawn comes from the item definition
    ///     (FortWeaponActorClasses), so one AFortWeapon can be a B_Assault_Auto_Athena_C on one
    ///     channel and a B_Athena_Pickaxe_Generic_C on the next.
    ///
    ///     Only the CLIENT's view is affected: the class is what SerializeNewActor exports as the
    ///     archetype, while this server's RepLayout and property getters are chosen from the C#
    ///     type. Cached per path because FNetGUIDCache is keyed by object identity, so two UClass
    ///     instances for the same path would export as two different GUIDs for the same client-side
    ///     class.
    /// </summary>
    public static UClass StaticClassForPath<T>(string nativePackagePath) where T : UObject =>
        StaticClassForPath(typeof(T), nativePackagePath);

    public static UClass StaticClassForPath(Type type, string nativePackagePath) {
        var key = (type, nativePackagePath);
        if (PathClasses.TryGetValue(key, out var existing)) return existing;

        var uClass = new UClass(type) { NativePackagePath = nativePackagePath };
        PathClasses[key] = uClass;
        return uClass;
    }

    private static readonly Dictionary<(Type, string), UClass> PathClasses = new();

    public static UClass StaticClass(Type type) {
        if (Classes.TryGetValue(type, out var existing)) return existing;

        var uClass = new UClass(type);
        if (NativePackagePaths.TryGetValue(type, out var nativePath)) uClass.NativePackagePath = nativePath;

        Classes[type] = uClass;
        return uClass;
    }
}
