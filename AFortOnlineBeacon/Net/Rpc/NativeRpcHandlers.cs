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

                if (actor is APawn trackedPawn && actor.GetWorld()?.NetDriver is { } driver) {
                    trackedPawn.TrackMovementSpeed(clientLoc, driver.GetElapsedTime());
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

        // The client reporting where a shot went - one per bullet, and the only honest "a round
        // left the barrel" signal this server has. Real Fortnite spends ammo through the ability's
        // cost GameplayEffect, which needs ability instances to run; nothing here can run one, and
        // neither reference server has anything to copy because on an injected server the native
        // GAS does it.
        //
        // Only the FIRST parameter is declared. UActorChannel.ReadContentBlockFields always resyncs
        // to the field's own declared bit count afterwards, so stopping early cannot desync the
        // bunch - the same trick ServerMove's tail relies on. That matters here because the rest is
        // an FGameplayAbilityTargetDataHandle, whose contents this project cannot read yet.
        ["ServerSetReplicatedTargetData"] = new FRpcDef(
            "ServerSetReplicatedTargetData",
            new[] { new FRpcParamDef("AbilityHandle", ERpcParamKind.Int32) },
            (actor, values) => OnShotReported(actor, values[0], "ServerSetReplicatedTargetData")
        ),

        // The BATCHED form, and the one Fortnite's ranged fire ability actually uses. UE's
        // FScopedServerAbilityRPCBatcher folds an activation, its target data and its end into a
        // single FServerAbilityRPCBatch instead of three RPCs - so a weapon that batches never sends
        // ServerSetReplicatedTargetData at all. A live capture of our own server made that plain:
        // 41 ServerAbilityRPCBatch and 40 ServerEndAbility, against 2 loose ServerTryActivateAbility
        // and zero target data.
        //
        // FServerAbilityRPCBatch's members, in order: AbilitySpecHandle (an int32), PredictionKey,
        // TargetData, InputPressed, Ended (Started is RepSkip). Only the handle is declared here.
        // Careful: the ONE "send" bit belongs to the whole BatchInfo parameter, not to its members -
        // declaring a second parameter would read a presence bit that is not on the wire and desync
        // the read. The field's own bit count resyncs afterwards regardless.
        ["ServerAbilityRPCBatch"] = new FRpcDef(
            "ServerAbilityRPCBatch",
            new[] { new FRpcParamDef("AbilitySpecHandle", ERpcParamKind.Int32) },
            (actor, values) => OnShotReported(actor, values[0], "ServerAbilityRPCBatch")
        )
    };

    /// <summary>
    ///     One reported shot, from either the loose or the batched path - UE sends one or the other
    ///     per activation, never both, so this cannot double-count.
    /// </summary>
    private static void OnShotReported(AActor actor, object? handleValue, string source) {
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
        _ => null
    };
}
