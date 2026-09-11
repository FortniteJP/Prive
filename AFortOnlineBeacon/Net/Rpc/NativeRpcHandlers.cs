using AFortOnlineBeacon.Runtime;
namespace AFortOnlineBeacon.Net.Rpc;

/// <summary>
///     Handlers for server-direction RPCs a real client sends us, keyed by name (matching
///     NativeClassNetCache's field names) rather than by wire index - counterpart to
///     NativeClassNetCache/NativeRepLayouts, but for FUNC_Net function calls instead of NetFields
///     membership or UPROPERTY replication.
///
///     Only RPCs with parameter types this project can actually decode are listed here (bool,
///     byte, uint32, float, FVector/FRotator and their quantized/compressed variants, FString).
///     RPCs with FName, FUniqueNetIdRepl, TArray-of-struct, or object-reference parameters
///     (ServerCamera, ServerUpdateLevelVisibility, ServerUpdateMultipleLevelsVisibility,
///     ServerMutePlayer/UnmutePlayer, ServerAcknowledgePossession, the full ServerMove/
///     ServerMoveDual family, ServerExecRPC, ...) aren't decoded yet - UActorChannel already skips
///     any field with no matching entry here by its own declared NumPayloadBits, so leaving them
///     out is safe, just inert.
/// </summary>
internal static class NativeRpcHandlers {
    /// <summary>
    ///     A real server's own ServerCreateBuildingActor hook deducts a flat 10 regardless of piece
    ///     type - confirmed independently against a live test's own empirical finding (the cost UI
    ///     appeared once 10+ of a resource was on hand, the same number across all four pieces).
    /// </summary>
    private const int BuildingPlaceResourceCost = 10;

    /// <summary>
    ///     Which resource item a building class's placement cost draws from, read off the class's
    ///     own asset path (e.g. ".../Wood/L1/PBWA_W1_Solid.PBWA_W1_Solid_C" -&gt; Wood) rather than
    ///     tracked separately - the class already names its own material tier. Paths match
    ///     AGameModeBase's starting-resource grant and FortHarvestResources' own ItemPaths table.
    /// </summary>
    private static string? BuildingResourcePathFor(string? buildingClassPath) {
        if (buildingClassPath == null) return null;
        if (buildingClassPath.Contains("/Wood/", StringComparison.OrdinalIgnoreCase)) return "/Game/Items/ResourcePickups/WoodItemData.WoodItemData";
        if (buildingClassPath.Contains("/Stone/", StringComparison.OrdinalIgnoreCase)) return "/Game/Items/ResourcePickups/StoneItemData.StoneItemData";
        if (buildingClassPath.Contains("/Metal/", StringComparison.OrdinalIgnoreCase)) return "/Game/Items/ResourcePickups/MetalItemData.MetalItemData";
        return null;
    }

    /// <summary>
    ///     The same three resources <see cref="BuildingResourcePathFor(string?)"/> resolves, keyed off
    ///     a piece this server already has in hand rather than off a class path. Repairing works from
    ///     the actor, not from the RPC that created it, so it has nothing to pattern-match on.
    /// </summary>
    private static string? BuildingResourcePathFor(EBuildingMaterial material) => material switch {
        EBuildingMaterial.Wood => "/Game/Items/ResourcePickups/WoodItemData.WoodItemData",
        EBuildingMaterial.Stone => "/Game/Items/ResourcePickups/StoneItemData.StoneItemData",
        EBuildingMaterial.Metal => "/Game/Items/ResourcePickups/MetalItemData.MetalItemData",
        _ => null
    };

    /// <summary>This world's share of NativeRpcHandlers's state - see FWorldSubsystem.</summary>
    private sealed class FRpcHandlerState : FWorldSubsystem {
        /// <summary>
        ///     Seeded so a session's loot is reproducible - the same reason FortSafeZoneSystem seeds its
        ///     own: a bug that only shows up with one particular drop is unchaseable if the drop changes
        ///     every run. LOOT_SEED overrides it.
        /// </summary>
        public Random LootRng = default!;

        /// <summary>
        ///     One-shot guard for ServerUpdateCamera's decode sanity check - see that handler. Once, not
        ///     every frame: the RPC arrives thousands of times a session and the answer cannot change.
        /// </summary>
        public bool _reportedCameraSanity;

        /// <summary>
        ///     Same, for the camera's ROTATION - reported separately because the first sample is always
        ///     zero (a freshly spawned player looks down +X) and zero proves nothing about the unpack.
        /// </summary>
        public bool _reportedCameraRotation;

        /// <summary>ServerUpdateCamera arrival statistics - see the handler for why they are measured.</summary>
        public int _cameraSampleCount;

        public float _cameraFirstSampleTime;

        public float _cameraLongestGap;

        /// <summary>Same idea for the level-visibility decode - report the first one and then stay quiet.</summary>
        public bool _reportedLevelVisibilitySanity;

        protected internal override void Initialize() {
            { LootRng = new(
                int.TryParse(Options.Get("LOOT_SEED"), out var lootSeed) ? lootSeed : 20191001); }
        }
    }

    private static FRpcHandlerState StateOf(UWorld world) => world.GetSubsystem<FRpcHandlerState>();

    /// <summary>The handler state of the world <paramref name="actor" /> belongs to.</summary>
    private static FRpcHandlerState StateOf(AActor actor) => StateOf(actor.GetWorld()!);

    /// <summary>
    ///     Records one level-visibility report on the connection, and CHECKS ITS OWN DECODE the first
    ///     time through.
    ///
    ///     The check is worth having because this parameter is two FNames, which arrive as strings -
    ///     so a framing error produces visible nonsense rather than a plausible wrong number. That is
    ///     a luxury the neighbouring ServerUpdateCamera does not have (see its handler), and it is
    ///     the reason this RPC was implemented first despite being the more complicated of the two.
    ///
    ///     After the first report the running total is logged rather than each name, because a client
    ///     streams hundreds of sublevels and the individual names stop being news immediately.
    /// </summary>
    private static void ApplyLevelVisibility(APlayerController pc, FUpdateLevelVisibilityLevelInfo info,
                                             string source) {
        var connection = pc.GetWorld()?.NetDriver?.ClientConnections
            .FirstOrDefault(candidate => candidate.PlayerController == pc);

        if (connection == null) return;

        if (!StateOf(pc)._reportedLevelVisibilitySanity) {
            StateOf(pc)._reportedLevelVisibilitySanity = true;

            Console.WriteLine($"NativeRpcHandlers: {source} first decode - PackageName='{info.PackageName}' " +
                              $"Filename='{info.Filename}' bIsVisible={info.bIsVisible}: " +
                              (info.LooksSane
                                  ? "looks like a real package path, so the struct framing is right"
                                  : "NOT a package path - the struct framing is WRONG, treat these names as garbage"));
        }

        if (!info.LooksSane) return;

        var changed = info.bIsVisible
            ? connection.ClientVisibleLevelNames.Add(info.PackageName)
            : connection.ClientVisibleLevelNames.Remove(info.PackageName);

        // AND THE COOKED -> LOADED MAPPING, which is the only way this server can ever name an actor
        // inside an instanced sublevel. See UNetConnection.ClientLevelInstances. Filename is empty
        // when the two are the same (the wire omits it - bFileNameIsPackageName), and an ordinary
        // sublevel needs no translation, so only the differing pairs are worth keeping.
        if (info.Filename.Length > 0 && !info.Filename.Equals(info.PackageName, StringComparison.OrdinalIgnoreCase)) {
            if (info.bIsVisible) connection.ClientLevelInstances[info.Filename] = info.PackageName;
            else connection.ClientLevelInstances.Remove(info.Filename);
        }

        if (changed && NetDebugLog.VerboseEnabled) {
            Console.WriteLine($"NativeRpcHandlers: {source} - {(info.bIsVisible ? "loaded" : "dropped")} " +
                              $"'{info.PackageName}', client now has {connection.ClientVisibleLevelNames.Count} level(s)");
        }
    }

    /// <summary>
    ///     Which loot tier group a container's PATH names, or null if this is not a container.
    ///
    ///     Name-matching, because the actor's name is all the client sends - the same approach, and the
    ///     same limitation, as FortHarvestResources' stem lookup. Athena's containers are consistently
    ///     named: `Tiered_Chest_*` for chests, `Tiered_Ammo_*` for ammo boxes. Anything else is left
    ///     alone rather than guessed at, which is the deliberate choice destructible scenery already
    ///     makes - under-covering is acceptable, acting on a wrong guess is not.
    /// </summary>
    /// <summary>
    ///     Whether an interact target's path names a door. Same name-matching limitation as
    ///     <see cref="ContainerTierGroupFor"/> - Athena's building-set doors are consistently named
    ///     with "Door" in them (`SM_..._Door_...`, `..._DoorS_...`), and anything that is not is left
    ///     alone rather than toggled on a guess.
    /// </summary>
    /// <summary>
    ///     Whether a client-supplied actor path names a real door - looked up in a CLASS-VERIFIED
    ///     table, never guessed from the name.
    ///
    ///     This used to be `name.Contains("Door")`, and that matched
    ///     `Prop_Athena_Doorbell_Interactable_2`, whose class is `BuildingPropSimpleInteract` and not a
    ///     wall at all. The server duly built an ABuildingWall stand-in for it, opened a channel and
    ///     pushed wall handles at it - and the client DROPPED THE CONNECTION, which is what a handle a
    ///     class does not have always does. The test also failed the other way: most real doors have
    ///     no "Door" in their actor name (only 17 of 400 do).
    ///
    ///     See FortMapWalls.Generated.cs. Anything not in that table is left completely alone, because
    ///     a door that will not open is a far better failure than a disconnect.
    /// </summary>
    /// <summary>
    ///     Spawns a player-built piece. The C# class comes from the UCLASS, not from here -
    ///     <see cref="ABuildingActor.ClassForPath" /> resolved a wall's path as an ABuildingWall, and
    ///     SpawnActor instantiates whatever type that UClass carries.
    ///
    ///     ASKING FOR ABuildingWall HERE IS WRONG and was tried first: SpawnActor's type parameter is
    ///     the type the result is CAST to, so a UClass built as an ABuildingActor threw
    ///     InvalidCastException the moment a wall was placed. The class table is the single place
    ///     that gets to decide, because it is also the place the cache is keyed on.
    ///
    ///     The C# type is not cosmetic here - it decides two things at once. It picks the wire
    ///     LAYOUT (BuildingWallProps is the building layout plus handles 68-74, so a wall spawned as
    ///     the base class simply has no bDoorOpen to send), and it is what the interact handler
    ///     switches on when the client names the piece by NetGUID. Both of those failing at once is
    ///     what "a built door opens and never closes" was: the client predicted the swing, the
    ///     server had no door to toggle and no handle to answer with, and nothing ever told the
    ///     client otherwise.
    ///
    ///     Every wall variant, not only the door ones. `PBWA_W1_Solid_C` is an ABuildingWall
    ///     subclass in the game exactly as `PBWA_W1_DoorSide_C` is, so this matches the real class
    ///     hierarchy rather than guessing from the piece's name - and a door handle sent to a wall
    ///     without a door does nothing, which is the same reasoning FortMapWalls' gate already rests
    ///     on.
    /// </summary>
    private static ABuildingActor? SpawnBuildingPiece(UWorld world, UClass buildingClass, EFortBuildingType type) =>
        world.SpawnActor<ABuildingActor>(buildingClass, new FActorSpawnParameters {
            ObjectFlags = EObjectFlags.RF_Transient
        });

    /// <summary>
    ///     A player piece placed by something OTHER than the build RPC - the floor a trap builds under
    ///     itself (FortTrapSystem). The same sequence ServerCreateBuildingActor runs, in the same
    ///     order and for the reasons documented there: transform, class-derived state, anchor, the
    ///     support grid, and only then replication. No resource cost - see the caller.
    /// </summary>
    internal static ABuildingActor? PlacePlayerBuilding(UWorld world, UClass buildingClass, FVector placeAt, float yaw) {
        var type = ABuildingActor.BuildingTypeFromClassPath(buildingClass.NativePackagePath);
        var building = SpawnBuildingPiece(world, buildingClass, type);
        if (building == null) return null;

        building.SetRole(ENetRole.ROLE_Authority);
        building.SetActorLocation(placeAt);
        building.SetActorRotation(new FRotator { Yaw = yaw });
        building.InitializeFromClass(buildingClass);
        building.SetAnchor(placeAt, yaw);
        BuildingStructuralSupportSystem.Of(world).Register(building);
        building.SetReplicates(true);
        return building;
    }

    /// <summary>What an interact reference actually resolved to, for the log line that says it did not work.</summary>
    private static string DescribeInteractTarget(object? target) => target switch {
        null => "nothing at all (an unresolved NetGUID, or a path this server never exported)",
        AActor actor => $"{actor.GetType().Name} '{actor.GetFName()}'",
        _ => target.GetType().Name
    };

    /// <summary>
    ///     Which way a player is HEADING, for the door they just opened - the camera yaw the client
    ///     last sent, falling back to the pawn's own facing.
    ///
    ///     The camera is the right first choice because interaction IS a camera trace: to touch the
    ///     door at all they were looking at it. The pawn's rotation is the fallback for the window
    ///     before any view has arrived, and 0 is only reached with no pawn at all, in which case
    ///     nothing is opening anything.
    /// </summary>
    private static float InteractorYaw(APlayerController pc) =>
        pc.LastClientCameraRotation?.Yaw
        ?? pc.Pawn?.LastClientViewRotation?.Yaw
        ?? pc.Pawn?.GetActorRotation().Yaw
        ?? 0f;

    private static bool LooksLikeDoor(string actorPath) => FortMapWalls.IsBuildingWall(actorPath);

    private static string? ContainerTierGroupFor(string actorPath) {
        var name = actorPath[(actorPath.LastIndexOf('.') + 1)..];

        if (name.Contains("Tiered_Chest", StringComparison.OrdinalIgnoreCase)) return FortLootTables.TreasureGroup;
        if (name.Contains("Tiered_Ammo", StringComparison.OrdinalIgnoreCase)) return FortLootTables.AmmoLargeGroup;
        if (name.Contains("AmmoBox", StringComparison.OrdinalIgnoreCase)) return FortLootTables.AmmoLargeGroup;

        return null;
    }

    /// <summary>EFortResourceType read back the other way - see ResourceTypeFor for the ordering.</summary>
    private static string ResourceNameFor(byte resourceType) => resourceType switch {
        0 => "Wood",
        1 => "Stone",
        2 => "Metal",
        3 => "Permanite",
        _ => "None"
    };

    /// <summary>
    ///     Take <paramref name="pc"/> off the battle bus and put a pawn under them where the bus is.
    ///
    ///     ServerAttemptAircraftJump's body, factored out because the DropEnd timeout needs exactly
    ///     the same thing: a real server drops everyone still aboard when the drop window closes
    ///     rather than carrying them off the end of the map, and this is that path. Reusing the
    ///     player-initiated one rather than writing a second is deliberate - the jump path is the one
    ///     that has actually been flown, and the skydive crash it took to get there is not worth
    ///     risking twice.
    ///
    ///     All the server can honestly do is clear bInAircraft: the client owns the skydive and the
    ///     glider from there, exactly as it owns movement everywhere else here (see ServerMoveNoBase).
    ///     What it must NOT do is leave the flag set - the client will not leave the bus until it
    ///     comes back cleared.
    /// </summary>
    public static void LeaveAircraft(APlayerController pc, APlayerState playerState, string reason) {
        playerState.bInAircraft = false;

        // Off the bus, so stop riding it (only relevant under BUS_ATTACH_PAWN=1 - the default model
        // destroys the pawn instead and there is nothing attached).
        if (pc.Pawn is { } jumpingPawn) jumpingPawn.AttachParent = null;

        AFortAthenaAircraft? jumpedFrom = null;
        if (pc.GetWorld()?.GameState is { } gameState) {
            foreach (var entry in gameState.Aircrafts) {
                if (entry is not AFortAthenaAircraft aircraft) continue;

                aircraft.JumpFlashCount++;
                jumpedFrom ??= aircraft;
            }
        }

        // A NEW PAWN, WHERE THE BUS IS. The warmup pawn was destroyed when the bus phase started
        // (AGameModeBase.TickBoarding), matching what a real server does - the capture shows the
        // local pawn's identity change across the bus - so the player has no pawn at all right now
        // and the skydive has to start from one that does not exist yet.
        // AFortAthenaAircraft.LocationAt puts it on the same straight line the client is already
        // interpolating the bus along.
        if (pc.Pawn == null && pc.GetWorld() is { } world && jumpedFrom != null) {
            var at = jumpedFrom.LocationAt(world.TimeSeconds);
            var spawned = AGameModeBase.SpawnAndPossessPawn(world, pc, at);

            // Only the server can tell a bus jump from a launch pad - they are the same custom
            // movement mode - so this one flag cannot come from the client's move like the rest.
            // See APawn.bIsSkydivingFromBus; TrackMoveFlags clears it when the descent ends.
            if (spawned != null) spawned.bIsSkydivingFromBus = true;

            Console.WriteLine($"NativeRpcHandlers.LeaveAircraft: " +
                              $"{(spawned == null ? "FAILED to spawn" : $"spawned {spawned.GetFName()}")} " +
                              $"at the bus {at}");
        }

        Console.WriteLine($"NativeRpcHandlers.LeaveAircraft: {playerState.GetFName()} left the aircraft - {reason}");
    }

    private static FRpcDef NoParams(string name, Action<APlayerController>? action = null) => new(
        name,
        Array.Empty<FRpcParamDef>(),
        (actor, _) => {
            if (NetDebugLog.VerboseEnabled) Console.WriteLine($"NativeRpcHandlers: {name} on {actor.GetFName()}");
            if (action != null && actor is APlayerController pc) action(pc);
        }
    );

    private static readonly Dictionary<string, FRpcDef> PlayerControllerRpcs = new() {
        ["ServerSetSpectatorLocation"] = new FRpcDef(
            "ServerSetSpectatorLocation",
            new[] { new FRpcParamDef("NewLoc", ERpcParamKind.Vector), new FRpcParamDef("NewRot", ERpcParamKind.Rotator) },
            (actor, values) => {
                if (actor is not APlayerController pc) return;
                if (values[0] is FVector loc) pc.LastSpectatorSyncLocation = loc;
                if (values[1] is FRotator rot) pc.LastSpectatorSyncRotation = rot;
                if (NetDebugLog.VerboseEnabled) Console.WriteLine($"NativeRpcHandlers: ServerSetSpectatorLocation on {pc.GetFName()} Loc={pc.LastSpectatorSyncLocation} Rot={pc.LastSpectatorSyncRotation}");
            }
        ),
        ["ServerSetSpectatorWaiting"] = new FRpcDef(
            "ServerSetSpectatorWaiting",
            new[] { new FRpcParamDef("bWaiting", ERpcParamKind.Bool) },
            (actor, values) => { if (NetDebugLog.VerboseEnabled) Console.WriteLine($"NativeRpcHandlers: ServerSetSpectatorWaiting on {actor.GetFName()} bWaiting={values[0]}"); }
        ),
        ["ServerChangeName"] = new FRpcDef(
            "ServerChangeName",
            new[] { new FRpcParamDef("S", ERpcParamKind.String) },
            (actor, values) => { if (NetDebugLog.VerboseEnabled) Console.WriteLine($"NativeRpcHandlers: ServerChangeName on {actor.GetFName()} S={values[0]}"); }
        ),

        // AFortPlayerController::ServerAttemptInventoryDrop(FGuid ItemGuid, int32 Count) - the
        // player dropping an item from the inventory UI, and the first client action in this project
        // that changes replicated state on the server.
        //
        // Real UE also spawns an AFortPickup where the item landed; that is not implemented, so the
        // item simply leaves the inventory. What DOES happen is the whole point: MarkItemDirty /
        // MarkArrayDirty move the fast array's replication key, and the next
        // UActorChannel.ReplicateCustomDeltaUpdate sends the delta - a removal as an explicit
        // delete, a partial drop as a changed element.
        ["ServerAttemptInventoryDrop"] = new FRpcDef(
            "ServerAttemptInventoryDrop",
            new[] { new FRpcParamDef("ItemGuid", ERpcParamKind.Guid), new FRpcParamDef("Count", ERpcParamKind.Int32) },
            (actor, values) => {
                if (actor is not APlayerController pc || pc.WorldInventory is not { } inventory) return;

                // A missing parameter means the caller left it zero-constructed - an all-zero FGuid
                // matches nothing, and Count 0 is "drop nothing".
                if (values[0] is not Guid itemGuid) return;
                var count = values[1] as int? ?? 0;

                var item = inventory.Inventory.Items.FirstOrDefault(entry => entry.ItemGuid == itemGuid);
                if (item == null) {
                    Console.WriteLine($"NativeRpcHandlers: ServerAttemptInventoryDrop for unknown ItemGuid={itemGuid}, ignoring");
                    return;
                }

                int droppedCount;
                var bWholeStackDropped = count <= 0 || count >= item.Count;

                if (bWholeStackDropped) {
                    droppedCount = item.Count;
                    inventory.Inventory.Remove(item);
                    Console.WriteLine($"NativeRpcHandlers: ServerAttemptInventoryDrop removed ItemGuid={itemGuid} " +
                                      $"(ReplicationId={item.ReplicationId}), {inventory.Inventory.Count} item(s) left, " +
                                      $"ArrayReplicationKey={inventory.Inventory.ArrayReplicationKey}");
                } else {
                    droppedCount = count;
                    item.Count -= count;
                    inventory.Inventory.MarkItemDirty(item);
                    Console.WriteLine($"NativeRpcHandlers: ServerAttemptInventoryDrop dropped {count} of ItemGuid={itemGuid}, " +
                                      $"{item.Count} left, ArrayReplicationKey={inventory.Inventory.ArrayReplicationKey}");
                }

                // Dropping what you are holding has to take the weapon out of your hands too: the
                // weapon actor is keyed to its inventory row by ItemEntryGuid, and that row is now
                // gone. A partial drop leaves the row (and so the weapon) alone.
                if (bWholeStackDropped
                    && pc.Pawn is { CurrentWeapon: { } weapon } dropPawn
                    && weapon.ItemEntryGuid == itemGuid) {
                    dropPawn.UnequipCurrentWeapon();
                }

                SpawnDroppedPickup(pc, item, droppedCount);
            }
        ),

        // AFortPlayerController::ServerExecuteInventoryItem(FGuid ItemGuid) - "equip this quickbar
        // slot". The client sends it whenever the player selects a slot, and it is the whole reason
        // a weapon ever appears in a pawn's hands.
        //
        // What a real (injected) server does here is one native call - AFortPawn::EquipWeaponDefinition,
        // which spawns the weapon actor, fills it in, and links it to the pawn. None of that is
        // callable from outside the process, so SpawnEquippedWeapon below does the three parts that
        // actually reach the wire: spawn an actor of the item's own WeaponActorClass, set the
        // properties the client reads (WeaponData / ItemEntryGuid / AmmoCount) and point
        // AFortPawn::CurrentWeapon at it.
        ["ServerExecuteInventoryItem"] = new FRpcDef(
            "ServerExecuteInventoryItem",
            new[] { new FRpcParamDef("ItemGuid", ERpcParamKind.Guid) },
            (actor, values) => {
                if (actor is not APlayerController pc || pc.WorldInventory is not { } inventory) return;
                if (values[0] is not Guid itemGuid) return;

                if (pc.Pawn is not { } pawn) {
                    Console.WriteLine("NativeRpcHandlers: ServerExecuteInventoryItem with no pawn to equip on, ignoring");
                    return;
                }

                var item = inventory.Inventory.Items.FirstOrDefault(entry => entry.ItemGuid == itemGuid);
                if (item == null) {
                    Console.WriteLine($"NativeRpcHandlers: ServerExecuteInventoryItem for unknown ItemGuid={itemGuid}, ignoring");
                    return;
                }

                Console.WriteLine($"NativeRpcHandlers: ServerExecuteInventoryItem ItemGuid={itemGuid} " +
                                  $"('{item.ItemDefinition.GetFName()}'), currently holding " +
                                  $"{(pawn.CurrentWeapon is { } held ? held.ItemEntryGuid.ToString() : "nothing")}");

                // clientInitiated: the client asked for this slot, so it must NOT be told about the
                // result - echoing it back makes it re-assert its focused slot and the two oscillate.
                // See APawn.bLastEquipWasClientInitiated.
                pawn.EquipInventoryItem(item, clientInitiated: true);
            }
        ),

        // AFortPlayerController::ServerReleaseInventoryItemKey(FGuid ItemGuid) - the "key up" half
        // of holding a quickbar slot, which only matters for items whose use is a hold (a
        // consumable, a building piece). Nothing here acts on it, and neither does
        // Project-Reboot-3.0, which hooks the whole quickbar path and skips this one.
        //
        // Decoded anyway rather than skipped as an unknown field: it is the clearest proof in the
        // log that the client is really driving its quickbar, which is the same input path
        // ServerExecuteInventoryItem rides on.
        ["ServerReleaseInventoryItemKey"] = new FRpcDef(
            "ServerReleaseInventoryItemKey",
            new[] { new FRpcParamDef("ItemGuid", ERpcParamKind.Guid) },
            (actor, values) => {
                if (NetDebugLog.VerboseEnabled) Console.WriteLine($"NativeRpcHandlers: ServerReleaseInventoryItemKey on {actor.GetFName()} ItemGuid={values[0]}");
            }
        ),

        // AFortPlayerController::ServerPlayEmoteItem(const UFortMontageItemDefinitionBase* EmoteAsset)
        // - the player picking something off the emote wheel. One object-reference parameter, and it
        // arrives in BOTH of the two shapes an object reference has: a full path export the first
        // time this client names the asset, and the bare NetGUID the server assigned it every time
        // after. AssetPath is the kind that copes with both - see its doc comment for the measured
        // 94.4-then-5.4-byte proof, and for what reading only the path would have cost.
        //
        // The path is all the server needs either way. What plays the emote is an ability spec whose
        // SourceObject is this same asset, and that goes back out as a path export of its own, so the
        // server is only ever relaying a name it never has to understand. See FortEmoteSystem.
        ["ServerPlayEmoteItem"] = new FRpcDef(
            "ServerPlayEmoteItem",
            new[] { new FRpcParamDef("EmoteAsset", ERpcParamKind.AssetPath) },
            (actor, values) => {
                if (actor is not APlayerController pc) return;

                if (values[0] is not string emoteAssetPath) {
                    Console.WriteLine("NativeRpcHandlers: ServerPlayEmoteItem named no asset this server can " +
                                      "identify (no path export), ignoring");
                    return;
                }

                Console.WriteLine($"NativeRpcHandlers: ServerPlayEmoteItem on {pc.GetFName()} -> {emoteAssetPath}");
                FortEmoteSystem.PlayEmoteItem(pc, emoteAssetPath);
            },
            expectsFullDecode: true
        ),

        // AFortPlayerController::ServerCreateBuildingActor(FCreateBuildingActorData) - the RPC that
        // places a building piece, and now fully decoded: the client sends the exact, already
        // grid-snapped transform its own ghost was standing on, and a real server uses it verbatim
        // without snapping anything itself (Project-Reboot-3.0's ServerCreateBuildingActorHook does
        // precisely `Transform.Translation = BuildLoc; Transform.Rotation = BuildRot.Quaternion()`).
        // Any server-side "snap to the grid" is therefore wrong by construction - the grid maths
        // already happened on the client, and re-deriving it from the pawn's position can only
        // disagree with the ghost the player was actually looking at.
        //
        // The parameter's wire layout - a hand-written native NetSerialize, which is why it resisted
        // both a flat member dump and a RepLayout-handle read - is documented and derived in
        // FCreateBuildingActorData.
        //
        // The building CLASS still does not come from this RPC (it carries only an opaque
        // BuildingClassHandle indexing a list the client owns). It comes from the last
        // ServerSetPlayerBuildableClass, exactly as it does for a real server.
        ["ServerCreateBuildingActor"] = new FRpcDef(
            "ServerCreateBuildingActor",
            new[] { new FRpcParamDef("CreateBuildingData", ERpcParamKind.CreateBuildingActorData) },
            (actor, values) => {
                if (actor is not APlayerController { Pawn: { } pawn } pc) return;
                if (values[0] is not FCreateBuildingActorData buildData) return;

                var world = pc.GetWorld();
                if (world == null) return;

                // Which piece, in descending order of how much the source actually knows:
                //
                //  1. BuildingClassHandle, via the measured table - the ONLY signal that tracks a
                //     piece switch made INSIDE build mode. See BuildingClassHandles for why nothing
                //     else can, and for how to calibrate it.
                //  2. ServerSetPlayerBuildableClass's last word. Correct when it arrives, but this
                //     client has never once sent it to this server (0 calls across every capture,
                //     including after the info actor's Owner was fixed) - kept because it costs
                //     nothing and would be authoritative if it ever does.
                //  3. CurrentWeapon.WeaponData - only ever names the piece build mode was ENTERED
                //     with, so it is wrong for every switch. Last resort, and the reason an
                //     uncalibrated handle is worth shouting about below.
                var buildingClass = BuildingClassHandles.ClassFor(buildData.BuildingClassHandle);

                if (buildingClass == null) {
                    buildingClass = pawn.SelectedBuildingActorClassPath is { } selectedPath
                        ? ABuildingActor.ClassForPath(selectedPath)
                        : FortWeaponActorClasses.BuildingActorClassFor(pawn.CurrentWeapon?.WeaponData);

                    Console.WriteLine($"NativeRpcHandlers: ServerCreateBuildingActor - BuildingClassHandle " +
                                      $"{buildData.BuildingClassHandle} is not in the handle table, falling back to " +
                                      $"'{buildingClass?.NativePackagePath ?? "(nothing)"}'. If that is not the piece you " +
                                      $"just placed, add a line '{buildData.BuildingClassHandle} = <Material>:<Piece>' " +
                                      $"(e.g. Wood:Stair) to BuildingClassHandles.txt - it is re-read live, no restart needed.");
                }

                if (buildingClass == null) {
                    Console.WriteLine($"NativeRpcHandlers: ServerCreateBuildingActor - handle " +
                                      $"{buildData.BuildingClassHandle} is uncalibrated and CurrentWeapon " +
                                      $"'{pawn.CurrentWeapon?.WeaponData?.GetFName()}' has no known building actor class, ignoring");
                    return;
                }

                // Straight from the client, unmodified - see this RPC's doc comment. The client has
                // already snapped this to the building grid; the server's job is to trust it (a real
                // server only validates it, via FStructuralSupportSystem::IsWorldLocValid and the
                // overlap check, neither of which this project models yet).
                var placeAt = buildData.BuildLoc;
                var buildYaw = buildData.BuildRot.Yaw;

                // A real placement has been observed sending this RPC twice back to back for what the
                // player experiences as one confirm; without this each send spawns its own building
                // actor on top of the last one.
                //
                // The test is "is a piece of this KIND standing there right now", not "was the last
                // thing this pawn placed here". Remembering the last transform (which is what this
                // used to do) never expired: destroy a piece and the slot stayed permanently
                // unbuildable. Asking the support grid instead rejects the genuine double-send just
                // as well - the first of the two has already registered by the time the second
                // arrives - while a destroyed piece unregisters immediately and frees the slot.
                //
                // It runs HERE, after the class is resolved, and not before it: the kind of piece is
                // part of the test, and it is only known once the class is. Matching on the
                // transform alone rejected a roof placed over a floor on the same tile - see
                // BuildingStructuralSupportSystem.IsOccupied for the whole failure.
                var placeKey = $"{placeAt},{buildYaw}";
                var placeType = ABuildingActor.BuildingTypeFromClassPath(buildingClass.NativePackagePath);
                if (BuildingStructuralSupportSystem.Of(world).IsOccupied(placeAt, buildYaw, placeType)) {
                    Console.WriteLine($"NativeRpcHandlers: ServerCreateBuildingActor - a {placeType} is already " +
                                      $"standing at {placeKey}, ignoring (duplicate send)");
                    return;
                }

                // Escape hatch (toggle via SKIP_BUILDING_SPAWN=1): log the decoded placement without
                // spawning anything, leaving the client's own locally-predicted ghost uncorrected.
                // Originally the only way to see where the client really wanted the piece, back when
                // the RPC's parameters could not be decoded; now just a way to watch placements go
                // by without building up world state.
                if (actor.WorldOptions.Get("SKIP_BUILDING_SPAWN") == "1") {
                    Console.WriteLine($"NativeRpcHandlers: ServerCreateBuildingActor - SKIP_BUILDING_SPAWN=1, " +
                                      $"not spawning (class would have been {buildingClass.NativePackagePath}, " +
                                      $"{buildData})");
                    return;
                }

                // A WALL PIECE IS SPAWNED AS AN ABuildingWall, not a plain ABuildingActor, and that
                // is what makes a built door a door. PBWA_W1_DoorSide_C and every other wall variant
                // really is an ABuildingWall subclass in the game, so the door handles are part of
                // its layout; spawning the C# stand-in as the base class threw them away, and the
                // interact handler - which switches on the C# type - could not see a door either.
                // Live symptom: a built door opened once and could never be closed.
                var building = SpawnBuildingPiece(world, buildingClass, placeType);
                if (building == null) return;

                building.SetRole(ENetRole.ROLE_Authority);
                building.SetActorLocation(placeAt);
                building.SetActorRotation(new FRotator { Yaw = buildYaw });
                building.SetMirrored(buildData.bMirrored);

                // HP/material/slot kind and the structural-support grid - see ABuildingActor and
                // BuildingStructuralSupportSystem's doc comments for what these feed. Register only
                // once the transform is set: the grid buckets on the piece's placed location.
                building.InitializeFromClass(buildingClass);

                // The fixed reference every future edit of this piece computes against - see
                // ABuildingActor.AnchorLocation/AnchorYaw. Set here, at the ONE point a piece's slot
                // is genuinely chosen, and carried forward unchanged by ServerEditBuildingActor.
                building.SetAnchor(placeAt, buildYaw);

                BuildingStructuralSupportSystem.Of(world).Register(building);

                // REPLICATES ONLY NOW, once the piece is fully in its as-placed state. This used to
                // be set before InitializeFromClass, which meant the actor became replicable while
                // its health was still MaxHitPoints and Register/BeginConstruction had not yet put
                // it under construction at a fraction of that.
                //
                // The symptom was on screen rather than in a log: the piece appeared at its FULL
                // look, then played the crumbling animation, then built back up. That middle step is
                // the client doing exactly what it was told - health going 150 -> 90 is damage, and
                // damage is what the breaking animation is for. A freshly placed wall should never
                // have had a 150 to fall from.
                building.SetReplicates(true);

                // A real server's own ServerCreateBuildingActor hook deducts a flat 10 units of
                // whatever resource the spawned class costs (confirmed independently: matches
                // exactly what a live test found the real in-game cost to be) - the building
                // ACTOR CLASS itself carries which resource that is (its path names the material
                // tier, e.g. .../Wood/L1/... ), not something this RPC's own parameters say, so no
                // separate "which material" tracking is needed beyond the class we already resolved
                // above. Best-effort: if the class path names no known material (a class this
                // project hasn't mapped), nothing is deducted rather than guessing wrong.
                if (pc.WorldInventory is { } inventory && BuildingResourcePathFor(buildingClass.NativePackagePath) is { } resourcePath) {
                    var resourceDef = UAssetRegistry.GetOrCreate(resourcePath);
                    var stack = inventory.Inventory.Items.FirstOrDefault(item => item.ItemDefinition == resourceDef);
                    if (stack != null) {
                        stack.Count = Math.Max(0, stack.Count - BuildingPlaceResourceCost);
                        inventory.Inventory.MarkItemDirty(stack);
                        Console.WriteLine($"NativeRpcHandlers: ServerCreateBuildingActor - deducted {BuildingPlaceResourceCost} x " +
                                          $"{resourceDef.GetFName()} -> {stack.Count}");
                    }
                }

                Console.WriteLine($"NativeRpcHandlers: ServerCreateBuildingActor - spawned {buildingClass.NativePackagePath} " +
                                  $"at {buildData}");
            }
        ),

        // AFortPlayerController::ServerBeginEditingBuildingActor(ABuildingSMActor* BuildingActorToEdit)
        // - the player aiming at a placed piece and pressing Edit. A real (injected) server's own
        // hook (Project-Reboot-3.0's ServerBeginEditingBuildingActorHook) does exactly three things,
        // none of which need the native engine to do: equip the EditTool item (already granted at
        // spawn - see AGameModeBase), point that tool's EditActor at the target so its OnRep raises
        // the client's edit UI, and lock the piece to this player via EditingPlayer so a second
        // player can't edit it out from under the first. All three are ordinary property pushes in
        // this project - see AFortWeapon.EditActor and ABuildingActor.EditingPlayer.
        ["ServerBeginEditingBuildingActor"] = new FRpcDef(
            "ServerBeginEditingBuildingActor",
            new[] { new FRpcParamDef("BuildingActorToEdit", ERpcParamKind.Object) },
            (actor, values) => {
                if (actor is not APlayerController { Pawn: { } pawn, PlayerState: { } playerState } pc) return;

                if (values[0] is not ABuildingActor building) {
                    Console.WriteLine("NativeRpcHandlers: ServerBeginEditingBuildingActor named no building actor this server can resolve, ignoring");
                    return;
                }

                if (!building.bPlayerPlaced || building.bDestroyed) {
                    Console.WriteLine($"NativeRpcHandlers: ServerBeginEditingBuildingActor on {building.GetFName()} - not a live player-placed piece, ignoring");
                    return;
                }

                if (building.EditingPlayer != null && !ReferenceEquals(building.EditingPlayer, playerState)) {
                    Console.WriteLine($"NativeRpcHandlers: ServerBeginEditingBuildingActor on {building.GetFName()} - " +
                                      $"already locked to {building.EditingPlayer.GetFName()}, ignoring");
                    return;
                }

                if (pc.WorldInventory is not { } inventory) return;

                var editToolDef = UAssetRegistry.GetOrCreate("/Game/Items/Weapons/BuildingTools/EditTool.EditTool");
                var editToolItem = inventory.Inventory.Items.FirstOrDefault(item => item.ItemDefinition == editToolDef);
                if (editToolItem == null) {
                    Console.WriteLine("NativeRpcHandlers: ServerBeginEditingBuildingActor - no EditTool in this player's inventory, ignoring");
                    return;
                }

                pawn.EquipInventoryItem(editToolItem);
                if (pawn.CurrentWeapon is { } editTool) editTool.EditActor = building;

                building.SetEditingPlayer(playerState);
                Console.WriteLine($"NativeRpcHandlers: ServerBeginEditingBuildingActor - {playerState.GetFName()} began editing {building.GetFName()}");
            }
        ),

        // AFortPlayerController::ServerEditBuildingActor(ABuildingSMActor* BuildingActorToEdit,
        // TSubclassOf<ABuildingSMActor> NewBuildingClass, uint8 RotationIterations, bool bMirrored) -
        // the player confirming an edit pattern. NewBuildingClass is a class the CLIENT already
        // resolved (which real Wall/Door/Window/etc. variant the chosen pattern means) and this
        // server never assigned a NetGUID to, so - exactly like ServerSetPlayerBuildableClass's
        // BuildableClass - it always arrives as an unresolvable path export; ObjectPath keeps the
        // path instead of discarding it.
        //
        // bMirrored IS applied (Round 44): it replicates straight through to the new piece's own
        // bMirrored (wire handle 45, see ABuildingActor.bMirrored) - that property already existed,
        // reserved and unsent, for exactly this.
        //
        // RotationIterations -> yaw offset is applied to EVERY family as of Round 56, including Stairs
        // and Pillar for the first time (Round 47/49/50 had added Wall, then Roof, then Floor one live
        // test at a time; that gate existed only to contain a rotation whose position half was wrong,
        // and the position half is now read from the game's own constants). RotationIterations is a
        // DELTA from the piece's current rotation - Round 52 had made it absolute, measured from a
        // fixed per-piece anchor, to stop a drift that turned out to be entirely in the position
        // maths; Round 57 put it back once stairs produced a test that could tell the two apart. See
        // the comment on targetYaw below.
        //
        // The PIVOT, separately, is Round 56's fix, and it is no longer inferred from anything: a
        // building pivot is a cell EDGE midpoint, and ABuildingActor's own BaseLocToPivotOffset /
        // CentroidOffset - read out of all 132 building CDOs in the client memory dump - say exactly
        // where. What must stay still across an edit is the piece's CENTROID; see the comment on
        // placeAt below, which also records why Round 53 (pin the pivot) and Round 55 (rotate about a
        // cell centre derived from the wrong local axis) both moved the body instead of holding it.
        //
        // Separately, Round 52 fixed WHY Floor's own round trip broke even once Round 51's type gate
        // started reading oldBuilding.BuildingType: a floor's "raise one corner" class - PBWA_W1_BalconyI, seen
        // directly in a live capture - contains neither "Floor" nor anything else
        // BuildingTypeFromClassPath recognises, so InitializeFromClass classified IT as None the
        // moment it was spawned as an edit result, and the NEXT edit (editing back to plain Floor)
        // inherited that wrong classification from oldBuilding.BuildingType and silently skipped
        // rotation. ABuildingActor.OverrideBuildingType now forces the ORIGINAL type through on every
        // edit-spawn rather than trusting the new class's name a second time - editing never changes
        // the base slot kind, so there is nothing left to re-derive.
        //
        // AnchorLocation/AnchorYaw are no longer what the transform is computed from, but they are
        // still carried forward and logged: they are the only record of where a piece's slot started,
        // which is what makes a drift visible at a glance if one ever comes back.
        //
        // "Replace" here means what it does for a weapon swap (APawn.EquipInventoryItem): destroy the
        // old actor outright and spawn a fresh one, rather than MarkDestroyed()'s flag-then-defer
        // dance - an edit is not a kill, and should not play the crumble animation MarkDestroyed
        // drives (EBA_Destruction). Old actor first, so BuildingStructuralSupportSystem never sees
        // both registered in the same cell at once (Destroy -> ABuildingActor.Destroyed -> Unregister
        // runs synchronously, before the new actor is spawned or registered).
        ["ServerEditBuildingActor"] = new FRpcDef(
            "ServerEditBuildingActor",
            new[] {
                new FRpcParamDef("BuildingActorToEdit", ERpcParamKind.Object),
                new FRpcParamDef("NewBuildingClass", ERpcParamKind.ObjectPath),
                new FRpcParamDef("RotationIterations", ERpcParamKind.Byte),
                new FRpcParamDef("bMirrored", ERpcParamKind.Bool)
            },
            (actor, values) => {
                if (actor is not APlayerController { Pawn: { } pawn, PlayerState: { } playerState } pc) return;

                if (values[0] is not ABuildingActor oldBuilding) {
                    Console.WriteLine("NativeRpcHandlers: ServerEditBuildingActor named no building actor this server can resolve, ignoring");
                    return;
                }

                if (values[1] is not string newClassPath) {
                    Console.WriteLine("NativeRpcHandlers: ServerEditBuildingActor named no resolvable NewBuildingClass path, ignoring");
                    return;
                }

                if (oldBuilding.bDestroyed || !ReferenceEquals(oldBuilding.EditingPlayer, playerState)) {
                    Console.WriteLine($"NativeRpcHandlers: ServerEditBuildingActor on {oldBuilding.GetFName()} - " +
                                      "not this player's edit lock, ignoring");
                    return;
                }

                var world = pc.GetWorld();
                if (world == null) return;

                // ClassForPath, like every other building-path lookup - editing a wall INTO a door
                // is the one place where getting the C# type from the path matters most, and this
                // site was missed the first time round: the created piece became an ABuildingWall
                // and the EDITED one stayed an ABuildingActor, so a door you built by editing a wall
                // could be opened (the client predicts it) and never closed (the interact handler
                // switches on the C# type and saw no door).
                var newClass = ABuildingActor.ClassForPath(newClassPath);
                if (newClass == null) {
                    Console.WriteLine($"NativeRpcHandlers: ServerEditBuildingActor - '{newClassPath}' is not a known building class, ignoring");
                    return;
                }

                var rotationIterations = values[2] as byte? ?? 0;
                var bMirrored = values[3] as bool? ?? false;

                // Editing never changes the base slot kind - a Floor edits into Floor variants, never
                // a Wall - so the type and the anchor both come from the piece already on hand, not
                // re-derived from the new class in any way. See ABuildingActor.OverrideBuildingType and
                // AnchorLocation/AnchorYaw for why each of these specifically has to be carried forward
                // rather than recomputed.
                var editedType = oldBuilding.BuildingType;
                var currentLocation = oldBuilding.GetActorLocation();
                var currentYaw = oldBuilding.GetActorRotation().Yaw;

                // Round 56 drops the per-type gate that Round 45-50 built up one live test at a time.
                // That gate only ever existed to contain the damage from a rotation whose POSITION
                // half was wrong; with the pivot maths read from the game's own constants below,
                // rotating is correct for every family, and RotationIterations=0 is an exact identity
                // in all of them anyway. Stairs and Pillar rotate for the first time here - the client
                // has always sent them RotationIterations 1/2/3 (visible throughout the capture logs)
                // and this server has always thrown it away.
                //
                // Normalised because it is otherwise cumulative in the LOG only - anchorYaw 180 plus
                // two iterations printed as "Yaw=360", which is correct but unreadable next to the
                // -90 it should be compared against.
                // Round 57: RotationIterations counts from the piece's CURRENT rotation, not from a
                // fixed per-piece anchor. Round 52 introduced the anchor to kill a drift that was
                // really in the position maths, and every test since then hid the difference: the
                // user's own test pattern is base -> variant -> base, and on the base piece the
                // current yaw IS the anchor yaw, so both models agree. Stairs finally separated them,
                // because a stair is edited variant -> variant, staying rotated the whole time:
                // the capture shows StairW -> StairW with RotationIterations=2 sent THREE TIMES IN A
                // ROW, then 3 and 1 alternating. Anchor-relative makes all three of those land on the
                // same yaw - the ramp visibly refuses to turn, which is the report - while
                // current-relative reads them as the player flipping the ramp back and forth, which is
                // what repeating an edit is for. (The symmetric pieces hid it too: a plain Floor or a
                // RoofC looks identical at any yaw.)
                //
                // Nothing can accumulate any more the way Round 52 feared, and not because a fixed
                // reference forbids it: the transform below preserves the centroid EXACTLY
                // (placeAt + Rotate(C, targetYaw) == centroid by construction, with the rotation
                // rounded to exact 0/+-1), so a chain of a hundred edits leaves the piece's body
                // where the first one put it.
                var targetYaw = NormalizeYaw(currentYaw + rotationIterations * 90f);
                var placeRot = new FRotator { Yaw = targetYaw };

                // A building pivot is NOT the centre of the piece - it is a cell-EDGE midpoint 256 out
                // along the piece's local -Y (ABuildingActor::BaseLocToPivotOffset, read from the real
                // CDOs; see FBuildingSupportCellIndex). Rotate an actor about a point that is not its
                // own centre and the BODY swings a half-tile arc somewhere else, so holding the pivot
                // still across a yaw change is precisely what MAKES the piece appear to move. Round 53
                // pinning the pivot could therefore never have worked, and Round 55 rotating about a
                // cell centre derived from the wrong local axis moved the body just as far.
                //
                // What has to stay still is the piece's CENTROID, and that one rule covers every
                // family without a special case, because ABuildingActor::CentroidOffset differs
                // between them exactly where the geometry does:
                //
                //   * Floor/Roof/Stair/Pillar - CentroidOffset cancels BaseLocToPivotOffset, so the
                //     centroid IS the cell centre. Holding it still holds the CELL still, which is what
                //     picking a different corner of the same 2x2 pattern has to do. The pivot itself
                //     moves between the cell's four edge midpoints, as it must.
                //   * Wall - CentroidOffset has no horizontal part, so the centroid is the pivot, out
                //     on the cell edge where a wall lives. Holding it still means A WALL NEVER MOVES
                //     AT ALL, whatever RotationIterations says - it turns in place on its own edge.
                //     That is the bug the live capture caught: walls DO send RotationIterations 2
                //     (Solid -> Brace, Solid -> ArchwayLargeSupport), and Round 55 was swinging them a
                //     full 512 onto the far edge of the cell.
                //
                // RotationIterations=0 is an exact identity for every family, so an unrotated edit
                // still returns the anchor bit-for-bit.
                var centroid = FBuildingSupportCellIndex.CentroidOf(currentLocation, currentYaw, editedType);
                var placeAt = FBuildingSupportCellIndex.PivotForCentroid(centroid, targetYaw, editedType);

                oldBuilding.SetEditingPlayer(null);
                oldBuilding.Destroy();

                // Same as the create path: an edit that turns a wall into a DOOR has to produce an
                // ABuildingWall or the door is a wall with a door-shaped hole and no way to work it.
                var building = SpawnBuildingPiece(world, newClass, editedType);
                if (building == null) return;

                building.SetRole(ENetRole.ROLE_Authority);
                building.SetActorLocation(placeAt);
                building.SetActorRotation(placeRot);
                building.SetMirrored(bMirrored);
                building.SetReplicates(true);
                building.InitializeFromClass(newClass);
                building.OverrideBuildingType(editedType);
                building.SetAnchor(oldBuilding.AnchorLocation, oldBuilding.AnchorYaw);
                BuildingStructuralSupportSystem.Of(world).Register(building);

                if (pawn.CurrentWeapon is { } editTool) editTool.EditActor = null;

                Console.WriteLine($"NativeRpcHandlers: ServerEditBuildingActor - replaced {oldBuilding.GetFName()} with " +
                                  $"{newClass.NativePackagePath} ({editedType}) at {placeAt} Yaw={placeRot.Yaw} " +
                                  $"(RotationIterations={rotationIterations}, from={currentLocation}/{currentYaw}, " +
                                  $"centroid={centroid}, base={FBuildingSupportCellIndex.BaseLocationOf(placeAt, targetYaw)}), " +
                                  $"bMirrored={bMirrored}");
            }
        ),

        // AFortPlayerController::ServerEndEditingBuildingActor(ABuildingSMActor* BuildingActorToStopEditing)
        // - the player backing out of edit mode without confirming a pattern (moving the crosshair
        // off the piece, swapping weapons). Only undoes what ServerBeginEditingBuildingActor set up;
        // it does not touch the piece's shape.
        ["ServerEndEditingBuildingActor"] = new FRpcDef(
            "ServerEndEditingBuildingActor",
            new[] { new FRpcParamDef("BuildingActorToStopEditing", ERpcParamKind.Object) },
            (actor, values) => {
                if (actor is not APlayerController { Pawn: { } pawn, PlayerState: { } playerState }) return;
                if (values[0] is not ABuildingActor building) return;
                if (!ReferenceEquals(building.EditingPlayer, playerState)) return;

                building.SetEditingPlayer(null);
                if (pawn.CurrentWeapon is { } editTool) editTool.EditActor = null;

                Console.WriteLine($"NativeRpcHandlers: ServerEndEditingBuildingActor - {playerState.GetFName()} stopped editing {building.GetFName()}");
            }
        ),

        // AFortPlayerController::ServerRepairBuildingActor(ABuildingSMActor* BuildingActorToRepair) -
        // the player holding the repair input on a damaged piece they own. Confirmed live: the client
        // has been sending this all along (it shows up in the capture logs as an unhandled field),
        // this server just never had a handler.
        //
        // COST IS A PLACEHOLDER, in the same sense as every other magnitude in this project that is
        // not read from real game data: the piece is charged its own material in proportion to how
        // much of its health bar is missing, scaled by the flat 10 a placement costs. What IS honest
        // about it is the partial case - a player who cannot afford a full repair gets exactly the
        // fraction they paid for rather than nothing, and is billed only for health actually
        // restored (see ABuildingActor.Repair, which caps and reports).
        //
        // Only PLAYER-placed pieces, and only ones still standing. Repairing map scenery is not a
        // thing, and a piece already at zero is mid-cascade - see ABuildingActor.Repair.
        ["ServerRepairBuildingActor"] = new FRpcDef(
            "ServerRepairBuildingActor",
            new[] { new FRpcParamDef("BuildingActorToRepair", ERpcParamKind.Object) },
            (actor, values) => {
                if (actor is not APlayerController pc) return;
                if (values[0] is not ABuildingActor { bPlayerPlaced: true, bDestroyed: false } building) return;

                var missing = building.MaxHitPoints - building.CurrentHitPoints;
                if (missing <= 0) return;

                if (pc.WorldInventory is not { } inventory) return;
                if (BuildingResourcePathFor(building.Material) is not { } resourcePath) return;

                var resourceDef = UAssetRegistry.GetOrCreate(resourcePath);
                var stack = inventory.Inventory.Items.FirstOrDefault(item => item.ItemDefinition == resourceDef);
                if (stack is not { Count: > 0 }) {
                    Console.WriteLine($"NativeRpcHandlers: ServerRepairBuildingActor - no {resourceDef.GetFName()} to repair " +
                                      $"{building.GetFName()} with, ignoring");
                    return;
                }

                // Round up so a scratch still costs something, then let the player pay what they can.
                var fullCost = Math.Max(1, (int) MathF.Ceiling(
                    missing / (float) building.MaxHitPoints * BuildingPlaceResourceCost));
                var paid = Math.Min(fullCost, stack.Count);
                var buying = Math.Max(1, missing * paid / fullCost);

                // Repair is BOUGHT here and DELIVERED over time. The health does NOT move on this
                // line - BeginRepair puts the piece back on the same harden ramp a freshly placed one
                // rides, climbing to the target second by second with the client playing the build-in
                // animation the whole way. That is what a repair looks like in the real game;
                // awarding the health outright is what made it read as instant.
                var target = building.CurrentHitPoints + buying;
                if (building.GetWorld() is not { } repairWorld
                    || !BuildingStructuralSupportSystem.Of(repairWorld).BeginRepair(building, target)) return;

                // Bill only once the ramp is actually running, and only for what it will deliver -
                // BeginRepair clamps the target to MaxHitPoints, so a player who asked for more than
                // the piece can hold is not charged for the overshoot.
                var healed = Math.Min(target, building.MaxHitPoints) - building.CurrentHitPoints;
                var charged = Math.Max(1, (int) MathF.Ceiling(
                    healed / (float) building.MaxHitPoints * BuildingPlaceResourceCost));
                stack.Count = Math.Max(0, stack.Count - charged);
                inventory.Inventory.MarkItemDirty(stack);

                Console.WriteLine($"NativeRpcHandlers: ServerRepairBuildingActor - {building.GetFName()} " +
                                  $"+{healed} HP hardening from {building.CurrentHitPoints}/{building.MaxHitPoints} " +
                                  $"for {charged} x {resourceDef.GetFName()} -> {stack.Count}");
            }
        ),

        // AFortPlayerController::ServerOnMaterialSelection(uint8 NewResourceType, uint8 NewResourceLevel)
        // - the build-material wheel. Nothing here needs it to decide anything (which material a piece
        // costs is read from the building CLASS the client names in ServerCreateBuildingActor, see
        // BuildingResourcePathFor), so this is recorded rather than acted on - but it is the only
        // signal of what the player has selected, so it is worth having in the log next to the
        // placements it explains.
        ["ServerOnMaterialSelection"] = new FRpcDef(
            "ServerOnMaterialSelection",
            new[] {
                new FRpcParamDef("NewResourceType", ERpcParamKind.Byte),
                new FRpcParamDef("NewResourceLevel", ERpcParamKind.Byte)
            },
            (actor, values) => {
                var resourceType = values[0] as byte? ?? 0;
                var resourceLevel = values[1] as byte? ?? 0;
                Console.WriteLine($"NativeRpcHandlers: ServerOnMaterialSelection - {actor.GetFName()} selected " +
                                  $"ResourceType={resourceType} ({ResourceNameFor(resourceType)}) Level={resourceLevel}");
            }
        ),

        // APlayerController::ServerSuicide - the player eliminating themselves. Goes through the same
        // FortDamageSystem.Kill every other death does, so the death report, the DeathInfo block and
        // the respawn/spectate transition are whatever they already are for a normal kill.
        //
        // EDeathCause has no plain "suicide": the closest real values are TeamSwitchSuicide (46),
        // which names a reason that is not this one, and Unspecified (48). Unspecified is the honest
        // pick - see EDeathCause, where it is already what an unattributable death goes out as.
        ["ServerSuicide"] = NoParams("ServerSuicide", pc => {
            if (pc.PlayerState is not { } playerState) return;
            FortDamageSystem.Kill(playerState, EDeathCause.Unspecified);
        }),

        // No-op / log-only: real gameplay behavior (spectator pawn swap, AI logging toggle, level
        // travel restart, etc.) isn't implemented yet, but decoding these (trivial - they take no
        // parameters) means they show up in the log as what they are instead of a raw field skip.
        ["ServerCheckClientPossession"] = NoParams("ServerCheckClientPossession"),
        ["ServerCheckClientPossessionReliable"] = NoParams("ServerCheckClientPossessionReliable"),
        ["ServerPause"] = NoParams("ServerPause"),
        ["ServerRestartPlayer"] = NoParams("ServerRestartPlayer"),
        ["ServerShortTimeout"] = NoParams("ServerShortTimeout"),
        ["ServerVerifyViewTarget"] = NoParams("ServerVerifyViewTarget"),
        ["ServerViewNextPlayer"] = NoParams("ServerViewNextPlayer"),
        ["ServerViewPrevPlayer"] = NoParams("ServerViewPrevPlayer"),
        ["ServerToggleAILogging"] = NoParams("ServerToggleAILogging"),

        // The rest of what a live client actually sends and this server used to skip as raw fields,
        // counted across the capture logs: ServerTouchActiveTime (647 - the AFK keepalive),
        // ServerLoadingScreenDropped / ServerReturnToMainMenu (63 / 43 - session lifecycle) and
        // ServerPlayUnableToPerformActionMontage (14 - a "can't do that" animation that only matters
        // with an audience). None of them has anything to act on here, but naming them keeps the
        // console log about the things that ARE unhandled, which is this project's main debugging
        // surface - see [[re_and_capture_techniques]].
        // AFortPlayerControllerAthena::ServerAttemptAircraftJump(FRotator ClientRotation) - the player
        // jumping out of the battle bus. Seen in the PR3.0 capture as field[166] on the player
        // controller (PriveDev/PacketProxy/decoded_new.txt), so this is what a real client sends.
        //
        // All this can honestly do is clear bInAircraft: the client owns the skydive and the glider
        // from there, exactly as it owns movement everywhere else on this server (see
        // ServerMoveNoBase - the pawn's position is whatever the client reports). What the server
        // must NOT do is keep the flag set, because the client will not leave the bus until it comes
        // back cleared.
        //
        // The jump WINDOW is not enforced here. The client refuses to send this at all while
        // AFortGameStateAthena::bAircraftIsLocked (handle 160) is set, and it checks the aircraft's
        // own DropStartTime itself - so the gate already exists on the side that has the flight
        // clock, and duplicating it here could only ever disagree with it.
        ["ServerAttemptAircraftJump"] = new FRpcDef(
            "ServerAttemptAircraftJump",
            new[] { new FRpcParamDef("ClientRotation", ERpcParamKind.Rotator) },
            (actor, values) => {
                if (actor is not APlayerController { PlayerState: { } playerState } pc) return;
                if (!playerState.bInAircraft) return;

                // BEFORE THE DOORS OPEN, REFUSE. AFortGameStateAthena::bAircraftIsLocked already
                // stops the client sending this, but a gate that only exists on the client is not a
                // gate - and this one matters more than most, because a jump before DropStartTime is
                // exactly what the user hit when the client crashed a second into the skydive.
                if (pc.GetWorld()?.GameState is { bAircraftIsLocked: true }) {
                    Console.WriteLine($"NativeRpcHandlers: ServerAttemptAircraftJump from " +
                                      $"{playerState.GetFName()} REFUSED - the bus doors are still shut");
                    return;
                }

                LeaveAircraft(pc, playerState, $"jumped (ClientRotation={values[0]})");
            }
        ),

        // AFortPlayerControllerAthena::ServerThankBusDriver - the emote-on-the-bus thank you. Sets a
        // replicated flag (bThankedBusDriver, handle 253) which this project does not send yet, so
        // this is named rather than acted on.
        ["ServerThankBusDriver"] = NoParams("ServerThankBusDriver"),

        ["ServerTouchActiveTime"] = NoParams("ServerTouchActiveTime"),

        // APlayerController::ServerUpdateLevelVisibility(const FUpdateLevelVisibilityLevelInfo&) -
        // the client reporting a streaming level it has loaded or dropped. The most frequent RPC
        // this server receives, and until now discarded entirely. See
        // UNetConnection.ClientVisibleLevelNames for what it is worth here, and
        // FUpdateLevelVisibilityLevelInfo for the wire format and why the 10.40 SDK is the authority
        // for it rather than the 4.23 source.
        // expectsFullDecode: TRUE. The declared layout is a hypothesis and is currently known to be
        // wrong, so the leftover-bits check is exactly what should fire - and it is what triggers the
        // automatic raw dump in UActorChannel.DumpRawRpcPayload. Without it the graceful null this
        // reader now returns would be silent evidence, which is the worst kind.
        ["ServerUpdateLevelVisibility"] = new FRpcDef(
            "ServerUpdateLevelVisibility",
            new[] { new FRpcParamDef("LevelVisibility", ERpcParamKind.LevelVisibility) },
            (actor, values) => {
                if (actor is not APlayerController pc) return;

                if (values[0] is not FUpdateLevelVisibilityLevelInfo info) {
                    if (StateOf(actor)._reportedLevelVisibilitySanity) return;
                    StateOf(actor)._reportedLevelVisibilitySanity = true;

                    Console.WriteLine("NativeRpcHandlers: ServerUpdateLevelVisibility - the declared parameter " +
                                      "layout (two FNames and a bit) does not fit the wire; the decode ran out of " +
                                      "room. See FUpdateLevelVisibilityLevelInfo, and run with " +
                                      "RPC_DUMP=ServerUpdateLevelVisibility to get a raw sample.");
                    return;
                }

                ApplyLevelVisibility(pc, info, "ServerUpdateLevelVisibility");
            },
            expectsFullDecode: true
        ),

        // APlayerController::ServerUpdateMultipleLevelsVisibility(const TArray<FUpdateLevelVisibilityLevelInfo>&)
        // - the same report, batched. Sent when several levels change state at once, which is why it
        // is rarer (195 calls against 7685) but far bigger: the samples are 7826 bits, about eight
        // levels' worth.
        //
        // Same self-check as its single sibling, and the same automatic raw dump if the layout is
        // wrong - which is why this could be written straight after the element format was derived
        // rather than needing an investigation of its own.
        ["ServerUpdateMultipleLevelsVisibility"] = new FRpcDef(
            "ServerUpdateMultipleLevelsVisibility",
            new[] { new FRpcParamDef("LevelVisibilities", ERpcParamKind.LevelVisibilityArray) },
            (actor, values) => {
                if (actor is not APlayerController pc) return;

                if (values[0] is not List<FUpdateLevelVisibilityLevelInfo> levels) {
                    if (StateOf(actor)._reportedLevelVisibilitySanity) return;
                    StateOf(actor)._reportedLevelVisibilitySanity = true;

                    Console.WriteLine("NativeRpcHandlers: ServerUpdateMultipleLevelsVisibility - the declared " +
                                      "layout (uint16 count, then that many elements) does not fit the wire.");
                    return;
                }

                foreach (var level in levels) {
                    ApplyLevelVisibility(pc, level, "ServerUpdateMultipleLevelsVisibility");
                }
            },
            expectsFullDecode: true
        ),

        // APlayerController::ServerUpdateCamera(const FVector_NetQuantize& CamLoc, int32
        // CamPitchAndYaw) - THE most frequent RPC this server receives (3908 of them across the logs
        // when the unhandled list was last counted) and it was being skipped entirely.
        //
        // THE DECODE CHECKS ITSELF. A quantized vector read at the wrong scale or bit width does not
        // fail, it returns a plausible-looking wrong number - this project has been caught by exactly
        // that before, with ReplicatedMovement arriving at one hundredth of its real size. So the
        // handler compares the camera against the pawn it is supposed to be looking at and says so
        // when they disagree by more than a camera boom could account for. If that warning appears,
        // the scale is wrong, not the client.
        ["ServerUpdateCamera"] = new FRpcDef(
            "ServerUpdateCamera",
            new[] {
                new FRpcParamDef("CamLoc", ERpcParamKind.VectorQuantize),
                new FRpcParamDef("CamPitchAndYaw", ERpcParamKind.Int32)
            },
            (actor, values) => {
                if (actor is not APlayerController pc) return;
                if (values[0] is not FVector camLoc) return;
                if (pc.Pawn is not { } camPawn) return;

                // THE VALUE IS ONLY KEPT IF IT IS PLAUSIBLE. A quantized vector read at the wrong
                // scale does not fail, it returns a plausible-LOOKING wrong number, so the check has
                // to be against something independent: the pawn this camera is supposed to be
                // looking at. A third-person boom is a few metres; anything past 50 m means the
                // framing is wrong, and storing a number known to be wrong is worse than storing
                // none. This gate stays even though the decode is now confirmed - it is what would
                // catch a regression.
                //
                // ...BUT THE PAWN CANNOT BE THE ONLY REFERENCE, and taking it as one built a loop
                // that fed itself. The server's pawn location goes stale the moment a player boards
                // a vehicle (a based ServerMove carries no world position - see the move handler),
                // so after driving 50 m every camera update failed this test and was thrown away -
                // which meant the server could never learn where the player was, which is what kept
                // the pawn stale. The one world-space position source still arriving was being
                // discarded precisely when it was the only one left.
                //
                // CONTINUITY IS THE SECOND REFERENCE, and it is a real check rather than a
                // concession: consecutive camera samples are metres apart because a camera moves
                // continuously, so a chain of them stays trustworthy however far it wanders from a
                // pawn position the server stopped updating. A mis-scaled decode still fails it,
                // because a wrong scale factor moves the value by kilometres between one sample and
                // the next, not metres.
                var pawnLoc = camPawn.GetActorLocation();
                var distance = Distance(camLoc, pawnLoc);

                var continuous = pc.LastClientCameraLocation is { } previous
                                 && Distance(camLoc, previous) <= 5000f;

                var plausible = distance <= 5000f || continuous;

                if (plausible) {
                    // HOW OFTEN THIS ACTUALLY ARRIVES, measured rather than assumed - the first
                    // staleness bound was set from an assumption ("several times a second") and made
                    // the relevancy viewpoint flap. UPlayerCameraManager::UpdateCamera only sends
                    // when the camera moved or turned, so a stationary player is legitimately quiet.
                    var cameraNow = pc.GetWorld()?.TimeSeconds ?? 0f;
                    // The gap is only meaningful from the SECOND sample on - LastClientCameraTime
                    // starts at negative infinity, so measuring against it on the first would record
                    // an infinite gap and poison the maximum for the rest of the session.
                    if (StateOf(actor)._cameraSampleCount++ == 0) StateOf(actor)._cameraFirstSampleTime = cameraNow;
                    else StateOf(actor)._cameraLongestGap = MathF.Max(StateOf(actor)._cameraLongestGap, cameraNow - pc.LastClientCameraTime);

                    if (StateOf(actor)._cameraSampleCount % 500 == 0) {
                        var elapsed = cameraNow - StateOf(actor)._cameraFirstSampleTime;
                        Console.WriteLine($"NativeRpcHandlers: ServerUpdateCamera rate - {StateOf(actor)._cameraSampleCount} samples " +
                                          $"in {elapsed:F1}s ({StateOf(actor)._cameraSampleCount / MathF.Max(elapsed, 0.001f):F1}/s), " +
                                          $"longest gap {StateOf(actor)._cameraLongestGap:F2}s. Any server-side staleness bound has " +
                                          "to be well above that longest gap - see UNetDriver.CameraViewpointTimeout.");
                    }

                    // THE BOOM LENGTH, measured while both ends are known good. `distance` is
                    // camera-to-pawn, and it only means anything while the pawn's own position is
                    // current - which is exactly while unbased moves are still arriving. Recorded
                    // here so a later movement correction can walk back along the camera's forward
                    // vector and land on the player instead of on their camera. See
                    // APawn.LastObservedCameraBoom for why this has to be measured per player, and
                    // APawn.ObserveCameraBoom for why the SMALLEST recent sample is the one kept.
                    //
                    // The skew bound is the load-bearing part: the two ends arrive in different
                    // messages, so any time between them shows up as extra distance. At one second
                    // it let a 552uu "boom" through where the real one is about 254, and the
                    // correction then overshot the player into the map.
                    if (cameraNow - camPawn.LastUnbasedMoveTime <= APawn.BoomSampleSkewSeconds) {
                        camPawn.ObserveCameraBoom(distance, cameraNow);
                    }

                    pc.LastClientCameraLocation = camLoc;
                    pc.LastClientCameraTime = cameraNow;

                    // APlayerController::ServerUpdateCamera_Implementation, verbatim: YAW is the
                    // HIGH half and PITCH the low one, each a compressed short. The name says
                    // "PitchAndYaw" and the packing is the other way round.
                    var packed = (uint) (values[1] as int? ?? 0);
                    pc.LastClientCameraRotation = new FRotator {
                        Yaw = FRotator.DecompressAxisFromShort((packed >> 16) & 65535),
                        Pitch = FRotator.DecompressAxisFromShort(packed & 65535)
                    };

                    // THE FIRST SAMPLE CANNOT CONFIRM AN ANGLE DECODE, because a player who has just
                    // spawned is looking straight down +X and both halves read zero - which is what
                    // the first live capture showed, and zero is exactly what a BROKEN unpack would
                    // also produce. So report the first NON-zero one as well: a yaw that tracks
                    // where the player actually turned is the check, and it costs one bool.
                    if (!StateOf(actor)._reportedCameraRotation
                        && (MathF.Abs(pc.LastClientCameraRotation.Yaw) > 1f
                            || MathF.Abs(pc.LastClientCameraRotation.Pitch) > 1f)) {
                        StateOf(actor)._reportedCameraRotation = true;
                        Console.WriteLine("NativeRpcHandlers: ServerUpdateCamera first NON-ZERO rotation - " +
                                          $"{pc.LastClientCameraRotation} (packed 0x{packed:X8}). Yaw is the HIGH " +
                                          "half; if this does not match where the player was looking, that is the " +
                                          "half to suspect.");
                    }
                }

                if (StateOf(actor)._reportedCameraSanity) return;
                StateOf(actor)._reportedCameraSanity = true;

                // THE DECODE IS RIGHT, AND AN EARLIER NOTE HERE SAID IT COULD NOT BE. That note
                // argued from a payload size of "exactly 83 bits on all 3908 calls", which no
                // reading of (FVector_NetQuantize, int32) can produce - and concluded the framing
                // must be wrong. A live client settled it the other way:
                //
                //     ServerUpdateCamera first decode - CamLoc=(-116873, -121234, 4010),
                //     pawn=(-116631, -121265, 3941), 254uu apart
                //
                // 254 units is a third-person camera boom, to the centimetre. The 83 was a bad
                // count, not a bad decode. The lesson is the cheaper one: a size that "cannot
                // happen" is evidence about the MEASUREMENT as much as about the model, and one
                // live value settled what an hour of arithmetic could not.
                Console.WriteLine($"NativeRpcHandlers: ServerUpdateCamera first decode - CamLoc={camLoc}, " +
                                  $"pawn={pawnLoc}, {distance:F0}uu apart, rotation={pc.LastClientCameraRotation}: " +
                                  (plausible
                                      ? "plausible - relevancy will be measured from here, as real UE does"
                                      : "IMPLAUSIBLE - value NOT kept, relevancy falls back to the pawn"));
            }
        ),
        // RECORDED, not just logged - see APlayerController's readiness block for why the values
        // matter and what ignoring them cost.
        ["ServerLoadingScreenDropped"] = new FRpcDef(
            "ServerLoadingScreenDropped",
            Array.Empty<FRpcParamDef>(),
            (actor, _) => {
                if (actor is not APlayerController pc || pc.bLoadingScreenDropped) return;

                pc.bLoadingScreenDropped = true;
                Console.WriteLine($"NativeRpcHandlers: ServerLoadingScreenDropped - {pc.GetFName()} can see the world");

                // WELCOME_MESSAGE is how the FText writer gets tested at all. ClientSendMessage is
                // the only arbitrary-text channel this server has, and it has never been sent, so
                // there needs to be a way to fire one on demand - and this is the first moment the
                // player can actually read anything. Unset by default: an unprompted message in
                // every session would be noise, and this exists to answer one question.
                if (actor.WorldOptions.Get("WELCOME_MESSAGE") is { Length: > 0 } message
                    && pc.GetWorld()?.NetDriver?.ClientConnections
                        .FirstOrDefault(c => c.PlayerController == pc)?.FindActorChannel(pc) is { } channel) {
                    // BOTH CHANNELS, because one of them showing and the other not is the answer.
                    // ClientSendMessage carries an FText, ClientTeamMessage carries an FString this
                    // project has been writing correctly for months. If only the FString one appears,
                    // the FText encoding is the bug; if neither does, neither RPC has an in-match UI
                    // bound to it and the search moves to which channel Fortnite's HUD listens on.
                    // See UActorChannel.SendClientTeamMessage.
                    // TAGGED SO THE CONSOLE SAYS WHICH ONE ARRIVED. The text turned up in the
                    // client's console at join - so it decodes and the channel is live - but both
                    // RPCs were carrying the identical string, which cannot say whether the FText
                    // one, the FString one, or both got through. One word of suffix settles it.
                    channel.SendClientSendMessage(message + " [FText/ClientSendMessage]");
                    channel.SendClientTeamMessage(pc.PlayerState, message + " [FString/ClientTeamMessage]",
                                                  typeNameIndex: 0);

                    FortWelcomeMessage.Schedule(pc, (float) (pc.GetWorld()?.TimeSeconds ?? 0d));

                    Console.WriteLine($"NativeRpcHandlers: sent ClientSendMessage(\"{message}\") as an FText AND " +
                                      "ClientTeamMessage as an FString, with resends queued. Which of the two " +
                                      "appears - and whether a LATER one does - separates a wrong encoding from " +
                                      "a channel nobody reads from a HUD that was not up yet.");
                }
            }
        ),
        ["ServerReturnToMainMenu"] = NoParams("ServerReturnToMainMenu"),

        /// The other half of the ride, and the one that has to exist or a tester is STUCK on the
        /// vehicle with no way off.
        ///
        /// AFortPlayerControllerAthena::ServerAttemptExitVehicle takes no parameters - field 284, and
        /// the capture agrees. It arrives on the CONTROLLER, not the pawn or the vehicle.
        ///
        /// REWRITTEN FOR THE STATE THAT ACTUALLY SEATS SOMEONE. The first version looked for
        /// `pc.Pawn is { AttachParent: AFortAthenaVehicle }`, because boarding was an attachment
        /// then. Boarding is now a movement base plus VehicleStateRep and nothing is ever attached,
        /// so that pattern never matched and getting out did nothing at all - the player was seated,
        /// pressed exit, and the server silently agreed with itself that they were not in a vehicle.
        ["ServerAttemptExitVehicle"] = new FRpcDef(
            "ServerAttemptExitVehicle",
            Array.Empty<FRpcParamDef>(),
            (actor, _) => {
                if (actor is not APlayerController pc) return;
                if (pc.Pawn is not { } rider) return;

                if (rider.VehicleStateVehicle is not AFortAthenaVehicle vehicle) {
                    Console.WriteLine($"NativeRpcHandlers: ServerAttemptExitVehicle - {rider.GetFName()} is not in " +
                                      "a vehicle as far as this server knows, ignoring");
                    return;
                }

                // BOTH HALVES, in the order they were set. Clearing VehicleStateRep is what tells the
                // driver's own client it is out; clearing the base is what tells everyone else.
                rider.SetVehicleState(null, 0, 0f);
                rider.SetMovementBase(null);

                // ...and the third: the seat array is what the client's own exit path reads, so
                // leaving it occupied would put the player back in a vehicle they just left.
                vehicle.SeatPawn(rider, -1, 0f);

                if (vehicle.Driver == rider) vehicle.Driver = null;
                vehicle.SetOwner(null);
                vehicle.FlushNetDormancy();

                // AND TELL THE CLIENT IT IS WALKING AGAIN. Getting out of the seat is not enough:
                // the client put itself in EFortCustomMovement::Driving and only a server movement
                // correction can take it out (ReplicatedMovementMode never reaches the pawn's own
                // player). Without this the player is out of the vehicle and cannot move at all.
                rider.RequestMovementModeCorrection(APawn.PackedMovementModeWalking);
                rider.BeginVehicleExitReport();

                Console.WriteLine($"NativeRpcHandlers: ServerAttemptExitVehicle - {rider.GetFName()} left " +
                                  $"{vehicle.GetFName()}");
            }
        ),

        /// AFortPlayerControllerZone::ServerRequestSeatChange(int32 TargetSeatIndex) - moving between
        /// seats without getting out.
        ///
        /// THE SERVER DOES NOT KNOW THE SEATS, and does not need to: the seat layout lives in the
        /// vehicle Blueprint the client already has, and the client only ever asks for an index it
        /// can see. What the server owns is the AUTHORITATIVE ANSWER - VehicleStateRep.SeatIndex is
        /// what every client, including the asker, reads the seating from - so this accepts the
        /// request and republishes the state. A real server would refuse an occupied seat; that needs
        /// the seat array (see [[vehicle-wire-flow]]), and with one player there is nothing to refuse.
        ["ServerRequestSeatChange"] = new FRpcDef(
            "ServerRequestSeatChange",
            new[] { new FRpcParamDef("TargetSeatIndex", ERpcParamKind.Int32) },
            (actor, values) => {
                if (actor is not APlayerController pc) return;
                if (pc.Pawn is not { } rider) return;

                if (rider.VehicleStateVehicle is not AFortAthenaVehicle vehicle) {
                    Console.WriteLine("NativeRpcHandlers: ServerRequestSeatChange from a pawn this server does " +
                                      "not have in a vehicle, ignoring");
                    return;
                }

                var target = values[0] as int? ?? 0;
                if (target is < 0 or > 255) return;

                // THE SEAT ARRAY IS NOW THE AUTHORITY on who sits where, so an occupied seat can
                // finally be refused - which the note above said needed exactly this.
                if (vehicle.SeatComponent is { } seats && target < seats.PlayerSlots.Length &&
                    seats.PlayerSlots[target].Player is { } occupant && occupant != rider) {
                    Console.WriteLine($"NativeRpcHandlers: ServerRequestSeatChange - seat {target} of " +
                                      $"{vehicle.GetFName()} is taken by {occupant.GetFName()}, refusing");
                    return;
                }

                vehicle.SeatPawn(rider, target, rider.VehicleStateEntryTime);
                rider.SetVehicleState(vehicle, (byte) target, rider.VehicleStateEntryTime);

                Console.WriteLine($"NativeRpcHandlers: ServerRequestSeatChange - {rider.GetFName()} moved to seat " +
                                  $"{target} of {vehicle.GetFName()}");
            }
        ),

        // Same idea, but these carry a parameter, so they need a real decode to stay in sync with the
        // bunch rather than relying on the field's bit-count resync.
        ["ServerClientPawnLoaded"] = new FRpcDef(
            "ServerClientPawnLoaded",
            new[] { new FRpcParamDef("bIsPawnLoaded", ERpcParamKind.Bool) },
            (actor, values) => {
                if (actor is not APlayerController pc) return;

                var loaded = values[0] as bool? ?? false;
                if (pc.bClientPawnLoaded == loaded) return;

                pc.bClientPawnLoaded = loaded;
                Console.WriteLine($"NativeRpcHandlers: ServerClientPawnLoaded on {pc.GetFName()} " +
                                  $"bIsPawnLoaded={loaded} - the client has its pawn");
            }
        ),
        ["ServerSetClientHasFinishedLoading"] = new FRpcDef(
            "ServerSetClientHasFinishedLoading",
            new[] { new FRpcParamDef("bInHasFinishedLoading", ERpcParamKind.Bool) },
            (actor, values) => {
                if (actor is not APlayerController pc) return;

                var finished = values[0] as bool? ?? false;
                if (pc.bClientHasFinishedLoading == finished) return;

                pc.bClientHasFinishedLoading = finished;

                // And out again on APlayerState handle 29 - but ONLY UPWARDS, never back to false.
                //
                // That asymmetry is deliberate. This server asserts bHasFinishedLoading = true at
                // login because it is half of the gate that dismisses Athena's loading screen, which
                // took a long time to get right. Letting a client-sent `false` pull it back down
                // would risk re-raising that screen for a reason nothing here understands yet. So the
                // client's signal is allowed to CONFIRM the flag and is only logged when it
                // disagrees - if that log ever appears, it is worth investigating rather than
                // silently obeying.
                if (pc.PlayerState is { } playerState) {
                    if (finished) {
                        playerState.bHasFinishedLoading = true;
                    } else if (playerState.bHasFinishedLoading) {
                        Console.WriteLine("NativeRpcHandlers: ServerSetClientHasFinishedLoading(false) while the " +
                                          "server had it TRUE - not lowering it, see the comment here.");
                    }
                }

                Console.WriteLine($"NativeRpcHandlers: ServerSetClientHasFinishedLoading on {pc.GetFName()} " +
                                  $"bInHasFinishedLoading={finished}");
            }
        ),

        // AFortPlayerController::ServerReadyToStartMatch - the client's own "I am done joining"
        // signal, and the hook Project-Reboot-3.0 uses as its join checkpoint
        // (FortPlayerControllerAthena.cpp:534). Nothing to do here - AGameModeBase.InitGameState
        // already declares the match InProgress immediately, so there is no ReadyToStartMatch gate
        // to release - but it is the clearest marker in the log of the client considering itself in
        // the match, so it is worth naming rather than skipping as an unknown field.
        ["ServerReadyToStartMatch"] = NoParams("ServerReadyToStartMatch")
    };

    /// <summary>
    ///     Puts the dropped item on the ground as an AFortPickup, which is what makes a drop visible
    ///     to anyone (including the player who dropped it) rather than just making the item vanish.
    ///
    ///     No channel is opened here. That is the whole point of
    ///     UNetDriver.ServerReplicateActors' dynamic pass: the actor simply exists and is relevant,
    ///     and every connection that does not already have a channel for it gets one on the next
    ///     tick. This is the first actor in the project to arrive that way.
    ///
    ///     Real UE tosses the pickup along an arc driven by PickupLocationData and a
    ///     UProjectileMovementComponent. There is no physics here, so it is placed directly at the
    ///     pawn and declared already at rest (bServerStoppedSimulation) - see
    ///     NativeRepLayouts.PickupProps.
    /// </summary>
    /// <summary>
    ///     How far in front of the pawn a dropped item is LAUNCHED, in Unreal units - not where it
    ///     lands, which is this plus however far TossSpeed carries it during the fall.
    ///
    ///     60, DOWN FROM 150. The launch point is what the player watches the item appear at, and
    ///     150 read as "it spawns some way away from me". The fall from DropLaunchHeight takes about
    ///     half a second, which at TossSpeed adds another ~130 units, so the item still lands about
    ///     two metres away - a toss, not a placement.
    /// </summary>
    private const float TossDistance = 60.0f;

    /// <summary>How wide a container fans its loot, either side of the player's heading.</summary>
    private const float LootFanHalfAngleDegrees = 42.0f;

    /// <summary>Nearest and furthest a container's loot lands, in Unreal units (~1.1m to ~1.9m).</summary>
    private const float LootFanMinDistance = 110.0f;
    private const float LootFanMaxDistance = 190.0f;

    /// <summary>
    ///     How far above the pawn's ORIGIN a dropped item is launched from - and the single number
    ///     that decides whether the drop animates at all.
    ///
    ///     THE ITEM WAS BEING SPAWNED INSIDE THE LANDSCAPE, and the client's own log said so in a
    ///     line nothing had been reading:
    ///
    ///         LogProjectileMovement: Projectile FortPickupAthena_2147476438: (Role: 1, Iteration 1,
    ///             step 0.017, ...) sim (Pos X=-117258.805 Y=-120989.305 Z=3914.900,
    ///             Vel X=150.100 Y=-212.300 Z=420.000)
    ///         LogMovement: ResolvePenetration: FortPickupAthena_2147476438.CollisionCylinder at
    ///             location ... Z=3914.900 inside LandscapeStreamingProxy4.
    ///             LandscapeHeightfieldCollisionComponent_3 ... by 95.585
    ///
    ///     ONE substep, then a depenetration, then the drop sound two milliseconds after the spawn.
    ///     The velocity was arriving perfectly and the client was building its projectile movement
    ///     component exactly as intended; the component simply had nowhere to go, because the item
    ///     began 95 units underground. All 17 dropped pickups in that session did the same, by
    ///     between 90 and 113 units.
    ///
    ///     That 95 is not terrain error, it is FortPickupAthena's own collision capsule - see
    ///     FortPickupToss.RestClearance for the measurement. A pickup needs its ORIGIN about 135
    ///     units above the ground merely to be free of it, so the old launch at `origin.Z + 40`
    ///     (the pawn's capsule centre is 96 above its feet, so 136 above the ground) cleared the
    ///     landscape by ONE UNIT. The item was either just touching the ground or just inside it,
    ///     which is what "it lands with no falling animation" and "it is buried" both are.
    ///
    ///     So this is the free height plus a real fall: 100 above the capsule centre is 196 above the
    ///     feet, a little over head height, where a player's hands are. **It may not go below about
    ///     40** (= 135 above the feet) without putting the item back inside the ground at the one
    ///     moment it matters; everything above that is fall to watch. About 90 units of visible movement over a third of a second.
    /// </summary>
    private const float DropLaunchHeight = 100.0f;

    /// <summary>
    ///     How hard a dropped item is thrown, forward and upward, in uu/s.
    ///
    ///     Bounded by the MaxSpeed Tools/ProjectileReplay measured off real dropped pickups (503.7,
    ///     IQR 12.2): a drop is a gentle lob, not a throw, and asking for more than the clamp just
    ///     gets clamped. These two are NOT measured - the capture records where tossed items went,
    ///     not the velocity they left with - so unlike everything in FortPickupToss they are chosen,
    ///     to put the item roughly where TossDistance used to place it outright.
    /// </summary>
    private const float TossSpeed = 370.0f;

    /// <summary>
    ///     Upward, and now ZERO: a dropped item leaves the hands and falls, it is not lobbed.
    ///
    ///     It was 420, on the reasoning that a pickup's measured gravity of 2800 makes any smaller
    ///     number invisible - 420 up buys a 31-unit rise, and 180 would buy six. That reasoning was
    ///     about a launch point at ground level. The launch is head height now (DropLaunchHeight),
    ///     so there is a real fall to watch either way, and the rise on top of it reads as a flick:
    ///     reported as "the little hop at the moment of the throw feels off".
    ///
    ///     THE REFERENCE DOES THROW THEM UP - Vz = +154, +234, +268, +361 in the PR3.0 logs - but
    ///     every one of those is a CONTAINER spilling its loot, which is a different motion from a
    ///     player dropping something. Raise this if a chest toss ever wants the lob back; it is the
    ///     one number here the capture has an opinion about.
    ///
    ///     TossSpeed went 260 -> 370 alongside, to keep the landing point where it already was: with
    ///     no upward push the fall is 0.37 s instead of 0.52 s, and the item would otherwise stop
    ///     about 40 units short.
    /// </summary>
    private const float TossUpSpeed = 0.0f;

    /// <summary>
    ///     The standard Fortnite character capsule half-height, used to get from a pawn's ORIGIN
    ///     (its capsule centre) to the ground it is standing on. Same 96 FortProjectileSystem uses
    ///     for ThrowerGroundZ, and a convention rather than something read from the paks.
    /// </summary>
    private const float PawnCapsuleHalfHeight = 96.0f;

    /// <summary>
    ///     internal rather than private since Round 47: FortHarvestResources.Grant calls this
    ///     directly for a resource stack that has no more room (drop the overflow instead of
    ///     discarding it) - the exact same "put it on the ground as a real pickup" mechanism an
    ///     inventory drop already uses, not a second implementation of it.
    /// </summary>
    /// <param name="at">
    ///     Where the pickup comes to rest. Null keeps the plain in-front-of-the-pawn drop an
    ///     inventory drop wants; container loot passes a fanned-out spot from
    ///     <see cref="ContainerLootScatter"/> so a five-item chest does not stack five pickups on
    ///     one point.
    /// </param>
    /// <param name="tossedFromContainer">
    ///     AFortPickup::bTossedFromContainer (handle 50). False means a player dropped it.
    /// </param>
    internal static void SpawnDroppedPickup(APlayerController pc, FFortItemEntry item, int count,
                                            (FVector Launch, FVector Velocity)? toss = null,
                                            bool tossedFromContainer = false) {
        var world = pc.GetWorld();
        if (world == null) return;

        var pawn = pc.Pawn;
        if (pawn == null) {
            Console.WriteLine("NativeRpcHandlers: SpawnDroppedPickup - no pawn to drop from, skipping the world pickup");
            return;
        }

        var pickup = world.SpawnActor<AFortPickup>(GUClassArray.StaticClass<AFortPickup>(), new FActorSpawnParameters {
            ObjectFlags = EObjectFlags.RF_Transient
        });

        if (pickup == null) return;

        // In front of the player, not inside them, and then TOSSED. Yaw comes from the last move the
        // client sent (APawn.LastClientViewRotation) - the pawn's own Rotation is never updated, so
        // that is the only heading available.
        //
        // THE ARC IS SIMULATED NOW. This used to end at the line below, leaving every dropped item
        // hanging at a fixed offset from the player: standing on a ramp, at the edge of a build or on
        // any slope, it floated or sank into geometry, and a pickup the client's interaction query
        // cannot reach is a pickup that is gone. The comment here used to say as much - "with no toss
        // to simulate, the least this server can do is not bury the pickup in the pawn's own
        // capsule". There is a toss to simulate now, measured off a real server: see FortPickupToss,
        // and Tools/ProjectileReplay for where each of its constants comes from.
        var yawRadians = (pawn.LastClientViewRotation?.Yaw ?? 0.0f) * MathF.PI / 180.0f;
        var origin = pawn.GetActorLocation();

        // The dropper's FEET as a floor of last resort - see FortPickupToss's _floorZ. The capsule
        // half-height is the same 96 FortProjectileSystem uses for ThrowerGroundZ.
        var floorZ = origin.Z - PawnCapsuleHalfHeight;

        // ONE PATH FOR BOTH, and it took the whole toss investigation to earn that. Container loot
        // used to be PLACED at an authored fan point and never tossed, because an earlier attempt to
        // simulate it made chest items invisible - the sweep found nothing, the item went underground
        // and the comment here said "a caller that named an exact spot meant it".
        //
        // What was really wrong was the same pair of bugs that stopped a player's drop animating: the
        // item spawned inside the ground, and Settle had no "it never came to rest" guard. Both are
        // fixed, so a container's fan point is now a TARGET to aim at rather than a placement to
        // honour, and its loot arcs out and lands on the real ground like anything else.
        var (launch, tossVelocity) = toss ?? (
            new FVector {
                X = origin.X + MathF.Cos(yawRadians) * TossDistance,
                Y = origin.Y + MathF.Sin(yawRadians) * TossDistance,
                Z = origin.Z + DropLaunchHeight
            },
            new FVector {
                X = MathF.Cos(yawRadians) * TossSpeed,
                Y = MathF.Sin(yawRadians) * TossSpeed,
                Z = TossUpSpeed
            });

        var streamed = FortPickupToss.Of(world).BeginStreamed(pickup, launch, tossVelocity, floorZ);
        var restLocation = streamed ? launch : FortPickupToss.Of(world).SettleActorLocation(launch, tossVelocity, floorZ);

        if (!streamed) {
            pickup.SetActorLocation(restLocation);
            pickup.RestLocation = restLocation;   // TossStartLocation follows it - see AFortPickup
        }
        pickup.SetRole(ENetRole.ROLE_Authority);

        // Set BEFORE SetReplicates, deliberately. SetReplicates is what makes
        // ServerReplicateActors consider this actor, and every getter in
        // NativeRepLayouts.PickupProps reads through PrimaryPickupItemEntry - a replication pass
        // that caught it null would throw from inside the world tick and take the server down.
        // Ordering it this way means the window never exists.
        //
        // A fresh entry rather than the one that was in the inventory: it is a different thing now,
        // owned by nobody, and a partial drop leaves the original behind with the rest of the stack.
        //
        // Deliberately a NEW ItemGuid too. The guid identifies an inventory item instance, and the
        // client has just been told the old one no longer exists; handing the same guid straight
        // back on a world pickup asks it to re-add something it believes it removed. A real server
        // does not carry it across either - Erbium's SpawnPickup copies only ItemDefinition, Count,
        // LoadedAmmo and Level onto the pickup's own freshly made entry.
        pickup.PrimaryPickupItemEntry = new FFortItemEntry {
            ItemDefinition = item.ItemDefinition,
            Count = count,
            Durability = item.Durability,
            Level = item.Level,
            LoadedAmmo = item.LoadedAmmo
        };

        pickup.bTossedFromContainer = tossedFromContainer;

        pickup.SetReplicates(true);

        Console.WriteLine($"NativeRpcHandlers: SpawnDroppedPickup at {pickup.GetActorLocation()} " +
                          $"count={count} guid={item.ItemGuid} - waiting for ServerReplicateActors to open its channel");
    }

    /// <summary>How far in front of the player a container's loot is thrown FROM - all of it from one
    /// point, the way a chest spills.</summary>
    private const float ContainerLaunchDistance = 40.0f;

    /// <summary>
    ///     The upward part of a container toss, in uu/s - and unlike a player's drop this one IS a lob.
    ///
    ///     MEASURED, in the sense that the reference's container tosses are the only launch velocities
    ///     the capture ever shows: Vz = +154, +234, +268, +361 in the PR3.0 logs, every one of them a
    ///     chest spilling its loot. 154 is the gentlest of them, which suits a fan that has to land
    ///     inside two metres. See TossUpSpeed for why a player's own drop gets none of this.
    /// </summary>
    private const float ContainerTossUpSpeed = 154.0f;

    /// <summary>
    ///     How each of a container's items is thrown.
    ///
    ///     Real Fortnite TOSSES them: the chest CDO carries `LootSpawnLocation_Athena = (0, 50, 30)`
    ///     in the container's own local space and `LootTossSpeed_Athena = 600`, and PR3.0's SpawnLoot
    ///     sets `bToss` and `bRandomRotation`, so a UProjectileMovementComponent scatters them over a
    ///     metre or two. That is what this now does too - but around the PLAYER, because THIS SERVER
    ///     DOES NOT KNOW WHERE THE CONTAINER IS. ServerAttemptInteract carries only ReceivingActor,
    ///     InteractComponent, InteractType and OptionalObjectData; no location, and a chest lives in
    ///     a streaming sublevel that is never loaded. The pawn is the one position known for certain,
    ///     and it is by definition within interaction range of the chest.
    ///
    ///     Spawning at the container proper needs its world location, which would mean generating a
    ///     placed-container table the way FortFloorLoot's spawn points were generated
    ///     (`pakreader spawnpoints`) - see [[pak-access]]. Worth doing; not needed for this.
    ///
    ///     THE FAN DISTANCE IS NOW A TARGET, NOT A PLACEMENT. Each item gets the horizontal speed
    ///     that would carry it that far (FortPickupToss.SpeedForDistance, which knows the measured
    ///     gravity and the rest clearance), and then the toss is simulated against real collision -
    ///     so an item aimed at a wall stops at the wall and one aimed downhill runs on. The point of
    ///     the fan is unchanged and is only that N items must not share ONE point, which is what a
    ///     single shared rest location did: a five-item chest looked like it had dropped one thing,
    ///     because five pickups were sitting inside each other.
    /// </summary>
    private static (FVector Launch, FVector Velocity)[] ContainerLootTosses(APawn pawn, int count) {
        var origin = pawn.GetActorLocation();
        var floorZ = origin.Z - PawnCapsuleHalfHeight;
        var baseYaw = pawn.LastClientViewRotation?.Yaw ?? 0.0f;
        var launchZ = origin.Z + DropLaunchHeight;
        var tosses = new (FVector Launch, FVector Velocity)[count];

        for (var i = 0; i < count; i++) {
            // Evenly spaced across the fan so two items can never coincide, plus a little jitter in
            // both angle and distance so a chest does not deal its loot out in a visibly perfect arc.
            var t = count == 1 ? 0.5f : i / (float) (count - 1);
            var yaw = baseYaw
                    + (t * 2.0f - 1.0f) * LootFanHalfAngleDegrees
                    + ((float) StateOf(pawn).LootRng.NextDouble() - 0.5f) * 12.0f;
            var distance = LootFanMinDistance
                         + (float) StateOf(pawn).LootRng.NextDouble() * (LootFanMaxDistance - LootFanMinDistance);
            var radians = yaw * MathF.PI / 180.0f;

            // The item is already ContainerLaunchDistance out when it is thrown, so only the REST of
            // the fan distance has to be covered in the air.
            var speed = FortPickupToss.Of(pawn.GetWorld()!).SpeedForDistance(
                launchZ, floorZ, ContainerTossUpSpeed, MathF.Max(distance - ContainerLaunchDistance, 0f));

            tosses[i] = (
                new FVector {
                    X = origin.X + MathF.Cos(radians) * ContainerLaunchDistance,
                    Y = origin.Y + MathF.Sin(radians) * ContainerLaunchDistance,
                    Z = launchZ
                },
                new FVector {
                    X = MathF.Cos(radians) * speed,
                    Y = MathF.Sin(radians) * speed,
                    Z = ContainerTossUpSpeed
                });
        }

        return tosses;
    }

    /// <summary>
    ///     Rolls a container's loot table and puts it on the ground, fanned out.
    ///
    ///     Shared by BOTH ways a container can arrive at ServerAttemptInteract - resolved to an
    ///     object (a chest this server has already registered) and named only by path. The by-id
    ///     branch used to open the chest and drop NOTHING, which is a difference no player could
    ///     have explained: the same chest gave loot or did not depending on whether it had been hit
    ///     with a pickaxe at some point earlier.
    /// </summary>
    private static void DropContainerLoot(APlayerController pc, string tierGroup, string label) {
        if (pc.Pawn is not { } pawn) return;

        var drops = FortLootTables.Roll(tierGroup, StateOf(pc).LootRng);
        var tosses = ContainerLootTosses(pawn, drops.Count);

        for (var i = 0; i < drops.Count; i++) {
            SpawnDroppedPickup(pc, FortWeaponActorClasses.WorldLootEntry(drops[i].ItemPath, drops[i].Count),
                               drops[i].Count, tosses[i], tossedFromContainer: true);
        }

        Console.WriteLine($"NativeRpcHandlers: ServerAttemptInteract - opened {label} ({tierGroup}), " +
                          $"dropped {drops.Count} item(s): " +
                          $"{string.Join(", ", drops.Select(d => $"{d.Count}x{d.ItemPath[(d.ItemPath.LastIndexOf('.') + 1)..]}"))}");
    }

    /// <summary>Straight-line distance between two points, for the camera plausibility checks.</summary>
    private static float Distance(FVector a, FVector b) {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    ///     ACharacter::ServerMove IN FULL (Character.h:248), tail included.
    ///
    ///     THIS USED TO STOP AT THE TIMESTAMP, and the comment justifying that said the tail carried
    ///     "a UPrimitiveComponent* and an FName, neither of which FRpcReader can read". It could read
    ///     the component all along - an object reference is exactly what ERpcParamKind.Object is -
    ///     and the FName needed nine lines (UPackageMap::StaticSerializeName already had a read side
    ///     for the Name PROPERTY kind).
    ///
    ///     WHAT THAT COST. A client sends the BASED variant whenever it stands on a MOVABLE
    ///     primitive, and the based variants were the ones being truncated - so the server threw away
    ///     ClientLoc for every player standing on a vehicle, and it never learned that the player was
    ///     standing on one at all. From boarding to long after exit, the server's pawn stayed at the
    ///     spot where the player got in.
    ///
    ///     `ClientBaseBoneName` is read and discarded on purpose: nothing here wants a bone name, but
    ///     the two parameters that matter most sit on the far side of it.
    /// </summary>
    private static readonly FRpcParamDef[] ServerMoveParams = {
        new FRpcParamDef("TimeStamp", ERpcParamKind.Float),
        new FRpcParamDef("InAccel", ERpcParamKind.VectorQuantize10),
        new FRpcParamDef("ClientLoc", ERpcParamKind.VectorQuantize100),
        new FRpcParamDef("CompressedMoveFlags", ERpcParamKind.Byte),
        new FRpcParamDef("ClientRoll", ERpcParamKind.Byte),
        new FRpcParamDef("View", ERpcParamKind.UInt32),
        new FRpcParamDef("ClientMovementBase", ERpcParamKind.Object),
        new FRpcParamDef("ClientBaseBoneName", ERpcParamKind.Name),
        new FRpcParamDef("ClientMovementMode", ERpcParamKind.Byte)
    };

    /// <summary>
    ///     ACharacter::ServerMoveNoBase - the bandwidth-saving variant a client sends whenever it is
    ///     NOT standing on a movable primitive, which for a player on the ground is almost always.
    /// </summary>
    private static readonly FRpcParamDef[] ServerMoveNoBaseParams = {
        new FRpcParamDef("TimeStamp", ERpcParamKind.Float),
        new FRpcParamDef("InAccel", ERpcParamKind.VectorQuantize10),
        new FRpcParamDef("ClientLoc", ERpcParamKind.VectorQuantize100),
        new FRpcParamDef("CompressedMoveFlags", ERpcParamKind.Byte),
        new FRpcParamDef("ClientRoll", ERpcParamKind.Byte),
        new FRpcParamDef("View", ERpcParamKind.UInt32),
        new FRpcParamDef("ClientMovementMode", ERpcParamKind.Byte)
    };

    /// <summary>
    ///     ACharacter::ServerMoveOld (Character.h) - an OLD move being resent because the server
    ///     never acknowledged it. Three parameters and no location or movement mode at all, so this
    ///     one genuinely is the whole signature rather than a prefix.
    /// </summary>
    private static readonly FRpcParamDef[] ServerMoveOldParams = {
        new FRpcParamDef("OldTimeStamp", ERpcParamKind.Float),
        new FRpcParamDef("OldAccel", ERpcParamKind.VectorQuantize10),
        new FRpcParamDef("OldMoveFlags", ERpcParamKind.Byte)
    };

    /// <summary>
    ///     ACharacter::ServerMoveDual (Character.h:263) - two moves in one call. The first four
    ///     parameters are the older, already-superseded move; everything from `TimeStamp` on is the
    ///     real one, and has the same shape as <see cref="ServerMoveParams" />.
    /// </summary>
    private static readonly FRpcParamDef[] ServerMoveDualParams = {
        new FRpcParamDef("TimeStamp0", ERpcParamKind.Float),
        new FRpcParamDef("InAccel0", ERpcParamKind.VectorQuantize10),
        new FRpcParamDef("PendingFlags", ERpcParamKind.Byte),
        new FRpcParamDef("View0", ERpcParamKind.UInt32),
        new FRpcParamDef("TimeStamp", ERpcParamKind.Float),
        new FRpcParamDef("InAccel", ERpcParamKind.VectorQuantize10),
        new FRpcParamDef("ClientLoc", ERpcParamKind.VectorQuantize100),
        new FRpcParamDef("NewFlags", ERpcParamKind.Byte),
        new FRpcParamDef("ClientRoll", ERpcParamKind.Byte),
        new FRpcParamDef("View", ERpcParamKind.UInt32),
        new FRpcParamDef("ClientMovementBase", ERpcParamKind.Object),
        new FRpcParamDef("ClientBaseBoneName", ERpcParamKind.Name),
        new FRpcParamDef("ClientMovementMode", ERpcParamKind.Byte)
    };

    /// <summary>
    ///     ACharacter::ServerMoveDualNoBase (Character.h:269) - the same two moves with the base
    ///     parameters implied null, so it stops two parameters short of the dual above.
    /// </summary>
    private static readonly FRpcParamDef[] ServerMoveDualNoBaseParams = {
        new FRpcParamDef("TimeStamp0", ERpcParamKind.Float),
        new FRpcParamDef("InAccel0", ERpcParamKind.VectorQuantize10),
        new FRpcParamDef("PendingFlags", ERpcParamKind.Byte),
        new FRpcParamDef("View0", ERpcParamKind.UInt32),
        new FRpcParamDef("TimeStamp", ERpcParamKind.Float),
        new FRpcParamDef("InAccel", ERpcParamKind.VectorQuantize10),
        new FRpcParamDef("ClientLoc", ERpcParamKind.VectorQuantize100),
        new FRpcParamDef("NewFlags", ERpcParamKind.Byte),
        new FRpcParamDef("ClientRoll", ERpcParamKind.Byte),
        new FRpcParamDef("View", ERpcParamKind.UInt32),
        new FRpcParamDef("ClientMovementMode", ERpcParamKind.Byte)
    };

    /// <summary>
    ///     ACharacter's ServerMoveNoBase (Character.h/CharacterMovementComponent.cpp) - the
    ///     bandwidth-saving ServerMove variant a client calls whenever it isn't standing on a moving
    ///     platform (the common case), so no MovementBase/BaseBoneName params are needed - which
    ///     conveniently means every parameter here is a type this project can already decode (no
    ///     object references or FNames like the full ServerMove/ServerMoveDual family need). This
    ///     project doesn't run real server-side movement simulation/validation - it just trusts
    ///     ClientLoc outright and writes it straight to the pawn's location, the same "authoritative
    ///     client" simplification SerializeNewActor already uses for spawn transforms.
    ///
    ///     NOT YET WIRED IN: this needs a ground-truth FieldNetIndex for "ServerMoveNoBase" from a
    ///     live NativeClassNetCache-style dump of ACharacter/AFortPawn/AFortPlayerPawn/
    ///     AFortPlayerPawnAthena/PlayerPawn_Athena_C (see NativeClassNetCache.cs's PawnOwnFields,
    ///     still just RemoteViewPitch/PlayerState/Controller) - registering the name here alone does
    ///     nothing until that class hierarchy's real field list replaces the current placeholder.
    /// </summary>
    /// <summary>
    ///     The controller's InteractionComp - see UFortControllerComponent_Interaction. Chests, ammo
    ///     boxes and doors ALL arrive here, as a sub-object content block on the controller's channel,
    ///     never on the controller itself.
    /// </summary>
    private static readonly Dictionary<string, FRpcDef> InteractionComponentRpcs = new() {
        // UFortControllerComponent_Interaction::ServerAttemptInteract(AActor* ReceivingActor,
        // UPrimitiveComponent* InteractComponent, uint8 InteractType, UObject* OptionalObjectData) -
        // the interact key. Chests, ammo boxes, doors: all of them arrive here.
        //
        // ReceivingActor is read as ObjectPath, not Object, because the interesting targets are MAP
        // actors: a chest lives in a streaming sublevel this server never loads, so it can never
        // resolve to something this server already has. Same situation destructible scenery is in,
        // and the same answer - UAssetRegistry.GetOrCreateSubObject builds a stably-named stand-in for
        // the path, which is enough to push properties at an actor this server never created. See
        // ABuildingContainer.
        //
        // Only the first two parameters are declared. What follows is an InteractType byte and an
        // object reference this server has nothing to do with; stopping early is free, because
        // UActorChannel.ReadContentBlockFields resynchronises to the field's own declared bit count
        // (the same reason ServerSetReplicatedTargetData stops where it does).
        ["ServerAttemptInteract"] = new FRpcDef(
            "ServerAttemptInteract",
            // All FOUR parameters, per the SDK:
            //   ServerAttemptInteract(AActor* ReceivingActor, UPrimitiveComponent* InteractComponent,
            //                         char InteractType, UObject* OptionalObjectData)
            // The last two are not used yet, but they are decoded so the field's declared bit count
            // actually balances - which is the only check available on a layout that was derived
            // rather than probed, and it is worthless while trailing parameters are missing.
            // InteractType is a plain byte: the objects dump lists no `.UnderlyingType` child for it,
            // unlike a real enum property such as OnGamePhaseChanged.NewPhase.
            new[] {
                new FRpcParamDef("ReceivingActor", ERpcParamKind.ObjectOrPath),
                new FRpcParamDef("InteractComponent", ERpcParamKind.Object),
                new FRpcParamDef("InteractType", ERpcParamKind.Byte),
                new FRpcParamDef("OptionalObjectData", ERpcParamKind.Object)
            },
            (actor, values) => {
                if (actor is not APlayerController pc) return;
                if (pc.GetWorld() is not { NetDriver: { } netDriver } world) return;

                // The SECOND and every later interaction with the same actor arrives as a bare id,
                // because the first one made this server open a channel for it and the client now has
                // a NetGUID to name it with. Those resolve straight back to the stand-in built the
                // first time, so they are handled here, before any path lookup - a door that could be
                // opened but never closed was exactly this: the open exported a path, the close did
                // not, and the handler only understood paths.
                switch (values[0]) {
                    case ABuildingWall knownDoor:
                        if (knownDoor.ToggleDoor(world.TimeSeconds,
                                UAssetRegistry.PathOf(knownDoor) is { } knownDoorPath
                                    ? knownDoorPath[(knownDoorPath.LastIndexOf('.') + 1)..]
                                    : knownDoor.GetFName().ToString(),
                                pc.Pawn?.GetActorLocation() ?? new Core.Math.FVector(),
                                InteractorYaw(pc))
                            is not { } byIdState) return;

                        // THE SAME DIAGNOSTIC THE PATH BRANCH PRINTS. Every interaction after the
                        // first arrives by id, and a player-built door only ever arrives that way -
                        // so leaving the swing numbers off this line meant the one case being
                        // debugged was the one case with no evidence.
                        Console.WriteLine($"NativeRpcHandlers: ServerAttemptInteract - door {knownDoor.GetFName()} " +
                                          $"(by id, InteractType={values[2]}) is now {(byIdState ? "OPEN" : "CLOSED")}, " +
                                          $"swing {knownDoor.DoorDesiredRotOffset.Yaw:F0} ({knownDoor.LastSwingSource}), " +
                                          $"player at {pc.Pawn?.GetActorLocation()}");
                        return;

                    // RIDING A VEHICLE - AN EXPERIMENT THAT HAS RUN, AND ITS ANSWER IS "NOT ENOUGH".
                    // OFF BY DEFAULT NOW (VEHICLE_RIDE=1 to re-run it).
                    //
                    // The question was whether attaching the pawn - the mechanism the battle bus
                    // work already proved - is enough to ride a vehicle without replicating
                    // UFortVehicleSeatComponent::PlayerSlots, the TArray<FAthenaCarPlayerSlot> on a
                    // SUB-OBJECT of the vehicle whose Player/Controller members tell a client it is
                    // aboard. If it were, a whole sub-object channel would not have been needed.
                    //
                    // IT IS NOT. Live, 2026-09-04: the six handles went out (125 payload bits) and
                    // the CLIENT APPLIED THEM - which is the useful part of the answer, because it
                    // rules out "the attachment never arrived". What it did NOT do is enter vehicle
                    // state: no seat, no vehicle input, no camera change. The pawn was simply
                    // reparented to the vehicle at relative (0,0,0), which put its capsule at the
                    // vehicle's origin - at ground level - so it sank through the floor and fell
                    // into the sea.
                    //
                    // So the seat component IS the feature, and it is left on by nothing until it
                    // exists. Kept rather than deleted because re-running it is how the next attempt
                    // will confirm the seat data is what changed the outcome.
                    //
                    // (Offset zero, scale ONE: see AGameModeBase's bus attachment for why a zero
                    // scale collapses the pawn's transform. No seat socket, because socket names
                    // live in that same unreplicated slot struct.)
                    // ENTERING A VEHICLE, as the PR3.0 capture shows a real server doing it - see
                    // [[vehicle-wire-flow]] for the whole sequence and AFortOnlineBeacon's own
                    // earlier attempt for what it is NOT.
                    //
                    // IT IS NOT AN ATTACHMENT. The previous version of this branch set
                    // AttachParent/offsets, and its own comment recorded the result: "the client
                    // applies the attachment but does not enter vehicle state, and the pawn sinks
                    // through the floor. The seat component is what is missing." The seat component
                    // was never what was missing - the real server does not attach the pawn at all.
                    // It sets a MOVEMENT BASE (a relationship the client's movement code
                    // understands) and hands the vehicle's ownership to the driver's connection.
                    //
                    // Still missing here, and why this is behind VEHICLE_RIDE: the two ServerOnly
                    // abilities (GA_AthenaEnterVehicle_C applying GE_AthenaInVehicle_C, then
                    // GA_AthenaInVehicle_C) and the ServerUpdateVehicleInputStateUnreliable /
                    // ClientAcknowledgeVehicleInputState pair that carries the driving itself.
                    case AFortAthenaVehicle vehicle when actor.WorldOptions.Get("VEHICLE_RIDE") is "1":
                        if (pc.Pawn is not { } rider) return;

                        if (rider.MovementBase != null) {
                            // Interacting again while already aboard means GET OUT - the client sends
                            // the same RPC for both, and the server is the only side that knows which
                            // it is.
                            rider.SetMovementBase(null);
                            rider.SetVehicleState(null, 0, 0f);
                            vehicle.SeatPawn(rider, -1, 0f);
                            if (vehicle.Driver == rider) vehicle.Driver = null;
                            vehicle.SetOwner(null);
                            vehicle.FlushNetDormancy();
                            rider.RequestMovementModeCorrection(APawn.PackedMovementModeWalking);
                            rider.BeginVehicleExitReport();

                            Console.WriteLine($"NativeRpcHandlers: ServerAttemptInteract - {rider.GetFName()} left " +
                                              $"{vehicle.GetFName()}");
                            return;
                        }

                        if (vehicle.Driver != null && vehicle.Driver != rider) {
                            Console.WriteLine($"NativeRpcHandlers: ServerAttemptInteract - {vehicle.GetFName()} " +
                                              $"already has a driver ({vehicle.Driver.GetFName()}), ignoring");
                            return;
                        }

                        // OWNERSHIP FIRST. The capture's giveaway is that the vehicle starts
                        // replicating with `bNetOwner: 1` for the driver's connection the moment they
                        // enter - the driver's input RPCs are only accepted from the owner.
                        vehicle.SetOwner(pc);
                        vehicle.Driver = rider;

                        // WAKE IT, or the owner change never goes anywhere: a parked vehicle is
                        // DormantAll from the moment its open bunch is acked, and a dormant actor is
                        // not diffed at all. The client would keep its "no owning connection" view
                        // and drop every RPC the driver sends from the seat.
                        vehicle.FlushNetDormancy();

                        // BOTH HALVES, because they reach different people. The movement base is
                        // COND_SimulatedOnly, so it tells everyone EXCEPT the driver that this pawn
                        // rides the vehicle; VehicleStateRep carries no condition and is what puts
                        // the driver in the seat on their own screen.
                        var entryTime = (float) (rider.GetWorld()?.TimeSeconds ?? 0d);

                        rider.SetMovementBase(vehicle.GetOrCreateMeshComponent());
                        rider.SetVehicleState(vehicle, 0, entryTime);

                        // AND THE SEAT ARRAY, which is the half the client reads when deciding
                        // whether it is allowed to get out again - see UFortVehicleSeatComponent.
                        if (!vehicle.SeatPawn(rider, 0, entryTime)) {
                            Console.WriteLine($"NativeRpcHandlers: ServerAttemptInteract - no seat data for " +
                                              $"{vehicle.GetClass()?.GetFName()}, so nothing will be told it is " +
                                              "occupied. Exiting and seat changes will not work on this vehicle; " +
                                              "re-run Tools/VehicleSeats/gen_vehicle_seats.py for its Blueprint.");
                        }

                        Console.WriteLine($"NativeRpcHandlers: ServerAttemptInteract - {rider.GetFName()} boarded " +
                                          $"{vehicle.GetFName()} ({vehicle.VehicleClassPath}) - seat 0, based on its " +
                                          "mesh. Driving itself still needs the input RPC pair - see vehicle-wire-flow.");
                        return;

                    // A VEHICLE THE GUARD ABOVE TURNED DOWN. Without this arm the RPC falls all the
                    // way through to "named no resolvable actor", which is a lie: it resolved
                    // perfectly and the server chose not to act. "I pressed interact and nothing
                    // happened" has at least four causes - the RPC never arrived, it arrived naming
                    // nothing, it named the vehicle and the switch is off, or it worked and the
                    // client ignored the result - and only the first three are visible from here, so
                    // each of them says which it is.
                    case AFortAthenaVehicle offVehicle:
                        Console.WriteLine($"NativeRpcHandlers: ServerAttemptInteract named vehicle " +
                                          $"{offVehicle.GetFName()} ({offVehicle.VehicleClassPath}) but VEHICLE_RIDE is " +
                                          $"'{actor.WorldOptions.Get("VEHICLE_RIDE") ?? "unset"}' - " +
                                          "set VEHICLE_RIDE=1 to board it.");
                        return;

                    // A LLAMA IS ALWAYS "BY ID", never by path: this server spawns it, so the
                    // client only ever knows it as a NetGUID. Above the container arm because the
                    // two are unrelated classes that happen to share a base - a llama is not an
                    // ABuildingContainer and has no bAlreadySearched.
                    case AFortAthenaSupplyDropLlama llama:
                        if (!llama.Search()) return;

                        DropContainerLoot(pc, FortLootTables.LlamaGroup, $"llama {llama.GetFName()}");
                        return;

                    case ABuildingContainer knownContainer:
                        if (!knownContainer.Search()) return;

                        // Loot too, not just the open. This branch is reachable whenever the chest
                        // was registered earlier (a pickaxe hit still does that) and it dropped
                        // nothing at all until now - see DropContainerLoot.
                        var knownPath = UAssetRegistry.PathOf(knownContainer);
                        if (knownPath == null || ContainerTierGroupFor(knownPath) is not { } knownTier) {
                            Console.WriteLine("NativeRpcHandlers: ServerAttemptInteract - opened " +
                                              $"{knownContainer.GetFName()} (by id) but its loot tier group " +
                                              "is unknown, so it drops nothing");
                            return;
                        }

                        DropContainerLoot(pc, knownTier, $"{knownContainer.GetFName()} (by id)");
                        return;
                }

                var path = values[0] as string ?? string.Empty;
                if (path.Length == 0) {
                    // SAY WHAT ARRIVED, because "no resolvable actor" covers two very different
                    // failures and they need opposite fixes: a NetGUID that resolved to an object of
                    // a type no branch above handles (the C# class is wrong for what it is - this is
                    // what a player-built door looked like while it was still a plain ABuildingActor),
                    // versus a reference that resolved to NOTHING at all (the client named an actor
                    // this server has no channel for).
                    Console.WriteLine("NativeRpcHandlers: ServerAttemptInteract named no actor this handler " +
                                      $"understands - the reference resolved to {DescribeInteractTarget(values[0])}, " +
                                      "ignoring");
                    return;
                }

                // A DOOR. Toggled, not opened once, and it needs nothing but the flag coming back -
                // the client has already predicted the swing and is waiting to be told it was right
                // (see ABuildingWall). Checked before containers because a door is far more common.
                if (LooksLikeDoor(path)) {
                    ABuildingWall door;
                    try {
                        door = netDriver.World!.MapActors.GetOrCreate<ABuildingWall>(path);
                    } catch (Exception ex) {
                        Console.WriteLine($"NativeRpcHandlers: ServerAttemptInteract could not turn door '{path}' into a level actor - {ex.Message}");
                        return;
                    }

                    door.MarkAsLevelActor();
                    if (!netDriver.NetworkObjectList.Contains(door)) {
                        door.SetReplicates(true);
                        netDriver.AddNetworkActor(door);
                    }

                    var interactorLocation = pc.Pawn?.GetActorLocation() ?? new Core.Math.FVector();
                    var doorName = path[(path.LastIndexOf('.') + 1)..];

                    if (door.ToggleDoor(world.TimeSeconds, doorName, interactorLocation, InteractorYaw(pc))
                        is not { } state) return;

                    Console.WriteLine($"NativeRpcHandlers: ServerAttemptInteract - door '{path}' " +
                                      $"(InteractType={values[2]}) is now {(state ? "OPEN" : "CLOSED")}, " +
                                      $"swing {door.DoorDesiredRotOffset.Yaw:F0} ({door.LastSwingSource}), " +
                                      $"player at {interactorLocation}");
                    return;
                }

                // Which kind of container this is comes from the actor's NAME, which is all the client
                // gives us - the same shape of lookup FortHarvestResources does for scenery, and with
                // the same caveat: an unrecognised name is left alone rather than guessed at.
                // NOT gated on verbose any more. This return being silent is what made a decoded,
                // dispatched, perfectly working RPC look like a dead one for two rounds of debugging:
                // every other path out of this handler logs, so silence read as "never got here".
                var tierGroup = ContainerTierGroupFor(path);
                if (tierGroup == null) {
                    Console.WriteLine($"NativeRpcHandlers: ServerAttemptInteract on '{path}' - " +
                                      "neither a known door nor a container this server knows, ignoring");
                    return;
                }

                ABuildingContainer container;
                try {
                    container = netDriver.World!.MapActors.GetOrCreate<ABuildingContainer>(path);
                } catch (Exception ex) {
                    Console.WriteLine($"NativeRpcHandlers: ServerAttemptInteract could not turn '{path}' into a level actor - {ex.Message}");
                    return;
                }

                container.MarkAsLevelActor();

                if (!netDriver.NetworkObjectList.Contains(container)) {
                    container.SetReplicates(true);
                    netDriver.AddNetworkActor(container);
                }

                // Already open: say nothing and drop nothing. The guard lives on the container so a
                // client holding the interact key cannot empty one chest repeatedly.
                if (!container.Search()) return;

                DropContainerLoot(pc, tierGroup, $"'{path}'");
            }
        ),

    };

    private static readonly Dictionary<string, FRpcDef> PawnRpcs = new() {
        // AFortPlayerPawn::ServerHandlePickup(AFortPickup*, float InFlyTime, FVector InStartDirection,
        // bool bPlayPickupSound) - declared on the PAWN, not the PlayerController, so it arrives as
        // an ordinary content-block field on the pawn's channel.
        //
        // This is the ADD side of fast array replication, the counterpart to the delete that
        // ServerAttemptInventoryDrop exercises: the item goes back into the inventory, MarkItemDirty
        // moves the array key, and the next tick sends a delta with one changed element.
        //
        // InFlyTime/InStartDirection describe the arc the client wants the item to travel while it
        // flies into the player - purely cosmetic, and nothing here simulates it.
        ["ServerHandlePickup"] = new FRpcDef(
            "ServerHandlePickup",
            new[] {
                new FRpcParamDef("Pickup", ERpcParamKind.Object),
                new FRpcParamDef("InFlyTime", ERpcParamKind.Float),
                new FRpcParamDef("InStartDirection", ERpcParamKind.Vector),
                new FRpcParamDef("bPlayPickupSound", ERpcParamKind.Bool)
            },
            (actor, values) => {
                if (actor is not APawn pawn) return;

                if (values[0] is not AFortPickup pickup) {
                    Console.WriteLine("NativeRpcHandlers: ServerHandlePickup named an object that is not a pickup, ignoring");
                    return;
                }

                if (pickup.bPickedUp) return; // already claimed - a second client racing for it

                if (pawn.Controller is not APlayerController pc || pc.WorldInventory is not { } inventory) {
                    Console.WriteLine("NativeRpcHandlers: ServerHandlePickup - pawn has no controller with an inventory, ignoring");
                    return;
                }

                var entry = pickup.PrimaryPickupItemEntry;
                if (entry?.ItemDefinition is not { } incoming) return;

                // NOTHING FITS AND NOTHING CAN BE TRADED FOR IT - only then does the pickup not
                // happen. A full bar is not a refusal in Fortnite: walking over a gun with five
                // slots used SWAPS it for whatever is in your hands, and the plain ServerHandlePickup
                // above is what the client sends for that case (it re-sent it 40 times in one live
                // session while the player stood on an item this server kept declining).
                // FortItemStacks.SwapCandidate has which item leaves and why.
                //
                // ASKED AS A DRY RUN because the grant itself now waits for the animation, and this
                // question cannot: there is no way to un-fly an item, so a refusal has to be decided
                // while the item is still on the ground. Same walk, same data, no mutation. Returning
                // here leaves the world actor exactly as it was, with no animation and no
                // destroy-and-respawn flicker.
                if (FortItemStacks.Give(inventory, entry, entry.Count, dryRun: true) >= entry.Count
                    && FortItemStacks.SwapCandidate(inventory, pawn, incoming) == null) {
                    Console.WriteLine($"NativeRpcHandlers: ServerHandlePickup - {pc.GetFName()} has no room for " +
                                      $"{entry.ItemDefinition?.GetFName()} and nothing it can trade for it, " +
                                      "leaving it on the ground");
                    return;
                }

                // Handle 49 - what drives the client's pickup feedback. It is NOT what removes the
                // world actor: OnRep_bPickedUp only hides it. The actor goes away when its channel
                // closes, which Destroy() below arranges via ServerReplicateActors.
                // THE FLUSH HERE IS A NO-OP TODAY AND IS KEPT ANYWAY - measured, not assumed. The
                // wake pass runs at the top of the next replication tick, and Destroy() happens on
                // this line, so a dormant pickup is still dormant when the destroy arrives and its
                // removal goes out as a destruction info instead (144 dormancy closes and 10
                // destruction infos in a live session, with zero wakes - the wake never gets a turn).
                //
                // That means bPickedUp does NOT reach the client for a dormant pickup. Harmless,
                // because OnRep_bPickedUp only HIDES the actor and the destruction info removes it
                // outright, which is why taking an item looks right. The flush stays because the
                // ordering is a coincidence of doing both in one RPC: anything that ever sets a
                // pickup property WITHOUT destroying it in the same breath needs exactly this call,
                // and finding that out the hard way would cost another silent-failure hunt.
                // THE FLIGHT, instead of destroying it where it stands. The client asked for one -
                // InStartDirection is its own parameter, decoded above and until now discarded - and
                // an actor removed on this line has nothing left to animate. See
                // FortPickupFlightSystem, which sets the flight properties, wakes the actor so they
                // actually go out, runs the grant below when the arc lands, and destroys it.
                //
                // InFlyTime is deliberately NOT passed on. It was, and the pace the client asks for
                // is Save The World's; FortPickupFlightSystem.FlightSeconds has the 0.40s that
                // replaced it and where that number comes from. Logged so the client's own request
                // stays visible rather than silently dropped.
                var requestedFlyTime = values[1] as float? ?? 0f;

                FortPickupFlightSystem.Begin(
                    pickup, pawn,
                    values[2] as FVector ?? new FVector(),
                    values[3] as bool? ?? false,
                    (float) (pawn.GetWorld()?.TimeSeconds ?? 0d),
                    () => {
                        // THE SWAP, decided here rather than carried from above. 0.40s is long
                        // enough to drop something, so the question "is there still no room?" is
                        // asked again at the moment the item arrives; the alternative is trading
                        // away a weapon to fill a slot that has since emptied.
                        var swappedOut = FortItemStacks.Give(inventory, entry, entry.Count, dryRun: true) >= entry.Count
                            ? FortItemStacks.SwapCandidate(inventory, pawn, incoming)
                            : null;

                        if (swappedOut != null) {
                            // In that order: OUT of the inventory, out of the player's HANDS, then
                            // back into the world. The weapon actor is keyed to its inventory row by
                            // ItemEntryGuid and that row is about to be gone, which is the same
                            // reason ServerAttemptInventoryDrop unequips - a weapon left in the
                            // hands of a pawn whose inventory no longer lists it is a gun that
                            // cannot be dropped, fired or replaced.
                            inventory.Inventory.Remove(swappedOut);
                            if (pawn.CurrentWeapon?.ItemEntryGuid == swappedOut.ItemGuid) pawn.UnequipCurrentWeapon();
                            SpawnDroppedPickup(pc, swappedOut, swappedOut.Count);

                            Console.WriteLine($"NativeRpcHandlers: ServerHandlePickup swapped out " +
                                              $"{swappedOut.ItemDefinition?.GetFName()} x{swappedOut.Count} " +
                                              $"for {incoming.GetFName()}");
                        }

                        // STACKED, not appended. This used to add a row unconditionally, so two
                        // boxes of light ammo became two slots of 30 instead of one of 60 - see
                        // FortItemStacks, which also builds the fresh entry this needs (the pickup's
                        // own copy carries the ReplicationId/Key it was given as a pickup, and
                        // reusing it would drag that state into a completely different fast array).
                        //
                        // RUN AGAIN FOR REAL rather than trusting the dry run above, for the same
                        // reason: the honest answer is whatever fits at the moment the item arrives.
                        var known = inventory.Inventory.Items.Select(item => item.ItemGuid).ToHashSet();
                        var leftOver = FortItemStacks.Give(inventory, entry, entry.Count);

                        // A SWAP PUTS THE NEW ITEM IN YOUR HANDS. The player was holding the item
                        // that just left, so without this they are left holding nothing at all - the
                        // client has no reason to send ServerExecuteInventoryItem, since from its
                        // point of view it never changed slot. PR3.0 does the same thing by calling
                        // ClientEquipItem, and gates it the same way: only when the swapped-out item
                        // was the one actually equipped.
                        if (swappedOut != null && pawn.CurrentWeapon == null
                            && inventory.Inventory.Items.FirstOrDefault(item => !known.Contains(item.ItemGuid)) is { } equipped) {
                            pawn.EquipInventoryItem(equipped);
                        }

                        // A PARTIAL take puts the rest back on the ground rather than vanishing -
                        // the same bargain harvesting already makes, and it keeps the items in the
                        // world. This now covers the case the dry run cannot: room that was there
                        // when the item set off and is gone by the time it lands.
                        if (leftOver > 0) {
                            SpawnDroppedPickup(pc, new FFortItemEntry { ItemDefinition = entry.ItemDefinition }, leftOver);
                            Console.WriteLine($"NativeRpcHandlers: ServerHandlePickup - {leftOver} x " +
                                              $"{entry.ItemDefinition?.GetFName()} did not fit in the stack and went " +
                                              "back on the ground");
                        }

                        Console.WriteLine($"NativeRpcHandlers: ServerHandlePickup landed guid={entry.ItemGuid} " +
                                          $"count={entry.Count} -> inventory now {inventory.Inventory.Count} item(s), " +
                                          $"ArrayReplicationKey={inventory.Inventory.ArrayReplicationKey}");
                    });

                Console.WriteLine($"NativeRpcHandlers: ServerHandlePickup {entry.ItemDefinition?.GetFName()} x{entry.Count} " +
                                  $"flying to {pc.GetFName()} for {pickup.FlyTime:0.00}s " +
                                  $"(client asked for {requestedFlyTime:0.00}s)");
            }
        ),

        // ACharacter's move RPCs. All six share one handler, because they are the same message at
        // three levels of detail: the NoBase variants imply a null base, the Dual variants carry an
        // older move ahead of the real one, and ServerMoveOld is a resend with no position at all.
        // The handler reads parameters BY NAME, so a variant that lacks one simply has nothing to
        // apply rather than needing its own code path.
        ["ServerMove"] = Move("ServerMove", ServerMoveParams),
        ["ServerMoveNoBase"] = Move("ServerMoveNoBase", ServerMoveNoBaseParams),
        ["ServerMoveOld"] = Move("ServerMoveOld", ServerMoveOldParams),
        ["ServerMoveDual"] = Move("ServerMoveDual", ServerMoveDualParams),
        ["ServerMoveDualNoBase"] = Move("ServerMoveDualNoBase", ServerMoveDualNoBaseParams),

        // The HybridRootMotion variant has the same signature as ServerMoveDual (Character.h:275) -
        // it differs only in what the server does with it, which is root-motion bookkeeping this
        // project does not have.
        ["ServerMoveDualHybridRootMotion"] = Move("ServerMoveDualHybridRootMotion", ServerMoveDualParams),

        // AFortPlayerPawn::ServerPlayUnableToPerformActionMontage() - the "you can't do that" animation
        // (no room to build, nothing to interact with). It exists so OTHER clients see the gesture;
        // the player who sent it already played it locally. Nothing to broadcast to yet, so this is
        // named rather than acted on.
        ["ServerPlayUnableToPerformActionMontage"] = NoParams("ServerPlayUnableToPerformActionMontage")
    };

    /// <summary>
    ///     RPCs the client sends on a WEAPON's own channel. Worth stating explicitly because it is not
    ///     obvious and it is easy to put them in the wrong table: the capture logs show both of these
    ///     arriving with `Actor=B_Athena_Pickaxe_Generic_C` / `Actor=B_Assault_Auto_Athena_C`, not on
    ///     the pawn or the controller, so a handler registered anywhere else is simply never reached.
    /// </summary>
    private static readonly Dictionary<string, FRpcDef> WeaponRpcs = new() {
        // AFortDecoTool::ServerSpawnDeco(FVector Location, FRotator Rotation, ABuildingSMActor*
        // AttachedActor, EBuildingAttachmentType) - placing a trap on a piece that already stands.
        // Types from the Dumper-7 parameter struct; the enum is a TEnumAsByte, so 4 bits
        // (CeilLogTwo(ATTACH_MAX = 9)), and like every non-bool parameter each is behind a presence
        // bit - an absent one is its zero value, which for the surface is ATTACH_Floor.
        ["ServerSpawnDeco"] = new FRpcDef(
            "ServerSpawnDeco",
            new[] {
                new FRpcParamDef("Location", ERpcParamKind.Vector),
                new FRpcParamDef("Rotation", ERpcParamKind.Rotator),
                new FRpcParamDef("AttachedActor", ERpcParamKind.Object),
                new FRpcParamDef("InBuildingAttachmentType", ERpcParamKind.Enum, 4)
            },
            (actor, values) => {
                if (actor is not AFortDecoTool tool) return;
                FortTrapSystem.SpawnDeco(tool,
                    values[0] as FVector ?? new FVector(), values[1] as FRotator ?? new FRotator(), values[2] as UObject,
                    (EBuildingAttachmentType) (values[3] as byte? ?? 0));
            }
        ),

        // AFortDecoTool::ServerCreateBuildingAndSpawnDeco(FVector_NetQuantize10 BuildingLocation,
        // FRotator BuildingRotation, FVector_NetQuantize10 Location, FRotator Rotation,
        // EBuildingAttachmentType) - placing a trap on bare ground, which builds the piece first.
        ["ServerCreateBuildingAndSpawnDeco"] = new FRpcDef(
            "ServerCreateBuildingAndSpawnDeco",
            new[] {
                new FRpcParamDef("BuildingLocation", ERpcParamKind.VectorQuantize10),
                new FRpcParamDef("BuildingRotation", ERpcParamKind.Rotator),
                new FRpcParamDef("Location", ERpcParamKind.VectorQuantize10),
                new FRpcParamDef("Rotation", ERpcParamKind.Rotator),
                new FRpcParamDef("InBuildingAttachmentType", ERpcParamKind.Enum, 4)
            },
            (actor, values) => {
                if (actor is not AFortDecoTool tool) return;
                FortTrapSystem.CreateBuildingAndSpawnDeco(tool,
                    values[0] as FVector ?? new FVector(), values[1] as FRotator ?? new FRotator(),
                    values[2] as FVector ?? new FVector(), values[3] as FRotator ?? new FRotator(),
                    (EBuildingAttachmentType) (values[4] as byte? ?? 0));
            }
        ),

        // AFortWeapon::ServerReleaseWeaponAbility(FGameplayAbilitySpecHandle SpecHandle) - the trigger
        // coming back UP, and by a wide margin the most frequent thing the client sends that this
        // server used to skip entirely (2753 times across the capture logs, pickaxe and rifle alike).
        //
        // Decoded and deliberately not acted on. Real Fortnite uses it to end the weapon's fire
        // ability instance; there are no ability instances here, and the two things a release could
        // plausibly drive on this server - ammo and damage - are both already driven by the shot
        // reports (ServerSetReplicatedTargetData / ServerAbilityRPCBatch, see OnShotReported), which
        // arrive per bullet whether or not the trigger is ever released. Acting on it as well would
        // double-count.
        ["ServerReleaseWeaponAbility"] = new FRpcDef(
            "ServerReleaseWeaponAbility",
            new[] { new FRpcParamDef("SpecHandle", ERpcParamKind.Int32) },
            (actor, values) => {
                if (NetDebugLog.VerboseEnabled) {
                    Console.WriteLine($"NativeRpcHandlers: ServerReleaseWeaponAbility on {actor.GetFName()} " +
                                      $"SpecHandle={values[0]}");
                }
            }
        ),

        // AFortWeapon::ServerSetMuzzleTraceNearWall(bool bIsNearWall) - the muzzle is against
        // geometry, which is what lowers the weapon in the third-person pose OTHER players see. The
        // sender already lowers it locally and there is nobody else here, so this is decoded and
        // dropped; it would need a replicated counterpart on the weapon to be worth acting on.
        ["ServerSetMuzzleTraceNearWall"] = new FRpcDef(
            "ServerSetMuzzleTraceNearWall",
            new[] { new FRpcParamDef("bIsNearWall", ERpcParamKind.Bool) },
            (actor, values) => {
                if (NetDebugLog.VerboseEnabled) {
                    Console.WriteLine($"NativeRpcHandlers: ServerSetMuzzleTraceNearWall on {actor.GetFName()} " +
                                      $"bIsNearWall={values[0] as bool? ?? false}");
                }
            }
        )
    };

    /// <summary>
    ///     Forces every RPC table to build, at startup, so a mis-ordered declaration fails HERE
    ///     rather than on the first bunch of a live match.
    ///
    ///     WHY THIS IS A REAL HAZARD AND NOT A STYLE POINT. C# runs static field initializers in
    ///     TEXTUAL order, so a shared FRpcParamDef[] declared BELOW a dictionary that references it
    ///     is simply null when that dictionary is built. FRpcDef's constructor catches it - and its
    ///     message already says "most likely declared after the dictionary that references it",
    ///     because this has happened before - but a type initializer only runs on FIRST USE, and the
    ///     first use is a client bunch arriving mid-match. The whole class then throws forever:
    ///
    ///         ReadContentBlockFields threw ... TypeInitializationException ... FRpcDef
    ///         'ServerMoveNoBase' was given a null parameter list
    ///
    ///     Every RPC in the game stops working, from one declaration being in the wrong place. This
    ///     turns that into a startup failure with the same message and no match in progress. It is
    ///     called next to VerifyLifetimeConditions for the same reason that one exists.
    /// </summary>
    public static void VerifyRpcTables() {
        var tables = typeof(NativeRpcHandlers)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(Dictionary<string, FRpcDef>))
            .ToArray();

        var total = 0;
        var missingParams = new List<string>();

        foreach (var table in tables) {
            var rpcs = (Dictionary<string, FRpcDef>) table.GetValue(null)!;
            total += rpcs.Count;

            // The constructor already refuses a null list, so reaching here means every entry built.
            // What is still worth saying is when a table came out EMPTY, which is the other shape
            // the same ordering mistake takes.
            if (rpcs.Count == 0) missingParams.Add(table.Name);
        }

        Console.WriteLine(missingParams.Count == 0
            ? $"NativeRpcHandlers: all {tables.Length} RPC tables built, {total} handlers."
            : $"NativeRpcHandlers: {string.Join(", ", missingParams)} built EMPTY - check declaration order.");
    }

    /// <summary>
    ///     Every ACharacter move RPC, handled once.
    ///
    ///     PARAMETERS ARE LOOKED UP BY NAME rather than by index, which is what lets six signatures
    ///     of three different lengths share this. A variant that does not carry ClientLoc (the
    ///     "old move" resend) simply has none to apply.
    ///
    ///     THE ONE RULE THAT IS NOT OBVIOUS: `ClientLoc` IS RELATIVE WHEN THE MOVE IS BASED.
    ///     CallServerMove (CharacterMovementComponent.cpp:8120) picks
    ///     `UseRelativeLocation(ClientMovementBase) ? SavedRelativeLocation : SavedLocation`, so the
    ///     vector in a based move is an offset inside the base's own space - a couple of hundred
    ///     units, near zero. Writing that into SetActorLocation would teleport the player to the
    ///     world origin, which is why a based move's position is READ AND REPORTED here but not
    ///     applied: this server does not track the base component's world transform, so it cannot
    ///     turn the offset back into a world position yet. Doing that properly needs the vehicle's
    ///     own replicated transform, and is its own change.
    /// </summary>
    private static FRpcDef Move(string name, FRpcParamDef[] paramDefs) => new(
        name,
        paramDefs,
        (actor, values) => {
            if (actor is not APawn pawn) return;

            object? Param(string param) {
                for (var i = 0; i < paramDefs.Length; i++) {
                    if (paramDefs[i].Name == param) return values[i];
                }

                return null;
            }

            // Every timestamp in the call - the Dual variants carry two, and MarkGoodMove takes the
            // max, so acking both is right and free.
            foreach (var value in values) if (value is float timeStamp) pawn.MarkGoodMove(timeStamp);

            var movementMode = Param("ClientMovementMode") as byte?;
            var movementBase = Param("ClientMovementBase") as UObject;
            // THE VARIANT DECIDES THIS, NOT THE RESOLVED OBJECT. CallServerMove picks the based
            // variant on `UseRelativeLocation(ClientMovementBase)`, which is
            // `IsDynamicBase(base)` = `base && base->Mobility == Movable` (Character.h:126) - null
            // safe, so a based variant on the wire PROVES the client had a real movable base and
            // therefore sent a RELATIVE location.
            //
            // Testing `movementBase != null` instead would have been wrong exactly when it matters:
            // FRpcReader resolves an object this server never gave a NetGUID to as null, and the
            // live log shows precisely that (`ServerMoveDual ... base=none`). The position would then
            // have been treated as absolute and the pawn teleported to a few hundred units from the
            // world origin.
            var isBased = paramDefs.Any(def => def.Name == "ClientMovementBase");

            pawn.ReportMoveAfterExit(name, movementMode, movementBase?.GetFName().ToString());

            // The flags parameter is called CompressedMoveFlags on the single moves and NewFlags on
            // the duals - the same byte either way.
            if (movementMode is { } mode) {
                pawn.TrackMoveFlags((Param("CompressedMoveFlags") ?? Param("NewFlags")) as byte? ?? 0, mode);
            }

            if (Param("View") is uint packedView) {
                var view = FRotator.FromPackedView(packedView);
                pawn.LastClientViewRotation = view;

                // And onto the ACTOR, so ReplicatedMovement carries it and other clients see this
                // player facing the way they are actually facing. Yaw only: a character's capsule
                // never pitches or rolls, and the view's pitch belongs to the control rotation,
                // which is a separate thing the owning client keeps for itself.
                pawn.SetActorRotation(new FRotator { Yaw = view.Yaw });

                // THE PITCH GOES SOMEWHERE TOO, and it is not the actor's rotation - it is
                // APawn::RemoteViewPitch (handle 16), the one property that tells everyone else which
                // way a player is looking vertically. Yaw alone is why heads stayed level for
                // onlookers while turning left and right looked perfectly correct.
                pawn.SetRemoteViewPitch(view.Pitch);
            }

            if (Param("ClientLoc") is not FVector clientLoc) return;

            if (isBased) {
                // KEPT, even though it is not a world position: it is the only thing a movement
                // correction can express a position with, because the client can turn it back into
                // one and this server cannot. See UActorChannel.SendMovementCorrection.
                pawn.LastClientRelativeLocation = clientLoc;

                // See the method comment: this is an offset in the base's space, not a world
                // position, and applying it would be far worse than leaving the position stale.
                if (NetDebugLog.VerboseEnabled) {
                    Console.WriteLine($"NativeRpcHandlers: {name} on {actor.GetFName()} is BASED on " +
                                      $"{movementBase?.GetFName().ToString() ?? "a component this server has no id for"} " +
                                      $"- ClientLoc={clientLoc} is relative to it, so this server's idea of where " +
                                      "the pawn is stays where it was.");
                }

                return;
            }

            actor.SetActorLocation(clientLoc);
            pawn.LastUnbasedMoveTime = actor.GetWorld()?.TimeSeconds ?? 0f;
            if (Param("TimeStamp") is float moveTimeStamp) pawn.RecordMoveSample(clientLoc, moveTimeStamp);

            // The ground the client is standing on, when it says it is standing on something.
            // See TerrainGroundTruth: this is the only source of true ground heights this server
            // has, and it costs one dictionary probe per move. Only unbased moves, and that is a
            // correctness point rather than an optimisation - a player standing on a vehicle is
            // standing on a vehicle, and recording its deck as terrain would poison the bake this
            // data exists to check.
            if (actor.GetWorld() is { } moveWorld) TerrainGroundTruth.Record(moveWorld, clientLoc, movementMode ?? 0);

            if (actor.GetWorld()?.NetDriver is { } driver) pawn.TrackMovementSpeed(clientLoc, driver.GetElapsedTime());

            // Fall damage. A falling character is by definition not based on anything, so this is
            // where a fall is visible.
            if (movementMode is { } fallMode) pawn.TrackFallDamage(clientLoc, fallMode);

            if (NetDebugLog.VerboseEnabled) {
                Console.WriteLine($"NativeRpcHandlers: {name} on {actor.GetFName()} ClientLoc={clientLoc} " +
                                  $"movementMode={movementMode} PendingAck={pawn.PendingAckGoodMoveTimeStamp}");
            }
        }
    );

    /// <summary>
    ///     AFortBroadcastRemoteClientInfo's server RPCs - see its own doc comment for why this actor
    ///     exists at all. Only ServerSetPlayerBuildableClass is handled; the other eight real Net
    ///     functions on this class (ServerSetPlayerBuildingMaterial, ServerSetPlayerInteracting, ...)
    ///     aren't implemented - UActorChannel already skips any field with no matching entry here by
    ///     its own declared NumPayloadBits, same as everywhere else in this file.
    /// </summary>
    private static readonly Dictionary<string, FRpcDef> BroadcastRemoteClientInfoRpcs = new() {
        // AFortBroadcastRemoteClientInfo::ServerSetPlayerBuildableClass(TSubclassOf<ABuildingSMActor>
        // BuildableClass). Sent the instant a player equips a building tool, immediately after the
        // (purely client-local) ghost preview mesh updates and right before the
        // "Fort_Build_BluePrint_Select_Cue" sound - see AFortBroadcastRemoteClientInfo's doc comment
        // for the full working-client log sequence this was found from. Originally modeled as a
        // pure broadcast/UI setter for OTHER clients, independent of this player's own placement -
        // revised 2026-08-29: it turns out to be the ONLY signal this project has for "the player
        // switched pieces while already in build mode" (cycling Wall/Floor/Stair/Roof mid-build
        // does NOT re-trigger ServerExecuteInventoryItem/EquipInventoryItem at all - CurrentWeapon
        // stays on whatever piece was equipped when build mode was FIRST entered), so
        // ServerCreateBuildingActor now reads the class THIS carries in preference to
        // CurrentWeapon.WeaponData. Read as ObjectPath rather than Object: this server never
        // assigned the class itself a NetGUID (it's a class the CLIENT picked, exactly like
        // FCreateBuildingActorData's own BuildingClassData.BuildingClass would be), so it always
        // arrives as an unresolvable path export - the path is the only usable part.
        ["ServerSetPlayerBuildableClass"] = new FRpcDef(
            "ServerSetPlayerBuildableClass",
            new[] {
                new FRpcParamDef("BuildableClass", ERpcParamKind.ObjectPath)
            },
            (actor, values) => {
                if (actor is not AFortBroadcastRemoteClientInfo info) return;

                var path = values[0] as string;
                Console.WriteLine($"NativeRpcHandlers: ServerSetPlayerBuildableClass on {info.GetFName()} -> {path ?? "(no path)"}");

                if (path != null && info.Owner is APlayerController { Pawn: { } pawn }) {
                    pawn.SelectedBuildingActorClassPath = path;
                }
            }
        )
    };

    /// <summary>
    ///     UAbilitySystemComponent's server RPCs. These arrive on the OWNER'S actor channel inside a
    ///     sub-object content block, not on a channel of their own - a component never gets one.
    ///
    ///     Field indices come from the component's own ClassNetCache (GetMaxIndex 53, so a 6-bit
    ///     index): ServerTryActivateAbility is 47, ServerSetReplicatedTargetData 45. Both were read
    ///     straight off a real Project-Reboot-3.0 capture before any of this was written.
    /// </summary>
    private static readonly Dictionary<string, FRpcDef> AbilitySystemComponentRpcs = new() {
        // 54 bits, fixed. Derived from FRepLayout::SendPropertiesForRPC (every non-bool parameter
        // carries a leading "send" bit; bools do not) plus FPredictionKey::NetSerialize:
        //   1 send + 32 handle + 1 InputPressed + 1 send + 3 PK flags + 16 PK Current = 54.
        ["ServerTryActivateAbility"] = new FRpcDef(
            "ServerTryActivateAbility",
            new[] {
                // FGameplayAbilitySpecHandle wraps one int32 and is NOT a NetSerialize struct, so it
                // is read as that bare member - the 54-bit total only closes this way.
                new FRpcParamDef("AbilityToActivate", ERpcParamKind.Int32),
                new FRpcParamDef("InputPressed", ERpcParamKind.Bool),
                new FRpcParamDef("PredictionKey", ERpcParamKind.PredictionKey)
            },
            (actor, values) => {
                Console.WriteLine($"NativeRpcHandlers: ServerTryActivateAbility on {actor.GetFName()} " +
                                  $"Handle={values[0]} InputPressed={values[1]} PredictionKey=[{values[2]}]");

                if (actor is not APlayerState { AbilitySystemComponent: { } abilitySystem } playerState) return;
                if (values[0] is not int handle) return;

                var spec = abilitySystem.ActivatableAbilities.Items.FirstOrDefault(item => item.Handle == handle);
                if (spec == null) {
                    Console.WriteLine($"NativeRpcHandlers: ServerTryActivateAbility for unknown spec handle {handle}, ignoring");
                    return;
                }

                // A HEALING CONSUMABLE IS JUST A WEAPON WHOSE FIRE ABILITY HEALS YOU, so it arrives
                // here like any other shot - same grant at equip, same spec handle, same RPC. This
                // is the one place that has to know the difference. See FortConsumableSystem.
                //
                // The Sneaky Snowman's SECOND button is not a heal - it is a disguise the server has
                // to put on the player itself. See FortSnowmanDisguise.
                if (!FortSnowmanDisguise.TryBegin(playerState, spec))
                    FortConsumableSystem.TryUse(playerState, spec);

                // A SHOT SPENDS A ROUND. Firing never did on this server - reloading worked, the
                // magazine simply never went down - so a player could empty a 30-round clip
                // indefinitely and the HUD counter never moved.
                //
                // This is the right hook because it is the SHOT, not the hit: it fires once per
                // trigger pull, whereas ReportTargetData arrives once per pellet, and it still fires
                // when the shot misses. AmmoCostPerFire comes from the same stat row as the damage.
                SpendAmmoForShot(playerState, spec);

                // Accept it. Real UE runs the ability's own CanActivate/cost/cooldown checks here and
                // may answer ClientActivateAbilityFailed instead; this server has no ability
                // instances to run, so the honest thing it CAN do is confirm the prediction the
                // client already played - which is what unblocks the client from asking again.
                var predictionKey = values[2] as FPredictionKey ?? new FPredictionKey();

                // Kept for ClientEndAbility, which is only obeyed when the key matches the one the
                // activation used - see FGameplayAbilitySpec.ActivationPredictionKey.
                spec.ActivationPredictionKey = predictionKey;

                playerState.GetWorld()?.NetDriver?.SendClientActivateAbilitySucceed(
                    playerState, abilitySystem, handle, predictionKey);
            }
        ),

        // The two ways a client tells the server an ability it was running has finished.
        // UAbilitySystemComponent::ReplicateEndOrCancelAbility picks between them for any
        // LocalPredicted or ServerInitiated ability, and takes the CLIENT branch whenever the
        // ability's owner is not the authority - so an emote, which the SERVER starts, is still
        // ended by the client. A live Project-Reboot-3.0 capture shows exactly that: a
        // ServerCancelAbility [12.1 bytes] immediately before the client logs
        // "GAB_Emote_Generic_C EndAbility".
        //
        // Declared to the FIRST PARAMETER AND NO FURTHER, the same way ServerSetReplicatedTargetData
        // is. What follows is an FGameplayAbilityActivationInfo, an ordinary (non-NetSerialize)
        // struct that RepLayout walks member by member - decodable in principle, but nothing here
        // reads it, so deriving its layout would only be a chance to get it wrong. Stopping is free:
        // UActorChannel.ReadContentBlockFields resynchronises to the field's own declared bit count.
        // (Which is also why neither is marked ExpectsFullDecode - leftover bits are expected.)
        ["ServerCancelAbility"] = new FRpcDef(
            "ServerCancelAbility",
            new[] { new FRpcParamDef("AbilityToCancel", ERpcParamKind.Int32) },
            (actor, values) => OnAbilityEndReported(actor, values[0], "ServerCancelAbility")
        ),
        ["ServerEndAbility"] = new FRpcDef(
            "ServerEndAbility",
            new[] { new FRpcParamDef("AbilityToEnd", ERpcParamKind.Int32) },
            (actor, values) => OnAbilityEndReported(actor, values[0], "ServerEndAbility")
        ),

        // The client reporting where a shot went - one per bullet, and the only honest "a round
        // left the barrel" signal this server has. Real Fortnite spends ammo through the ability's
        // cost GameplayEffect, which needs ability instances to run; nothing here can run one, and
        // neither reference server has anything to copy because on an injected server the native
        // GAS does it.
        //
        // Declared through the target data and NO FURTHER. What follows is an FGameplayTag, whose
        // NetSerialize_Packed encoding is a bounded int over Fortnite's own tag table - a table this
        // server has no copy of, so the width of that field is genuinely unknown. Stopping is safe:
        // UActorChannel.ReadContentBlockFields always resyncs to the field's own declared bit count,
        // the same trick ServerMove's tail relies on. (Which is also why this one is not marked
        // ExpectsFullDecode - leftover bits here are expected, not a bug.)
        ["ServerSetReplicatedTargetData"] = new FRpcDef(
            "ServerSetReplicatedTargetData",
            new[] {
                new FRpcParamDef("AbilityHandle", ERpcParamKind.Int32),
                new FRpcParamDef("AbilityOriginalPredictionKey", ERpcParamKind.PredictionKey),
                new FRpcParamDef("ReplicatedTargetDataHandle", ERpcParamKind.TargetDataHandle)
            },
            (actor, values) => OnShotReported(actor, values[0],
                values[2] as FGameplayAbilityTargetDataHandle, "ServerSetReplicatedTargetData")
        ),

        // The BATCHED form, and the one Fortnite's ranged fire ability actually uses. UE's
        // FScopedServerAbilityRPCBatcher folds an activation, its target data and its end into a
        // single FServerAbilityRPCBatch instead of three RPCs - so a weapon that batches never sends
        // ServerSetReplicatedTargetData at all. A live capture of our own server made that plain:
        // 41 ServerAbilityRPCBatch and 40 ServerEndAbility, against 2 loose ServerTryActivateAbility
        // and zero target data.
        //
        // ONE parameter, not five. The whole FServerAbilityRPCBatch is a single RPC parameter, so it
        // takes a single leading "send" bit and its members then follow back to back with no framing
        // of their own - declaring them as separate FRpcParamDefs would read four presence bits that
        // are not on the wire. See FServerAbilityRPCBatch for the member layout.
        ["ServerAbilityRPCBatch"] = new FRpcDef(
            "ServerAbilityRPCBatch",
            new[] { new FRpcParamDef("BatchInfo", ERpcParamKind.AbilityRpcBatch) },
            (actor, values) => {
                if (values[0] is not FServerAbilityRPCBatch batch) {
                    Console.WriteLine("NativeRpcHandlers: ServerAbilityRPCBatch arrived with its send bit clear - " +
                                      "no batch to act on");
                    return;
                }

                Console.WriteLine($"NativeRpcHandlers: ServerAbilityRPCBatch on {actor.GetFName()} {batch}");
                OnShotReported(actor, batch.AbilitySpecHandle, batch.TargetData, "ServerAbilityRPCBatch");
            },
            expectsFullDecode: true
        )
    };

    /// <summary>
    ///     One reported shot, from either the loose or the batched path - UE sends one or the other
    ///     per activation, never both, so this cannot double-count.
    ///
    ///     <paramref name="targetData"/> is what the client says it hit, and it is the client's word
    ///     alone: a real server re-traces it before believing it. Nothing is trusted here yet - it is
    ///     only reported - but it is now READ, which it was not before, and that is what any damage,
    ///     harvesting or hit-marker work needs first.
    /// </summary>
    /// <summary>
    ///     One ability the client says it has finished, from either ServerEndAbility or
    ///     ServerCancelAbility. Both arrive on the ability system COMPONENT, so the actor here is the
    ///     PlayerState that owns it, not the controller.
    ///
    ///     Only the emote acts on this so far. A spec this server granted but does not track (a
    ///     weapon's fire ability, jump, sprint) simply ends client-side and is left alone: real UE
    ///     would run the ability instance's EndAbility, and there is no instance here to end.
    /// </summary>
    /// <summary>
    ///     A yaw folded back into (-180, 180]. Only cosmetic - an FRotator serialises the same either
    ///     way - but a chain of edits otherwise prints "Yaw=360" or "Yaw=630" in the log, which is
    ///     impossible to compare against the -90 the anchor is recorded at.
    /// </summary>
    private static float NormalizeYaw(float yaw) {
        var wrapped = yaw % 360f;
        if (wrapped > 180f) wrapped -= 360f;
        if (wrapped <= -180f) wrapped += 360f;
        return wrapped;
    }

    private static void OnAbilityEndReported(AActor actor, object? handleValue, string source) {
        if (handleValue is not int handle) return;
        if (actor is not APlayerState playerState) return;
        if (playerState.GetOwningController() is not APlayerController pc) return;

        if (NetDebugLog.VerboseEnabled) {
            Console.WriteLine($"NativeRpcHandlers: {source} on {actor.GetFName()} Handle={handle}");
        }

        FortEmoteSystem.OnAbilityEnded(pc, handle, $"the client sent {source}");

        // A CANCEL - a weapon swap mid-animation - means the disguise was never put on. An END is
        // the ability finishing normally, which the disguise's own timer already covers.
        if (source == "ServerCancelAbility") FortSnowmanDisguise.Cancel(playerState, handle);
    }

    private static void OnShotReported(AActor actor, object? handleValue,
                                       FGameplayAbilityTargetDataHandle? targetData, string source) {
        ReportTargetData(actor, targetData, source, IsThrowAbility(actor, handleValue));

        if (actor is not APlayerState playerState) {
            Console.WriteLine($"NativeRpcHandlers: {source} arrived on {actor.GetType().Name}, not a PlayerState");
            return;
        }

        if (handleValue is not int abilityHandle) {
            Console.WriteLine($"NativeRpcHandlers: {source} - AbilitySpecHandle absent (its 'send' bit was 0), " +
                              "cannot tell which ability fired");
            return;
        }

        var pawn = playerState.GetOwningPawn();
        if (pawn == null) {
            Console.WriteLine($"NativeRpcHandlers: {source} handle={abilityHandle} - no pawn reachable from " +
                              $"{playerState.GetFName()} (Owner={playerState.Owner?.GetFName().ToString() ?? "null"})");
            return;
        }

        if (pawn.CurrentWeapon is not { } weapon) {
            Console.WriteLine($"NativeRpcHandlers: {source} handle={abilityHandle} - pawn holds no weapon");
            return;
        }

        // The same batch RPC carries a RELOAD activation, told apart by which spec it names.
        if (weapon.ReloadAbilitySpecHandle == abilityHandle) {
            Reload(pawn, weapon);
            return;
        }

        // The batch arrives for whatever ability produced it. Only the held weapon's own fire
        // ability spends its magazine.
        if (weapon.GrantedAbilitySpecHandle != abilityHandle) {
            Console.WriteLine($"NativeRpcHandlers: {source} handle={abilityHandle} does not match the held " +
                              $"weapon's granted spec {weapon.GrantedAbilitySpecHandle}, ignoring");
            return;
        }

        ConsumeAmmo(pawn, weapon);
    }

    /// <summary>
    ///     Whether the target data in this batch came from a THROW rather than from firing whatever
    ///     the player is holding.
    ///
    ///     WHY IT HAS TO BE ASKED. `ReportTargetData` prices every reported hit with `DamageFor`,
    ///     which reads the pawn's CURRENT WEAPON - and a thrown grenade does not change that. So the
    ///     impact the client reports for a grenade was being charged at the rifle still in the
    ///     player's hands: "the buildings take exactly 30" is `WID_Assault_Auto`'s EnvPB, applied to
    ///     every build near where an impulse grenade landed. The grenade's own effect is applied by
    ///     this server, in FortProjectileSystem.Explode, from the projectile's row - and for an
    ///     impulse or a shockwave that row is zero.
    ///
    ///     EXCLUDES THROWS RATHER THAN REQUIRING A WEAPON, which is the conservative direction and
    ///     deliberately not the tidier one. The obvious test is "does this handle match the held
    ///     weapon's own fire spec", which is what the ammo path below already asks - but if a
    ///     pickaxe's spec ever failed that test, harvesting and every structure hit would go silent,
    ///     and this is not a change worth risking that on. A throw is identified positively, by the
    ///     same WithTrajectory/Throw test FortWarmupThrowables uses on the ability's own path, so
    ///     nothing else changes behaviour at all.
    /// </summary>
    private static bool IsThrowAbility(AActor actor, object? handleValue) {
        if (handleValue is not int abilityHandle) return false;
        if (actor is not APlayerState playerState) return false;
        if (playerState.AbilitySystemComponent is not { } abilitySystem) return false;

        var spec = abilitySystem.ActivatableAbilities.Items.FirstOrDefault(item => item.Handle == abilityHandle);
        if (spec?.Ability?.GetFName().ToString() is not { Length: > 0 } ability) return false;

        return ability.Contains("WithTrajectory", StringComparison.OrdinalIgnoreCase)
               || ability.Contains("Throw", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Logs what the client reported hitting. Map geometry is almost never something this server
    ///     has a NetGUID for, so <see cref="FHitResult.Actor"/> is usually null and the exported path
    ///     is the only name available - "Athena_Tree_Medium_01_12" is a perfectly good answer even
    ///     though no object backs it here.
    /// </summary>
    /// <param name="thrown">
    ///     True when this batch belongs to a THROW ability, in which case the hits are reported and
    ///     LOGGED but nothing is damaged or harvested - see <see cref="IsThrowAbility" />.
    /// </param>
    private static void ReportTargetData(AActor actor, FGameplayAbilityTargetDataHandle? targetData, string source,
                                         bool thrown = false) {
        if (targetData == null) {
            Console.WriteLine($"NativeRpcHandlers: {source} on {actor.GetFName()} carried no target data " +
                              "(its send bit was clear, or the decode stopped before it)");
            return;
        }

        if (targetData.Data.Count == 0) {
            Console.WriteLine(targetData.bDecodeFailed
                ? $"NativeRpcHandlers: {source} on {actor.GetFName()} carried target data that would not decode at all"
                : $"NativeRpcHandlers: {source} on {actor.GetFName()} reported no targets");
            return;
        }

        foreach (var entry in targetData.Data) {
            if (entry.bUnknownShape) {
                // Worth a full sentence rather than a shrug: this names the exact struct someone has
                // to work out next, and it is the only place that name ever appears.
                Console.WriteLine($"NativeRpcHandlers: {source} carried target data of type " +
                                  $"'{entry.ScriptStructPath}', which this server has no layout for - " +
                                  "decoding stopped there. Add it to FGameplayAbilityTargetDataHandle.");
                continue;
            }

            if (entry.HitResult is not { } hit) continue;

            Console.WriteLine($"NativeRpcHandlers: {source} HIT {hit}");

            if (hit.ComponentPath.Length > 0 || hit.PhysMaterialPath.Length > 0) {
                Console.WriteLine($"NativeRpcHandlers:   component='{hit.ComponentPath}' " +
                                  $"physMaterial='{hit.PhysMaterialPath}' item={hit.Item} face={hit.FaceIndex}");
            }

            // A THROW REPORTS WHERE IT LANDED, NOT A HIT THAT COSTS ANYTHING. Everything below
            // prices the hit with the held weapon's stats, and the player is still holding whatever
            // they had before they threw - see IsThrowAbility. The throw's own damage is this
            // server's to apply, from the projectile's row, in FortProjectileSystem.Explode.
            if (thrown) {
                Console.WriteLine($"NativeRpcHandlers:   ...from a THROW - logged, not damaged " +
                                  "(the projectile's own explosion is what damages).");
                continue;
            }

            // A hit's Actor only ever resolves to a real object when the thing hit has its own
            // NetGUID - true for a placed building (this server spawned it and replicated it) but
            // essentially never true for map geometry (a tree, a rock - see FHitResult's own doc
            // comment on why ActorPath is the fallback for everything else). That is exactly the
            // split needed here: a building takes HP damage and can bring its neighbours down with
            // it, scenery just harvests.
            //
            // bPlayerPlaced:true is required, not just "is ABuildingActor" - DamageLevelActor's own
            // stand-in for a piece of LEVEL geometry is an ABuildingActor too (see there for why),
            // and once it has a NetGUID a client names it directly on every later hit exactly like a
            // real building. Without this guard that second hit lands here instead of in
            // DamageLevelActor, whose ApplyDamage/MarkDestroyed the stand-in expects - and reaching
            // BuildingStructuralSupportSystem's cascade/Destroy() through THIS branch crashed outright
            // the first time it was tried live: the stand-in's outer chain is UAssetRegistry's
            // synthetic Package/World/Level objects, none of which carry a UClass, and
            // AActor.Destroy -> GetWorld -> GetLevel -> GetTypedOuter -> IsA NREs walking it
            // (UObjectBaseUtility.IsA now null-guards that too, but the real fix is not reaching
            // Destroy() on this object at all).
            if (hit.Actor is ABuildingActor { bPlayerPlaced: true } building) {
                // Weak spots on a player-placed piece, not just level-actor scenery (Round 46) - the
                // detection (IsWeakspotHit) and the reveal/relocate latch (ShouldSpawnWeakSpot/
                // ResetWeakSpot) were never level-actor-specific, they just hadn't been wired in here
                // yet. Same sequence DamageLevelActor already uses: reset (this hit consumed the
                // current marker) before checking whether it's time to reveal the next one.
                if (actor is APlayerState weakSpotInstigator) {
                    if (IsWeakspotHit(hit)) building.ResetWeakSpot();

                    if (weakSpotInstigator.GetOwningPawn()?.GetWorld() is { } world
                        && building.ShouldSpawnWeakSpot(world.TimeSeconds, WeakSpotRevealDelaySeconds)) {
                        SpawnWeakSpot(weakSpotInstigator, building, hit);
                    }
                }

                var damage = DamageFor(hit, actor as APlayerState);
                var wasKilled = building.CurrentHitPoints <= damage;
                if (building.GetWorld() is { } buildingWorld)
                    BuildingStructuralSupportSystem.Of(buildingWorld).ApplyDamage(building, damage);
                ReportDamagedBuilding(actor, building, wasKilled, IsWeakspotHit(hit));
                continue;
            }

            // A hit on another PLAYER. Reachable for the same reason a hit on a building is: the
            // pawn is an actor this server spawned and replicated, so it has a NetGUID and the
            // client's FHitResult names it by that rather than by a path.
            //
            // The client's word is taken at face value here, exactly as it is for buildings - no
            // re-trace, no range check, no line of sight. That is a cheat surface and a knowing one;
            // the honest fix is a server-side trace, which needs collision geometry this project
            // does not have (only the baked terrain heightmap, see TerrainHeightMap).
            if (hit.Actor is APawn victimPawn) {
                DamagePlayer(actor as APlayerState, victimPawn, hit);
                continue;
            }

            var damagedLevelActor = (ABuildingActor?) null;
            var bFelledThisHit = DestructibleSceneryEnabled(actor) && DamageLevelActor(actor, hit, out damagedLevelActor);

            Harvest(actor, hit, bFelledThisHit, damagedLevelActor);
        }
    }

    /// <summary>
    ///     Per-hit damage, boosted for a weak-spot hit (Round 36's `IsWeakspotHit`) - real Fortnite
    ///     rewards aiming for the weak spot with more than just bonus resources, and this project had
    ///     no way to model that until the weak-spot detection itself existed. `WeakspotDamageMultiplier`
    ///     is NOT ground truth, same footing as `FellingBonusMultiplier`/`WeakspotBonusMultiplier` -
    ///     only the WEAK-SPOT DETECTION (`IsWeakspotHit`) is derived from the real PAK data; how much
    ///     extra damage/resources it's worth is a placeholder throughout this file.
    /// </summary>
    private static int DamageFor(FHitResult hit, APlayerState? instigator = null) {
        var weapon = instigator?.GetOwningPawn()?.CurrentWeapon?.WeaponData?.GetFName().ToString();

        if (FortWeaponStats.For(weapon) is not { } stats) {
            if (weapon != null && _warnedUnknownWeapons.TryAdd(weapon, 0)) {
                Console.WriteLine($"NativeRpcHandlers: no baked stats for '{weapon}' - structure damage falls " +
                                  $"back to the flat {BuildingDamagePerHit}. Re-run " +
                                  "Tools/WeaponStats/gen_weapon_stats.py if this weapon should be in the table.");
            }

            return IsWeakspotHit(hit) ? BuildingDamagePerHit * WeakspotDamageMultiplier : BuildingDamagePerHit;
        }

        var damage = stats.EnvironmentDamageAt(ShotDistance(hit));

        // A WEAK-SPOT HIT IS x2 AGAINST A STRUCTURE, not the weapon row's DamageZone_Vulnerability.
        //
        // That row value is 10.0 on every pickaxe, and taking it literally gives 500 environment
        // damage - which one-shot every prop in Athena the moment the real health table landed
        // (a tree is 300). Battle Royale's numbers are flat and well known: a pickaxe does 50 to a
        // structure and 100 on the weak spot, identical for every pickaxe skin, and 50/100 against
        // the real per-class health reproduces the familiar six-swing tree exactly. 10.0 is a Save
        // the World number for husk weak points; it is in the row, it is simply not this rule.
        //
        // Vulnerability is still what decides WHETHER a weapon gets the bonus at all - it is 0 on
        // every gun, and a gun does not get a structural weak-spot bonus.
        if (IsWeakspotHit(hit) && stats.Vulnerability > 0f) damage *= WeakspotEnvironmentMultiplier;

        return (int) MathF.Round(damage);
    }

    /// <summary>
    ///     How far the shot travelled, for the damage falloff - the client's own trace start to the
    ///     point it says it hit.
    ///
    ///     Taken from the hit rather than from the two actors' positions on purpose: the falloff is a
    ///     property of the SHOT, and the server's idea of where either party is can lag (a player on
    ///     a vehicle has no world position here at all - see the move handler). The client already
    ///     reports both ends of the trace in the same struct, and they are consistent with each other
    ///     by construction.
    /// </summary>
    private static float ShotDistance(FHitResult hit) {
        var dx = hit.ImpactPoint.X - hit.TraceStart.X;
        var dy = hit.ImpactPoint.Y - hit.TraceStart.Y;
        var dz = hit.ImpactPoint.Z - hit.TraceStart.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _warnedUnknownWeapons =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The fallback weak-spot multiplier, used only for a weapon the bake does not know. Still a
    ///     placeholder - the real number is the row's own DamageZone_Vulnerability, which DamageFor
    ///     now uses whenever it has one.
    /// </summary>
    private const int WeakspotDamageMultiplier = 3;

    /// <summary>
    ///     What a weak-spot hit multiplies ENVIRONMENT damage by. See DamageFor for why this is 2
    ///     and not the weapon row's DamageZone_Vulnerability.
    /// </summary>
    private const float WeakspotEnvironmentMultiplier = 2f;

    /// <summary>DESTRUCTIBLE_SCENERY=1 gates <see cref="DamageLevelActor"/> - see there for what it does and why it defaults off.</summary>
    private static bool DestructibleSceneryEnabled(AActor actor) =>
        actor.WorldOptions.Get("DESTRUCTIBLE_SCENERY") == "1";

    /// <summary>
    ///     Round 29's live experiment (see [[afortonlinebeacon-status]]): does a real client accept a
    ///     server-opened channel for an actor it already loaded from the LEVEL, the way it does for
    ///     one this server spawned? A map prop's FHitResult never resolves to an object (this server
    ///     never gave it a NetGUID), only to <see cref="FHitResult.ActorPath"/> - the exact exported
    ///     path the client itself sent, which is by construction the one string the client's own
    ///     package map already knows how to resolve back to that actor.
    ///
    ///     <see cref="UAssetRegistry.GetOrCreateSubObject{T}"/> turns that path into a stably-named
    ///     (RF_WasLoaded) <see cref="ABuildingActor"/> stand-in - the same mechanism already used for
    ///     AGameModeBase's WorldManager reference, just with an AActor leaf instead of a plain UObject
    ///     so it can go through a real actor channel. Reusing ABuildingActor rather than inventing a
    ///     new type means bDestroyed/BuildingAnimation (NativeRepLayouts, UActorChannel) and the
    ///     UNetDriver per-tick diff walk all apply unmodified - see that class and
    ///     UPackageMapClient.SerializeNewActor's `if (!netGuid.IsDynamic()) return;` early-out, which
    ///     is what should make the channel-open header carry ONLY the identity and none of a dynamic
    ///     spawn's class/location/rotation, exactly as a real net-startup actor's does.
    ///
    ///     Registering via UNetDriver.AddNetworkActor (rather than opening a channel by hand here) is
    ///     deliberate: OpenChannelsForNewlyRelevantActors already opens a channel for anything new in
    ///     NetworkObjectList on its own very next tick, and the same per-tick walk that already
    ///     replicates ABuildingActor property changes for player builds picks this instance up too -
    ///     nothing about that path needs to know its actor came from a hit rather than
    ///     ServerCreateBuildingActor. No BuildingStructuralSupportSystem.Register call and no Destroy()
    ///     - a broken piece of level geometry does not cascade and, unlike a player-placed piece, is
    ///     not expected to vanish, only to switch to its destroyed look.
    ///
    ///     Called on EVERY hit that reaches here, not just the first: once the stand-in has a
    ///     NetGUID, a live client stops re-exporting ActorPath and names it directly instead (see
    ///     UAssetRegistry's own doc comment on an emote asset's second reference for the identical
    ///     behaviour), so hit.Actor resolves on later hits and the caller routes those here too -
    ///     see the dispatch loop's bPlayerPlaced:true guard on the ABuildingActor branch above.
    ///     MarkAsLevelActor() is what keeps that guard correctly excluding this stand-in every time.
    ///
    ///     UNTESTED against a live client - the whole point of turning this on is to find out.
    ///
    ///     Returns true on the ONE hit that actually broke the piece - the caller uses that to pay a
    ///     felling bonus, see <see cref="Harvest"/>. <paramref name="levelActorOut"/> comes back set
    ///     whenever a stand-in was actually touched (destroyed or not), so <see cref="Harvest"/> can
    ///     send the same damaged-resource popup a player-placed building already gets.
    /// </summary>
    private static bool DamageLevelActor(AActor actor, FHitResult hit, out ABuildingActor? levelActorOut) {
        levelActorOut = null;

        if (actor is not APlayerState playerState) return false;
        if (playerState.GetOwningPawn()?.GetWorld() is not { } world) return false;
        if (world.NetDriver is not { } netDriver) return false;

        ABuildingActor levelActor;
        if (hit.Actor is ABuildingActor resolved) {
            levelActor = resolved;
        } else {
            if (hit.ActorPath.Length == 0) return false;

            // Round 37 tried refusing only UE's auto-generated actor-label shape
            // (LooksLikeEngineDefaultActorLabel - "StaticMeshActor17", no level-designer name) and it
            // was not enough: "S_Elevation_Ground_Z_127" - a HAND-NAMED prop, no different in shape
            // from Tree_0839 or Car_KCar4 - crashed the client the same way, and the client's own log
            // named its real class too: FortStaticMeshActor, same as StaticMeshActor17. So an
            // actor's NAME simply does not predict its class; something that actually tracks class
            // identity is needed instead.
            //
            // FortHarvestResources.StemYields is exactly that, already built and already proven: it
            // is generated by Tools/HarvestTable from real CDOs, and a stem only ever lands in it
            // because ITS CLASS's CDO carries BuildingResourceAmountOverride - a property that only
            // exists on ABuildingSMActor. A stem the harvest table does not recognise is therefore a
            // class this project has no evidence is building-shaped, which is precisely the
            // uncertainty that crashed the client twice now. Gating on it means DamageLevelActor only
            // ever runs against a class independently confirmed, from the PAK itself, to be an
            // ABuildingSMActor - not a guess about the actor's NAME.
            //
            // The real cost: a genuinely destructible prop that yields NO resource
            // (bAllowResourceDrop=false on its CDO, per Erbium's OnDamageServer) will not break
            // either, since it never entered StemYields in the first place. Under-covering
            // destructibility is the acceptable failure here; disconnecting the client is not.
            //
            // That under-coverage used to be much wider than intended and it was a BUG, not the
            // tradeoff: the generator read BuildingResourceAmountOverride off leaf CDOs only, and a
            // cooked Blueprint does not re-serialise a property it inherits unchanged. 103 classes
            // - `NeoTilted_Car12` among them, a car with a perfectly real 400 HP - therefore looked
            // like they had no resource data and were unbreakable. Tools/HarvestTable now walks the
            // SuperStruct chain, which is the same evidence one level up, not a weaker gate.
            if (FortHarvestResources.ResolveHit(hit.ActorPath) == null) return false;

            // A CHEST OR AMMO BOX IS NOT SCENERY - but it must still be REGISTERED here, and this is
            // the only place that can do it.
            //
            // A client is not the NetGUID authority. Its own log spells out what happens when it tries
            // to name a chest this server has never introduced:
            //
            //     GetOrAssignNetGUID: NetGUIDLookup did not contain object on client, returning
            //     default. Object ...PersistentLevel.Tiered_Chest_6_Parent2_1705
            //
            // so ServerAttemptInteract's ReceivingActor arrives as the default guid with NO path, and
            // no amount of decoding can recover which chest was meant. The real server never hits this
            // because it has already replicated the chest and the client has an id for it.
            //
            // A pickaxe HIT is different: FHitResult carries the actor's exported PATH, so this is the
            // one moment the server learns a chest exists and can hand the client an id for it. Doing
            // that here - with the CORRECT class - is what makes the later interaction resolvable.
            // Registering it as a plain ABuildingActor (what this used to do) gave the client an id
            // for the wrong kind of actor and left it with a chest bound to None.None; skipping it
            // entirely (Round 96) left the interaction unnameable instead.
            if (ContainerTierGroupFor(hit.ActorPath) != null) {
                RegisterContainerFromHit(netDriver, hit.ActorPath);
                return false;
            }

            try {
                levelActor = netDriver.World!.MapActors.GetOrCreate<ABuildingActor>(hit.ActorPath);
            } catch (Exception ex) {
                Console.WriteLine($"NativeRpcHandlers: DESTRUCTIBLE_SCENERY could not turn '{hit.ActorPath}' " +
                                  $"into a level-actor path - {ex.Message}");
                return false;
            }
        }

        // A CHEST OR AMMO BOX IS NOT SCENERY - and the guard for that has to sit on BOTH paths, not
        // just the by-path one above. Once a chest has been registered it HAS a NetGUID, so every
        // later hit resolves to an object and takes the `hit.Actor is ABuildingActor` branch, which
        // walked straight into ApplyDamage/MarkDestroyed. The effect was that the single hit which
        // makes a chest interactable at all is immediately followed by hits that destroy it:
        //
        //     registered container '...Tiered_Chest_6_Parent2_1705' so the client can name it
        //     ...three more swings...
        //     DESTRUCTIBLE_SCENERY marked 'Tiered_Chest_6_Parent2_1705' destroyed (0/200 HP)
        //     ReplicateActorUpdate: ABuildingContainer ChIndex=13 changed=[bDestroyed]
        //
        // 30 + 90 + 90 (two weak-spot hits) = 210 against 200 HP, so four swings did it every time -
        // which is why "hit it, then hold to search" never worked. Registration is the ONLY thing a
        // hit on a container may do.
        if (levelActor is ABuildingContainer) return false;

        // NOR IS A SUPPLY LLAMA, and the reason is the same one, one class over. A llama derives from
        // ABuildingActor, so the `hit.Actor is ABuildingActor` branch above catches it the moment a
        // player shoots one - and from here it would be marked as a level actor, given a weak spot,
        // damaged and destroyed as though it were a rock. It is none of those things: it is a
        // container that is opened by interacting with it, and everything this function would push at
        // it (bDestroyed, the weak spot, MinimalReplicationProxy) belongs to ABuildingSMActor, which a
        // llama is not a subclass of. See NativeRepLayouts.SupplyDropLlamaProps.
        //
        // Real Fortnite does let you shoot a llama open. Doing that here means routing the damage to
        // AFortAthenaSupplyDropLlama.Search rather than to this scenery path, which is a separate
        // piece of work; refusing outright is the correct behaviour until it exists.
        if (levelActor is AFortAthenaSupplyDropLlama) return false;

        levelActor.MarkAsLevelActor();
        levelActorOut = levelActor;

        if (!netDriver.NetworkObjectList.Contains(levelActor)) {
            // ITS REAL HIT POINTS, before the first hit is applied. Without this every piece of map
            // geometry shared ABuildingActor's 200 HP class default, so a tree, a pickup truck and a
            // brick wall all broke in the same four swings. FortHarvestResources.MaxHealthFor reads
            // the number the game itself resolves for that class; a stem it does not cover keeps the
            // default rather than being made indestructible.
            if (FortHarvestResources.MaxHealthFor(hit.ActorPath) is { } maxHealth) {
                levelActor.InitializeLevelActorHitPoints(maxHealth);
            }

            levelActor.SetReplicates(true);
            netDriver.AddNetworkActor(levelActor);
            Console.WriteLine($"NativeRpcHandlers: DESTRUCTIBLE_SCENERY registered level actor " +
                              $"'{hit.ActorPath}' for replication ({levelActor.MaxHitPoints} HP) - " +
                              "its channel opens on the next tick");
        }

        // This hit landed on the marker that's currently up (Round 36's PhysMaterial detection) -
        // that marker is spent. Reset before the ShouldSpawnWeakSpot check below so a replacement
        // starts revealing again immediately, the same way the very first one did - real Fortnite
        // relocates the weak spot once it's struck rather than leaving one in place all match.
        if (IsWeakspotHit(hit)) levelActor.ResetWeakSpot();

        if (levelActor.ShouldSpawnWeakSpot(world.TimeSeconds, WeakSpotRevealDelaySeconds)) {
            SpawnWeakSpot(playerState, levelActor, hit);
        }

        if (!levelActor.ApplyDamage(DamageFor(hit, actor as APlayerState)) || !levelActor.MarkDestroyed()) return false;

        Console.WriteLine($"NativeRpcHandlers: DESTRUCTIBLE_SCENERY marked '{hit.ActorPath}' destroyed " +
                          $"({levelActor.CurrentHitPoints}/{levelActor.MaxHitPoints} HP)");
        return true;
    }

    /// <summary>
    ///     Gives the client a NetGUID for a chest or ammo box, as the RIGHT class, the first time a hit
    ///     tells this server that one exists. Never damages or opens it - opening is
    ///     ServerAttemptInteract's job, and this only exists so that RPC has something nameable to
    ///     refer to. See the call site for why a hit is the only opportunity.
    /// </summary>
    private static void RegisterContainerFromHit(UNetDriver netDriver, string actorPath) {
        ABuildingContainer container;
        try {
            container = netDriver.World!.MapActors.GetOrCreate<ABuildingContainer>(actorPath);
        } catch (Exception ex) {
            Console.WriteLine($"NativeRpcHandlers: could not turn container '{actorPath}' into a level actor - {ex.Message}");
            return;
        }

        container.MarkAsLevelActor();
        if (netDriver.NetworkObjectList.Contains(container)) return;

        container.SetReplicates(true);
        netDriver.AddNetworkActor(container);
        Console.WriteLine($"NativeRpcHandlers: registered container '{actorPath}' so the client can name it");
    }

    /// <summary>
    ///     How long after a piece's first hit (or after its last marker was struck - see
    ///     ABuildingActor.ResetWeakSpot) its weak-spot marker appears.
    ///
    ///     Round 39 sent this at 3.0f and the RPC itself worked perfectly - the client's own log shows
    ///     it actually spawning `WeakSpot_C`, identical to a real ProjectReboot3.0 capture. What was
    ///     wrong: at BuildingDamagePerHit=40 against DefaultHitPoints=200, any piece dies in exactly 5
    ///     hits, which at ordinary swing speed took barely longer than 3 seconds - so the marker kept
    ///     spawning on the SAME hit that destroyed the piece and was never actually seen. Round 40
    ///     lowered this to 1.0f, which fixed visibility but meant the marker only ever appeared from
    ///     the SECOND hit onward (first hit merely starts the clock) - not what a live test expected.
    ///     0f makes it appear on the piece's very FIRST hit, and - combined with ResetWeakSpot - on
    ///     the very same hit that consumes the previous one, matching real Fortnite's "always exactly
    ///     one marker up somewhere on the piece" feel far more closely than an authentic multi-second
    ///     delay would on a piece this cheap to destroy.
    ///
    ///     THE HP SIDE OF THAT TRADEOFF IS NO LONGER A PLACEHOLDER: a map prop now carries its real
    ///     health (a tree is 300, six swings of a 50-damage pickaxe), so pieces last long enough for
    ///     a genuine reveal delay to be worth revisiting. Left at 0 because nothing has measured what
    ///     the real one is.
    /// </summary>
    private const float WeakSpotRevealDelaySeconds = 0.0f;

    /// <summary>
    ///     Sends ClientSpawnWeakSpotOnBuildingActor to the instigator only - a real server would send
    ///     it to every connection that can see the piece (ActiveWeakSpots is per-PlayerController),
    ///     but this project only ever has the one player to test with. Uses the TRIGGERING hit's own
    ///     impact point/normal as the marker's position, since this project has no collision geometry
    ///     to pick a point on the mesh surface the way a real server presumably does - see
    ///     UActorChannel.SendClientSpawnWeakSpotOnBuildingActor for the wire encoding and how
    ///     confident that part is.
    ///
    ///     Shared by both hit-dispatch branches: originally DESTRUCTIBLE_SCENERY-only (Round 36-41),
    ///     now also called for a player-placed piece (the dispatch loop's `bPlayerPlaced:true` branch,
    ///     Round 46) - <see cref="ABuildingActor"/>'s own ShouldSpawnWeakSpot/ResetWeakSpot latch is
    ///     not level-actor-specific, it just went unused for a player build until now.
    /// </summary>
    private static void SpawnWeakSpot(APlayerState playerState, ABuildingActor building, FHitResult hit) {
        if (playerState.GetOwningController() is not APlayerController pc) return;
        var connection = pc.GetWorld()?.NetDriver?.ClientConnections
            .FirstOrDefault(candidate => candidate.PlayerController == pc);

        if (connection?.FindActorChannel(pc) is not { } pcChannel) return;

        pcChannel.SendClientSpawnWeakSpotOnBuildingActor(building, hit.ImpactNormal, hit.ImpactPoint);
        Console.WriteLine($"NativeRpcHandlers: spawned a weak spot on '{building.GetFName()}' at {hit.ImpactPoint}");
    }

    /// <summary>
    ///     Per-hit building damage. Like <see cref="ABuildingActor.BaseHitPointsFor"/>, NOT read off a
    ///     real weapon stat row (WeaponStatHandle -&gt; a DataTable this project does not parse) - a
    ///     flat placeholder, pickaxe-sized, so a wall visibly comes down over several hits rather
    ///     than in one. Neither the per-weapon damage nor the pickaxe/gun split real Fortnite makes
    ///     (guns do less damage to builds than harvesting tools) is modelled; when they are, this is
    ///     the constant that becomes a lookup off the instigator's CurrentWeapon.
    /// </summary>
    private const int BuildingDamagePerHit = 40;

    /// <summary>
    ///     Tells the player who landed the hit that they hit this building - the HUD half of building
    ///     damage, which is local to the instigator and does NOT come from replicated actor state.
    ///     See UActorChannel.SendClientReportDamagedResourceBuilding.
    ///
    ///     PotentialResourceCount is 0 because a player-built piece yields nothing when broken (only
    ///     map scenery does); the parameter still has to be sent, since only bools may be omitted.
    /// </summary>
    private static void ReportDamagedBuilding(AActor actor, ABuildingActor building, bool bDestroyed, bool bJustHitWeakspot) {
        if (actor is not APlayerState playerState) return;
        if (playerState.GetOwningController() is not APlayerController pc) return;
        // UNetConnection::PlayerController is the back-link; there is no GetNetConnection() on the
        // controller itself in this project, so the owning connection is found by matching it.
        var connection = pc.GetWorld()?.NetDriver?.ClientConnections
            .FirstOrDefault(candidate => candidate.PlayerController == pc);

        if (connection?.FindActorChannel(pc) is not { } pcChannel) return;

        pcChannel.SendClientReportDamagedResourceBuilding(
            building, ResourceTypeFor(building.Material), resourceCount: 0, bDestroyed, bJustHitWeakspot);
    }

    /// <summary>
    ///     Whether a hit landed on a weak spot - DERIVED, not guessed: the PAK ships four physical
    ///     material assets in `/Game/FortressPhysicalMaterials/`, one plain per resource
    ///     (Wood/Stone/Metal, already seen live on ordinary hits as `hit.PhysMaterialPath`) and one
    ///     "WeakSpot" variant each (`WeakSpot`, `WeakSpot_Wood`, `WeakSpot_Stone`, `WeakSpot_Metal`) -
    ///     confirming FortGameData's WeakSpotWoodPhysicalMaterial/WeakSpotStonePhysicalMaterial/
    ///     WeakSpotMetalPhysicalMaterial properties (found via Explore agent research into raider3.5's
    ///     SDK dumps) name exactly these. A weak spot is therefore a specific FACE of the mesh painted
    ///     with the WeakSpot material rather than the ordinary one - so the client reporting a
    ///     PhysMaterial from this set IS the weak-spot signal, with no bone/component guessing
    ///     required. `bIsWeakspot = Damage == 100.0f` (seen in several other reimplemented servers in
    ///     PriveDev, all flagged in their own comments as guesses, two of four shipped disabled) was
    ///     deliberately NOT used - it is not derived from anything.
    /// </summary>
    private static bool IsWeakspotHit(FHitResult hit) =>
        hit.PhysMaterialPath.Contains("/FortressPhysicalMaterials/WeakSpot", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     One reported hit on a player. Damage, and the elimination if it was the last one, both
    ///     live in <see cref="FortDamageSystem"/>; this only works out who shot whom with what.
    ///
    ///     The death cause is taken from the weapon the shooter is holding rather than from the hit,
    ///     which is what the elimination feed's icon is driven by. It is a coarse mapping - see
    ///     <see cref="DeathCauseFor"/> - because a precise one needs the weapon stat table.
    /// </summary>
    private static void DamagePlayer(APlayerState? instigator, APawn victimPawn, FHitResult hit) {
        if (victimPawn.PlayerState is not { } victim) {
            Console.WriteLine($"NativeRpcHandlers: hit on {victimPawn.GetFName()}, which has no PlayerState - " +
                              "nothing to damage");
            return;
        }

        // Self-inflicted gunfire is not a thing, and a client reporting it would mean the hit
        // decode is wrong rather than that the player shot themselves.
        if (ReferenceEquals(victim, instigator)) {
            Console.WriteLine($"NativeRpcHandlers: {victim.GetFName()} reported hitting THEMSELVES - ignoring; " +
                              "this points at a bad FHitResult decode, not at anything a client can really do.");
            return;
        }

        var weapon = instigator?.GetOwningPawn()?.CurrentWeapon;
        var weaponName = weapon?.WeaponData?.GetFName().ToString();
        var stats = FortWeaponStats.For(weaponName);

        // THE BONE IS LOGGED ON EVERY PLAYER HIT, and not only when it looks like a head. The
        // critical-zone rule below is the one assumption in this path (see IsCriticalHit), and the
        // only way to check it is to see what a real client actually names when someone lands a
        // headshot - so the value goes in the log whether or not it matched.
        var bone = hit.BoneName?.ToString() ?? "(none)";
        var critical = IsCriticalHit(hit);

        if (stats == null) {
            if (weaponName != null && _warnedUnknownWeapons.TryAdd(weaponName, 0)) {
                Console.WriteLine($"NativeRpcHandlers: no baked stats for '{weaponName}' - player damage falls " +
                                  $"back to the flat {FortDamageSystem.FallbackWeaponDamage(victimPawn.GetWorld()!)}. Re-run " +
                                  "Tools/WeaponStats/gen_weapon_stats.py if this weapon should be in the table.");
            }

            FortDamageSystem.ApplyDamage(victim, FortDamageSystem.FallbackWeaponDamage(victimPawn.GetWorld()!),
                                         DeathCauseFor(weapon), instigator);
            return;
        }

        var distance = ShotDistance(hit);
        var damage = stats.DamageAt(distance);
        if (critical) damage *= stats.Critical;

        Console.WriteLine($"NativeRpcHandlers: {victim.GetFName()} hit by '{weaponName}' at {distance:F0}uu " +
                          $"on bone '{bone}'{(critical ? " (CRITICAL)" : string.Empty)} - " +
                          $"{damage:F1} damage (falloff {stats.DmgPB:g}@{stats.RngPB:g} -> " +
                          $"{stats.DmgMax:g}@{stats.RngMax:g}, crit x{stats.Critical:g})");

        FortDamageSystem.ApplyDamage(victim, damage, DeathCauseFor(weapon), instigator);
    }

    /// <summary>
    ///     Takes this shot's rounds out of the equipped weapon's magazine, if the activated ability
    ///     is that weapon's fire ability.
    ///
    ///     THE SPEC HANDLE IS THE TEST, not the ability's name or class: `AFortWeapon` already keeps
    ///     `GrantedAbilitySpecHandle` (wire handle 32) because the CLIENT needs it, and it is the
    ///     only thing that distinguishes "the player fired" from any other ability they might
    ///     activate - a reload, an emote, a consumable. Matching on it costs one comparison and
    ///     cannot mistake one for another.
    ///
    ///     Runs out of ammo silently rather than refusing: the client has already played the shot it
    ///     is predicting, and this server has no ClientActivateAbilityFailed path to answer with
    ///     (see the activation handler). Clamping at zero at least keeps the count honest.
    /// </summary>
    private static void SpendAmmoForShot(APlayerState playerState, FGameplayAbilitySpec spec) {
        if (playerState.GetOwningPawn()?.CurrentWeapon is not { } weapon) return;
        if (weapon.GrantedAbilitySpecHandle != spec.Handle) return;

        var weaponName = weapon.WeaponData?.GetFName().ToString();
        var cost = FortWeaponStats.For(weaponName) is { } stats ? (int) MathF.Round(stats.AmmoPerFire) : 1;
        if (cost <= 0) return;

        var before = weapon.AmmoCount;
        weapon.AmmoCount = Math.Max(0, weapon.AmmoCount - cost);

        if (weapon.AmmoCount == before) return;

        Console.WriteLine($"NativeRpcHandlers: {playerState.GetFName()} fired '{weaponName}' - " +
                          $"ammo {before} -> {weapon.AmmoCount}");
    }

    /// <summary>
    ///     Whether a hit landed on the CRITICAL damage zone - a headshot.
    ///
    ///     THE MULTIPLIER IS DERIVED, THE ZONE IS NOT. `DamageZone_Critical` comes straight from the
    ///     weapon's stat row (2.0 on most guns, 1.0 on the pickaxe), so how much a headshot is worth
    ///     is real data. WHICH bone counts as the head is the assumption: Fortnite's pawns use the
    ///     UE mannequin skeleton, whose head bone is `head`, and the zone tables that would say so
    ///     properly have not been read out of the paks.
    ///
    ///     Which is why DamagePlayer logs the bone name on EVERY hit, matched or not - one live
    ///     headshot names the real bone, and if it is not `head` this rule is one string away from
    ///     being right.
    /// </summary>
    private static bool IsCriticalHit(FHitResult hit) =>
        hit.BoneName?.ToString().StartsWith("head", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    ///     What killed someone, for the elimination feed. Only the pickaxe is told apart with any
    ///     confidence (it is the one weapon whose class this project already keys on); everything
    ///     else reports as Rifle, which is what an unspecified gun looks like in the feed.
    ///
    ///     A real mapping is per-weapon and comes out of the same stat table the damage numbers do.
    /// </summary>
    private static EDeathCause DeathCauseFor(AFortWeapon? weapon) {
        if (weapon?.WeaponData == null) return EDeathCause.Unspecified;

        var name = weapon.WeaponData.GetFName().ToString();
        if (name.Contains("Pickaxe", StringComparison.OrdinalIgnoreCase)) return EDeathCause.Melee;
        if (name.Contains("Shotgun", StringComparison.OrdinalIgnoreCase)) return EDeathCause.Shotgun;
        if (name.Contains("Sniper", StringComparison.OrdinalIgnoreCase)) return EDeathCause.Sniper;
        if (name.Contains("Pistol", StringComparison.OrdinalIgnoreCase)) return EDeathCause.Pistol;
        if (name.Contains("SMG", StringComparison.OrdinalIgnoreCase)) return EDeathCause.SMG;

        return EDeathCause.Rifle;
    }

    /// <summary>EFortResourceType, whose ordering (Wood=0, Stone=1, Metal=2, Permanite=3, None=4) is the real enum's.</summary>
    private static byte ResourceTypeFor(EBuildingMaterial material) => material switch {
        EBuildingMaterial.Wood => 0,
        EBuildingMaterial.Stone => 1,
        EBuildingMaterial.Metal => 2,
        _ => 4
    };

    /// <summary>
    ///     Pays out whatever the thing that was hit is made of. Every swing at a tree yields, the way
    ///     it does in the real game - resources are granted per hit as damage is dealt, not only in
    ///     one lump when the tree falls.
    ///
    ///     Any weapon counts, not just the pickaxe. That matches Fortnite, where shooting scenery
    ///     harvests it too, and it costs nothing to allow: the yield comes from what was hit.
    /// </summary>
    /// <param name="bFellingBonus">
    ///     True on the one hit DamageLevelActor reports as having just broken the piece - see there.
    ///     Real Fortnite pays substantially more for the hit that fells a tree/rock than for an
    ///     ordinary chip, which this server had no way to know before DESTRUCTIBLE_SCENERY gave a
    ///     piece of level geometry an actual HP/destruction state to ask about. FellingBonusMultiplier
    ///     is NOT ground truth, same as every other number in FortHarvestResources - chosen to make
    ///     finishing a tree feel like it paid off, pending the real per-material table.
    /// </param>
    /// <param name="levelActor">
    ///     The stand-in DamageLevelActor resolved for this hit, when DESTRUCTIBLE_SCENERY actually
    ///     touched one - null otherwise (feature off, or the hit didn't resolve to a recognised
    ///     class). When set, this also sends the SAME damaged-resource popup a player-placed building
    ///     already gets (see ReportDamagedBuilding) - previously harvesting scenery granted the
    ///     resource silently with no client-side popup at all.
    /// </param>
    private static void Harvest(AActor actor, FHitResult hit, bool bFellingBonus = false, ABuildingActor? levelActor = null) {
        if (actor is not APlayerState playerState) return;
        if (playerState.GetOwningPawn()?.Controller is not APlayerController controller) return;

        if (FortHarvestResources.ResolveHit(hit.ActorPath) is not { } yield) return;

        var bJustHitWeakspot = IsWeakspotHit(hit);

        // THE REAL FORMULA WHEREVER THE TARGET'S REAL HEALTH IS KNOWN: a hit pays the curve row's
        // full-break total scaled by the fraction of health it removed. Both invented multipliers
        // are gone with it, because the formula already contains them - a weak-spot hit does double
        // damage and therefore pays double on its own, and the hit that fells a tree pays for
        // exactly the health it took. See FortHarvestResources.AmountForHit.
        //
        // For a stem with no baked health the old flat model stands unchanged: full amount per hit,
        // with the two placeholder bonuses. The two models must not be mixed - scaling a payout by
        // health this server does not know is worse than paying the documented placeholder.
        var amount = FortHarvestResources.AmountForHit(hit.ActorPath, DamageFor(hit, playerState));

        if (amount == null) {
            // IsWeakspotHit is DERIVED (the four WeakSpot* physical materials the PAK actually
            // ships - see there); WeakspotBonusMultiplier, like FellingBonusMultiplier, is not -
            // only the DETECTION is ground truth here, not the payout size.
            var multiplier = 1;
            if (bFellingBonus) multiplier *= FellingBonusMultiplier;
            if (bJustHitWeakspot) multiplier *= WeakspotBonusMultiplier;
            amount = yield.Amount * multiplier;
        }

        FortHarvestResources.Grant(controller, yield.ItemPath, amount.Value);

        if (levelActor != null) {
            ReportHarvestedScenery(playerState, levelActor, ResourceTypeForItemPath(yield.ItemPath), amount.Value,
                                   levelActor.bDestroyed, bJustHitWeakspot);
        }
    }

    /// <summary>See Harvest's bFellingBonus parameter. Placeholder, like every other harvest amount in this project.</summary>
    private const int FellingBonusMultiplier = 3;

    /// <summary>Bonus for IsWeakspotHit - detection is derived, this multiplier is not (see IsWeakspotHit).</summary>
    private const int WeakspotBonusMultiplier = 2;

    /// <summary>EFortResourceType off the item path Harvest already resolved, rather than off a building's Material - a level-actor stand-in never has one (see ABuildingActor.Material's default).</summary>
    private static byte ResourceTypeForItemPath(string itemPath) {
        if (itemPath.Contains("WoodItemData", StringComparison.OrdinalIgnoreCase)) return 0;
        if (itemPath.Contains("StoneItemData", StringComparison.OrdinalIgnoreCase)) return 1;
        if (itemPath.Contains("MetalItemData", StringComparison.OrdinalIgnoreCase)) return 2;
        return 4;
    }

    /// <summary>
    ///     The scenery counterpart to ReportDamagedBuilding - same RPC, same HUD popup, just for a
    ///     DamageLevelActor stand-in instead of a player-placed piece, and with the ACTUAL granted
    ///     amount (bonuses included) rather than the hardcoded 0 a player build always sends (a
    ///     player-placed piece yields nothing when broken; scenery does, and the popup is supposed to
    ///     say how much).
    /// </summary>
    private static void ReportHarvestedScenery(APlayerState playerState, ABuildingActor levelActor,
                                               byte resourceType, int amount, bool bDestroyed, bool bJustHitWeakspot) {
        if (playerState.GetOwningController() is not APlayerController pc) return;
        var connection = pc.GetWorld()?.NetDriver?.ClientConnections
            .FirstOrDefault(candidate => candidate.PlayerController == pc);

        if (connection?.FindActorChannel(pc) is not { } pcChannel) return;

        pcChannel.SendClientReportDamagedResourceBuilding(levelActor, resourceType, amount, bDestroyed, bJustHitWeakspot);
    }

    /// <summary>
    ///     Refills the magazine from the reserve. Two things the server owes the client here, and
    ///     without either one the reload looks like it works and changes nothing: the weapon's
    ///     AmmoCount has to go up, and the reserve ammo ITEM has to go down.
    ///
    ///     ClipSize comes from the weapon's stat-table row (see FortWeaponActorClasses) - guessing
    ///     it would make every weapon behave like a rifle. ReloadWholeClip is the only reload type
    ///     modelled: the magazine is topped up in one go, which is what every Athena weapon in the
    ///     table uses.
    /// </summary>
    private static void Reload(APawn pawn, AFortWeapon weapon) {
        if (pawn.Controller is not APlayerController { WorldInventory: { } inventory }) return;

        var clipSize = FortWeaponActorClasses.ClipSizeFor(weapon.WeaponData);
        if (clipSize <= 0) return;

        var wanted = clipSize - weapon.AmmoCount;
        if (wanted <= 0) return; // already full - a real server has nothing to do either

        var ammoDefinition = FortWeaponActorClasses.AmmoItemFor(weapon.WeaponData);
        if (ammoDefinition == null) return;

        var reserve = inventory.Inventory.Items.FirstOrDefault(item => item.ItemDefinition == ammoDefinition);
        if (reserve == null || reserve.Count <= 0) {
            Console.WriteLine($"NativeRpcHandlers: Reload - no {ammoDefinition.GetFName()} in the inventory, nothing to load");
            return;
        }

        var loaded = Math.Min(wanted, reserve.Count);
        weapon.AmmoCount += loaded;
        reserve.Count -= loaded;

        if (reserve.Count > 0) inventory.Inventory.MarkItemDirty(reserve);
        else inventory.Inventory.Remove(reserve);

        var entry = inventory.Inventory.Items.FirstOrDefault(item => item.ItemGuid == weapon.ItemEntryGuid);
        if (entry != null) {
            entry.LoadedAmmo = weapon.AmmoCount;
            inventory.Inventory.MarkItemDirty(entry);
        }

        Console.WriteLine($"NativeRpcHandlers: reloaded {weapon.GetFName()} +{loaded} -> {weapon.AmmoCount}/{clipSize}, " +
                          $"{reserve.Count} spare left");
    }

    /// <summary>
    ///     Spends one round. The count lives in two places that have to agree: the weapon actor
    ///     (AFortWeapon::AmmoCount, what the client reads to draw the counter) and the inventory row
    ///     (FFortItemEntry::LoadedAmmo, what survives a weapon swap - the actor does not, it is
    ///     destroyed and rebuilt every time).
    ///
    ///     Nothing reloads yet, so a magazine simply runs dry and stays there.
    /// </summary>
    private static void ConsumeAmmo(APawn pawn, AFortWeapon weapon) {
        if (weapon.AmmoCount <= 0) return;

        weapon.AmmoCount--;

        if (pawn.Controller is APlayerController { WorldInventory: { } inventory }) {
            var entry = inventory.Inventory.Items.FirstOrDefault(item => item.ItemGuid == weapon.ItemEntryGuid);
            if (entry != null) {
                entry.LoadedAmmo = weapon.AmmoCount;
                inventory.Inventory.MarkItemDirty(entry);
            }
        }

        Console.WriteLine($"NativeRpcHandlers: shot fired - {weapon.GetFName()} ammo now {weapon.AmmoCount}");
    }

    /// <summary>The RPC table for a replicated sub-object, keyed by what the sub-object actually is.</summary>
    /// <summary>
    ///     `Server_SpawnProjectile(FVector Location, FRotator Direction)` - how a thrown consumable
    ///     actually gets thrown, and the payoff for the ability-instance work in
    ///     UGameplayAbilityInstance: this RPC is what that unblocked. The client does NOT spawn the
    ///     projectile; the ability's own spawn is behind a HasAuthority gate and it asks here.
    ///
    ///     THE SECOND PARAMETER IS A ROTATOR, NOT A VECTOR - which is the whole reason the layout
    ///     needed measuring. "Direction" reads like an FVector, and two raw FVectors would be
    ///     1 + 96 + 1 + 96 = 194 bits (SendPropertiesForRPC writes one presence bit per non-bool
    ///     parameter, RepLayout.cpp:5656). The field measured 133. The SDK settles the type: the
    ///     ubergraph hands this value to UFortAbilityTask_SpawnProjectileAndWait::SpawnProjectileAndWait,
    ///     whose SpawnRotation and SpawnDirection are both `const FRotator&` - and the ability passes
    ///     this ONE value as both.
    ///
    ///     So the payload is:
    ///         1 bit   Location present
    ///         96      Location - three raw floats (a plain FVector has no NetSerialize in 4.23)
    ///         1 bit   Direction present
    ///         3..51   Direction - FRotator::SerializeCompressedShort, which INTERLEAVES a presence
    ///                 bit with each axis: [bit][pitch16?][bit][yaw16?][bit][roll16?]
    ///
    ///     VARIABLE LENGTH, and the 133 is a coincidence of one axis being zero. Four live samples
    ///     all measured 133 because Roll was always exactly 0 (1+16+1+16+1 = 35); a rotator with all
    ///     three axes set is 51 and the field would be 149. Reading it with the real variable-length
    ///     reader rather than a fixed width is therefore not pedantry - a rolled throw would desync.
    ///
    ///     Verified against four throws: the decoded Yaw equalled the pawn's own yaw to 0.01 degrees
    ///     (one sample differed by 0.47, the pawn having turned between the RPC and the log line),
    ///     Pitch was -9 to -14 degrees (the upward throw arc), Roll exactly 0, and every sample ended
    ///     on the field's last bit. Location landed 70 units above the pawn and about 12 forward and
    ///     20 left of it - a muzzle, as expected.
    /// </summary>
    private static readonly FRpcParamDef[] ServerSpawnProjectileParams = {
        new("Location", ERpcParamKind.Vector),
        new("Direction", ERpcParamKind.Rotator)
    };

    private static readonly Dictionary<string, FRpcDef> ThrownAbilityRpcs = new() {
        ["Server_SpawnProjectile"] = new FRpcDef(
            "Server_SpawnProjectile",
            ServerSpawnProjectileParams,
            (actor, values) => {
                var location = (FVector) values[0]!;
                var direction = (FRotator) values[1]!;
                // The RPC arrives on the ability instance, whose outer is the PlayerState - the pawn
                // is one hop further out, and it is the pawn that knows which item is in hand.
                var pawn = actor is APlayerState { Owner: APlayerController pc } ? pc.Pawn : null;

                Console.WriteLine($"NativeRpcHandlers: Server_SpawnProjectile at {location} direction {direction}" +
                                  (pawn == null ? " (no pawn resolved)" : $" from pawn '{pawn.GetFName()}' at {pawn.GetActorLocation()}"));

                FortProjectileSystem.SpawnFor(pawn, location, direction);
            },
            expectsFullDecode: true)
    };

    public static Dictionary<string, FRpcDef>? GetForSubObject(UObject subObject) => subObject switch {
        UFortAbilitySystemComponent => AbilitySystemComponentRpcs,
        UFortControllerComponent_Interaction => InteractionComponentRpcs,
        UGameplayAbilityInstance => ThrownAbilityRpcs,
        _ => null
    };

    public static Dictionary<string, FRpcDef>? Get(AActor actor) => actor switch {
        APlayerController => PlayerControllerRpcs,
        APawn => PawnRpcs,
        AFortWeapon => WeaponRpcs,
        AFortBroadcastRemoteClientInfo => BroadcastRemoteClientInfoRpcs,
        _ => null
    };
}
