using AFortOnlineBeacon.Runtime;
namespace AFortOnlineBeacon.Core.Objects;

public class GUClassArray {
    // A UClass is type metadata, so one shared instance per type stays correct however many worlds
    // are running. The dictionaries still need guarding: those worlds tick on their own threads.
    private static readonly object Gate = new();

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
        // AFortPlayerController::BroadcastRemoteClientInfo's real engine type - native (final,
        // non-Blueprint per the Dumper-7 SDK class declaration), so always resident on the client.
        // See AFortBroadcastRemoteClientInfo's own doc comment for why this actor is spawned at all.
        [typeof(AFortBroadcastRemoteClientInfo)] = "/Script/FortniteGame.FortBroadcastRemoteClientInfo",
        // AFortPickupAthena : AFortPickup : AActor - the Battle Royale dropped-item actor. Native,
        // so it is always resident on the client; AFortPickupAthena adds no replicated properties
        // of its own, so NativeRepLayouts.PickupProps is really AFortPickup's layout.
        [typeof(AFortPickup)] = "/Script/FortniteGame.FortPickupAthena",
        // The player's AbilitySystemComponent. Native, so always resident on the client - which
        // matters more than usual here: a sub-object content block that has to CREATE the object
        // client-side sends this class reference, and an unresolvable one leaves the client with a
        // block it cannot construct. UFortAbilitySystemComponentAthena adds no net fields over its
        // parent, so either class name gives the same field index space (see the component's own
        // doc comment).
        [typeof(UFortAbilitySystemComponent)] = "/Script/FortniteGame.FortAbilitySystemComponentAthena",
        // The controller's InteractionComp. Registered only so NewObject can construct it - this
        // component is never replicated OUTWARDS, it exists so the client's own sub-object reference
        // resolves to something this server can dispatch ServerAttemptInteract on.
        [typeof(UFortControllerComponent_Interaction)] = "/Script/FortniteGame.FortControllerComponent_Interaction",
        // A vehicle's SkeletalMeshComponent. Registered for the same reason and used the other way
        // round: this server never receives anything on it, it hands it OUT as the driver's movement
        // base. See UFortVehicleSkelMeshComponent.
        [typeof(UFortVehicleSkelMeshComponent)] = "/Script/FortniteGame.FortVehicleSkelMeshComponent",
        // A vehicle's VehicleSeatComponent - the seat array. Name-stable under the vehicle, so the
        // client resolves it by path and never has to construct it from this class; registered
        // because NewObject needs a UClass either way. See UFortVehicleSeatComponent.
        [typeof(UFortVehicleSeatComponent)] = "/Script/FortniteGame.FortVehicleSeatComponent",
        // A placed building's health attribute set. Native, and it MUST be: unlike the PlayerState's
        // stably-named sets, this one is created at runtime and so travels as a sub-object content
        // block carrying its class, which the client has to be able to resolve to construct it - the
        // same requirement (and the same failure mode when unmet) as the AbilitySystemComponent
        // above. See UFortBuildingActorSet's own doc comment.
        [typeof(UFortBuildingActorSet)] = "/Script/FortniteGame.FortBuildingActorSet",
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
        [typeof(AFortTimeOfDayManager)] = FBeaconProcess.Options.Get("TODM_CLASS") is { Length: > 0 } todm
            ? todm
            : "/Game/TimeOfDay/TODM/BR/TODM_BR.TODM_BR_C",
        // The battle bus. Read straight off the PR3.0 capture, which exports
        // `/Game/Athena/Aircraft/AthenaAircraft.Default__AthenaAircraft_C` as the spawned actor's
        // archetype (PriveDev/PacketProxy/decoded_new.txt packet #357) - so this is the class it is
        // the CDO of. A Blueprint, like TODM_BR above, and it relies on the same MustBeMappedGuids
        // machinery to survive the client's async load.
        [typeof(AFortAthenaAircraft)] = "/Game/Athena/Aircraft/AthenaAircraft.AthenaAircraft_C",
        // The storm circle. Read off the PR3.0 capture the same way the bus was - packet #16837
        // exports `/Game/Athena/SafeZone/SafeZoneIndicator.Default__SafeZoneIndicator_C` as the
        // spawned actor's archetype. A Blueprint, so it relies on MustBeMappedGuids for the client's
        // async load, exactly as TODM_BR and the aircraft do.
        [typeof(AFortSafeZoneIndicator)] = "/Game/Athena/SafeZone/SafeZoneIndicator.SafeZoneIndicator_C",
        // The six management actors - see Net/Actors/FortManagementActors.cs for what they are for.
        // Every path here is copied out of the PR3.0 capture's own archetype exports rather than
        // assembled from a class name, because the capture is the stronger evidence of the two: it
        // is what the real server actually put on the wire, and it is what the client resolved.
        //
        //   #302 Default__FortPropertyOverrideReplShared   #318 Default__FortPoiManager
        //   #320 Default__FortSpecialActorReplicationInfo
        //   #321 /Game/Athena/BuildingActors/FortVolumeManager_BP.Default__FortVolumeManager_BP_C
        //   #321 Default__FortClientAnnouncementManager  #328 Default__FortTeamPrivateInfo
        //
        // Five are native (always resident, nothing to stream); FortVolumeManager_BP is a Blueprint
        // and relies on MustBeMappedGuids the same way TODM_BR and the aircraft above do.
        [typeof(AFortPoiManager)] = "/Script/FortniteGame.FortPoiManager",
        [typeof(AFortClientAnnouncementManager)] = "/Script/FortniteGame.FortClientAnnouncementManager",
        [typeof(AFortSpecialActorReplicationInfo)] = "/Script/FortniteGame.FortSpecialActorReplicationInfo",
        [typeof(AFortPropertyOverrideReplShared)] = "/Script/FortniteGame.FortPropertyOverrideReplShared",
        [typeof(AFortTeamPrivateInfo)] = "/Script/FortniteGame.FortTeamPrivateInfo",
        [typeof(AFortVolumeManager)] = "/Game/Athena/BuildingActors/FortVolumeManager_BP.FortVolumeManager_BP_C",
        // A supply llama. Straight out of the PR3.0 capture's archetype export (packet #320,
        // `/Game/Athena/SupplyDrops/Llama/AthenaSupplyDrop_Llama.Default__AthenaSupplyDrop_Llama_C`),
        // and named again by DefaultMapInfo's LlamaClass. A Blueprint, so it rides MustBeMappedGuids
        // like TODM_BR - see FortSupplyLlamas and AFortAthenaSupplyDropLlama.
        [typeof(AFortAthenaSupplyDropLlama)] = "/Game/Athena/SupplyDrops/Llama/AthenaSupplyDrop_Llama.AthenaSupplyDrop_Llama_C"
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
        lock (Gate) {
            var key = (type, nativePackagePath);
            if (PathClasses.TryGetValue(key, out var existing)) return existing;

            var uClass = new UClass(type) { NativePackagePath = nativePackagePath };
            PathClasses[key] = uClass;
            return uClass;
        }
    }

    private static readonly Dictionary<(Type, string), UClass> PathClasses = new();

    public static UClass StaticClass(Type type) {
        lock (Gate) {
            if (Classes.TryGetValue(type, out var existing)) return existing;

            var uClass = new UClass(type);
            if (NativePackagePaths.TryGetValue(type, out var nativePath)) uClass.NativePackagePath = nativePath;

            Classes[type] = uClass;
            return uClass;
        }
    }
}
