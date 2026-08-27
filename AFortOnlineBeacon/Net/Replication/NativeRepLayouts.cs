using AFortOnlineBeacon.Core;
using AFortOnlineBeacon.Net.Actors;

namespace AFortOnlineBeacon.Net.Replication;

/// <summary>
///     Ground-truth FRepLayout property tables for the native engine classes AActor/AController/
///     APlayerController/APawn map to - counterpart to NativeClassNetCache, but for ordinary
///     UPROPERTY replication (FRepLayout) rather than RPC/legacy-field dispatch (ClassNetCache).
///
///     Handle numbering here is ground-truthed against LIVE wire probes against a real 10.40
///     client (2026-08-24), not UEDumper's dumped "repIndex" column - those two turned out to be
///     different things after several rounds of live testing kept contradicting the dump. The
///     probe technique: build a payload naming a specific absolute handle with a value 1 bit wide,
///     which is too narrow for almost anything real: the client's own
///     "ReceiveProperties_r: ... Property=X, ..., ReadHandle=N" error then names the actual
///     property occupying handle N. Doing this for N=6..20 on AGameState/APlayerController found:
///     RemoteRole=5, ReplicatedMovement=6, AttachmentReplication=7..12 (SIX handles, not one),
///     Owner=13, Role=14, Instigator=15, then (Controller) PlayerState=16/Pawn=17, then
///     (PlayerController) TargetViewRotation=18, then (GameState) GameModeClass=16/
///     SpectatorClass=17/ReplicatedWorldTimeSeconds=19.
///
///     AttachmentReplication spanning 6 handles is the whole reason every handle computed here
///     earlier this session (via the UEDumper repIndex dump, which showed AttachmentReplication as
///     a single repIndex=6 slot) was wrong by a consistent -5, INCLUDING the "fix" that made it
///     atomic. The dump's repIndex is apparently a per-property class index (counting the whole
///     AttachmentReplication array as one entry), not FRepLayout's flattened wire handle (which
///     counts one per array element) - UEDumper's repIndex column is NOT a reliable source for
///     handle numbering whenever an array (arrayDim>1) or a recursing struct is involved; only the
///     live client's own ReceiveProperties_r error text is authoritative. Ironically, this means
///     the ORIGINAL code before this session's edits (which recursed AttachmentReplication into 6
///     children, landing Role at handle 14) had the right handle COUNT all along, just for the
///     wrong conceptual reason (member recursion, not array elements) - the six live-probed handles
///     7-12 all report back the identical name "AttachmentReplication" (not six different member
///     names), which is what array elements look like, not struct-member recursion.
/// </summary>
internal static class NativeRepLayouts {
    private static FRepPropertyDef Reserved(string name, ERepPropertyKind kind = ERepPropertyKind.Bool) => new() {
        Name = name,
        Kind = kind
    };

    private static readonly FRepPropertyDef[] ActorProps = {
        Reserved("bHidden"),
        Reserved("bReplicateMovement"),
        Reserved("bTearOff"),
        Reserved("bCanBeDamaged"),
        new() {
            // NOT swapped on send: SendProperties_r (RepLayout.cpp) reads RepState->SavedRole /
            // SavedRemoteRole directly for these two cmds, and CompareRoleProperties populates
            // those Saved* fields straight from the same-named actual property (SavedRemoteRole =
            // Actor.RemoteRole, no cross-field substitution). The swap only happens on the
            // receiving end (ReceivePropertyHelper redirects the WRITE destination via
            // Parent.RoleSwapIndex) - a prior pass here mistakenly added a send-side swap too,
            // which was wrong per a direct re-read of RepLayout.cpp's SendProperties_r.
            Name = "RemoteRole",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = (int) ENetRole.ROLE_MAX,
            GetByteValue = obj => (byte) ((AActor) obj).RemoteRole
        },
        Reserved("ReplicatedMovement", ERepPropertyKind.StructAtomic),
        // Live-probed 2026-08-24: handles 7,8,9,10 (and, by the Owner=13/Role=14/Instigator=15
        // handles that follow) 11,12 too all resolve to "AttachmentReplication" on a real 10.40
        // client - six consecutive wire handles for what UEDumper's static dump shows as a single
        // repIndex slot. Modeled as StructRecurse with six identically-named placeholder children
        // purely to reserve six handles in a row (this project never sends any of them - the
        // actual per-element type doesn't matter here, only the count).
        new() {
            Name = "AttachmentReplication",
            Kind = ERepPropertyKind.StructRecurse,
            Children = new[] {
                Reserved("AttachmentReplication[0]"),
                Reserved("AttachmentReplication[1]"),
                Reserved("AttachmentReplication[2]"),
                Reserved("AttachmentReplication[3]"),
                Reserved("AttachmentReplication[4]"),
                Reserved("AttachmentReplication[5]")
            }
        },
        new() {
            // Live-probed handle 13.
            Name = "Owner",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((AActor) obj).Owner
        },
        new() {
            // Not swapped on send either - see RemoteRole above. Live-probed handle 14.
            Name = "Role",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = (int) ENetRole.ROLE_MAX,
            GetByteValue = obj => (byte) ((AActor) obj).Role
        },
        Reserved("Instigator") // live-probed handle 15
    };

    /// <summary>
    ///     AFortPickupAthena - a dropped item lying in the world. Derived with
    ///     `python Tools/RepHandles/rep_handles.py AFortPickupAthena`, which reports 56 handles and
    ///     shows AFortPickupAthena itself contributing NONE of them: everything comes from
    ///     AFortPickup, so the Athena subclass is a spawn-class choice, not a layout change.
    ///
    ///     PrimaryPickupItemEntry is the interesting part. It is an ordinary Net struct with no
    ///     native NetSerialize, so InitFromProperty_r recurses and gives every member its OWN wire
    ///     handle (17-36) - the exact opposite of how the same FFortItemEntry is written inside
    ///     AFortInventory::Inventory, where the FastArray serializes it as a bare struct body with
    ///     no handles at all. Same struct, two framings; what decides it is whether the parent
    ///     property is a custom delta.
    ///
    ///     PickupLocationData (38-47) is left reserved. It drives the toss arc; the actor's spawn
    ///     transform already says where the item is and bServerStoppedSimulation (53) says it is at
    ///     rest, which is all this server can honestly claim without simulating the toss.
    /// </summary>
    private static readonly FRepPropertyDef[] PickupProps = ActorProps.Concat(new FRepPropertyDef[] {
        Reserved("bRandomRotation"), // 16

        // 17-36: PrimaryPickupItemEntry, one handle per member, in the struct's declaration order.
        new() { Name = "PrimaryPickupItemEntry.Count", Kind = ERepPropertyKind.Int32, GetIntValue = obj => Entry(obj).Count },
        new() { Name = "PrimaryPickupItemEntry.ItemDefinition", Kind = ERepPropertyKind.ObjectRef, GetObjectValue = obj => Entry(obj).ItemDefinition },
        new() { Name = "PrimaryPickupItemEntry.OrderIndex", Kind = ERepPropertyKind.Int16, GetIntValue = obj => Entry(obj).OrderIndex },
        new() { Name = "PrimaryPickupItemEntry.Durability", Kind = ERepPropertyKind.Float, GetFloatValue = obj => Entry(obj).Durability },
        new() { Name = "PrimaryPickupItemEntry.Level", Kind = ERepPropertyKind.Int32, GetIntValue = obj => Entry(obj).Level },
        new() { Name = "PrimaryPickupItemEntry.LoadedAmmo", Kind = ERepPropertyKind.Int32, GetIntValue = obj => Entry(obj).LoadedAmmo },
        // FGuid is not one of RepLayout's atomic special cases, so its four int32s are four handles.
        new() { Name = "PrimaryPickupItemEntry.ItemGuid.A", Kind = ERepPropertyKind.Int32, GetIntValue = obj => GuidPart(obj, 0) },
        new() { Name = "PrimaryPickupItemEntry.ItemGuid.B", Kind = ERepPropertyKind.Int32, GetIntValue = obj => GuidPart(obj, 1) },
        new() { Name = "PrimaryPickupItemEntry.ItemGuid.C", Kind = ERepPropertyKind.Int32, GetIntValue = obj => GuidPart(obj, 2) },
        new() { Name = "PrimaryPickupItemEntry.ItemGuid.D", Kind = ERepPropertyKind.Int32, GetIntValue = obj => GuidPart(obj, 3) },
        new() { Name = "PrimaryPickupItemEntry.inventory_overflow_date", Kind = ERepPropertyKind.Bool, GetByteValue = obj => (byte) (Entry(obj).InventoryOverflowDate ? 1 : 0) },
        new() { Name = "PrimaryPickupItemEntry.bWasGifted", Kind = ERepPropertyKind.Bool, GetByteValue = obj => (byte) (Entry(obj).bWasGifted ? 1 : 0) },
        new() { Name = "PrimaryPickupItemEntry.bIsReplicatedCopy", Kind = ERepPropertyKind.Bool, GetByteValue = obj => (byte) (Entry(obj).bIsReplicatedCopy ? 1 : 0) },
        new() { Name = "PrimaryPickupItemEntry.bIsDirty", Kind = ERepPropertyKind.Bool, GetByteValue = obj => (byte) (Entry(obj).bIsDirty ? 1 : 0) },
        new() { Name = "PrimaryPickupItemEntry.bUpdateStatsOnCollection", Kind = ERepPropertyKind.Bool, GetByteValue = obj => (byte) (Entry(obj).bUpdateStatsOnCollection ? 1 : 0) },
        new() { Name = "PrimaryPickupItemEntry.StateValues", Kind = ERepPropertyKind.EmptyDynamicArray },
        new() { Name = "PrimaryPickupItemEntry.ParentInventory", Kind = ERepPropertyKind.ObjectRef, GetObjectValue = obj => Entry(obj).ParentInventory },
        new() { Name = "PrimaryPickupItemEntry.GameplayAbilitySpecHandle", Kind = ERepPropertyKind.Int32, GetIntValue = obj => Entry(obj).GameplayAbilitySpecHandle },
        new() { Name = "PrimaryPickupItemEntry.AlterationInstances", Kind = ERepPropertyKind.EmptyDynamicArray },
        new() { Name = "PrimaryPickupItemEntry.GenericAttributeValues", Kind = ERepPropertyKind.EmptyDynamicArray },

        Reserved("MultiItemPickupEntries", ERepPropertyKind.EmptyDynamicArray), // 37

        // 38-47: PickupLocationData - see the doc comment for why none of it is sent.
        Reserved("PickupLocationData.PickupTarget"),
        Reserved("PickupLocationData.CombineTarget"),
        Reserved("PickupLocationData.ItemOwner"),
        new() { Name = "PickupLocationData.LootInitialPosition", Kind = ERepPropertyKind.VectorQuantize10, GetVectorValue = obj => ((AFortPickup) obj).RestLocation },
        new() { Name = "PickupLocationData.LootFinalPosition", Kind = ERepPropertyKind.VectorQuantize10, GetVectorValue = obj => ((AFortPickup) obj).RestLocation },
        Reserved("PickupLocationData.FlyTime", ERepPropertyKind.Float),
        Reserved("PickupLocationData.StartDirection", ERepPropertyKind.StructAtomic),
        new() { Name = "PickupLocationData.FinalTossRestLocation", Kind = ERepPropertyKind.VectorQuantize10, GetVectorValue = obj => ((AFortPickup) obj).RestLocation },
        // EFortPickupTossState_MAX = 3 -> CeilLogTwo(3) = 2 bits.
        new() { Name = "PickupLocationData.TossState", Kind = ERepPropertyKind.ByteEnum, EnumMaxValue = (int) EFortPickupTossState.EFortPickupTossState_MAX, GetByteValue = obj => (byte) ((AFortPickup) obj).TossState },
        Reserved("PickupLocationData.bPlayPickupSound"),

        Reserved("OptionalOwnerID", ERepPropertyKind.Int32), // 48
        new() { Name = "bPickedUp", Kind = ERepPropertyKind.Bool, GetByteValue = obj => (byte) (((AFortPickup) obj).bPickedUp ? 1 : 0) },                            // 49
        new() { Name = "bTossedFromContainer", Kind = ERepPropertyKind.Bool, GetByteValue = obj => (byte) (((AFortPickup) obj).bTossedFromContainer ? 1 : 0) },      // 50
        Reserved("bForceHideMinimapIndicator"),                                                                                                                      // 51
        Reserved("bCombinePickupsWhenTossCompletes"),                                                                                                                // 52
        new() { Name = "bServerStoppedSimulation", Kind = ERepPropertyKind.Bool, GetByteValue = obj => (byte) (((AFortPickup) obj).bServerStoppedSimulation ? 1 : 0) }, // 53
        Reserved("ServerImpactSoundFlash", ERepPropertyKind.ByteEnum),                                                                                               // 54
        Reserved("PawnWhoDroppedPickup"),                                                                                                                            // 55
        Reserved("SpecialActorID", ERepPropertyKind.Name)                                                                                                            // 56
    }).ToArray();

    private static FFortItemEntry Entry(object obj) =>
        ((AFortPickup) obj).PrimaryPickupItemEntry
        ?? throw new InvalidOperationException("AFortPickup.PrimaryPickupItemEntry is null - nothing to replicate.");

    /// <summary>FGuid's four int32 members A/B/C/D, in declaration order.</summary>
    private static int GuidPart(object obj, int index) {
        var bytes = Entry(obj).ItemGuid.ToByteArray();
        return BitConverter.ToInt32(bytes, index * 4);
    }

    private static readonly FRepPropertyDef[] ControllerProps = ActorProps.Concat(new FRepPropertyDef[] {
        new() {
            // Handle 16 (ActorProps' 15 + 1) - not independently live-probed, but PlayerController's
            // own TargetViewRotation landing on the predicted handle 18 (16+Pawn17+TargetViewRotation18)
            // corroborates this base.
            Name = "PlayerState",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((AController) obj).PlayerState
        },
        new() {
            // Handle 17. AController::Pawn - the other half of the possession link, alongside the
            // Pawn's own Controller/Owner/PlayerState (see PawnProps).
            Name = "Pawn",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((AController) obj).Pawn
        }
    }).ToArray();

    /// <summary>
    ///     TargetViewRotation live-probed to handle 18 on 2026-08-24, confirming ControllerProps'
    ///     PlayerState=16/Pawn=17 base. SpawnLocation/bFailedToRespawn/bHasInitiallySpawned/
    ///     bHasServerFinishedLoading follow it in the same declaration order used before this
    ///     session's now-reverted AttachmentReplication detour (not independently re-verified, but
    ///     these were the ones "confirmed correct via a live UEDumper dump" earlier this session,
    ///     and only the ActorProps base offset was ever actually wrong).
    ///
    ///     Handles 23-51 are AFortPlayerController's own properties, NOT individually identified (the
    ///     SDK/NetFields.txt declaration order between bHasServerFinishedLoading and
    ///     DelayedQuickBarActions turned out to be a poor predictor of wire handles here, unlike
    ///     everywhere else this project has relied on it: only 5 dump entries separate them by
    ///     declaration, but a live client places a full 26 handles in between, and live-probing that
    ///     whole range wasn't practical). Handle 49 (DelayedQuickBarActions) and handle 52
    ///     (OverriddenBackpackSize) are both live-probe-confirmed directly, which pins this reserved
    ///     block to exactly 29 slots (handles 23-51).
    ///
    ///     WorldInventory (AFortPlayerController::WorldInventory) went through two wrong models
    ///     before landing here: first as a UObject default-subobject sent via a "sub-object content
    ///     block" (the real client rejected that - "Sub-object cannot be actor class" - proving
    ///     /Script/FortniteGame.FortInventory is actually an ACTOR class, not a UObject). It's now
    ///     AFortInventory, a real actor with its own channel (see AGameModeBase.Login/
    ///     UWorld.SpawnPlayActor), referenced here as a plain ObjectRef Cmd exactly like
    ///     AController.PlayerState already is - no export-path machinery needed since a
    ///     dynamically-spawned actor is never name-stable. Handle 50 is a best guess (matching the
    ///     live-probe work that narrowed it to 50 or 51 before the subobject detour) - NOT
    ///     independently reconfirmed since going back to this model live. If a live client error
    ///     ever names a different property at handle 50, trust that and fix this the same way
    ///     AttachmentReplication got fixed.
    ///
    ///     Everything AFortPlayerController declares after WorldInventory, and everything
    ///     FortPlayerControllerGameplay/Zone/PvP/Athena add on top, is deliberately NOT declared
    ///     here - this project never sends anything from those classes, so no Cmd needs to exist for
    ///     them.
    /// </summary>
    private static readonly FRepPropertyDef[] PlayerControllerProps = ControllerProps.Concat(
        new[] {
            Reserved("TargetViewRotation"), // live-probed handle 18
            Reserved("SpawnLocation"),
            Reserved("bFailedToRespawn"), // 20
            new FRepPropertyDef {
                // 21 - confirmed by Tools/RepHandles/rep_handles.py, which reproduces this table's
                // three live-probed anchors exactly (TargetViewRotation 18, WorldInventory 34, and
                // bCheatFly/bEnableShotLogging/bIsNearActiveEncounters 49/50/51).
                Name = "bHasInitiallySpawned",
                Kind = ERepPropertyKind.Bool,
                GetByteValue = obj => (byte) (((APlayerController) obj).bHasInitiallySpawned ? 1 : 0)
            },
            new FRepPropertyDef {
                Name = "bHasServerFinishedLoading",
                Kind = ERepPropertyKind.Bool,
                GetByteValue = obj => (byte) (((APlayerController) obj).bHasServerFinishedLoading ? 1 : 0)
            }
        }.Concat(new[] {
            // Handles 23-33, now individually identified rather than a block of anonymous filler.
            // Derived by combining the live truncated name probe (see HandleProbe) with Dumper-7's
            // struct layouts, then closing the arithmetic against two live-probed anchors:
            // bHasServerFinishedLoading = handle 22 and bCheatFly = handle 49. Parents 16-34 must
            // therefore span exactly 27 handles, and the per-parent counts below are the only
            // assignment that sums to 27 - which is what pins WorldInventory to handle 34.
            //
            // The Parent index reported by the name probe is what made this possible: FRepParentCmd
            // entries are one per replicated property in declaration order, so Parent maps 1:1 onto
            // NetFields.txt line order. Verified at both ends of the FortPlayerController section:
            // bFailedToRespawn (first entry) = Parent 14, bCheatFly (22nd entry) = Parent 35, i.e.
            // Parent = section position + 14 with no gaps - which also proves the dump lists every
            // replicated property in that range, in the right order.
            //
            // A recursing struct is ONE parent spanning several handles, and the client's error log
            // prints the PARENT's name for every inner cmd - which is finally what explains
            // AttachmentReplication reporting the same name at six consecutive handles.
            Reserved("IntensityGraphInfo.Timestamp"),        // 23 |
            Reserved("IntensityGraphInfo.DebugGraphData"),   // 24 | Parent 17, FAIDirectorDebugInfo
            Reserved("PIDValuesGraphInfo.Timestamp"),        // 25 |
            Reserved("PIDValuesGraphInfo.DebugGraphData"),   // 26 | Parent 18, same struct
            Reserved("PIDContributionsGraphInfo.Timestamp"),      // 27 |
            Reserved("PIDContributionsGraphInfo.DebugGraphData"), // 28 | Parent 19, same struct
            Reserved("bBuildFree"),             // 29, Parent 20
            Reserved("bCraftFree"),             // 30, Parent 21
            // Parent 22. FDelayedQuickBarActionContainer derives from FFastArraySerializer, so it is
            // a Custom Delta property and takes a single handle instead of recursing into its
            // members (recursing would give 2 - ArrayReplicationKey + Items - and the total would
            // then overshoot 27, which is the evidence it does not recurse). NOTE: an earlier
            // session had this property at handle 49 from the now-deleted value-feeding probe; that
            // was wrong, and 49 is really bCheatFly.
            Reserved("DelayedQuickBarActions"), // 31
            Reserved("PinnedSchematics"),       // 32, Parent 23, TArray<FString> - arrays are 1 top-level handle
            Reserved("bAutoEquipBetterItems"),  // 33, Parent 24
            new FRepPropertyDef {
                // Handle 34, Parent 25. AFortPlayerController::WorldInventory - a plain
                // UObjectProperty (AFortInventory*), so an ordinary ObjectRef Cmd, exactly like
                // AController.PlayerState at handle 16. AFortInventory is spawned as a real actor
                // with its own channel (AGameModeBase.Login / UWorld.SpawnPlayActor) because a live
                // client rejected the default-subobject model outright with "Sub-object cannot be
                // actor class".
                //
                // Every earlier attempt put this at handle 50 or 51 and failed identically no matter
                // what was sent there or which object was referenced. The reason is now known and is
                // not subtle: 50 and 51 are bEnableShotLogging and bIsNearActiveEncounters, two
                // 1-bit bools. Hand-decoding a real failing payload showed the client consumed
                // exactly 1 bit at handle 50, and UBoolProperty is the only UE type that reads 1
                // bit; the live name probe then confirmed both names directly.
                Name = "WorldInventory",
                Kind = ERepPropertyKind.ObjectRef,
                GetObjectValue = obj => ((APlayerController) obj).WorldInventory
            },

            // 35-51 are only here to carry the handle numbering as far as 52. Derived with
            // `python Tools/RepHandles/rep_handles.py AFortPlayerControllerAthena --from 33 --to 54`;
            // LatestRewardReport is one FFortRewardReport that recurses into six handles, and the
            // seven cheat bools at 45-51 are three separate offsets' worth of bitfield.
            Reserved("OutpostInventory"),                       // 35, 0x1648 - AFortInventory*
            Reserved("LatestRewardReport.MissionName"),         // 36, FText
            Reserved("LatestRewardReport.TheaterName"),         // 37, FText
            Reserved("LatestRewardReport.Difficulty"),          // 38, FText
            Reserved("LatestRewardReport.DifficultyValue", ERepPropertyKind.Float),   // 39
            Reserved("LatestRewardReport.RewardActivities", ERepPropertyKind.EmptyDynamicArray), // 40
            Reserved("LatestRewardReport.bIsFinalized"),        // 41
            Reserved("UpdatedObjectiveStats", ERepPropertyKind.EmptyDynamicArray),    // 42
            Reserved("bHasUnsavedPrimaryMissionProgress"),      // 43
            Reserved("TutorialCompletedState", ERepPropertyKind.ByteEnum),            // 44
            Reserved("bCheatGhost"),                            // 45
            Reserved("bInfiniteAmmo"),                          // 46
            Reserved("bInfiniteDurability"),                    // 47
            Reserved("bNoCoolDown"),                            // 48
            Reserved("bCheatFly"),                              // 49
            Reserved("bEnableShotLogging"),                     // 50
            Reserved("bIsNearActiveEncounters"),                // 51

            new FRepPropertyDef {
                // 52, 0x1B58. Live-probe confirmed on 2026-08-25 and re-derived from the SDK.
                //
                // The client's inventory capacity, and the reason it refused every pickup with
                // "inventory full" while holding a single item: this is a Net property that was
                // never sent, so the client read the zero it was constructed with. Items the SERVER
                // pushes into the inventory land regardless - the capacity check only runs when the
                // client tries to pick something up, which is exactly the shape of the symptom.
                //
                // Both reference servers set it explicitly in their login path:
                // Project-Reboot-3.0 (FortGameModeAthena.cpp:1768) uses 5, raider3.5 (Hooks.h:75)
                // uses 100. 5 is Battle Royale's real backpack.
                Name = "OverriddenBackpackSize",
                Kind = ERepPropertyKind.Int32,
                GetIntValue = obj => ((APlayerController) obj).OverriddenBackpackSize
            }
        })
        // Nothing past handle 52 is declared - this project sends none of it.
    ).ToArray();

    /// <summary>
    ///     APawn's own properties, after AActor's 15. All three come straight from
    ///     APawn::PossessedBy on a real server (see AController.Possess) and none of them were being
    ///     sent before - a client could see the pawn actor but nothing tying it to its controller.
    ///
    ///     Handles 19-61 exist only to carry the numbering as far as CurrentWeapon at 62. They are
    ///     the whole ACharacter -> AFortPawn stretch of the real chain this project's single APawn
    ///     stands in for, GENERATED by
    ///     `python Tools/RepHandles/rep_handles.py AFortPlayerPawnAthena --from 19 --to 61`. Note how
    ///     much of it is nested struct members rather than properties: ReplicatedBasedMovement costs
    ///     7 handles and RepRootMotion 12, because neither struct is STRUCT_NetSerializeNative, so
    ///     FRepLayout::InitFromProperty_r recurses into every member.
    ///
    ///     Which makes the OPPOSITE mistake cost exactly as much, and this table paid for it.
    ///     RepRootMotion's own AuthoritativeRootMotion (43) IS atomic - FRootMotionSourceGroup sets
    ///     WithNetSerializer in UE 4.23's RootMotionSource.h:865 - so recursing into its 5 members
    ///     put every handle after it 4 too high, and a real client dropped the connection outright.
    ///     It was caught the only way a handle error ever can be, by the client naming the property
    ///     it found where we wrote: "ReceiveProperties_r: Failed to receive property, BunchIsError -
    ///     Property=LocalSpin, Parent=43, Cmd=65, ReadHandle=66" - LocalSpin at 66 where this table
    ///     had predicted 70. Parent=43 corroborated the rest: counting ClassReps, index 43 IS
    ///     LocalSpin, so only the Cmd expansion was ever wrong.
    /// </summary>
    private static readonly FRepPropertyDef[] PawnProps = ActorProps.Concat(new[] {
        Reserved("RemoteViewPitch"), // 16
        new FRepPropertyDef {
            Name = "PlayerState", // 17
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APawn) obj).PlayerState
        },
        new FRepPropertyDef {
            Name = "Controller", // 18
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APawn) obj).Controller
        },
        // ---------------------------------- ACharacter ----------------------------------
        Reserved("ReplicatedBasedMovement.MovementBase"), // 19, 0x02C0 - class UPrimitiveComponent*
        Reserved("ReplicatedBasedMovement.BoneName"), // 20, 0x02C0 - class FName
        Reserved("ReplicatedBasedMovement.Location"), // 21, 0x02C0 - atomic struct FVector_NetQuantize100
        Reserved("ReplicatedBasedMovement.Rotation"), // 22, 0x02C0 - atomic struct FRotator
        Reserved("ReplicatedBasedMovement.bServerHasBaseComponent"), // 23, 0x02C0 - bool
        Reserved("ReplicatedBasedMovement.bRelativeRotation"), // 24, 0x02C0 - bool
        Reserved("ReplicatedBasedMovement.bServerHasVelocity"), // 25, 0x02C0 - bool
        Reserved("AnimRootMotionTranslationScale"), // 26, 0x02F0 - float
        Reserved("ReplicatedServerLastTransformUpdateTimeStamp"), // 27, 0x0310 - float
        Reserved("ReplayLastTransformUpdateTimeStamp"), // 28, 0x0314 - float
        Reserved("ReplicatedMovementMode"), // 29, 0x0318 - uint8
        Reserved("bIsCrouched"), // 30, 0x0320 - uint8
        Reserved("bProxyIsJumpForceApplied"), // 31, 0x0320 - uint8
        Reserved("JumpMaxHoldTime"), // 32, 0x0330 - float
        Reserved("JumpMaxCount"), // 33, 0x0334 - int32
        Reserved("RepRootMotion.bIsActive"), // 34, 0x04D0 - bool
        Reserved("RepRootMotion.AnimMontage"), // 35, 0x04D0 - class UAnimMontage*
        Reserved("RepRootMotion.Position"), // 36, 0x04D0 - float
        Reserved("RepRootMotion.Location"), // 37, 0x04D0 - atomic struct FVector_NetQuantize100
        Reserved("RepRootMotion.Rotation"), // 38, 0x04D0 - atomic struct FRotator
        Reserved("RepRootMotion.MovementBase"), // 39, 0x04D0 - class UPrimitiveComponent*
        Reserved("RepRootMotion.MovementBaseBoneName"), // 40, 0x04D0 - class FName
        Reserved("RepRootMotion.bRelativePosition"), // 41, 0x04D0 - bool
        Reserved("RepRootMotion.bRelativeRotation"), // 42, 0x04D0 - bool
        Reserved("RepRootMotion.AuthoritativeRootMotion"), // 43, 0x04D0 - atomic struct FRootMotionSourceGroup
        Reserved("RepRootMotion.Acceleration"), // 44, 0x04D0 - atomic struct FVector_NetQuantize10
        Reserved("RepRootMotion.LinearVelocity"), // 45, 0x04D0 - atomic struct FVector_NetQuantize10

        // ---------------------------------- AFortPawn ----------------------------------
        Reserved("bIgnoreNextFallingDamage"), // 46, 0x06A8 - uint8
        Reserved("bIsDying"), // 47, 0x06A8 - uint8
        Reserved("bIsHiddenForDeath"), // 48, 0x06A8 - uint8
        Reserved("bIsKnockedback"), // 49, 0x06A8 - uint8
        Reserved("bIsStaggered"), // 50, 0x06A8 - uint8
        Reserved("bIsInvulnerable"), // 51, 0x06A9 - uint8
        Reserved("bMovingEmote"), // 52, 0x06A9 - uint8
        Reserved("bMovingEmoteForwardOnly"), // 53, 0x06A9 - uint8
        Reserved("bSpotted"), // 54, 0x06A9 - uint8
        Reserved("bWeaponActivated"), // 55, 0x06AA - uint8
        Reserved("bWeaponHolstered"), // 56, 0x06AE - uint8
        Reserved("bIsDBNO"), // 57, 0x06AF - uint8
        Reserved("CurrentMovementStyle"), // 58, 0x06B0 - T1ByteEnum<EFortMovementStyle>
        Reserved("TeleportCounter"), // 59, 0x06B2 - uint8
        Reserved("StormShieldComponent"), // 60, 0x06D0 - class UFortStormShieldComponent*
        Reserved("PawnUniqueID"), // 61, 0x06F8 - int32

        new FRepPropertyDef {
            // 62, 0x0700 - class AFortWeapon*. What the pawn is holding. The weapon is a separate
            // replicated actor on its own channel (see AFortWeapon), so this is an ordinary
            // ObjectRef to a dynamic NetGUID - the same shape as AController::Pawn.
            //
            // Ordering is handled for free by UNetDriver.ServerReplicateActors: its
            // OpenChannelsForNewlyRelevantActors pass runs BEFORE the per-channel update walk, so a
            // weapon spawned while dispatching ServerExecuteInventoryItem already has a channel
            // (and therefore a NetGUID the client can resolve) by the time this handle goes out.
            Name = "CurrentWeapon",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APawn) obj).CurrentWeapon
        }
    }).ToArray();

    /// <summary>
    ///     AFortWeapon's own properties, after AActor's 15 - DERIVED by
    ///     `python Tools/RepHandles/rep_handles.py AFortWeapon`, which reproduces every live-probed
    ///     handle elsewhere in this file.
    ///
    ///     Declared only as far as AmmoCount (28); the ability-system handles after it (29-35) are
    ///     server-side spec handles into a UAbilitySystemComponent this project does not have, so
    ///     there is nothing honest to put in them.
    ///
    ///     One layout covers every weapon class. AFortWeaponRanged and AFortWeaponPickaxeAthena both
    ///     append their own properties after these, but nothing here sends any of them, and a
    ///     subclass only ever APPENDS - so an assault rifle and a pickaxe agree on handles 1-28.
    /// </summary>
    private static readonly FRepPropertyDef[] WeaponProps = ActorProps.Concat(new FRepPropertyDef[] {
        Reserved("bIsEquippingWeapon"),  // 16, 0x0248 - bool
        Reserved("bIsReloadingWeapon"),  // 17, 0x0249 - bool
        Reserved("bIsChargingWeapon"),   // 18, 0x024A - bool
        new() {
            // 19, 0x0278 - class UFortWeaponItemDefinition*. The WID_ asset, exported by path.
            Name = "WeaponData",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((AFortWeapon) obj).WeaponData
        },
        Reserved("CosmeticOverrideWeaponData"),                                  // 20, 0x0280
        Reserved("EquippedWeaponDestroyWrapperRepCounter", ERepPropertyKind.ByteEnum), // 21, 0x02A9 - uint8
        Reserved("LastFireTimeVerified", ERepPropertyKind.Float),                // 22, 0x06B8

        // 23-26: FGuid is not one of RepLayout's atomic special cases, so its four int32s are four
        // handles - the same shape as FFortItemEntry::ItemGuid in PickupProps.
        new() { Name = "ItemEntryGuid.A", Kind = ERepPropertyKind.Int32, GetIntValue = obj => WeaponGuidPart(obj, 0) },
        new() { Name = "ItemEntryGuid.B", Kind = ERepPropertyKind.Int32, GetIntValue = obj => WeaponGuidPart(obj, 1) },
        new() { Name = "ItemEntryGuid.C", Kind = ERepPropertyKind.Int32, GetIntValue = obj => WeaponGuidPart(obj, 2) },
        new() { Name = "ItemEntryGuid.D", Kind = ERepPropertyKind.Int32, GetIntValue = obj => WeaponGuidPart(obj, 3) },

        new() { Name = "WeaponLevel", Kind = ERepPropertyKind.Int32, GetIntValue = obj => ((AFortWeapon) obj).WeaponLevel }, // 27, 0x0724
        new() { Name = "AmmoCount", Kind = ERepPropertyKind.Int32, GetIntValue = obj => ((AFortWeapon) obj).AmmoCount }      // 28, 0x0728
    }).ToArray();

    /// <summary>AFortWeapon::ItemEntryGuid's four int32 members A/B/C/D, in declaration order.</summary>
    private static int WeaponGuidPart(object obj, int index) {
        var bytes = ((AFortWeapon) obj).ItemEntryGuid.ToByteArray();
        return BitConverter.ToInt32(bytes, index * 4);
    }

    /// <summary>
    ///     AGameState : AInfo : AActor - AInfo itself adds no properties, so AGameStateBase's own
    ///     Net properties start right after ActorProps. GameModeClass=16 and SpectatorClass=17 were
    ///     live-probed directly on 2026-08-24; ReplicatedWorldTimeSeconds=19 was also live-probed
    ///     directly, which pins bReplicatedHasBegunPlay=18 by elimination (the only slot between
    ///     them, matching GameStateBase.cpp's own declaration order). MatchState (handle 20, an
    ///     FName - see AGameState.MatchState) follows ReplicatedWorldTimeSeconds per
    ///     GameStateBase.cpp's own DOREPLIFETIME order. Nothing past MatchState
    ///     (AGameState/AFortGameState*/AFortGameStateAthena's own many properties) is declared,
    ///     since this project never sends anything from those classes.
    /// </summary>
    private static readonly FRepPropertyDef[] GameStateProps = ActorProps.Concat(new[] {
        Reserved("GameModeClass"), // live-probed handle 16
        Reserved("SpectatorClass"), // live-probed handle 17
        new() {
            Name = "bReplicatedHasBegunPlay",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((AGameState) obj).bReplicatedHasBegunPlay ? 1 : 0)
        },
        new FRepPropertyDef {
            // 19, live-probed. AGameStateBase::ReplicatedWorldTimeSeconds - see AGameState for why
            // the client cannot evaluate any of the server's deadlines without it.
            Name = "ReplicatedWorldTimeSeconds",
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AGameState) obj).ReplicatedWorldTimeSeconds
        },
        new() {
            // 20, offset 0x258 - live-probed.
            Name = "MatchState",
            Kind = ERepPropertyKind.Name,
            GetNameValue = obj => ((AGameState) obj).MatchState
        },
        Reserved("ElapsedTime"), // 21, offset 0x268 - AGameState's other property. Reserved entries only
                                // hold a handle number; their Kind never reaches the wire.
        new() {
            // 22, offset 0x2A0. AFortGameStateBase (the class directly above AGameState in
            // Fortnite's chain) has exactly TWO net properties - FortTimeOfDayManager @0x2A0 and
            // StormShield @0x2A8 - so under the offset-ascending rule they are simply 22 and 23.
            // The four AGameStateBase handles above (16-19) and MatchState (20) are all live-probed
            // and match this derivation exactly, which is what makes 22 trustworthy without its own
            // probe.
            //
            // This is the property Athena's loading screen blocks on - see AFortTimeOfDayManager.
            Name = "FortTimeOfDayManager",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((AGameState) obj).FortTimeOfDayManager
        }
    }).Concat(new FRepPropertyDef[] {
        // Handles 23-157, covering AFortGameStateBase's second property and the whole
        // AFortGameStateBase -> AFortGameState -> AFortGameState_InGame -> AFortGameStateZone ->
        // AFortGameStatePvP -> AFortGameStateAthena chain up to bGameModeWillSkipAircraft.
        //
        // GENERATED by Tools/RepHandles/rep_handles.py against the Dumper-7 10.40 SDK. That script
        // reimplements UClass::SetUpRuntimeReplicationData (own CPF_Net properties per class,
        // sorted by offset with name as tie-break, appended base-first, ArrayDim entries each) plus
        // FRepLayout::InitFromProperty_r (a TArray is ONE handle; a struct is one handle only if it
        // is STRUCT_NetSerializeNative, otherwise it recurses into every non-RepSkip member). It
        // reproduces, with no fudging, all 15 live-probed AActor handles, AGameStateBase 16-19,
        // MatchState 20, FortTimeOfDayManager 22 and the entire live-checked APlayerState table -
        // which is the evidence that the untested numbers here are right.
        //
        // NOTE the FastArraySerializer/custom-delta members below (CurrentPlaylistInfo.*,
        // GameMemberInfoArray.Members, ActiveGameplayModifiers.*, SpawnMachineRepData.*). They DO
        // consume handles - InitFromClass runs InitFromProperty_r for every ClassReps entry
        // regardless - but they must never be named in a changed set: UE sends those through
        // UActorChannel::SendCustomDeltaProperty as ClassNetCache-indexed fields, not through the
        // handle stream at all.

        // ---------------------------------- AFortGameStateBase ----------------------------------
        Reserved("StormShield"), // 23, 0x02A8 - class AFortMissionStormShield*

        // ---------------------------------- AFortGameState ----------------------------------
        Reserved("CurrentWUID"), // 24, 0x02B0 - class FString
        Reserved("ParTime"), // 25, 0x02C0 - int32
        Reserved("WorldLevel"), // 26, 0x02C4 - int32
        Reserved("CraftingBonus"), // 27, 0x02C8 - int32
        Reserved("CurrentReadyToContinueTimer"), // 28, 0x02CC - float
        Reserved("TeamCount"), // 29, 0x02D0 - int32
        Reserved("bDBNOEnabledForGameMode"), // 30, 0x02D4 - bool
        Reserved("GameFlagData"), // 31, 0x02D8 - uint32
        Reserved("PoiManager"), // 32, 0x02E0 - class AFortPoiManager*
        Reserved("bPlayerRespawningBlocked_Temporarily"), // 33, 0x0320 - bool
        Reserved("AdditionalPlaylistLevelsStreamed"), // 34, 0x0338 - DynamicArray TArray<class FName>
        Reserved("WorldDaysElapsed"), // 35, 0x0348 - int32
        Reserved("FeedbackManager"), // 36, 0x0368 - class AFortFeedbackManager*
        Reserved("MissionManager"), // 37, 0x0370 - class AFortMissionManager*
        Reserved("AnnouncementManager"), // 38, 0x0378 - class AFortClientAnnouncementManager*
        new FRepPropertyDef {
            // 39, offset 0x0390 - class AFortWorldManager*. The single property the client's in-game
            // UI state is gated on; see AGameState.WorldManager for the disassembly that proves it.
            Name = "WorldManager",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((AGameState) obj).WorldManager
        },
        Reserved("GameplayState"), // 40, 0x0398 - T1ByteEnum<EFortGameplayState>
        Reserved("MusicManagerSubclass"), // 41, 0x03A0 - TSubclassOf<class AFortMusicManager>
        Reserved("MusicManagerBank"), // 42, 0x03A8 - class UFortMusicManagerBank*
        Reserved("GameSessionId"), // 43, 0x03B8 - class FString
        Reserved("PawnForReplayRelevancy"), // 44, 0x03E8 - class AFortPawn*
        Reserved("RecorderPlayerState"), // 45, 0x03F0 - class AFortPlayerState*
        Reserved("VisibilityManager"), // 46, 0x0418 - class AFortVisibilityManager*
        Reserved("Teams"), // 47, 0x0438 - DynamicArray TArray<class AFortTeamInfo*>
        Reserved("PendingTeamChangeRequests"), // 48, 0x0450 - DynamicArray TArray<struct FTeamChangeRequest>
        Reserved("TreasureChestInfos"), // 49, 0x0700 - DynamicArray TArray<struct FBuildingContainerDebugInfo>
        Reserved("AmmoBoxInfos"), // 50, 0x0710 - DynamicArray TArray<struct FBuildingContainerDebugInfo>

        // ---------------------------------- AFortGameStateZone ----------------------------------
        Reserved("WaitingToLeaveZoneTimeLeft"), // 51, 0x0724 - int32
        Reserved("HostilityMeterPercent"), // 52, 0x0728 - float
        Reserved("IntensityPercent"), // 53, 0x072C - float
        Reserved("SpawnPointsCap"), // 54, 0x0730 - int32
        Reserved("SpawnPointsAllocated"), // 55, 0x0734 - int32
        Reserved("MaxTotalAI"), // 56, 0x0738 - int32
        Reserved("MaxEncounterAI"), // 57, 0x073C - int32
        Reserved("MaxEncounterSP"), // 58, 0x0740 - int32
        Reserved("ReplicatedMontageMap.Mappings"), // 59, 0x0748 - DynamicArray TArray<struct FReplicatedMontageIndexPair>
        Reserved("AllSpawnGroupUpgradeModifierDefs"), // 60, 0x0760 - DynamicArray TArray<class UFortGameplayModifierItemDefinition*>
        Reserved("CompletionResult"), // 61, 0x0770 - EFortCompletionResult
        Reserved("PlayerBuildingSkillLevel"), // 62, 0x07C4 - float
        Reserved("PlayerSharedMaxTrapAttributes"), // 63, 0x07C8 - DynamicArray TArray<float>
        Reserved("ExplicitGloballyBlockedAbilityTags"), // 64, 0x0910 - atomic struct FGameplayTagContainer
        Reserved("bInvitesRestricted"), // 65, 0x09D0 - bool
        Reserved("bDBNODeathEnabled"), // 66, 0x09D1 - bool
        Reserved("ServerGameplayTagIndexHash"), // 67, 0x09D4 - uint32
        Reserved("TotalPlayerStructures"), // 68, 0x0A54 - int32
        Reserved("MaxPlayerStructures"), // 69, 0x0A58 - int32
        Reserved("bGlobalCeaseFire"), // 70, 0x0A5C - bool
        Reserved("GlobalEnvironmentAbilityActor"), // 71, 0x0A78 - class AFortGlobalEnvironmentAbilityActor*
        Reserved("ActiveGameplayModifiers.Items"), // 72, 0x0A90 - DynamicArray TArray<struct FActiveGameplayModifier>
        Reserved("ActiveGameplayModifiers.DeferredGameplayModifiers"), // 73, 0x0A90 - DynamicArray TArray<struct FActiveGameplayModifier>
        Reserved("ZoneDifficultyInfoRow.DataTable"), // 74, 0x0BD0 - class UDataTable*
        Reserved("ZoneDifficultyInfoRow.RowName"), // 75, 0x0BD0 - class FName
        Reserved("ZoneTheme"), // 76, 0x0BE0 - class UFortZoneTheme*
        Reserved("MissionGeneratorClass"), // 77, 0x0BE8 - TSoftClassPtr<class UClass>
        Reserved("MissionRewards"), // 78, 0x0C10 - DynamicArray TArray<struct FFortItemQuantityPair>
        Reserved("DifficultyIncreaseRewards"), // 79, 0x0C20 - DynamicArray TArray<struct FFortZoneDifficultyIncreaseRewardData>
        Reserved("MissionAlertData.MissionAlertRewards"), // 80, 0x0C30 - DynamicArray TArray<struct FFortItemQuantityPair>
        Reserved("MissionAlertData.MissionAlertCategoryName"), // 81, 0x0C30 - class FString
        Reserved("MissionAlertData.MissionAlertID"), // 82, 0x0C30 - class FString
        Reserved("ClientPreloadMissionClasses"), // 83, 0x0CF0 - DynamicArray TArray<struct FSoftObjectPath>
        Reserved("ThreatVisualsManager"), // 84, 0x0D00 - class AFortThreatVisualsManager*
        Reserved("ThreatParticleActor"), // 85, 0x0D08 - class AFortThreatParticleActor*
        Reserved("GameDifficulty"), // 86, 0x0D2C - float
        Reserved("bIsGroupContent"), // 87, 0x0D34 - bool
        Reserved("DifficultyIncreaseRewardTier"), // 88, 0x0D38 - int32
        Reserved("UIMapManager"), // 89, 0x0DF0 - class AFortInGameMapManager*
        Reserved("TheaterUniqueId"), // 90, 0x0E30 - class FString
        Reserved("MissionLogDebugString"), // 91, 0x0E40 - class FString
        Reserved("bAllowBuildingAtLayoutRequirements"), // 92, 0x0E6C - bool
        Reserved("bAllowBuildingWithoutLayoutRequirements"), // 93, 0x0E6D - bool
        Reserved("bAllowLayoutRequirementsFeature"), // 94, 0x0E6E - bool
        Reserved("NumSurvivorsRescued"), // 95, 0x0E7C - int32
        Reserved("GameplayVotesArray"), // 96, 0x0ED8 - DynamicArray TArray<struct FVoteData>
        Reserved("CreativeRealEstatePlotManager"), // 97, 0x0F48 - class AFortCreativeRealEstatePlotManager*

        // ---------------------------------- AFortGameStateAthena ----------------------------------
        Reserved("PlaylistTimeRemaining"), // 98, 0x1170 - int32
        Reserved("TotalFinalCountdownTime"), // 99, 0x1178 - int32
        Reserved("bPlaylistStoppedSafeZonePhases"), // 100, 0x117D - bool
        Reserved("bSafeZonePaused"), // 101, 0x117E - bool
        Reserved("bSkyTubesShuttingDown"), // 102, 0x117F - bool
        Reserved("bSkyTubesDisabled"), // 103, 0x1180 - bool
        Reserved("ServerChangelistNumber"), // 104, 0x118C - int32
        Reserved("SpecialActorData"), // 105, 0x1190 - class AFortSpecialActorReplicationInfo*
        Reserved("ReplOverrideData"), // 106, 0x1198 - class AFortPropertyOverrideReplShared*
        Reserved("bIsInCountdown"), // 107, 0x1261 - bool
        Reserved("bIsInFinalCountdown"), // 108, 0x1262 - bool
        new() {
            // 109, offset 0x1264 - float.
            Name = "WarmupCountdownStartTime",
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AGameState) obj).WarmupCountdownStartTime
        },
        new() {
            // 110, offset 0x1268 - float.
            Name = "WarmupCountdownEndTime",
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AGameState) obj).WarmupCountdownEndTime
        },
        new() {
            // 111, offset 0x126C - float.
            Name = "AircraftStartTime",
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AGameState) obj).AircraftStartTime
        },
        Reserved("SafeZonesStartTime"), // 112, 0x1270 - float
        Reserved("EndGameStartTime"), // 113, 0x1274 - float
        Reserved("EndGameKickPlayerTime"), // 114, 0x1278 - float
        new() {
            // 115, offset 0x127C - int32.
            Name = "TotalPlayers",
            Kind = ERepPropertyKind.Int32,
            GetIntValue = obj => ((AGameState) obj).TotalPlayers
        },
        new() {
            // 116, offset 0x1280 - int32.
            Name = "PlayersLeft",
            Kind = ERepPropertyKind.Int32,
            GetIntValue = obj => ((AGameState) obj).PlayersLeft
        },
        Reserved("ClientVehicleClassesToLoad"), // 117, 0x1288 - DynamicArray TArray<class UClass*>
        Reserved("bCheatRespawnEnabled"), // 118, 0x1298 - bool
        Reserved("StormCapState"), // 119, 0x1299 - EAthenaStormCapState
        Reserved("DamageForStormCapMarking"), // 120, 0x12A0 - float
        Reserved("TeamXPlayersLeft"), // 121, 0x12A8 - DynamicArray TArray<int32>
        Reserved("WinningPlayerList"), // 122, 0x12B8 - DynamicArray TArray<struct FFortWinnerPlayerData>
        Reserved("TeamsLeft"), // 123, 0x12C8 - int32
        Reserved("WinningTeamsCN"), // 124, 0x12D0 - DynamicArray TArray<uint8>
        Reserved("ServerToClientPreloadList"), // 125, 0x12E0 - DynamicArray TArray<class UObject*>
        Reserved("DefaultBattleBus"), // 126, 0x12F0 - class UAthenaBattleBusItemDefinition*
        Reserved("bAllowUserPickedCosmeticBattleBus"), // 127, 0x12F8 - bool
        Reserved("TeamFlightPaths"), // 128, 0x1300 - DynamicArray TArray<struct FAircraftFlightInfo>
        Reserved("FlightPathMidLine.FlightStartLocation"), // 129, 0x1310 - atomic struct FVector_NetQuantize100
        Reserved("FlightPathMidLine.FlightStartRotation"), // 130, 0x1310 - atomic struct FRotator
        Reserved("FlightPathMidLine.FlightSpeed"), // 131, 0x1310 - float
        Reserved("FlightPathMidLine.TimeTillFlightEnd"), // 132, 0x1310 - float
        Reserved("FlightPathMidLine.TimeTillDropStart"), // 133, 0x1310 - float
        Reserved("FlightPathMidLine.TimeTillDropEnd"), // 134, 0x1310 - float
        Reserved("UtcTimeStartedMatch"), // 135, 0x1348 - struct FDateTime (no members parsed - VERIFY)
        Reserved("bIsLargeTeamGame"), // 136, 0x1350 - bool
        Reserved("WinningPlayerState"), // 137, 0x1358 - class APlayerState*
        Reserved("WinningTeam"), // 138, 0x1370 - int32
        Reserved("WinningScore"), // 139, 0x1374 - int32
        Reserved("CurrentHighScore"), // 140, 0x1378 - int32
        Reserved("CurrentHighScoreTeam"), // 141, 0x137C - int32
        Reserved("SupplyDropWaveStartedSoundCue"), // 142, 0x1380 - class USoundCue*
        Reserved("AirCraftBehavior"), // 143, 0x13C8 - EAirCraftBehavior
        Reserved("bStormReachedFinalPosition"), // 144, 0x13CA - bool
        Reserved("FriendlyFireType"), // 145, 0x13CB - EFriendlyFireType
        Reserved("GameMemberInfoArray.Members"), // 146, 0x14E0 - DynamicArray TArray<struct FGameMemberInfo>
        Reserved("ActiveTeamNums"), // 147, 0x1660 - DynamicArray TArray<uint8>
        new() {
            // 148, offset 0x1670 - int32.
            Name = "CurrentPlaylistId",
            Kind = ERepPropertyKind.Int32,
            GetIntValue = obj => ((AGameState) obj).CurrentPlaylistId
        },
        Reserved("SafeZoneIndicator"), // 149, 0x1678 - class AFortSafeZoneIndicator*
        Reserved("MapInfo"), // 150, 0x1BA0 - class AFortAthenaMapInfo*
        Reserved("BroadcastSpectatorInfo"), // 151, 0x1BB0 - class AFortBroadcastSpectatorInfo*
        new() {
            // 152, offset 0x1BB8 - EAthenaGamePhase.
            Name = "GamePhase",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = (int) EAthenaGamePhase.EAthenaGamePhase_MAX,
            GetByteValue = obj => (byte) ((AGameState) obj).GamePhase
        },
        Reserved("EventTournamentRound"), // 153, 0x1BB9 - EEventTournamentRound
        Reserved("CurrentPlaylistInfo.PropertyOverrides"), // 154, 0x1BC0 - DynamicArray TArray<struct FPropertyOverride>
        new FRepPropertyDef {
            // 155, offset 0x1BC0 - class UFortPlaylistAthena*.
            //
            // CurrentPlaylistInfo is a Custom Delta parent, so REAL UE never sends handles 154/155:
            // CompareProperties skips custom-delta parents outright (RepLayout.cpp:4113 and :4269).
            // But the receive side has no matching check - ReceiveProperties_r walks Cmds purely by
            // handle, with no IsCustomDelta test anywhere - so a handle written here is accepted,
            // writes straight into CurrentPlaylistInfo.BasePlaylist, and queues the parent's
            // RepNotify, which is exactly the OnRep_CurrentPlaylistInfo the client's in-game UI
            // state waits on.
            //
            // This is deliberately not how a real server does it. It is how THIS server can do it,
            // because the alternative - Fortnite's hand-written FPlaylistPropertyArray::
            // NetDeltaSerialize - was measured to carry no BasePlaylist at all (see
            // FFastArraySerializerWriter.WritePlaylistPropertyArrayDelta for the two runs that
            // showed it), and whatever really populates it on a stock server is in native code this
            // build encrypts.
            Name = "CurrentPlaylistInfo.BasePlaylist",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((AGameState) obj).BasePlaylist
        },
        new() {
            // 156, offset 0x1DA8 - bool.
            Name = "bGameModeWillSkipAircraft",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((AGameState) obj).bGameModeWillSkipAircraft ? 1 : 0)
        },
        Reserved("SafeZonePhase"), // 157, 0x1DA9 - uint8
    }).ToArray();

    /// <summary>
    ///     APlayerState -> AFortPlayerState (spawned as /Script/FortniteGame.FortPlayerStateAthena,
    ///     see GUClassArray). AInfo adds nothing, so APlayerState's own properties start at handle
    ///     16, after AActor's 15.
    ///
    ///     ORDERING RULE, corrected 2026-08-25: handles are NOT in declaration order.
    ///     FRepLayout::InitFromClass (RepLayout.cpp:4929) walks UClass::ClassReps, and
    ///     UClass::SetUpRuntimeReplicationData builds ClassReps from NetProperties sorted with
    ///     FCompareUFieldOffsets - by memory OFFSET ascending, ties broken by NAME. Bitfield bools
    ///     packed into one byte therefore all share an offset and come out alphabetised, which is
    ///     NOT the order UEDumper's NetFields.txt lists them in. This is confirmed live: on
    ///     AFortPlayerController the three bools at offset 6997 probed as bCheatFly(49),
    ///     bEnableShotLogging(50), bIsNearActiveEncounters(51) - exactly name order.
    ///
    ///     Offsets below are from NetFields.txt; the bool runs at 582, 856 and 912 are alphabetised
    ///     here accordingly. Note this reordering does NOT move bHasStartedPlaying, which stays at
    ///     handle 30 as before - every property it displaces is another single-handle bool.
    /// </summary>
    private static readonly FRepPropertyDef[] PlayerStateProps = ActorProps.Concat(new[] {
        Reserved("Score"),                  // 16, offset 536
        Reserved("PlayerID"),               // 17, offset 576
        Reserved("Ping"),                   // 18, offset 580
        Reserved("bFromPreviousLevel"),     // 19 |
        Reserved("bIsABot"),                // 20 |
        Reserved("bIsInactive"),            // 21 | offset 582, one bitfield byte,
        Reserved("bIsSpectator"),           // 22 | alphabetised by the tie-break rule
        Reserved("bOnlySpectator"),         // 23 |
        Reserved("StartTime"),              // 24, offset 584
        new FRepPropertyDef {
            // 25, offset 624. FUniqueNetIdRepl is one of the few structs RepLayout special-cases by
            // name as atomic (AddPropertyCmd, RepLayout.cpp) - ERepLayoutCmdType::PropertyNetId -
            // so it is one handle carrying FUniqueNetIdRepl::NetSerialize's byte blob.
            Name = "UniqueId",
            Kind = ERepPropertyKind.NetId,
            GetNetIdValue = obj => ((APlayerState) obj).UniqueId
        },
        new FRepPropertyDef {
            // 26, offset 800. See APlayerState.PlayerNamePrivate for why this is worth sending.
            Name = "PlayerNamePrivate",
            Kind = ERepPropertyKind.String,
            GetStringValue = obj => ((APlayerState) obj).PlayerNamePrivate
        },
        Reserved("bIsGameSessionOwner"),    // 27 | offset 856 (0x358), two bools only
        Reserved("bIsWorldDataOwner"),      // 28 |
        new FRepPropertyDef {
            // 29, offset 912. First of the 0x390 bitfield byte under the name tie-break, i.e. the
            // bit immediately before bHasStartedPlaying - confirmed against the real 10.40 SDK,
            // where 0x390 holds bIsGameSessionAdmin/bIsReadyToContinue/bHasFinishedLoading/
            // bHasStartedPlaying/bShowHeroBackpack/bShowHeroHeadAccessories/bRepFlag1 and sorting
            // those by name reproduces handles 29-35 exactly as numbered here. (An earlier comment
            // filed this one under offset 856; the handle was right, the attribution was not.)
            Name = "bHasFinishedLoading",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APlayerState) obj).bHasFinishedLoading ? 1 : 0)
        },
        new FRepPropertyDef {
            // 30, offset 912. Unchanged by the ordering correction above.
            Name = "bHasStartedPlaying",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APlayerState) obj).bHasStartedPlaying ? 1 : 0)
        },
        Reserved("bIsGameSessionAdmin"),      // 31 |
        Reserved("bIsReadyToContinue"),       // 32 | offset 912, one bitfield byte,
        Reserved("bRepFlag1"),                // 33 | alphabetised
        Reserved("bShowHeroBackpack"),        // 34 |
        Reserved("bShowHeroHeadAccessories"), // 35 |
        Reserved("PlayerRole"),               // 36, offset 916 (EFortPlayerRole)
        Reserved("PartyOwnerUniqueId", ERepPropertyKind.StructAtomic), // 37, offset 920, FUniqueNetIdRepl
        Reserved("WorldPlayerId"),            // 38, offset 960 (int32)
        new FRepPropertyDef {
            // 39, offset 968. See APlayerState.HeroId - an empty value here is what
            // AFortPlayerState::InitializeHero was failing on, and quickbars come out of that.
            Name = "HeroId",
            Kind = ERepPropertyKind.String,
            GetStringValue = obj => ((APlayerState) obj).HeroId
        },
        new FRepPropertyDef {
            // 40, offset 984. PREDICTED, not yet live-probed - confirm with
            // REPLAYOUT_PROBE_ACTOR=APlayerState REPLAYOUT_PROBE_HANDLE=40 before relying on it.
            // The getter is wired but HeroType is still null, so this must not be named in a
            // changed set yet - see APlayerState.HeroType for the static-asset export it needs.
            Name = "HeroType",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APlayerState) obj).HeroType
        }
    }.Concat(new[] {
        // Handles 41-69, predicted from NetFields.txt offsets plus Dumper-7 struct layouts under the
        // offset-ascending / name-tiebreak rule. All distinct offsets here, so no alphabetising is
        // involved; the only real assumptions are the handle COUNTS of the aggregate members below.
        // Nothing in this block is sent - it exists so later handles number correctly and so the
        // truncated name probe has something to aim at.
        Reserved("CurrentCharXP"),           // 41, offset 1016
        Reserved("MyBackpackPickup"),        // 42, offset 1096
        Reserved("InitialExperienceLevel"),  // 43, offset 1104
        Reserved("InitialExperienceAmount"), // 44, offset 1108
        Reserved("ExperienceDeltas"),        // 45, offset 1112 - TArray, one top-level handle
        Reserved("Platform"),                // 46, offset 1184 - FString
        Reserved("CharacterGender"),         // 47, offset 1224
        Reserved("CharacterBodyType"),       // 48, offset 1225
        // 49-59: FCustomCharacterData (offset 1232) recursed. Dumper-7 gives it
        // WasPartReplicatedFlags(uint8) + UCustomCharacterPart* Parts[6] + UAthenaCharmItemDefinition*
        // Charms[4], with bReplicationFailed excluded as RepSkip - 11 handles. A C-array member takes
        // one handle PER ELEMENT (InitFromClass loops ArrayIdx over ArrayDim), the same rule that made
        // AActor::AttachmentReplication span six.
        //
        // Parts[6] is the cosmetic character loadout - the real server fills it with things like
        // /Game/Athena/Heroes/Meshes/Heads/F_Med_Head1_ATH and CP_001_Athena_Body, which the PR3.0
        // capture shows arriving at packet #462, well after the join.
        new FRepPropertyDef {
            // 49. A plain uint8 with no enum attached, so UByteProperty::NetSerializeItem writes a
            // full 8 bits - expressed here as a ByteEnum with EnumMaxValue 256, since
            // CeilLogTwo(256) is exactly 8.
            Name = "CharacterData.WasPartReplicatedFlags",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = 256,
            GetByteValue = obj => ((APlayerState) obj).WasPartReplicatedFlags
        },
        new FRepPropertyDef {
            Name = "CharacterData.Parts[0]", // 50
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APlayerState) obj).CharacterParts[0]
        },
        new FRepPropertyDef {
            Name = "CharacterData.Parts[1]", // 51
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APlayerState) obj).CharacterParts[1]
        },
        new FRepPropertyDef {
            Name = "CharacterData.Parts[2]", // 52
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APlayerState) obj).CharacterParts[2]
        },
        new FRepPropertyDef {
            Name = "CharacterData.Parts[3]", // 53
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APlayerState) obj).CharacterParts[3]
        },
        new FRepPropertyDef {
            Name = "CharacterData.Parts[4]", // 54
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APlayerState) obj).CharacterParts[4]
        },
        new FRepPropertyDef {
            Name = "CharacterData.Parts[5]", // 55
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APlayerState) obj).CharacterParts[5]
        },
        Reserved("CharacterData.Charms[0]"), // 56
        Reserved("CharacterData.Charms[1]"), // 57
        Reserved("CharacterData.Charms[2]"), // 58
        Reserved("CharacterData.Charms[3]"), // 59
        Reserved("CharacterColorSwatches[0]"),     // 60 | offset 1328, arrayDim=2
        Reserved("CharacterColorSwatches[1]"),     // 61 |
        Reserved("CharacterPartColorSwatches[0]"), // 62 |
        Reserved("CharacterPartColorSwatches[1]"), // 63 |
        Reserved("CharacterPartColorSwatches[2]"), // 64 | offset 1456, arrayDim=6
        Reserved("CharacterPartColorSwatches[3]"), // 65 |
        Reserved("CharacterPartColorSwatches[4]"), // 66 |
        Reserved("CharacterPartColorSwatches[5]"), // 67 |
        // 68 is the landmark worth probing: if it reports PlayerTeam, every count above - and in
        // particular FCustomCharacterData being 11 handles - is confirmed in one shot.
        Reserved("PlayerTeam"),        // 68, offset 1552
        Reserved("PlayerTeamPrivate")  // 69, offset 1560
    }).Concat(new FRepPropertyDef[] {
        // Handles 70-248, GENERATED by Tools/RepHandles/rep_handles.py and checked by
        // Tools/RepHandles/verify_cs_handles.py. Everything here is filler except TeamIndex (230)
        // and SquadId (248).
        //
        // Why those two: diffing our match against the one working 10.40 capture we have
        // (Saved.Prive/Logs/FortniteGame-backup-2026.08.23-17.13.29.log, a private server on
        // 127.0.0.1:7777) leaves a very short list of things the working client did that ours does
        // not, and the substantive one is
        //     AFortGameStateAthena::NotifyGameMemberAdded: Adding Player state with UniqueId: ...,
        //     in team: 3, and in squad: 0
        // together with AddCachedRecentPlayers / HandleZonePlayerStateInitialized. All of that is
        // team bookkeeping, and this server had never sent a team at all.

        // ---------------------------------- AFortPlayerState ----------------------------------
        Reserved("ReplicatedStats_Campaign[0].StatValue"), // 70, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[0].ScoreValue"), // 71, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[1].StatValue"), // 72, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[1].ScoreValue"), // 73, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[2].StatValue"), // 74, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[2].ScoreValue"), // 75, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[3].StatValue"), // 76, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[3].ScoreValue"), // 77, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[4].StatValue"), // 78, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[4].ScoreValue"), // 79, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[5].StatValue"), // 80, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[5].ScoreValue"), // 81, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[6].StatValue"), // 82, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[6].ScoreValue"), // 83, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[7].StatValue"), // 84, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[7].ScoreValue"), // 85, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[8].StatValue"), // 86, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[8].ScoreValue"), // 87, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[9].StatValue"), // 88, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[9].ScoreValue"), // 89, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[10].StatValue"), // 90, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[10].ScoreValue"), // 91, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[11].StatValue"), // 92, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[11].ScoreValue"), // 93, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[12].StatValue"), // 94, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[12].ScoreValue"), // 95, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[13].StatValue"), // 96, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[13].ScoreValue"), // 97, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[14].StatValue"), // 98, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[14].ScoreValue"), // 99, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[15].StatValue"), // 100, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[15].ScoreValue"), // 101, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[16].StatValue"), // 102, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[16].ScoreValue"), // 103, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[17].StatValue"), // 104, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[17].ScoreValue"), // 105, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[18].StatValue"), // 106, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[18].ScoreValue"), // 107, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[19].StatValue"), // 108, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[19].ScoreValue"), // 109, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[20].StatValue"), // 110, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[20].ScoreValue"), // 111, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[21].StatValue"), // 112, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[21].ScoreValue"), // 113, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[22].StatValue"), // 114, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[22].ScoreValue"), // 115, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[23].StatValue"), // 116, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[23].ScoreValue"), // 117, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[24].StatValue"), // 118, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[24].ScoreValue"), // 119, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[25].StatValue"), // 120, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[25].ScoreValue"), // 121, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[26].StatValue"), // 122, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[26].ScoreValue"), // 123, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[27].StatValue"), // 124, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[27].ScoreValue"), // 125, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[28].StatValue"), // 126, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[28].ScoreValue"), // 127, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[29].StatValue"), // 128, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[29].ScoreValue"), // 129, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[30].StatValue"), // 130, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[30].ScoreValue"), // 131, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[31].StatValue"), // 132, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[31].ScoreValue"), // 133, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[32].StatValue"), // 134, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[32].ScoreValue"), // 135, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[33].StatValue"), // 136, 0x063C - int32
        Reserved("ReplicatedStats_Campaign[33].ScoreValue"), // 137, 0x063C - int32
        Reserved("ReplicatedStats_Zone[0].StatValue"), // 138, 0x074C - int32
        Reserved("ReplicatedStats_Zone[0].ScoreValue"), // 139, 0x074C - int32
        Reserved("ReplicatedStats_Zone[1].StatValue"), // 140, 0x074C - int32
        Reserved("ReplicatedStats_Zone[1].ScoreValue"), // 141, 0x074C - int32
        Reserved("ReplicatedStats_Zone[2].StatValue"), // 142, 0x074C - int32
        Reserved("ReplicatedStats_Zone[2].ScoreValue"), // 143, 0x074C - int32
        Reserved("ReplicatedStats_Zone[3].StatValue"), // 144, 0x074C - int32
        Reserved("ReplicatedStats_Zone[3].ScoreValue"), // 145, 0x074C - int32
        Reserved("ReplicatedStats_Zone[4].StatValue"), // 146, 0x074C - int32
        Reserved("ReplicatedStats_Zone[4].ScoreValue"), // 147, 0x074C - int32
        Reserved("ReplicatedStats_Zone[5].StatValue"), // 148, 0x074C - int32
        Reserved("ReplicatedStats_Zone[5].ScoreValue"), // 149, 0x074C - int32
        Reserved("ReplicatedStats_Zone[6].StatValue"), // 150, 0x074C - int32
        Reserved("ReplicatedStats_Zone[6].ScoreValue"), // 151, 0x074C - int32
        Reserved("ReplicatedStats_Zone[7].StatValue"), // 152, 0x074C - int32
        Reserved("ReplicatedStats_Zone[7].ScoreValue"), // 153, 0x074C - int32
        Reserved("ReplicatedStats_Zone[8].StatValue"), // 154, 0x074C - int32
        Reserved("ReplicatedStats_Zone[8].ScoreValue"), // 155, 0x074C - int32
        Reserved("ReplicatedStats_Zone[9].StatValue"), // 156, 0x074C - int32
        Reserved("ReplicatedStats_Zone[9].ScoreValue"), // 157, 0x074C - int32
        Reserved("ReplicatedStats_Zone[10].StatValue"), // 158, 0x074C - int32
        Reserved("ReplicatedStats_Zone[10].ScoreValue"), // 159, 0x074C - int32
        Reserved("ReplicatedStats_Zone[11].StatValue"), // 160, 0x074C - int32
        Reserved("ReplicatedStats_Zone[11].ScoreValue"), // 161, 0x074C - int32
        Reserved("ReplicatedStats_Zone[12].StatValue"), // 162, 0x074C - int32
        Reserved("ReplicatedStats_Zone[12].ScoreValue"), // 163, 0x074C - int32
        Reserved("ReplicatedStats_Zone[13].StatValue"), // 164, 0x074C - int32
        Reserved("ReplicatedStats_Zone[13].ScoreValue"), // 165, 0x074C - int32
        Reserved("ReplicatedStats_Zone[14].StatValue"), // 166, 0x074C - int32
        Reserved("ReplicatedStats_Zone[14].ScoreValue"), // 167, 0x074C - int32
        Reserved("ReplicatedStats_Zone[15].StatValue"), // 168, 0x074C - int32
        Reserved("ReplicatedStats_Zone[15].ScoreValue"), // 169, 0x074C - int32
        Reserved("ReplicatedStats_Zone[16].StatValue"), // 170, 0x074C - int32
        Reserved("ReplicatedStats_Zone[16].ScoreValue"), // 171, 0x074C - int32
        Reserved("ReplicatedStats_Zone[17].StatValue"), // 172, 0x074C - int32
        Reserved("ReplicatedStats_Zone[17].ScoreValue"), // 173, 0x074C - int32
        Reserved("ReplicatedStats_Zone[18].StatValue"), // 174, 0x074C - int32
        Reserved("ReplicatedStats_Zone[18].ScoreValue"), // 175, 0x074C - int32
        Reserved("ReplicatedStats_Zone[19].StatValue"), // 176, 0x074C - int32
        Reserved("ReplicatedStats_Zone[19].ScoreValue"), // 177, 0x074C - int32
        Reserved("ReplicatedStats_Zone[20].StatValue"), // 178, 0x074C - int32
        Reserved("ReplicatedStats_Zone[20].ScoreValue"), // 179, 0x074C - int32
        Reserved("ReplicatedStats_Zone[21].StatValue"), // 180, 0x074C - int32
        Reserved("ReplicatedStats_Zone[21].ScoreValue"), // 181, 0x074C - int32
        Reserved("ReplicatedStats_Zone[22].StatValue"), // 182, 0x074C - int32
        Reserved("ReplicatedStats_Zone[22].ScoreValue"), // 183, 0x074C - int32
        Reserved("ReplicatedStats_Zone[23].StatValue"), // 184, 0x074C - int32
        Reserved("ReplicatedStats_Zone[23].ScoreValue"), // 185, 0x074C - int32
        Reserved("ReplicatedStats_Zone[24].StatValue"), // 186, 0x074C - int32
        Reserved("ReplicatedStats_Zone[24].ScoreValue"), // 187, 0x074C - int32
        Reserved("ReplicatedStats_Zone[25].StatValue"), // 188, 0x074C - int32
        Reserved("ReplicatedStats_Zone[25].ScoreValue"), // 189, 0x074C - int32
        Reserved("ReplicatedStats_Zone[26].StatValue"), // 190, 0x074C - int32
        Reserved("ReplicatedStats_Zone[26].ScoreValue"), // 191, 0x074C - int32
        Reserved("ReplicatedStats_Zone[27].StatValue"), // 192, 0x074C - int32
        Reserved("ReplicatedStats_Zone[27].ScoreValue"), // 193, 0x074C - int32
        Reserved("ReplicatedStats_Zone[28].StatValue"), // 194, 0x074C - int32
        Reserved("ReplicatedStats_Zone[28].ScoreValue"), // 195, 0x074C - int32
        Reserved("ReplicatedStats_Zone[29].StatValue"), // 196, 0x074C - int32
        Reserved("ReplicatedStats_Zone[29].ScoreValue"), // 197, 0x074C - int32
        Reserved("ReplicatedStats_Zone[30].StatValue"), // 198, 0x074C - int32
        Reserved("ReplicatedStats_Zone[30].ScoreValue"), // 199, 0x074C - int32
        Reserved("ReplicatedStats_Zone[31].StatValue"), // 200, 0x074C - int32
        Reserved("ReplicatedStats_Zone[31].ScoreValue"), // 201, 0x074C - int32
        Reserved("ReplicatedStats_Zone[32].StatValue"), // 202, 0x074C - int32
        Reserved("ReplicatedStats_Zone[32].ScoreValue"), // 203, 0x074C - int32
        Reserved("ReplicatedStats_Zone[33].StatValue"), // 204, 0x074C - int32
        Reserved("ReplicatedStats_Zone[33].ScoreValue"), // 205, 0x074C - int32
        Reserved("bAreZoneStatsFinalized"), // 206, 0x0860 - bool
        Reserved("ReadyCheckState"), // 207, 0x0861 - EReadyCheckState
        Reserved("HomeActor"), // 208, 0x0868 - class AActor*
        Reserved("PlatformUniqueNetId"), // 209, 0x08D8 - atomic struct FUniqueNetIdRepl

        // ---------------------------------- AFortPlayerStateZone ----------------------------------
        Reserved("SpectatingTarget"), // 210, 0x0958 - class AFortPlayerStateZone*
        Reserved("Spectators.SpectatorArray"), // 211, 0x0960 - DynamicArray TArray<struct FFortSpectatorZoneItem>
        Reserved("KickedFromSessionReason"), // 212, 0x0AD8 - EFortKickReason
        Reserved("CarriedObject"), // 213, 0x0BF0 - class AFortCarriedObject*
        Reserved("NumRejoins"), // 214, 0x0BF8 - int32
        Reserved("bInvincibleDueToUI"), // 215, 0x0C18 - bool
        Reserved("CurrentHealth"), // 216, 0x0C1C - float
        Reserved("MaxHealth"), // 217, 0x0C20 - float
        Reserved("CurrentShield"), // 218, 0x0C24 - float
        Reserved("MaxShield"), // 219, 0x0C28 - float
        Reserved("CurrentSignalInStorm"), // 220, 0x0C2C - float
        Reserved("MaxSignalInStorm"), // 221, 0x0C30 - float
        Reserved("AccumulatedItems"), // 222, 0x0C38 - DynamicArray TArray<struct FAccumulatedItemEntry>
        Reserved("SimulatedAttributes"), // 223, 0x0C58 - DynamicArray TArray<struct FSimulatedAttributeEntry>
        Reserved("bHasEverSkydivedFromBus"), // 224, 0x0C70 - bool
        Reserved("bHasEverSkydivedFromBusAndLanded"), // 225, 0x0C71 - bool
        Reserved("QuickbarEquippedItems"), // 226, 0x0C78 - DynamicArray TArray<class UFortItemDefinition*>

        // ---------------------------------- AFortPlayerStateAthena ----------------------------------
        Reserved("PersonalLobbyAction"), // 227, 0x0C94 - int32
        Reserved("ReplicatedTeamMemberState"), // 228, 0x0CB8 - ETeamMemberState
        Reserved("TeamKillScore"), // 229, 0x0D0C - int32
        new FRepPropertyDef {
            // 230, offset 0x0DB0 - a plain uint8 UByteProperty with no UEnum attached, so
            // UByteProperty::NetSerializeItem writes a full 8 bits; ByteEnum with EnumMaxValue
            // 256 expresses that, since CeilLogTwo(256) == 8.
            // the Athena team number. The reference capture's working join logs
            // "NotifyGameMemberAdded: Adding Player state ... in team: 3, and in squad: 0"; Athena
            // reserves teams 0-2, so a real solo player starts at 3 and our default 0 is not a
            // legal team at all.
            Name = "TeamIndex",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = 256,
            GetByteValue = obj => ((APlayerState) obj).TeamIndex
        },
        Reserved("TeamScorePlacement"), // 231, 0x0DB4 - int32
        Reserved("TeamScore"), // 232, 0x0DB8 - int32
        Reserved("Place"), // 233, 0x0DBC - int32
        Reserved("DownScore"), // 234, 0x0DC0 - int32
        Reserved("KillScore"), // 235, 0x0DC4 - int32
        Reserved("NumChestsOpened"), // 236, 0x0DDC - int32
        Reserved("NumAmmoCansOpened"), // 237, 0x0DE4 - int32
        Reserved("NumSupplyDropsOpened"), // 238, 0x0DEC - int32
        Reserved("NumLlamasOpened"), // 239, 0x0DF4 - int32
        Reserved("NumForagedItemsConsumed"), // 240, 0x0DFC - int32
        Reserved("NumMinutesAlive"), // 241, 0x0E04 - int32
        Reserved("NumBronzeCoinsCollected"), // 242, 0x0E0C - int32
        Reserved("NumSilverCoinsCollected"), // 243, 0x0E14 - int32
        Reserved("NumGoldCoinsCollected"), // 244, 0x0E1C - int32
        Reserved("TotalPlayerScore"), // 245, 0x0E24 - int32
        Reserved("StormSurgeEffectCount"), // 246, 0x0EF8 - uint8
        Reserved("TeamAverageDamageDealt"), // 247, 0x0EFA - uint16
        new FRepPropertyDef {
            // 248, offset 0x0EFC - a plain uint8 UByteProperty with no UEnum attached, so
            // UByteProperty::NetSerializeItem writes a full 8 bits; ByteEnum with EnumMaxValue
            // 256 expresses that, since CeilLogTwo(256) == 8.
            // the squad within the team - 0 for solo.
            Name = "SquadId",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = 256,
            GetByteValue = obj => ((APlayerState) obj).SquadId
        },
    })).ToArray();

    /// <summary>
    ///     /Script/FortniteGame.FortInventory - the class AFortPlayerController::WorldInventory
    ///     points at (handle 34 on the PlayerController, live-confirmed). Derives straight from
    ///     AActor, so its own properties start at handle 16, after AActor's 15.
    ///
    ///     Dumper-7 gives it exactly three Net properties, in this declaration order:
    ///     InventoryType (EFortInventoryType, offset 0x221), Inventory (FFortItemList, 0x228) and
    ///     ReplayPawn (AFortPawn*, 0x3F0). Note UEDumper's NetFields.txt has NO FortInventory
    ///     section at all - one of several gaps that make Dumper-7 the source to trust here.
    ///
    ///     Inventory is deliberately only a reserved slot: FFortItemList derives from
    ///     FFastArraySerializer, which makes it a Custom Delta property (STRUCT_NetDeltaSerializeNative).
    ///     Custom Delta properties are NOT sent through this ordinary handle+value stream at all -
    ///     real UE routes them through FObjectReplicator::ReplicateCustomDeltaProperties and the
    ///     RepIndex/ReadFieldHeaderAndPayload field loop, the same mechanism RPCs use. It still
    ///     occupies a handle slot for numbering purposes, so it must be counted here, but it can
    ///     never be named in a changed set until that subsystem exists.
    /// </summary>
    private static readonly FRepPropertyDef[] InventoryProps = ActorProps.Concat(new[] {
        new FRepPropertyDef {
            // Handle 16. EFortInventoryType is uint8-backed with MAX=3, so
            // UByteProperty::NetSerializeItem writes CeilLogTwo(3) = 2 bits. Always World (0) here:
            // this is the actor behind WorldInventory, not OutpostInventory.
            Name = "InventoryType",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = (int) EFortInventoryType.MAX,
            GetByteValue = obj => (byte) ((AFortInventory) obj).InventoryType
        },
        Reserved("Inventory"),  // handle 17 - Custom Delta, see above; never sendable via FRepLayout
        Reserved("ReplayPawn")  // handle 18
    }).ToArray();

    public static readonly FRepLayout Actor = new(ActorProps);
    public static readonly FRepLayout Controller = new(ControllerProps);
    public static readonly FRepLayout PlayerController = new(PlayerControllerProps);
    public static readonly FRepLayout Pawn = new(PawnProps);
    public static readonly FRepLayout GameState = new(GameStateProps);
    public static readonly FRepLayout PlayerState = new(PlayerStateProps);

    public static readonly FRepLayout Inventory = new(InventoryProps);
    public static readonly FRepLayout Pickup = new(PickupProps);
    public static readonly FRepLayout Weapon = new(WeaponProps);

    public static FRepLayout Get(AActor actor) => actor switch {
        AFortPickup => Pickup,
        AFortWeapon => Weapon,
        AFortInventory => Inventory,
        APlayerController => PlayerController,
        AController => Controller,
        APawn => Pawn,
        AGameState => GameState,
        APlayerState => PlayerState,
        _ => Actor
    };
}
