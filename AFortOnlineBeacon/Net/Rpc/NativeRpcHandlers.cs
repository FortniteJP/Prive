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

                pawn.EquipInventoryItem(item);
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
                        ? GUClassArray.StaticClassForPath<ABuildingActor>(selectedPath)
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
                if (BuildingStructuralSupportSystem.IsOccupied(placeAt, buildYaw, placeType)) {
                    Console.WriteLine($"NativeRpcHandlers: ServerCreateBuildingActor - a {placeType} is already " +
                                      $"standing at {placeKey}, ignoring (duplicate send)");
                    return;
                }

                // Escape hatch (toggle via SKIP_BUILDING_SPAWN=1): log the decoded placement without
                // spawning anything, leaving the client's own locally-predicted ghost uncorrected.
                // Originally the only way to see where the client really wanted the piece, back when
                // the RPC's parameters could not be decoded; now just a way to watch placements go
                // by without building up world state.
                if (Environment.GetEnvironmentVariable("SKIP_BUILDING_SPAWN") == "1") {
                    Console.WriteLine($"NativeRpcHandlers: ServerCreateBuildingActor - SKIP_BUILDING_SPAWN=1, " +
                                      $"not spawning (class would have been {buildingClass.NativePackagePath}, " +
                                      $"{buildData})");
                    return;
                }

                var building = world.SpawnActor<ABuildingActor>(buildingClass, new FActorSpawnParameters {
                    ObjectFlags = EObjectFlags.RF_Transient
                });
                if (building == null) return;

                building.SetRole(ENetRole.ROLE_Authority);
                building.SetActorLocation(placeAt);
                building.SetActorRotation(new FRotator { Yaw = buildYaw });
                building.SetMirrored(buildData.bMirrored);
                building.SetReplicates(true);

                // HP/material/slot kind and the structural-support grid - see ABuildingActor and
                // BuildingStructuralSupportSystem's doc comments for what these feed. Register only
                // once the transform is set: the grid buckets on the piece's placed location.
                building.InitializeFromClass(buildingClass);

                // The fixed reference every future edit of this piece computes against - see
                // ABuildingActor.AnchorLocation/AnchorYaw. Set here, at the ONE point a piece's slot
                // is genuinely chosen, and carried forward unchanged by ServerEditBuildingActor.
                building.SetAnchor(placeAt, buildYaw);

                BuildingStructuralSupportSystem.Register(building);

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

                var newClass = GUClassArray.StaticClassForPath<ABuildingActor>(newClassPath);
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

                var building = world.SpawnActor<ABuildingActor>(newClass, new FActorSpawnParameters {
                    ObjectFlags = EObjectFlags.RF_Transient
                });
                if (building == null) return;

                building.SetRole(ENetRole.ROLE_Authority);
                building.SetActorLocation(placeAt);
                building.SetActorRotation(placeRot);
                building.SetMirrored(bMirrored);
                building.SetReplicates(true);
                building.InitializeFromClass(newClass);
                building.OverrideBuildingType(editedType);
                building.SetAnchor(oldBuilding.AnchorLocation, oldBuilding.AnchorYaw);
                BuildingStructuralSupportSystem.Register(building);

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
    /// <summary>How far in front of the pawn a dropped item lands, in Unreal units (~1.5m).</summary>
    private const float TossDistance = 150.0f;

    /// <summary>Slightly above the pawn's origin so the item is not half-buried in the ground.</summary>
    private const float TossHeight = 40.0f;

    /// <summary>
    ///     internal rather than private since Round 47: FortHarvestResources.Grant calls this
    ///     directly for a resource stack that has no more room (drop the overflow instead of
    ///     discarding it) - the exact same "put it on the ground as a real pickup" mechanism an
    ///     inventory drop already uses, not a second implementation of it.
    /// </summary>
    internal static void SpawnDroppedPickup(APlayerController pc, FFortItemEntry item, int count) {
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

        // In front of the player, not inside them. Real UE tosses the item along an arc; with no
        // toss to simulate, the least this server can do is not bury the pickup in the pawn's own
        // capsule, where the client's interaction query cannot see it and the player cannot walk
        // onto it. Yaw comes from the last move the client sent (APawn.LastClientViewRotation) -
        // the pawn's own Rotation is never updated, so that is the only heading available.
        var yawRadians = (pawn.LastClientViewRotation?.Yaw ?? 0.0f) * MathF.PI / 180.0f;
        var origin = pawn.GetActorLocation();
        var restLocation = new FVector {
            X = origin.X + MathF.Cos(yawRadians) * TossDistance,
            Y = origin.Y + MathF.Sin(yawRadians) * TossDistance,
            Z = origin.Z + TossHeight
        };

        pickup.SetActorLocation(restLocation);
        pickup.RestLocation = restLocation;
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

        pickup.SetReplicates(true);

        Console.WriteLine($"NativeRpcHandlers: SpawnDroppedPickup at {pickup.GetActorLocation()} " +
                          $"count={count} guid={item.ItemGuid} - waiting for ServerReplicateActors to open its channel");
    }

    private static readonly FRpcParamDef[] ServerMoveTimeStampPrefix = {
        new FRpcParamDef("TimeStamp", ERpcParamKind.Float)
    };

    private static readonly FRpcParamDef[] ServerMoveDualTimeStampPrefix = {
        new FRpcParamDef("TimeStamp0", ERpcParamKind.Float),
        new FRpcParamDef("InAccel0", ERpcParamKind.VectorQuantize10),
        new FRpcParamDef("PendingFlags", ERpcParamKind.Byte),
        new FRpcParamDef("View0", ERpcParamKind.UInt32),
        new FRpcParamDef("TimeStamp", ERpcParamKind.Float)
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
                if (entry == null) return;

                // A fresh entry again: this one belongs to an inventory now, and the pickup's copy
                // keeps whatever ReplicationId/Key it was given as a pickup - reusing it would carry
                // that state into a completely different fast array.
                inventory.Inventory.Add(new FFortItemEntry {
                    ItemDefinition = entry.ItemDefinition,
                    Count = entry.Count,
                    Durability = entry.Durability,
                    Level = entry.Level,
                    LoadedAmmo = entry.LoadedAmmo
                });

                // Handle 49 - what drives the client's pickup feedback. It is NOT what removes the
                // world actor: OnRep_bPickedUp only hides it. The actor goes away when its channel
                // closes, which Destroy() below arranges via ServerReplicateActors.
                pickup.bPickedUp = true;
                pickup.Destroy();

                Console.WriteLine($"NativeRpcHandlers: ServerHandlePickup guid={entry.ItemGuid} count={entry.Count} -> " +
                                  $"inventory now {inventory.Inventory.Count} item(s), " +
                                  $"ArrayReplicationKey={inventory.Inventory.ArrayReplicationKey}");
            }
        ),

        ["ServerMoveNoBase"] = new FRpcDef(
            "ServerMoveNoBase",
            new[] {
                new FRpcParamDef("TimeStamp", ERpcParamKind.Float),
                new FRpcParamDef("InAccel", ERpcParamKind.VectorQuantize10),
                new FRpcParamDef("ClientLoc", ERpcParamKind.VectorQuantize100),
                new FRpcParamDef("CompressedMoveFlags", ERpcParamKind.Byte),
                new FRpcParamDef("ClientRoll", ERpcParamKind.Byte),
                new FRpcParamDef("View", ERpcParamKind.UInt32),
                new FRpcParamDef("ClientMovementMode", ERpcParamKind.Byte)
            },
            (actor, values) => {
                if (values[0] is float timeStamp && actor is APawn movedPawn) movedPawn.MarkGoodMove(timeStamp);
                if (values[2] is not FVector clientLoc) return;

                actor.SetActorLocation(clientLoc);

                if (actor is APawn trackedPawn && actor.GetWorld()?.NetDriver is { } driver) {
                    trackedPawn.TrackMovementSpeed(clientLoc, driver.GetElapsedTime());
                    trackedPawn.TrackMoveFlags(values[3] as byte? ?? 0, values[6] as byte? ?? 0);
                    // Fall damage. Only this move variant carries a location AND a movement mode -
                    // the based ServerMove* variants below are decoded as a timestamp-only prefix -
                    // and a falling character is by definition not based on anything, so this is
                    // where a fall is visible.
                    trackedPawn.TrackFallDamage(clientLoc, values[6] as byte? ?? 0);
                }

                var view = values[5] is uint v ? FRotator.FromPackedView(v) : null;
                if (view != null && actor is APawn viewPawn) viewPawn.LastClientViewRotation = view;
                if (NetDebugLog.VerboseEnabled) Console.WriteLine($"NativeRpcHandlers: ServerMoveNoBase on {actor.GetFName()} TimeStamp={values[0]} ClientLoc={clientLoc} CompressedMoveFlags={values[3]} ClientRoll={values[4]} View={view} ClientMovementMode={values[6]}");
            }
        ),

        // ACharacter's other move RPCs. Only a PREFIX of each parameter list is declared here, which
        // is deliberate and safe: UActorChannel.ReadContentBlockFields always resyncs to the field's
        // own declared NumPayloadBits afterwards, so a handler that stops reading early cannot
        // desync the bunch. That is what makes ServerMove and the two based ServerMoveDual variants
        // decodable at all - their tails carry a UPrimitiveComponent* and an FName, neither of which
        // FRpcReader can read, but both sit AFTER everything we actually need.
        //
        // What we need is the move's timestamp, because that is the only thing ClientAckGoodMove
        // carries and one ack frees every client saved move up to it.
        ["ServerMove"] = MovePrefix("ServerMove", ServerMoveTimeStampPrefix),
        ["ServerMoveOld"] = MovePrefix("ServerMoveOld", ServerMoveTimeStampPrefix),

        // The Dual variants pack two moves per call: (TimeStamp0, InAccel0, PendingFlags, View0)
        // then the real, newer move starting with its own TimeStamp. Acking the older TimeStamp0
        // would leave the newer move unacknowledged, so decode through to the fifth parameter.
        // MarkGoodMove takes the max, so reading both is harmless either way.
        ["ServerMoveDual"] = MovePrefix("ServerMoveDual", ServerMoveDualTimeStampPrefix),
        ["ServerMoveDualNoBase"] = MovePrefix("ServerMoveDualNoBase", ServerMoveDualTimeStampPrefix),
        ["ServerMoveDualHybridRootMotion"] = MovePrefix("ServerMoveDualHybridRootMotion", ServerMoveDualTimeStampPrefix)
    };

    /// <summary>A move RPC we decode only far enough to learn which timestamps it acknowledges.</summary>
    private static FRpcDef MovePrefix(string name, FRpcParamDef[] paramDefs) => new(
        name,
        paramDefs,
        (actor, values) => {
            if (actor is not APawn pawn) return;

            foreach (var value in values) if (value is float timeStamp) pawn.MarkGoodMove(timeStamp);

            if (NetDebugLog.VerboseEnabled) Console.WriteLine($"NativeRpcHandlers: {name} on {actor.GetFName()} PendingAckGoodMoveTimeStamp={pawn.PendingAckGoodMoveTimeStamp}");
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

                // Accept it. Real UE runs the ability's own CanActivate/cost/cooldown checks here and
                // may answer ClientActivateAbilityFailed instead; this server has no ability
                // instances to run, so the honest thing it CAN do is confirm the prediction the
                // client already played - which is what unblocks the client from asking again.
                var predictionKey = values[2] as FPredictionKey ?? new FPredictionKey();
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
    }

    private static void OnShotReported(AActor actor, object? handleValue,
                                       FGameplayAbilityTargetDataHandle? targetData, string source) {
        ReportTargetData(actor, targetData, source);

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
    ///     Logs what the client reported hitting. Map geometry is almost never something this server
    ///     has a NetGUID for, so <see cref="FHitResult.Actor"/> is usually null and the exported path
    ///     is the only name available - "Athena_Tree_Medium_01_12" is a perfectly good answer even
    ///     though no object backs it here.
    /// </summary>
    private static void ReportTargetData(AActor actor, FGameplayAbilityTargetDataHandle? targetData, string source) {
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

                var damage = DamageFor(hit);
                var wasKilled = building.CurrentHitPoints <= damage;
                BuildingStructuralSupportSystem.ApplyDamage(building, damage);
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
                DamagePlayer(actor as APlayerState, victimPawn);
                continue;
            }

            var damagedLevelActor = (ABuildingActor?) null;
            var bFelledThisHit = DestructibleSceneryEnabled && DamageLevelActor(actor, hit, out damagedLevelActor);

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
    private static int DamageFor(FHitResult hit) =>
        IsWeakspotHit(hit) ? BuildingDamagePerHit * WeakspotDamageMultiplier : BuildingDamagePerHit;

    /// <summary>See DamageFor. Placeholder, like FellingBonusMultiplier/WeakspotBonusMultiplier.</summary>
    private const int WeakspotDamageMultiplier = 3;

    /// <summary>DESTRUCTIBLE_SCENERY=1 gates <see cref="DamageLevelActor"/> - see there for what it does and why it defaults off.</summary>
    private static bool DestructibleSceneryEnabled =>
        Environment.GetEnvironmentVariable("DESTRUCTIBLE_SCENERY") == "1";

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
            if (FortHarvestResources.ResolveHit(hit.ActorPath) == null) return false;

            try {
                levelActor = UAssetRegistry.GetOrCreateSubObject<ABuildingActor>(hit.ActorPath);
            } catch (Exception ex) {
                Console.WriteLine($"NativeRpcHandlers: DESTRUCTIBLE_SCENERY could not turn '{hit.ActorPath}' " +
                                  $"into a level-actor path - {ex.Message}");
                return false;
            }
        }

        levelActor.MarkAsLevelActor();
        levelActorOut = levelActor;

        if (!netDriver.NetworkObjectList.Contains(levelActor)) {
            levelActor.SetReplicates(true);
            netDriver.AddNetworkActor(levelActor);
            Console.WriteLine($"NativeRpcHandlers: DESTRUCTIBLE_SCENERY registered level actor " +
                              $"'{hit.ActorPath}' for replication - its channel opens on the next tick");
        }

        // This hit landed on the marker that's currently up (Round 36's PhysMaterial detection) -
        // that marker is spent. Reset before the ShouldSpawnWeakSpot check below so a replacement
        // starts revealing again immediately, the same way the very first one did - real Fortnite
        // relocates the weak spot once it's struck rather than leaving one in place all match.
        if (IsWeakspotHit(hit)) levelActor.ResetWeakSpot();

        if (levelActor.ShouldSpawnWeakSpot(world.TimeSeconds, WeakSpotRevealDelaySeconds)) {
            SpawnWeakSpot(playerState, levelActor, hit);
        }

        if (!levelActor.ApplyDamage(DamageFor(hit)) || !levelActor.MarkDestroyed()) return false;

        Console.WriteLine($"NativeRpcHandlers: DESTRUCTIBLE_SCENERY marked '{hit.ActorPath}' destroyed " +
                          $"({levelActor.CurrentHitPoints}/{levelActor.MaxHitPoints} HP)");
        return true;
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
    ///     delay would on a piece this cheap to destroy. See ABuildingActor.BaseHitPointsFor for why
    ///     the HP side of this tradeoff is still a placeholder.
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
    private static void DamagePlayer(APlayerState? instigator, APawn victimPawn) {
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

        FortDamageSystem.ApplyDamage(victim, FortDamageSystem.WeaponDamage,
                                     DeathCauseFor(instigator?.GetOwningPawn()?.CurrentWeapon), instigator);
    }

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

        // IsWeakspotHit is DERIVED (the four WeakSpot* physical materials the PAK actually ships -
        // see there); WeakspotBonusMultiplier, like FellingBonusMultiplier, is not - only the
        // DETECTION is ground truth here, not the payout size.
        var bJustHitWeakspot = IsWeakspotHit(hit);
        var multiplier = 1;
        if (bFellingBonus) multiplier *= FellingBonusMultiplier;
        if (bJustHitWeakspot) multiplier *= WeakspotBonusMultiplier;
        var amount = yield.Amount * multiplier;

        FortHarvestResources.Grant(controller, yield.ItemPath, amount);

        if (levelActor != null) {
            ReportHarvestedScenery(playerState, levelActor, ResourceTypeForItemPath(yield.ItemPath), amount,
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
    public static Dictionary<string, FRpcDef>? GetForSubObject(UObject subObject) => subObject switch {
        UFortAbilitySystemComponent => AbilitySystemComponentRpcs,
        _ => null
    };

    public static Dictionary<string, FRpcDef>? Get(AActor actor) => actor switch {
        APlayerController => PlayerControllerRpcs,
        APawn => PawnRpcs,
        AFortBroadcastRemoteClientInfo => BroadcastRemoteClientInfoRpcs,
        _ => null
    };
}
