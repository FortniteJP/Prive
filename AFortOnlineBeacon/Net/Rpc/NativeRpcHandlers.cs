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

    private static void SpawnDroppedPickup(APlayerController pc, FFortItemEntry item, int count) {
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

    public static Dictionary<string, FRpcDef>? Get(AActor actor) => actor switch {
        APlayerController => PlayerControllerRpcs,
        APawn => PawnRpcs,
        _ => null
    };
}
