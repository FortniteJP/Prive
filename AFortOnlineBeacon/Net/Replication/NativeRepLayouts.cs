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
        new() {
            // 2, and the client's own gate on everything below - see AActor.bReplicateMovement.
            Name = "bReplicateMovement",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((AActor) obj).bReplicateMovement ? 1 : 0)
        },
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
        new() {
            // 6 - where every OTHER client sees this actor. See Core.Math.FRepMovement.
            Name = "ReplicatedMovement",
            Kind = ERepPropertyKind.RepMovement,
            GetRepMovementValue = obj => ((AActor) obj).ReplicatedMovement
        },
        // Live-probed 2026-08-24: handles 7,8,9,10 (and, by the Owner=13/Role=14/Instigator=15
        // handles that follow) 11,12 too all resolve to "AttachmentReplication" on a real 10.40
        // client - six consecutive wire handles for what UEDumper's static dump shows as a single
        // repIndex slot.
        //
        // These were six same-named placeholders for a long time, reserving the handle count and
        // nothing else, because nothing needed to attach one actor to another. The battle bus does:
        // bInAircraft gets the client as far as EnterAircraft and ClientSetViewTarget moves the
        // camera, but the PAWN stays where it is until the client is told what it is attached to.
        // The six are now real, named and typed exactly as FRepAttachment declares them
        // (EngineTypes.h:3197) - which is the order FRepLayout flattens them into handles 7-12, so
        // the count that was verified against a live client is unchanged.
        new() {
            Name = "AttachmentReplication",
            Kind = ERepPropertyKind.StructRecurse,
            Children = new FRepPropertyDef[] {
                new() {                                                           // 7
                    Name = "AttachmentReplication.AttachParent",
                    Kind = ERepPropertyKind.ObjectRef,
                    GetObjectValue = obj => ((AActor) obj).AttachParent
                },
                new() {                                                           // 8
                    Name = "AttachmentReplication.LocationOffset",
                    Kind = ERepPropertyKind.VectorQuantize100,
                    GetVectorValue = obj => ((AActor) obj).AttachLocationOffset
                },
                new() {                                                           // 9
                    Name = "AttachmentReplication.RelativeScale3D",
                    Kind = ERepPropertyKind.VectorQuantize100,
                    GetVectorValue = obj => ((AActor) obj).AttachRelativeScale3D
                },
                new() {                                                           // 10
                    Name = "AttachmentReplication.RotationOffset",
                    Kind = ERepPropertyKind.Rotator,
                    GetRotatorValue = obj => ((AActor) obj).AttachRotationOffset
                },
                new() {                                                           // 11
                    Name = "AttachmentReplication.AttachSocket",
                    Kind = ERepPropertyKind.Name,
                    GetNameValue = obj => ((AActor) obj).AttachSocket
                },
                // 12 - AttachComponent. Always null here: attaching to the actor is enough, and a
                // component reference would need the aircraft's own components replicated first.
                new() {
                    Name = "AttachmentReplication.AttachComponent",
                    Kind = ERepPropertyKind.ObjectRef,
                    GetObjectValue = _ => null
                }
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
        new FRepPropertyDef {
            // Live-probed handle 15. Promoted from a Reserved slot once a real client proved it is
            // load-bearing: a weapon whose Instigator never arrives makes AFortWeaponRanged log
            // "The instigator pawn is null when it shouldn't be!" every frame and never works.
            Name = "Instigator",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((AActor) obj).GetInstigator()
        }
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
            Reserved("LatestRewardReport.RewardActivities"), // 40
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
            },

            // ---------------------------------------------------------------------------
            // 53-75, derived with Tools/RepHandles/rep_handles.py AFortPlayerControllerAthena.
            // Declared purely to REACH handle 75: a handle stream is positional, so bMarkedAlive
            // cannot be sent without every handle before it existing in the table, even though
            // none of them is ever put on the wire.
            // ---------------------------------------------------------------------------
            Reserved("AimHelpMode"), // 53 - uint32
            Reserved("JumpStaminaCost"), // 54 - byte enum
            Reserved("CameraPrototypeName"), // 55 - FName
            Reserved("bFinalXPUpdateFailed"), // 56
            Reserved("PoiTagContainerTableID"), // 57 - int16
            Reserved("CreativeQuickbarComponent"), // 58 - object
            Reserved("GhostModeRepData.bInGhostMode"), // 59
            Reserved("GhostModeRepData.GhostModeItemDef"), // 60 - object
            Reserved("ServerNumNPCs"), // 61 - uint16
            Reserved("ServerMaxNumNPCs"), // 62 - uint16
            Reserved("bDisplayNPCNumbers"), // 63
            Reserved("FlyingModifierIndex"), // 64 - int32
            Reserved("bIsFlightSprinting"), // 65
            Reserved("bIsCreativeModeEnabled"), // 66
            Reserved("bIsCreativeQuickbarEnabled"), // 67
            Reserved("VoiceChatChannel"), // 68 - FString
            Reserved("DesyncNotifyList"), // 69 - TArray<AActor*>
            Reserved("SkydiveLeader"), // 70 - object
            Reserved("ViewTargetInventory"), // 71 - object
            Reserved("bNextRespawnInAir"), // 72
            Reserved("bCanUseSolaris"), // 73
            Reserved("MaxPlotCount"), // 74 - int32

            new FRepPropertyDef {
                // 75, 0x2AB8 - AFortPlayerControllerAthena::bMarkedAlive.
                //
                // "Is this player alive?", and it defaults to FALSE on a client that was never
                // told otherwise. Walking never asked - that is the movement component running
                // its own physics - but the things a DEAD player must not do are gated on it,
                // which is the shape of the bug it was found for: crouch and fire and harvest
                // all worked while jump and building placement were refused before they ever
                // reached the server.
                //
                // It sits past the end of what this table used to declare (52), so it was not
                // merely unset - there was no way to express it at all.
                Name = "bMarkedAlive",
                Kind = ERepPropertyKind.Bool,
                GetByteValue = obj => (byte) (((APlayerController) obj).bMarkedAlive ? 1 : 0)
            },

            // 76-79, same reason as 53-75 above: carrying the numbering as far as
            // BroadcastRemoteClientInfo at 80. Derived with
            // `python Tools/RepHandles/rep_handles.py AFortPlayerControllerAthena --from 76 --to 80`.
            Reserved("CreativeIslands", ERepPropertyKind.EmptyDynamicArray), // 76
            Reserved("LastUsedCreativeIsland", ERepPropertyKind.String),     // 77
            Reserved("bIsAllowedToPublish"),                                 // 78
            Reserved("PartyAssistedMemberData", ERepPropertyKind.EmptyDynamicArray), // 79

            new FRepPropertyDef {
                // 80, 0x2BF8 - AFortPlayerControllerAthena::BroadcastRemoteClientInfo. A plain
                // UObjectProperty (AFortBroadcastRemoteClientInfo*), so an ordinary ObjectRef Cmd,
                // exactly like WorldInventory at handle 34. See AFortBroadcastRemoteClientInfo's own
                // doc comment for why this actor exists and what silently breaks without it.
                Name = "BroadcastRemoteClientInfo",
                Kind = ERepPropertyKind.ObjectRef,
                GetObjectValue = obj => ((APlayerController) obj).BroadcastRemoteClientInfo
            }
        })
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
        new() {
            // 29, 0x0318 - walking / falling / one of Fortnite's custom modes. See APawn.
            Name = "ReplicatedMovementMode",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = 256,
            GetByteValue = obj => ((APawn) obj).ReplicatedMovementMode
        },
        new() {
            // 30, 0x0320 - crouching. Resizes the proxy's capsule as well as animating it.
            Name = "bIsCrouched",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APawn) obj).bIsCrouched ? 1 : 0)
        },
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
        new FRepPropertyDef {
            // 47, 0x06A8. AFortPawn::bIsDying - the one bit that tells a client this pawn is dead.
            // A plain replicated bool, so 1 bit on the wire and no width risk; the client's own
            // death handling (ragdoll, hiding the pawn, the death camera) hangs off it.
            Name = "bIsDying",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APawn) obj).bIsDying ? 1 : 0)
        },
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
        },

        Reserved("SpawnImmunityTime", ERepPropertyKind.Float),       // 63, 0x0760 - float
        Reserved("bIsStunned"),                                      // 64, 0x0788 - bool
        Reserved("PushMomentum", ERepPropertyKind.StructAtomic),     // 65, 0x0798 - atomic FVector_NetQuantize
        Reserved("LocalSpin", ERepPropertyKind.Float),               // 66, 0x07A8 - float
        Reserved("DamageZoneActiveBitMask", ERepPropertyKind.ByteEnum), // 67, 0x08F8 - uint8
        Reserved("JumpFlashCountPacked", ERepPropertyKind.ByteEnum),    // 68, 0x0900 - uint8
        Reserved("LandingFlashCountPacked", ERepPropertyKind.ByteEnum), // 69, 0x0901 - uint8

        new FRepPropertyDef {
            // 70, 0x0A30 - class UFortItemDefinition*, RepNotify. The emote a pawn is CURRENTLY
            // playing, and the only way a player OTHER than the emoter ever sees it.
            //
            // The emoter's own client plays the montage from the granted GAB_Emote_Generic ability
            // (see FortEmoteSystem); that whole path is owner-only - the ability spec goes out on
            // the PlayerState channel, which is bOnlyRelevantToOwner, and ClientActivateAbilitySucceed
            // is a client RPC to one connection. Nothing in it reaches a spectator. This property is
            // what does: AFortPawn::OnRep_LastReplicatedEmoteExecuted is the hook Fortnite itself
            // uses, and two independent server reconstructions (Erbium, Magnesium) set exactly this
            // straight after granting the ability.
            //
            // Cleared back to null when the emote ends (the client's own ServerCancelAbility /
            // ServerEndAbility) - the RepNotify only fires on a CHANGE, so a value left standing
            // would make the same emote played twice in a row invisible to everyone else.
            Name = "LastReplicatedEmoteExecuted",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APawn) obj).LastReplicatedEmoteExecuted
        },

        // 71, 0x0A40 - float. How fast a MOVING emote walks. Reserved rather than sent: the value
        // lives on the emote asset itself (UAthenaDanceItemDefinition::WalkForwardSpeed), which an
        // out-of-process server has no way to read - see FortEmoteSystem for the same limitation on
        // bMovingEmote (52) / bMovingEmoteForwardOnly (53).
        Reserved("EmoteWalkSpeed", ERepPropertyKind.Float),

        // 72..145, derived with Tools/RepHandles (`rep_handles.py AFortPlayerPawn`) and
        // cross-checked by Tools/RepHandles/verify_cs_handles.py. All reserved except the
        // last, because handles are POSITIONAL - CosmeticLoadout.Glider is 145 and there is
        // no way to reach it without declaring everything in front of it.
        //
        // Why bother: the client crashes about a second into the skydive without it.
        // AFortPlayerPawn resolves "which glider am I using" as
        // GliderOverrideStack.Last() -> GliderClass (+0x22C8) -> CosmeticLoadout.Glider
        // (+0x18C0+0x28 = 0x18E8), and with all three null the last one is dereferenced
        // anyway - the fault was `mov rax,[rcx]` at 0x141962AC7 on a null rcx. A real
        // server sends /Game/Athena/Items/Cosmetics/Gliders/DefaultGlider here, which the
        // PR3.0 capture registers as NetGUID 979 in the pawn's very first property burst.
        Reserved("VocalChords"), // 72, 0x0C40 - DynamicArray TArray<struct FFortPawnVocalChord>
        Reserved("DisplayName"), // 73, 0x0D38 - class FText
        Reserved("CurrentCalloutTag"), // 74, 0x0DA0 - atomic struct FGameplayTag
        Reserved("CurrentSentence.SpeechAudio.Audio"), // 75, 0x0ED8 - TSoftObjectPtr<class USoundBase>
        Reserved("CurrentSentence.SpeechAudio.Handle.FeedbackBank"), // 76, 0x0ED8 - class UFortFeedbackBank*
        Reserved("CurrentSentence.SpeechAudio.Handle.EventName"), // 77, 0x0ED8 - class FName
        Reserved("CurrentSentence.SpeechAudio.Handle.bReadOnly"), // 78, 0x0ED8 - bool
        Reserved("CurrentSentence.SpeechAudio.Handle.bBankDefined"), // 79, 0x0ED8 - bool
        Reserved("CurrentSentence.SpeechAudio.Handle.BroadcastFilterOverride"), // 80, 0x0ED8 - T1ByteEnum<EFortFeedbackBroadcastFilter>
        Reserved("CurrentSentence.SpeechText"), // 81, 0x0ED8 - class FText
        Reserved("CurrentSentence.TalkingHeadTexture"), // 82, 0x0ED8 - TSoftObjectPtr<class UTexture2D>
        Reserved("CurrentSentence.TalkingHeadTitle"), // 83, 0x0ED8 - class FText
        Reserved("CurrentSentence.AnimMontage"), // 84, 0x0ED8 - TSoftObjectPtr<class UAnimMontage>
        Reserved("CurrentSentence.PostSentenceDelay"), // 85, 0x0ED8 - float
        Reserved("CurrentSentence.DisplayDuration"), // 86, 0x0ED8 - float
        Reserved("VehicleInputStateReliable.bIgnoreForwardInAir"), // 87, 0x1130 - uint8
        Reserved("VehicleInputStateReliable.bIsBraking"), // 88, 0x1130 - uint8
        Reserved("VehicleInputStateReliable.bIsHonking"), // 89, 0x1130 - uint8
        Reserved("VehicleInputStateReliable.bIsJumping"), // 90, 0x1130 - uint8
        Reserved("VehicleInputStateReliable.bIsSprinting"), // 91, 0x1130 - uint8
        Reserved("VehicleInputStateReliable.bMovementModifier0"), // 92, 0x1130 - uint8
        Reserved("VehicleInputStateReliable.bMovementModifier1"), // 93, 0x1130 - uint8
        Reserved("VehicleInputStateReliable.bMovementModifier2"), // 94, 0x1130 - uint8
        new() {
            // 95, 0x1131 - the storm wall is close. See APawn.bIsNearSafeZoneEdge.
            Name = "bIsNearSafeZoneEdge",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APawn) obj).bIsNearSafeZoneEdge ? 1 : 0)
        },
        Reserved("bIsTargeting"), // 96, 0x1132 - uint8
        Reserved("StasisMode"), // 97, 0x1134 - EFortPawnStasisMode
        Reserved("BuildingState"), // 98, 0x1135 - T1ByteEnum<EFortBuildingState>
        Reserved("AccelerationZPack"), // 99, 0x1136 - int8
        Reserved("bIsInWaterVolume"), // 100, 0x1170 - bool
        Reserved("CachedTeamControllingRC"), // 101, 0x1171 - uint8
        Reserved("BalloonActiveCount"), // 102, 0x1172 - uint8
        new() {
            // 103, 0x1174 - falling from the bus. OnRep_IsSkydiving. See APawn.
            Name = "bIsSkydiving",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APawn) obj).bIsSkydiving ? 1 : 0)
        },
        new() {
            // 104, 0x1175 - the glider is out. OnRep_IsParachuteOpen.
            Name = "bIsParachuteOpen",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APawn) obj).bIsParachuteOpen ? 1 : 0)
        },
        Reserved("bIsParachuteForcedOpen"), // 105, 0x1176 - uint8
        new() {
            // 106, 0x1176 - from the BUS rather than a launch pad. OnRep_IsSkydivingFromBus.
            Name = "bIsSkydivingFromBus",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APawn) obj).bIsSkydivingFromBus ? 1 : 0)
        },
        Reserved("bIsSkydivingFromLaunchPad"), // 107, 0x1176 - uint8
        Reserved("bReplicatedIsInVortex"), // 108, 0x1176 - uint8
        Reserved("bInGliderRedeploy"), // 109, 0x1177 - uint8
        Reserved("bIsProxySimulationTimedOut"), // 110, 0x1177 - uint8
        Reserved("bIsSlopeSliding"), // 111, 0x1177 - uint8
        Reserved("bReplicatedIsInSlipperyMovement"), // 112, 0x1177 - uint8
        Reserved("bIsPlayingEmote"), // 113, 0x1178 - uint8
        Reserved("bIsRespawning"), // 114, 0x1178 - uint8
        Reserved("bIsUsingJetpack"), // 115, 0x1178 - uint8
        Reserved("bStartedInteractSearch"), // 116, 0x1178 - uint8
        Reserved("bIsRespawningInAir"), // 117, 0x1179 - uint8
        Reserved("VehicleInputStateUnreliable.ForwardAlpha"), // 118, 0x120C - float
        Reserved("VehicleInputStateUnreliable.RightAlpha"), // 119, 0x120C - float
        Reserved("VehicleInputStateUnreliable.PitchAlpha"), // 120, 0x120C - float
        Reserved("VehicleInputStateUnreliable.LookUpDelta"), // 121, 0x120C - float
        Reserved("VehicleInputStateUnreliable.TurnDelta"), // 122, 0x120C - float
        Reserved("VehicleInputStateUnreliable.SteerAlpha"), // 123, 0x120C - float
        Reserved("VehicleInputStateUnreliable.GravityOffset"), // 124, 0x120C - float
        Reserved("VehicleInputStateUnreliable.MovementDir"), // 125, 0x120C - atomic struct FVector_NetQuantize100
        new() {
            // 126, 0x126C bit 0 - THE storm flag. This is the one with an OnRep, and so the one that
            // turns the screen effect on. See APawn.bIsInAnyStorm.
            Name = "bIsInAnyStorm",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APawn) obj).bIsInAnyStorm ? 1 : 0)
        },
        new() {
            // 127, 0x126C - in the circle or in the storm. This is what the client draws the storm
            // vignette from; without it a player standing in the storm takes damage with no visual
            // sign of why. See APawn.bIsInsideSafeZone.
            Name = "bIsInsideSafeZone",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APawn) obj).bIsInsideSafeZone ? 1 : 0)
        },
        Reserved("ZiplineState.Zipline"), // 128, 0x1338 - class AFortAthenaZipline*
        Reserved("ZiplineState.bIsZiplining"), // 129, 0x1338 - bool
        Reserved("ZiplineState.bJumped"), // 130, 0x1338 - bool
        Reserved("ZiplineState.AuthoritativeValue"), // 131, 0x1338 - int32
        Reserved("ZiplineState.SocketOffset"), // 132, 0x1338 - atomic struct FVector
        Reserved("bCanPredictJumpApex"), // 133, 0x13D0 - bool
        Reserved("VehicleStateRep.Vehicle"), // 134, 0x1530 - class AActor*
        Reserved("VehicleStateRep.VehicleApexZ"), // 135, 0x1530 - float
        Reserved("VehicleStateRep.SeatIndex"), // 136, 0x1530 - uint8
        Reserved("VehicleStateRep.ExitSocketIndex"), // 137, 0x1530 - uint8
        Reserved("VehicleStateRep.bOverrideVehicleExit"), // 138, 0x1530 - bool
        Reserved("VehicleStateRep.SeatTransitionVector"), // 139, 0x1530 - atomic struct FVector
        Reserved("VehicleStateRep.EntryTime"), // 140, 0x1530 - float
        Reserved("PossessedProp"), // 141, 0x15C0 - class ABuildingGameplayActorPlayerPropAttachment*
        Reserved("CosmeticLoadout.BannerIconId"), // 142, 0x18C0 - class FString
        Reserved("CosmeticLoadout.BannerColorId"), // 143, 0x18C0 - class FString
        Reserved("CosmeticLoadout.SkyDiveContrail"), // 144, 0x18C0 - class UAthenaSkyDiveContrailItemDefinition*
        new() {
            // 145, 0x18C0 (+0x28) - the glider. See above.
            Name = "CosmeticLoadout.Glider",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APawn) obj).CosmeticGlider
        }
    }).ToArray();

    /// <summary>
    ///     AFortWeapon's own properties, after AActor's 15 - DERIVED by
    ///     `python Tools/RepHandles/rep_handles.py AFortWeap_BuildingTool`, which reproduces every
    ///     live-probed handle elsewhere in this file and, being the deepest subclass with an added
    ///     property, reaches all the way to handle 36.
    ///
    ///     ImpactAbilitySpecHandle (34) and AppliedAlterations (35) are Reserved: server-side spec
    ///     handles and an always-empty array this project has nothing honest to put in. DefaultMetadata
    ///     (36, AFortWeap_BuildingTool's own only property) IS sent - see AFortWeapon.DefaultMetadata.
    ///
    ///     One layout covers every weapon class, building tools included. AFortWeaponRanged,
    ///     AFortWeaponPickaxeAthena and AFortWeap_BuildingTool each append their own properties after
    ///     these, but a subclass only ever APPENDS - so every weapon agrees on handles 1-35, and only
    ///     a building tool's client cares that 36 is populated.
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
        new() { Name = "AmmoCount", Kind = ERepPropertyKind.Int32, GetIntValue = obj => ((AFortWeapon) obj).AmmoCount },     // 28, 0x0728

        Reserved("ChargeStatusPack", ERepPropertyKind.Int16), // 29, 0x0754
        Reserved("ActiveAbility"),                            // 30, 0x0770 - class UFortGameplayAbility*
        new() {
            // 31, 0x0778 - FGameplayAbilitySpecHandle, i.e. one bare int32. NOT an atomic
            // NetSerialize struct: the engine source has no WithNetSerializer for it, and the
            // 54-bit ServerTryActivateAbility measured off a real capture only adds up if this
            // recurses to its single member.
            //
            // This is the link that makes a weapon fireable: it indexes the ASC's
            // ActivatableAbilities, so without it the client holds a weapon that knows no ability.
            Name = "PrimaryAbilitySpecHandle",
            Kind = ERepPropertyKind.Int32,
            GetIntValue = obj => ((AFortWeapon) obj).GrantedAbilitySpecHandle
        },

        Reserved("SecondaryAbilitySpecHandle"), // 32, 0x077C - int32
        new() {
            // 33, 0x0780. Same shape as 31: one bare int32 indexing ActivatableAbilities. Without
            // it the client has a magazine it can empty and no way to refill - the reload input has
            // no ability to reach.
            Name = "ReloadAbilitySpecHandle",
            Kind = ERepPropertyKind.Int32,
            GetIntValue = obj => ((AFortWeapon) obj).ReloadAbilitySpecHandle
        },

        Reserved("ImpactAbilitySpecHandle", ERepPropertyKind.Int32), // 34, 0x0784
        Reserved("AppliedAlterations", ERepPropertyKind.EmptyDynamicArray), // 35, 0x07A8

        // 36, 0x09B8/0x0968 - the ONE slot two different sibling subclasses of AFortWeapon each add,
        // and each lands here for the same reason: AFortWeap_BuildingTool's DefaultMetadata
        // (rep_handles.py AFortWeap_BuildingTool) and AFortWeap_EditingTool's EditActor
        // (rep_handles.py AFortWeap_EditingTool) are both the ONLY own property their class adds
        // after AFortWeapon's shared 35, so both land on handle 36 - never both on the same weapon
        // instance, since a weapon is never both a building tool and an edit tool. DefaultMetadata's
        // OnRep draws the ghost/pencil preview; EditActor's OnRep raises/lowers the edit UI. See
        // AFortWeapon.DefaultMetadata/EditActor and FortWeaponActorClasses.BuildingMetadataFor.
        new() {
            Name = "DefaultMetadata/EditActor",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((AFortWeapon) obj).DefaultMetadata ?? (UObject?) ((AFortWeapon) obj).EditActor
        }
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
        new() {
            // 29, 0x02D0 - int32, on AFortGameState (not the Athena subclass). How many teams the
            // match has, which for a solo playlist is one per player slot. The client uses it to
            // size its own team bookkeeping; left at its default it treats the match as having none,
            // which is why anything team-shaped (the "N players left" style counters that are keyed
            // by team) had nothing to hang off.
            Name = "TeamCount",
            Kind = ERepPropertyKind.Int32,
            GetIntValue = obj => ((AGameState) obj).TeamCount
        },
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
        new() {
            // 112, 0x1270 - float. When the FIRST circle starts closing, in match-clock seconds. The
            // client's map shows the countdown against it.
            Name = "SafeZonesStartTime",
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AGameState) obj).SafeZonesStartTime
        },
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
        new() {
            // 149, 0x1678 - class AFortSafeZoneIndicator*. How the client FINDS the circle: it has an
            // OnRep (OnRep_SafeZoneIndicator, seen in ObjectsDump) and the map/minimap hang off it.
            // The indicator actor replicating on its own channel is not enough - without this
            // reference the client has an actor and nothing pointing at it.
            Name = "SafeZoneIndicator",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((AGameState) obj).SafeZoneIndicator
        },
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
        new() {
            // 157, 0x1DA9 - uint8. Which circle the match is on; the HUD prints it as "Storm phase N".
            // A plain uint8, not an enum property, so it is eight raw bits - which this table spells
            // as ByteEnum with EnumMaxValue 256, the same way TeamIndex does (CeilLogTwo(256) == 8).
            Name = "SafeZonePhase",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = 256,
            GetByteValue = obj => ((AGameState) obj).SafeZonePhase
        },
        Reserved("PlayerBotsLeft", ERepPropertyKind.Int32), // 158, 0x1DB8 - int32
        new() {
            // 159, 0x1DD0 - TArray<AFortAthenaAircraft*>. The client finds the battle bus through
            // THIS, not by looking for an actor of that class: AFortAthenaAircraft.AircraftIndex is
            // the index into this array. One bus, so one element.
            Name = "Aircrafts",
            Kind = ERepPropertyKind.ObjectRefArray,
            GetObjectArrayValue = obj => ((AGameState) obj).Aircrafts
        },
        new() {
            // 160, 0x1DE0 - uint8. True while the doors are shut; the client refuses to send
            // ServerAttemptAircraftJump at all while this is set, so it is the server's actual
            // control over when players may leave.
            Name = "bAircraftIsLocked",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((AGameState) obj).bAircraftIsLocked ? 1 : 0)
        }
    }).ToArray();

    /// <summary>
    ///     AFortAthenaAircraft - the battle bus. AFortAircraft adds JumpFlashCount (16) over AActor's
    ///     15, then AFortAthenaAircraft's own flight plan runs 17-28. Handles derived with
    ///     Tools/RepHandles (`rep_handles.py AFortAthenaAircraft`), which reproduces every
    ///     live-probed handle this project has - see [[rep_handle_derivation]].
    ///
    ///     FlightInfo is an FAircraftFlightInfo with no native NetSerialize, so RepLayout RECURSES
    ///     into it and its six members take six handles (17-22) rather than the struct taking one.
    ///     That is the same rule that splits MinimalReplicationProxy on a building.
    /// </summary>
    private static readonly FRepPropertyDef[] AircraftProps = ActorProps.Concat(new FRepPropertyDef[] {
        Reserved("JumpFlashCount", ERepPropertyKind.Int32),                       // 16, 0x0218
        new() {
            Name = "FlightInfo.FlightStartLocation",                              // 17, 0x02A8
            Kind = ERepPropertyKind.VectorQuantize100,
            GetVectorValue = obj => ((AFortAthenaAircraft) obj).FlightStartLocation
        },
        new() {
            Name = "FlightInfo.FlightStartRotation",                              // 18
            Kind = ERepPropertyKind.Rotator,
            GetRotatorValue = obj => ((AFortAthenaAircraft) obj).FlightStartRotation
        },
        new() {
            Name = "FlightInfo.FlightSpeed",                                      // 19
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortAthenaAircraft) obj).FlightSpeed
        },
        new() {
            Name = "FlightInfo.TimeTillFlightEnd",                                // 20
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortAthenaAircraft) obj).TimeTillFlightEnd
        },
        new() {
            Name = "FlightInfo.TimeTillDropStart",                                // 21
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortAthenaAircraft) obj).TimeTillDropStart
        },
        new() {
            Name = "FlightInfo.TimeTillDropEnd",                                  // 22
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortAthenaAircraft) obj).TimeTillDropEnd
        },
        new() {
            Name = "FlightStartTime",                                             // 23, 0x02D0
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortAthenaAircraft) obj).FlightStartTime
        },
        new() {
            Name = "FlightEndTime",                                               // 24
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortAthenaAircraft) obj).FlightEndTime
        },
        new() {
            Name = "DropStartTime",                                               // 25
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortAthenaAircraft) obj).DropStartTime
        },
        new() {
            Name = "DropEndTime",                                                 // 26
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortAthenaAircraft) obj).DropEndTime
        },
        new() {
            Name = "ReplicatedFlightTimestamp",                                   // 27, 0x02E0
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortAthenaAircraft) obj).ReplicatedFlightTimestamp
        },
        new() {
            Name = "AircraftIndex",                                               // 28, 0x0438
            Kind = ERepPropertyKind.Int32,
            GetIntValue = obj => ((AFortAthenaAircraft) obj).AircraftIndex
        }
    }).ToArray();

    /// <summary>
    ///     AFortSafeZoneIndicator - the storm circle. Handles 16-32 over AActor's 15, and this is the
    ///     one layout in this file that has been verified END TO END against a real server rather than
    ///     only derived: packet #16837 of the PR3.0 capture was hand-decoded bit by bit against these
    ///     exact numbers and parsed cleanly to its handle-0 terminator. See AFortSafeZoneIndicator for
    ///     the decoded values.
    ///
    ///     Handles 24-28 and 30-31 stay Reserved on purpose - the real server does not send them
    ///     either, because they were still at their class defaults in that capture.
    /// </summary>
    private static readonly FRepPropertyDef[] SafeZoneIndicatorProps = ActorProps.Concat(new FRepPropertyDef[] {
        new() {
            Name = "LastRadius",                                                  // 16, 0x0220
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortSafeZoneIndicator) obj).LastRadius
        },
        new() {
            Name = "NextRadius",                                                  // 17
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortSafeZoneIndicator) obj).NextRadius
        },
        new() {
            Name = "NextNextRadius",                                              // 18
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortSafeZoneIndicator) obj).NextNextRadius
        },
        new() {
            Name = "LastCenter",                                                  // 19, 0x022C
            Kind = ERepPropertyKind.VectorQuantize100,
            GetVectorValue = obj => ((AFortSafeZoneIndicator) obj).LastCenter
        },
        new() {
            Name = "NextCenter",                                                  // 20
            Kind = ERepPropertyKind.VectorQuantize100,
            GetVectorValue = obj => ((AFortSafeZoneIndicator) obj).NextCenter
        },
        new() {
            Name = "NextNextCenter",                                              // 21
            Kind = ERepPropertyKind.VectorQuantize100,
            GetVectorValue = obj => ((AFortSafeZoneIndicator) obj).NextNextCenter
        },
        new() {
            Name = "SafeZoneStartShrinkTime",                                     // 22, 0x0250
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortSafeZoneIndicator) obj).SafeZoneStartShrinkTime
        },
        new() {
            Name = "SafeZoneFinishShrinkTime",                                    // 23
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortSafeZoneIndicator) obj).SafeZoneFinishShrinkTime
        },
        Reserved("bSafezoneEventDriven"),                                         // 24, 0x0258
        Reserved("bPaused"),                                                      // 25, 0x0259
        Reserved("bPausedForPreview"),                                            // 26, 0x025A
        Reserved("NextNextMegaStormGridCellThickness", ERepPropertyKind.Int32),   // 27, 0x0264
        Reserved("NextMegaStormGridCellThickness", ERepPropertyKind.Int32),       // 28, 0x0268
        new() {
            Name = "MegaStormDelayTimeBeforeDestruction",                         // 29, 0x026C
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortSafeZoneIndicator) obj).MegaStormDelayTimeBeforeDestruction
        },
        Reserved("NumActiveMegaStormCircles", ERepPropertyKind.Int32),            // 30, 0x0270
        Reserved("ActiveMegaStormCircleGridCellCountFromEdge", ERepPropertyKind.Int32), // 31, 0x0274
        new() {
            Name = "Radius",                                                      // 32, 0x039C
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((AFortSafeZoneIndicator) obj).Radius
        }
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
        // 216-219, 0x0C1C-0x0C28. The PlayerState's OWN plain-float mirror of what the GAS
        // HealthSet holds - four ordinary replicated floats, nothing to do with attribute sets.
        // Fortnite keeps both because a simulated proxy (a teammate on the HUD, a spectated player)
        // has no local attribute set to read; the values are pushed here by the server instead.
        //
        // Sent alongside the attribute set, not instead of it, and that is deliberate: this is the
        // second independent path a client could be reading a health bar from, and running both at
        // once is what makes the round-9 question answerable - if the player's bar tracks damage
        // while a building's still does not, the difference is that a building has no such mirror
        // and the honest conclusion follows. Values come from the HealthSet so there is exactly one
        // source of truth (see APlayerState.HealthSet).
        new FRepPropertyDef {
            Name = "CurrentHealth", // 216
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((APlayerState) obj).HealthSet?.Health ?? 0.0f
        },
        new FRepPropertyDef {
            Name = "MaxHealth", // 217
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((APlayerState) obj).HealthSet?.MaxHealth ?? 0.0f
        },
        new FRepPropertyDef {
            Name = "CurrentShield", // 218
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((APlayerState) obj).HealthSet?.CurrentShield ?? 0.0f
        },
        new FRepPropertyDef {
            // The cap. Named MaxShield on the PlayerState even though the attribute set calls the
            // same quantity plain "Shield" - both names are the SDK's own.
            Name = "MaxShield", // 219
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((APlayerState) obj).HealthSet?.Shield ?? 0.0f
        },
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
        Reserved("Banner.IconId"), // 249, 0x0F00 - FString
        Reserved("Banner.ColorId"), // 250, 0x0F00 - FString
        Reserved("Banner.Level"), // 251, 0x0F00 - int32
        new() {
            // 252, 0x0F28 - uint8. Whether this player is still aboard the battle bus. The client
            // gates a great deal on it: the HUD, whether the pawn is drawn, and whether the jump
            // input is even offered. Nothing sent it before the aircraft existed.
            Name = "bInAircraft",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APlayerState) obj).bInAircraft ? 1 : 0)
        },
        Reserved("bThankedBusDriver"), // 253, 0x0F28 - uint8
        Reserved("bUsingAnonymousCharacterMode"), // 254, 0x0F28 - uint8
        Reserved("bUsingAnonymousMode"), // 255, 0x0F28 - uint8
        Reserved("StreamerModeName"), // 256, 0x0F30 - FText
        Reserved("bIsDisconnected"), // 257, 0x1150 - bool

        // FDeathInfo (0x1180), recursed into its non-RepSkip members - Downer and DeathLocation are
        // RepSkip and take no handle at all, which is why FinisherOrDowner is followed straight by
        // bDBNO. This is the struct the client's elimination feed and death screen read; setting
        // bInitialized is what marks it as a real death rather than the empty default.
        new FRepPropertyDef {
            // 258. The killer - a pawn, not a PlayerState. Left null for a death nobody caused
            // (fall damage, the storm), which is exactly what real Fortnite does with it.
            Name = "DeathInfo.FinisherOrDowner",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((APlayerState) obj).DeathInfoFinisherOrDowner
        },
        new FRepPropertyDef {
            // 259. Downed-but-not-out, i.e. a squad revive state. Always false here: solo has no
            // DBNO, and this project does not model it.
            Name = "DeathInfo.bDBNO",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APlayerState) obj).DeathInfoDBNO ? 1 : 0)
        },
        new FRepPropertyDef {
            // 260. EDeathCause, 49 values in the 10.40 SDK (EDeathCause_MAX = 49), so
            // UEnumProperty::NetSerializeItem writes CeilLogTwo(49) = 6 bits - NOT a full byte.
            // Getting this width wrong desyncs the rest of the stream, the way a wrong cmd width
            // always does here.
            Name = "DeathInfo.DeathCause",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = 49,
            GetByteValue = obj => (byte) ((APlayerState) obj).DeathInfoCause
        },
        new FRepPropertyDef {
            // 261. Metres between killer and victim, for the elimination feed's "at 42m".
            Name = "DeathInfo.Distance",
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((APlayerState) obj).DeathInfoDistance
        },
        new FRepPropertyDef {
            // 262. The flag that makes the whole struct count. Sent LAST of the five in handle
            // order anyway, since the handle stream is ascending - the client reads the cause and
            // the distance before it reads the bit that says to believe them.
            Name = "DeathInfo.bInitialized",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APlayerState) obj).DeathInfoInitialized ? 1 : 0)
        },
        // 263, DeathInfo.DeathTags - an FGameplayTagContainer, atomic (one handle carrying its own
        // NetSerialize). Deliberately NOT sent: this project has no tag-container serializer, and
        // an empty container is what a death with no special tags looks like anyway.
        Reserved("DeathInfo.DeathTags", ERepPropertyKind.StructAtomic), // 263
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

    /// <summary>
    ///     AFortBroadcastRemoteClientInfo's own properties, after AActor's 15. DERIVED with
    ///     `python Tools/RepHandles/rep_handles.py AFortBroadcastRemoteClientInfo`. Only
    ///     RemoteBuildableClass (20, the ServerSetPlayerBuildableClass target) is actually modeled;
    ///     everything else here exists purely to carry the handle numbering correctly past it, since
    ///     the handle stream is positional - see AFortBroadcastRemoteClientInfo's own doc comment for
    ///     why this actor exists at all.
    /// </summary>
    private static readonly FRepPropertyDef[] BroadcastRemoteClientInfoProps = ActorProps.Concat(new[] {
        new FRepPropertyDef {
            // Handle 16.
            Name = "bActive",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((AFortBroadcastRemoteClientInfo) obj).bActive ? 1 : 0)
        },
        Reserved("bRemoteIsInteracting"),                        // 17
        Reserved("RemoteEditActor", ERepPropertyKind.ObjectRef), // 18 - class ABuildingSMActor*
        Reserved("RemoteEditTileData", ERepPropertyKind.EmptyDynamicArray), // 19 - TArray<int32>
        new FRepPropertyDef {
            // Handle 20 - the actual target of ServerSetPlayerBuildableClass. TSubclassOf<T> is a
            // UObjectPropertyBase underneath (a UClass reference), so an ordinary ObjectRef Cmd like
            // any other object-reference property.
            Name = "RemoteBuildableClass",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((AFortBroadcastRemoteClientInfo) obj).RemoteBuildableClass
        },
        Reserved("RemoteBuildingMaterial", ERepPropertyKind.ByteEnum) // 21 - EFortResourceType
        // 22 onward (bRemoteIsFullScreenMapActive, bRemoteIsInventoryActive, bRemoteCanDBNORevive,
        // RemoteChatEntry, RemoteWeakspotData, RemoteRespawnTime, RemotePoiTagID, RemoteEventScore)
        // is never sent - nothing needs it, so the table simply stops here rather than reserving
        // handles nothing after them ever has to reach.
    }).ToArray();

    /// <summary>
    ///     UFortAbilitySystemComponentAthena's RepLayout - the first COMPONENT layout in this
    ///     project. Components chain off UObject, not AActor, so this does NOT start from ActorProps:
    ///     handle 1 is UActorComponent's own first property.
    ///
    ///     DERIVED by `python Tools/RepHandles/rep_handles.py UFortAbilitySystemComponentAthena`.
    ///     Declared only as far as AvatarActor (8) - nothing past it is sent.
    ///
    ///     Handles 7 and 8 are why a player could fire but only crawl. Fortnite drives walk speed
    ///     from GameplayAttributes, and those only reach a pawn's CharacterMovement once the ASC
    ///     knows which actor it belongs to (OwnerActor) and which one it acts through (AvatarActor)
    ///     - UAbilitySystemComponent::InitAbilityActorInfo. Without them the client had healthy
    ///     attributes (WalkSpeed 200, RunSpeed 410) that were bound to nothing, and its velocity sat
    ///     clamped at exactly 1.0 uu/s while acceleration read a perfectly normal 940.
    /// </summary>
    private static readonly FRepPropertyDef[] AbilitySystemComponentProps = {
        Reserved("bReplicates"),                                       // 1, 0x0084 - uint8
        Reserved("bIsActive"),                                         // 2, 0x0086 - uint8
        Reserved("SimulatedTasks", ERepPropertyKind.EmptyDynamicArray), // 3, 0x00C0
        new() {
            // 4, 0x0140 - TArray<UAttributeSet*>. See UFortAbilitySystemComponent.SpawnedAttributes.
            Name = "SpawnedAttributes",
            Kind = ERepPropertyKind.ObjectRefArray,
            GetObjectArrayValue = obj => ((UFortAbilitySystemComponent) obj).SpawnedAttributes
        },
        Reserved("ClientDebugStrings", ERepPropertyKind.EmptyDynamicArray), // 5, 0x0338
        Reserved("ServerDebugStrings", ERepPropertyKind.EmptyDynamicArray), // 6, 0x0348
        new() {
            // 7, 0x03F8 - the actor that OWNS the component: the PlayerState.
            Name = "OwnerActor",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((UFortAbilitySystemComponent) obj).OwnerActor
        },
        new() {
            // 8, 0x0400 - the actor the component ACTS THROUGH: the pawn. This is the one that
            // makes movement attributes reach the character.
            Name = "AvatarActor",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((UFortAbilitySystemComponent) obj).AvatarActor
        }
    };

    /// <summary>
    ///     UFortMovementSet's RepLayout, as far as SpeedMultiplier (handles 91-92).
    ///
    ///     A GameplayAttribute is NOT one handle - FFortGameplayAttributeData is a plain struct with
    ///     no native NetSerialize, so FRepLayout recurses into all NINE of its members
    ///     (BaseValue, CurrentValue, Minimum, Maximum, three clamp bools, and the two Unclamped
    ///     values). Eleven attributes at nine handles each is why SpeedMultiplier lands at 91.
    ///
    ///     The first 90 handles are carried by name only, generated with
    ///     `python Tools/RepHandles/rep_handles.py UFortMovementSet --from 1 --to 90`.
    /// </summary>
    private static readonly FRepPropertyDef[] MovementSetReservedNames = new[] {
         "WalkSpeed.BaseValue", "WalkSpeed.CurrentValue", "WalkSpeed.Minimum", "WalkSpeed.Maximum",
         "WalkSpeed.bIsCurrentClamped", "WalkSpeed.bIsBaseClamped", "WalkSpeed.bShouldClampBase",
         "WalkSpeed.UnclampedBaseValue", "WalkSpeed.UnclampedCurrentValue", "RunSpeed.BaseValue",
         "RunSpeed.CurrentValue", "RunSpeed.Minimum", "RunSpeed.Maximum", "RunSpeed.bIsCurrentClamped",
         "RunSpeed.bIsBaseClamped", "RunSpeed.bShouldClampBase", "RunSpeed.UnclampedBaseValue",
         "RunSpeed.UnclampedCurrentValue", "SprintSpeed.BaseValue", "SprintSpeed.CurrentValue",
         "SprintSpeed.Minimum", "SprintSpeed.Maximum", "SprintSpeed.bIsCurrentClamped",
         "SprintSpeed.bIsBaseClamped", "SprintSpeed.bShouldClampBase", "SprintSpeed.UnclampedBaseValue",
         "SprintSpeed.UnclampedCurrentValue", "FlySpeed.BaseValue", "FlySpeed.CurrentValue", "FlySpeed.Minimum",
         "FlySpeed.Maximum", "FlySpeed.bIsCurrentClamped", "FlySpeed.bIsBaseClamped",
         "FlySpeed.bShouldClampBase", "FlySpeed.UnclampedBaseValue", "FlySpeed.UnclampedCurrentValue",
         "CrouchedRunSpeed.BaseValue", "CrouchedRunSpeed.CurrentValue", "CrouchedRunSpeed.Minimum",
         "CrouchedRunSpeed.Maximum", "CrouchedRunSpeed.bIsCurrentClamped", "CrouchedRunSpeed.bIsBaseClamped",
         "CrouchedRunSpeed.bShouldClampBase", "CrouchedRunSpeed.UnclampedBaseValue",
         "CrouchedRunSpeed.UnclampedCurrentValue", "CrouchedSprintSpeed.BaseValue",
         "CrouchedSprintSpeed.CurrentValue", "CrouchedSprintSpeed.Minimum", "CrouchedSprintSpeed.Maximum",
         "CrouchedSprintSpeed.bIsCurrentClamped", "CrouchedSprintSpeed.bIsBaseClamped",
         "CrouchedSprintSpeed.bShouldClampBase", "CrouchedSprintSpeed.UnclampedBaseValue",
         "CrouchedSprintSpeed.UnclampedCurrentValue", "BackwardSpeedMultiplier.BaseValue",
         "BackwardSpeedMultiplier.CurrentValue", "BackwardSpeedMultiplier.Minimum",
         "BackwardSpeedMultiplier.Maximum", "BackwardSpeedMultiplier.bIsCurrentClamped",
         "BackwardSpeedMultiplier.bIsBaseClamped", "BackwardSpeedMultiplier.bShouldClampBase",
         "BackwardSpeedMultiplier.UnclampedBaseValue", "BackwardSpeedMultiplier.UnclampedCurrentValue",
         "JumpHeight.BaseValue", "JumpHeight.CurrentValue", "JumpHeight.Minimum", "JumpHeight.Maximum",
         "JumpHeight.bIsCurrentClamped", "JumpHeight.bIsBaseClamped", "JumpHeight.bShouldClampBase",
         "JumpHeight.UnclampedBaseValue", "JumpHeight.UnclampedCurrentValue", "GravityZScale.BaseValue",
         "GravityZScale.CurrentValue", "GravityZScale.Minimum", "GravityZScale.Maximum",
         "GravityZScale.bIsCurrentClamped", "GravityZScale.bIsBaseClamped", "GravityZScale.bShouldClampBase",
         "GravityZScale.UnclampedBaseValue", "GravityZScale.UnclampedCurrentValue",
         "VehicleGravityZScale.BaseValue", "VehicleGravityZScale.CurrentValue", "VehicleGravityZScale.Minimum",
         "VehicleGravityZScale.Maximum", "VehicleGravityZScale.bIsCurrentClamped",
         "VehicleGravityZScale.bIsBaseClamped", "VehicleGravityZScale.bShouldClampBase",
         "VehicleGravityZScale.UnclampedBaseValue", "VehicleGravityZScale.UnclampedCurrentValue"
    }.Select(name => Reserved(name)).ToArray();

    /// <summary>
    ///     A GameplayAttribute's BaseValue and CurrentValue, at the two handles it owns. They are
    ///     always sent together: a client that took only one would have an attribute whose base and
    ///     current disagree.
    /// </summary>
    private static IEnumerable<FRepPropertyDef> Attribute(string name, int baseHandle, Func<UFortMovementSet, float> get) {
        yield return new FRepPropertyDef { Name = $"{name}.BaseValue", Kind = ERepPropertyKind.Float, GetFloatValue = obj => get((UFortMovementSet) obj) };
        yield return new FRepPropertyDef { Name = $"{name}.CurrentValue", Kind = ERepPropertyKind.Float, GetFloatValue = obj => get((UFortMovementSet) obj) };
    }

    private static readonly FRepPropertyDef[] MovementSetProps = BuildMovementSetProps();

    private static FRepPropertyDef[] BuildMovementSetProps() {
        // Start from the derived names, then replace the two value slots of each attribute we
        // actually send. Handles are 1-based: attribute N occupies 9N+1 .. 9N+9.
        var props = MovementSetReservedNames.ToList();

        void Send(int baseHandle, string name, Func<UFortMovementSet, float> get) {
            var replacement = Attribute(name, baseHandle, get).ToArray();
            props[baseHandle - 1] = replacement[0];
            props[baseHandle] = replacement[1];
        }

        Send(1, "WalkSpeed", set => set.WalkSpeed);
        Send(10, "RunSpeed", set => set.RunSpeed);
        Send(19, "SprintSpeed", set => set.SprintSpeed);
        Send(37, "CrouchedRunSpeed", set => set.CrouchedRunSpeed);
        Send(46, "CrouchedSprintSpeed", set => set.CrouchedSprintSpeed);
        Send(55, "BackwardSpeedMultiplier", set => set.BackwardSpeedMultiplier);
        Send(64, "JumpHeight", set => set.JumpHeight);

        return props.Concat(new FRepPropertyDef[] {
        new() {
            // 91, 0x01C0. BaseValue and CurrentValue are sent together: a client that took only one
            // of them would have an attribute whose base and current disagree.
            Name = "SpeedMultiplier.BaseValue",
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((UFortMovementSet) obj).SpeedMultiplier
        },
        new() {
            Name = "SpeedMultiplier.CurrentValue", // 92
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((UFortMovementSet) obj).SpeedMultiplier
        }
        }).ToArray();
    }

    /// <summary>
    ///     UFortPlayerAttrSet, handles 1-36, derived with
    ///     Tools/RepHandles/rep_handles.py UFortPlayerAttrSet. Nine handles per attribute, in
    ///     offset order: Stamina 1, StaminaRegenRate 10, StaminaRegenDelay 19, MaxStamina 28.
    ///
    ///     Everything past MaxStamina is left undeclared - the handle stream is positional, so a
    ///     table only has to reach the furthest handle it actually sends.
    /// </summary>
    private static readonly FRepPropertyDef[] PlayerAttrSetProps = BuildPlayerAttrSetProps();

    private static FRepPropertyDef[] BuildPlayerAttrSetProps() {
        var props = new List<FRepPropertyDef> {
            Reserved("Stamina.BaseValue"),
            Reserved("Stamina.CurrentValue"),
            Reserved("Stamina.Minimum"),
            Reserved("Stamina.Maximum"),
            Reserved("Stamina.bIsCurrentClamped"),
            Reserved("Stamina.bIsBaseClamped"),
            Reserved("Stamina.bShouldClampBase"),
            Reserved("Stamina.UnclampedBaseValue"),
            Reserved("Stamina.UnclampedCurrentValue"),
            Reserved("StaminaRegenRate.BaseValue"),
            Reserved("StaminaRegenRate.CurrentValue"),
            Reserved("StaminaRegenRate.Minimum"),
            Reserved("StaminaRegenRate.Maximum"),
            Reserved("StaminaRegenRate.bIsCurrentClamped"),
            Reserved("StaminaRegenRate.bIsBaseClamped"),
            Reserved("StaminaRegenRate.bShouldClampBase"),
            Reserved("StaminaRegenRate.UnclampedBaseValue"),
            Reserved("StaminaRegenRate.UnclampedCurrentValue"),
            Reserved("StaminaRegenDelay.BaseValue"),
            Reserved("StaminaRegenDelay.CurrentValue"),
            Reserved("StaminaRegenDelay.Minimum"),
            Reserved("StaminaRegenDelay.Maximum"),
            Reserved("StaminaRegenDelay.bIsCurrentClamped"),
            Reserved("StaminaRegenDelay.bIsBaseClamped"),
            Reserved("StaminaRegenDelay.bShouldClampBase"),
            Reserved("StaminaRegenDelay.UnclampedBaseValue"),
            Reserved("StaminaRegenDelay.UnclampedCurrentValue"),
            Reserved("MaxStamina.BaseValue"),
            Reserved("MaxStamina.CurrentValue"),
            Reserved("MaxStamina.Minimum"),
            Reserved("MaxStamina.Maximum"),
            Reserved("MaxStamina.bIsCurrentClamped"),
            Reserved("MaxStamina.bIsBaseClamped"),
            Reserved("MaxStamina.bShouldClampBase"),
            Reserved("MaxStamina.UnclampedBaseValue"),
            Reserved("MaxStamina.UnclampedCurrentValue"),
        };

        // Base and Current always travel together: a client that took only one would have an
        // attribute whose base and current disagree.
        void Send(int baseHandle, string name, Func<UFortPlayerAttrSet, float> get) {
            props[baseHandle - 1] = new FRepPropertyDef {
                Name = $"{name}.BaseValue", Kind = ERepPropertyKind.Float,
                GetFloatValue = obj => get((UFortPlayerAttrSet) obj)
            };
            props[baseHandle] = new FRepPropertyDef {
                Name = $"{name}.CurrentValue", Kind = ERepPropertyKind.Float,
                GetFloatValue = obj => get((UFortPlayerAttrSet) obj)
            };
        }

        Send(1, "Stamina", set => set.Stamina);
        Send(10, "StaminaRegenRate", set => set.StaminaRegenRate);
        Send(19, "StaminaRegenDelay", set => set.StaminaRegenDelay);
        Send(28, "MaxStamina", set => set.MaxStamina);

        return props.ToArray();
    }

    /// <summary>
    ///     UFortBuildingActorSet (: UFortHealthSet), the attribute set a placed building keeps its
    ///     health in - see UFortBuildingActorSet's doc comment for why a building's set travels
    ///     differently from the PlayerState's ten.
    ///
    ///     Handles from `python Tools/RepHandles/rep_handles.py UFortHealthSet`: every
    ///     FFortGameplayAttributeData is nine handles wide, so Health starts at 1 and MaxHealth at
    ///     10, exactly the nine-wide stride UFortPlayerAttrSet above already depends on. Only the
    ///     first two of each nine (BaseValue, CurrentValue) are sent; the remaining seven are the
    ///     clamping bookkeeping, which the client recomputes.
    /// </summary>
    private static readonly FRepPropertyDef[] BuildingActorSetProps = BuildBuildingActorSetProps();

    private static FRepPropertyDef[] BuildBuildingActorSetProps() {
        var props = new List<FRepPropertyDef>();
        foreach (var name in new[] { "Health", "MaxHealth" }) {
            foreach (var member in new[] {
                         "BaseValue", "CurrentValue", "Minimum", "Maximum", "bIsCurrentClamped",
                         "bIsBaseClamped", "bShouldClampBase", "UnclampedBaseValue", "UnclampedCurrentValue"
                     }) {
                props.Add(Reserved($"{name}.{member}"));
            }
        }

        // Four leaves per attribute, exactly as the player's health set sends them and for the same
        // measured reason - see BuildHealthSetProps above for the GetAll output that showed the
        // unclamped pair left at 0 behind a correct BaseValue/CurrentValue. A building's set is the
        // same class, so it had the same hole.
        void Send(int baseHandle, string name, Func<UFortBuildingActorSet, float> get) {
            float Value(object obj) => get((UFortBuildingActorSet) obj);

            props[baseHandle - 1] = new FRepPropertyDef {
                Name = $"{name}.BaseValue", Kind = ERepPropertyKind.Float, GetFloatValue = Value
            };
            props[baseHandle] = new FRepPropertyDef {
                Name = $"{name}.CurrentValue", Kind = ERepPropertyKind.Float, GetFloatValue = Value
            };
            props[baseHandle + 6] = new FRepPropertyDef {
                Name = $"{name}.UnclampedBaseValue", Kind = ERepPropertyKind.Float, GetFloatValue = Value
            };
            props[baseHandle + 7] = new FRepPropertyDef {
                Name = $"{name}.UnclampedCurrentValue", Kind = ERepPropertyKind.Float, GetFloatValue = Value
            };
        }

        Send(1, "Health", set => set.Health);
        Send(10, "MaxHealth", set => set.MaxHealth);

        return props.ToArray();
    }

    /// <summary>
    ///     UFortHealthSet - the PLAYER's health and shield, handles 1-36, derived with
    ///     `python Tools/RepHandles/rep_handles.py UFortHealthSet`: Health 1, MaxHealth 10,
    ///     CurrentShield 19, Shield (the shield CAP - see UFortHealthSet on that name) 28. Nine
    ///     handles per attribute, of which only BaseValue and CurrentValue are sent.
    ///
    ///     Structurally identical to BuildingActorSetProps below, and deliberately so: it is the
    ///     same class, sent to the same client, through the same sub-object content block. The one
    ///     difference is WHOSE - this set hangs off the PlayerState, where it is a stably named
    ///     default subobject the client already built for itself.
    /// </summary>
    private static readonly FRepPropertyDef[] HealthSetProps = BuildHealthSetProps();

    private static FRepPropertyDef[] BuildHealthSetProps() {
        var props = new List<FRepPropertyDef>();
        foreach (var name in new[] { "Health", "MaxHealth", "CurrentShield", "Shield" }) {
            foreach (var member in new[] {
                         "BaseValue", "CurrentValue", "Minimum", "Maximum", "bIsCurrentClamped",
                         "bIsBaseClamped", "bShouldClampBase", "UnclampedBaseValue", "UnclampedCurrentValue"
                     }) {
                props.Add(Reserved($"{name}.{member}"));
            }
        }

        // FOUR leaves per attribute travel together, not two. BaseValue/CurrentValue for the same
        // reason as UFortPlayerAttrSet (a client holding only one would have an attribute whose base
        // and current disagree) - and UnclampedBaseValue/UnclampedCurrentValue because a live client
        // proved they matter. `GetAll FortRegenHealthSet Health` on a damaged player reported:
        //
        //   bShouldClampBase=True, UnclampedBaseValue=0.000000, UnclampedCurrentValue=0.000000,
        //   BaseValue=59.260422, CurrentValue=59.260422
        //
        // i.e. the damage arrived exactly (59.260422 was the server's value to the last digit - so
        // this push is NOT where the health-bar problem lives), but the struct was left internally
        // inconsistent: every healthy attribute in that same dump - the AI proxy's, built by the
        // client itself - reads UnclampedBaseValue == UnclampedCurrentValue == BaseValue ==
        // CurrentValue. Fortnite's own FFortGameplayAttributeData keeps the unclamped pair as the
        // raw value behind the clamp, and it is what bShouldClampBase clamps FROM.
        void Send(int baseHandle, string name, Func<UFortHealthSet, float> get) {
            float Value(object obj) => get((UFortHealthSet) obj);

            props[baseHandle - 1] = new FRepPropertyDef {
                Name = $"{name}.BaseValue", Kind = ERepPropertyKind.Float, GetFloatValue = Value
            };
            props[baseHandle] = new FRepPropertyDef {
                Name = $"{name}.CurrentValue", Kind = ERepPropertyKind.Float, GetFloatValue = Value
            };
            // +7 and +8 from the attribute's base handle - the last two of its nine leaves.
            props[baseHandle + 6] = new FRepPropertyDef {
                Name = $"{name}.UnclampedBaseValue", Kind = ERepPropertyKind.Float, GetFloatValue = Value
            };
            props[baseHandle + 7] = new FRepPropertyDef {
                Name = $"{name}.UnclampedCurrentValue", Kind = ERepPropertyKind.Float, GetFloatValue = Value
            };
        }

        Send(1, "Health", set => set.Health);
        Send(10, "MaxHealth", set => set.MaxHealth);
        Send(19, "CurrentShield", set => set.CurrentShield);
        Send(28, "Shield", set => set.Shield);

        return props.ToArray();
    }

    /// <summary>
    ///     ABuildingActor's own replicated properties, handles 16 onward - derived with
    ///     `python Tools/RepHandles/rep_handles.py ABuildingActor`, and re-checked against
    ///     ABuildingSMActor (the class a real PBWA_* piece actually derives from) to confirm the
    ///     numbering is unchanged there: ABuildingActor's own properties come first either way, and
    ///     ABuildingSMActor's start at 38.
    ///
    ///     Only three are sent. ReplicatedBuildingAttributeSet (19) is the object reference that
    ///     points the client at the health values; bDestroyed (24) is what tells it the piece was
    ///     destroyed rather than merely going out of relevance - it is Net + RepNotify on the real
    ///     class, so the client has an OnRep to run off it; bPlayerPlaced (25) marks the piece as
    ///     player-built. Everything else is reserved to hold the handle numbering.
    ///
    ///     NOT live-verified yet, and the one thing to reach for first if a health bar does not
    ///     appear: ReplicatedAbilitySystemComponent (20), left reserved here. The bet this makes is
    ///     that the client reads a building's health straight off the attribute set it caches in
    ///     OnRep_BuildingAttributeSet - which is why that RepNotify exists as its own function
    ///     separate from OnRep_AbilitySystemComponent. If it instead goes through GAS proper
    ///     (UAbilitySystemComponent::GetNumericAttribute searches SpawnedAttributes, and an empty
    ///     one reads every attribute as zero - the exact failure that once left walk speed clamped
    ///     at 1 uu/s, see UFortAttributeSet), then the building needs its own ASC introducing the
    ///     set, the same way APlayerState's does. That is the next lever, not a rewrite.
    /// </summary>
    private static readonly int HealthBarDifficultyRating =
        int.TryParse(Environment.GetEnvironmentVariable("HEALTH_BAR_DIFFICULTY"), out var rating) ? rating : 1;

    private static readonly FRepPropertyDef[] BuildingActorProps = ActorProps.Concat(new FRepPropertyDef[] {
        Reserved("OwnerPersistentID", ERepPropertyKind.Int32),                    // 16
        new() { Name = "InitialOverlappingVehicles", Kind = ERepPropertyKind.EmptyDynamicArray }, // 17
        Reserved("CurrentBuildingLevel", ERepPropertyKind.Int32),                 // 18
        new() {
            // Written only after the sub-object content block carrying the set has gone out on this
            // same channel, so the reference resolves. See UActorChannel.ReplicateBuildingAttributeSet
            // for the ordering, and for the MarkPropertyDirty that repairs the case where the
            // actor's own initial push got here first.
            Name = "ReplicatedBuildingAttributeSet",                              // 19
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((ABuildingActor) obj).BuildingAttributeSet
        },
        new() {
            // Sent for the same reason handle 19 is, and with the same ordering care: the sub-object
            // block carrying the component goes out before the actor's own bunch, so this reference
            // resolves. Without it the attribute set is inert - see ABuildingActor.AbilitySystemComponent.
            Name = "ReplicatedAbilitySystemComponent",                            // 20
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((ABuildingActor) obj).AbilitySystemComponent
        },
        new() {
            // The ONLY replicated property on the whole class whose name mentions the health bar.
            // Its siblings that gate the indicator (bUseFortHealthBarIndicator, bSurpressHealthBar)
            // are Edit-only Blueprint defaults and cannot be sent at all, so this is the single
            // lever the wire has over the health-bar indicator. Sent non-zero on the chance the
            // client only builds an indicator for a piece it has been given a rating for; costs one
            // int32 once per building, and sits well inside the handle range already proven correct
            // (bDestroyed at 24 works). HEALTH_BAR_DIFFICULTY overrides it without a rebuild.
            Name = "HealthBarIndicatorDifficultyRating",                          // 21
            Kind = ERepPropertyKind.Int32,
            GetIntValue = _ => HealthBarDifficultyRating
        },
        Reserved("ForceMetadataRelevant"),                                        // 22
        Reserved("bIsInvulnerable"),                                              // 23
        new() {
            Name = "bDestroyed",                                                  // 24
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((ABuildingActor) obj).bDestroyed ? 1 : 0)
        },
        new() {
            Name = "bPlayerPlaced",                                               // 25
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((ABuildingActor) obj).bPlayerPlaced ? 1 : 0)
        },
        Reserved("bDestroyOnPlayerBuildingPlacement"),                            // 26
        Reserved("bDoNotBlockBuildings"),                                         // 27
        Reserved("bForceBlockBuildings"),                                         // 28
        Reserved("bUseCentroidForBlockBuildingsCheck"),                           // 29
        Reserved("bInstantDeath"),                                                // 30
        Reserved("bCollisionBlockedByPawns"),                                     // 31
        Reserved("bForceReplayRollback"),                                         // 32
        Reserved("TeamIndex", ERepPropertyKind.ByteEnum),                           // 33
        Reserved("AssociatedMissionParam", ERepPropertyKind.ObjectRef),           // 34
        Reserved("OriginatingPlacementActor", ERepPropertyKind.ObjectRef),        // 35
        Reserved("CustomState", ERepPropertyKind.String),                         // 36
        Reserved("BaselineScale", ERepPropertyKind.Float),                        // 37

        // ------------------------------------------------------------------ ABuildingSMActor
        Reserved("TextureData[0]", ERepPropertyKind.ObjectRef),                   // 38
        Reserved("TextureData[1]", ERepPropertyKind.ObjectRef),                   // 39
        Reserved("TextureData[2]", ERepPropertyKind.ObjectRef),                   // 40
        Reserved("TextureData[3]", ERepPropertyKind.ObjectRef),                   // 41
        Reserved("StaticMesh", ERepPropertyKind.ObjectRef),                       // 42
        Reserved("AltMeshIdx", ERepPropertyKind.Int32),                           // 43
        Reserved("ResourceType", ERepPropertyKind.ByteEnum),                      // 44
        new() {
            // 45. See ABuildingActor.bMirrored - carries ServerEditBuildingActor's own bMirrored
            // parameter through to the client for a piece whose edit pattern is left/right-handed.
            Name = "bMirrored",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((ABuildingActor) obj).bMirrored ? 1 : 0)
        },
        Reserved("bNoCameraCollision"),                                           // 46
        Reserved("bNoCollision"),                                                 // 47
        Reserved("bNoPhysicsCollision"),                                          // 48
        Reserved("bSupportsRepairing"),                                           // 49
        Reserved("bAttachmentPlacementBlockedFront"),                             // 50
        Reserved("bHiddenDueToTrapPlacement"),                                    // 51
        Reserved("bAttachmentPlacementBlockedBack"),                              // 52
        Reserved("bIsForPreviewing"),                                             // 53
        new() {
            Name = "bUnderConstruction",                                          // 54
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((ABuildingActor) obj).bUnderConstruction ? 1 : 0)
        },
        Reserved("bUnderRepair"),                                                 // 55
        new() {
            Name = "bIsInitiallyBuilding",                                        // 56
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((ABuildingActor) obj).bIsInitiallyBuilding ? 1 : 0)
        },
        new() {
            Name = "BuildingAnimation",                                           // 57
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = (int) EBuildingAnim.EBA_MAX,
            GetByteValue = obj => (byte) ((ABuildingActor) obj).BuildingAnimation
        },
        // A building's scale, and the ONLY way a client ever learns a piece is MIRRORED. Round 58
        // established what mirroring is - real ABuildingSMActor::SetMirrored just forces the sign of
        // RelativeScale3D.X - and sent that scale in the actor's spawn bunch, which changed nothing:
        // `ABuildingSMActor` has its own replicated scale with its own RepNotify,
        // `OnRep_ReplicatedDrawScale3D`, and that is what the client applies to the mesh. A scale sent
        // only in the spawn bunch is overwritten by whatever this property says, and this property was
        // `Reserved` - declared, never sent - so every mirrored piece arrived at scale (1,1,1) and drew
        // the unmirrored half. That is the "preview and result are complementary halves of one stair"
        // report. bMirrored (handle 45) still goes out too, as the real server does, but it has no
        // OnRep and drives nothing visual on its own.
        new() {
            Name = "ReplicatedDrawScale3D",                                       // 58
            Kind = ERepPropertyKind.VectorQuantize100,
            GetVectorValue = obj => ((ABuildingActor) obj).GetActorScale3D()
        },

        // MinimalReplicationProxy recurses into its four leaves rather than occupying one handle.
        // PROVEN, not assumed: its TCppStructOps vtable in a real 10.40 memory dump has
        // HasNetSerializer as `xor al,al; ret` (found via the FStructParams -> NewStructOps ->
        // vtable route, struct size 0x18 and alignment 8 both matching). This mattered - had it
        // been NetSerializeNative it would be ONE handle, handles 60-62 would not exist at all, and
        // writing them would have killed the connection outright the way the weapon handle-36 bug
        // once did.
        new() {
            // NOT a float on the wire despite its single float member - FQuantizedBuildingAttribute
            // is STRUCT_NetSerializeNative (proven from the client's own CppStructOps vtable) and
            // writes 16 quantized bits. See ERepPropertyKind.QuantizedBuildingAttribute for the
            // decoded format and for what sending it as a 32-bit float did to everything after it.
            Name = "MinimalReplicationProxy.BuildTime",                           // 59
            Kind = ERepPropertyKind.QuantizedBuildingAttribute,
            GetFloatValue = obj => ((ABuildingActor) obj).BuildTime
        },
        Reserved("MinimalReplicationProxy.RepairTime", ERepPropertyKind.QuantizedBuildingAttribute), // 60
        new() {
            Name = "MinimalReplicationProxy.Health",                              // 61
            Kind = ERepPropertyKind.Int16,
            GetIntValue = obj => ((ABuildingActor) obj).CurrentHitPoints
        },
        new() {
            Name = "MinimalReplicationProxy.MaxHealth",                           // 62
            Kind = ERepPropertyKind.Int16,
            GetIntValue = obj => ((ABuildingActor) obj).MaxHitPoints
        },
        Reserved("BuildingReplacementType", ERepPropertyKind.ByteEnum),           // 63
        new() {
            // 64 - who has this piece locked into the edit tool right now, or null. See
            // ABuildingActor.EditingPlayer and NativeRpcHandlers' Server*EditingBuildingActor family.
            Name = "EditingPlayer",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((ABuildingActor) obj).EditingPlayer
        },
        Reserved("DamagerOwner", ERepPropertyKind.ObjectRef),                     // 65
        Reserved("RelevantBASE", ERepPropertyKind.ObjectRef),                     // 66
        new() {
            Name = "ProxyGameplayCueDamagePhysical.ProxyGameplayCueDamagePhysicalMagnitude", // 67
            Kind = ERepPropertyKind.Float,
            GetFloatValue = obj => ((ABuildingActor) obj).DamageMagnitude
        }
        // 68 is ProxyGameplayCueDamagePhysical.EffectContext, an FGameplayEffectContextHandle -
        // atomic, format not decoded, deliberately not declared and never written.
    }).ToArray();

    /// <summary>
    ///     ABuildingContainer - a chest or ammo box. Derives from ABuildingSMActor, so it inherits
    ///     every building handle unchanged and adds its own at 69-77.
    ///
    ///     Handle 68 has to be DECLARED here even though it is never sent: handles are positional, and
    ///     BuildingActorProps stops at 67 with 68 left as a comment. Skipping it would renumber
    ///     everything after it by one - the exact failure mode [[rep-handle-derivation]] warns about.
    ///
    ///     FSearchBounceData has no native NetSerialize, so RepLayout recurses and its two members are
    ///     handles 75 and 76 rather than the struct taking one.
    /// </summary>
    private static readonly FRepPropertyDef[] BuildingContainerProps = BuildingActorProps.Concat(new FRepPropertyDef[] {
        Reserved("ProxyGameplayCueDamagePhysical.EffectContext", ERepPropertyKind.StructAtomic), // 68
        Reserved("SearchedMesh", ERepPropertyKind.ObjectRef),                     // 69, 0x0B30
        new() {
            Name = "ReplicatedLootTier",                                          // 70, 0x0B64
            Kind = ERepPropertyKind.Int32,
            GetIntValue = obj => ((ABuildingContainer) obj).ReplicatedLootTier
        },
        new() {
            // 71, 0x0C41 - the one that matters. OnRep_bAlreadySearched swaps in the opened mesh.
            Name = "bAlreadySearched",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((ABuildingContainer) obj).bAlreadySearched ? 1 : 0)
        },
        Reserved("bBuriedTreasure"),                                              // 72, 0x0C42
        Reserved("bHasRaisedTreasure"),                                           // 73, 0x0C42
        Reserved("bRegenerateLoot"),                                              // 74, 0x0C42
        Reserved("SearchBounceData.BounceNormal", ERepPropertyKind.StructAtomic),  // 75, 0x0C48
        new() {
            // 76 - the open ANIMATION. The client watches for a change, not a value.
            Name = "SearchBounceData.SearchAnimationCount",
            Kind = ERepPropertyKind.Int32,
            GetIntValue = obj => (int) ((ABuildingContainer) obj).SearchAnimationCount
        },
        Reserved("TimeUntilLootRegenerates", ERepPropertyKind.Float)              // 77, 0x0CA4
    }).ToArray();

    /// <summary>
    ///     ABuildingWall - a wall with a door. Also derives from ABuildingSMActor, so it too has to
    ///     re-declare handle 68 (see BuildingContainerProps for why) and then takes 69-74 of its own.
    /// </summary>
    private static readonly FRepPropertyDef[] BuildingWallProps = BuildingActorProps.Concat(new FRepPropertyDef[] {
        Reserved("ProxyGameplayCueDamagePhysical.EffectContext", ERepPropertyKind.StructAtomic), // 68
        Reserved("DoorMesh", ERepPropertyKind.ObjectRef),                         // 69, 0x0B78
        new() {
            // 70, 0x0BD0 - an FRotator, and the answer to "how far, and which way".
            //
            // bDoorOpen alone tells the client THAT the door is open, not what open looks like. Left
            // unsent this stays at whatever the client's own default is, and the swing came out wrong
            // - the door appeared to turn right round. Sent as a plain FRotator NetSerialize, the same
            // path AFortAthenaAircraft's rotation already uses.
            //
            // DOOR_ROT_OFFSET=0 stops sending it. That escape hatch exists because handle 70 has never
            // been on the wire before, and a property written at the wrong WIDTH does not look wrong -
            // it silently drops the connection a few ticks later.
            Name = "DoorDesiredRotOffset",
            Kind = ERepPropertyKind.Rotator,
            GetRotatorValue = obj => ((ABuildingWall) obj).DoorDesiredRotOffset
        },
        Reserved("DoorDesiredXLocation", ERepPropertyKind.Float),                 // 71, 0x0C0C
        Reserved("SlidingDoorDesiredXLocation", ERepPropertyKind.Float),          // 72, 0x0C10
        new() {
            // 73, 0x0C25 - OnRep_bDoorOpen swings the mesh. The client predicts the swing locally
            // (bLocalDoorOpen) and runs VerifyDoorOpenMatchesServer, so this really has to come back.
            Name = "bDoorOpen",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((ABuildingWall) obj).bDoorOpen ? 1 : 0)
        },
        new() {
            Name = "bDoorCollisionDisabled",                                      // 74, 0x0C27
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((ABuildingWall) obj).bDoorCollisionDisabled ? 1 : 0)
        }
    }).ToArray();

    public static readonly FRepLayout PlayerAttrSet = new(PlayerAttrSetProps);
    public static readonly FRepLayout MovementSet = new(MovementSetProps);
    public static readonly FRepLayout BuildingActorSet = new(BuildingActorSetProps);
    public static readonly FRepLayout HealthSet = new(HealthSetProps);

    public static readonly FRepLayout AbilitySystemComponent = new(AbilitySystemComponentProps);

    public static readonly FRepLayout Actor = new(ActorProps);
    public static readonly FRepLayout Controller = new(ControllerProps);
    public static readonly FRepLayout PlayerController = new(PlayerControllerProps);
    public static readonly FRepLayout Pawn = new(PawnProps);
    public static readonly FRepLayout GameState = new(GameStateProps);
    public static readonly FRepLayout Aircraft = new(AircraftProps);
    public static readonly FRepLayout SafeZoneIndicator = new(SafeZoneIndicatorProps);
    public static readonly FRepLayout PlayerState = new(PlayerStateProps);

    public static readonly FRepLayout Inventory = new(InventoryProps);
    public static readonly FRepLayout BroadcastRemoteClientInfo = new(BroadcastRemoteClientInfoProps);
    public static readonly FRepLayout Pickup = new(PickupProps);
    public static readonly FRepLayout Weapon = new(WeaponProps);

    public static readonly FRepLayout BuildingActor = new(BuildingActorProps);
    public static readonly FRepLayout BuildingContainer = new(BuildingContainerProps);
    public static readonly FRepLayout BuildingWall = new(BuildingWallProps);

    public static FRepLayout Get(AActor actor) => actor switch {
        // Before ABuildingActor: a container IS one, and the first arm wins.
        ABuildingContainer => BuildingContainer,
        ABuildingWall => BuildingWall,
        ABuildingActor => BuildingActor,
        AFortAthenaAircraft => Aircraft,
        AFortSafeZoneIndicator => SafeZoneIndicator,
        AFortPickup => Pickup,
        AFortWeapon => Weapon,
        AFortInventory => Inventory,
        AFortBroadcastRemoteClientInfo => BroadcastRemoteClientInfo,
        APlayerController => PlayerController,
        AController => Controller,
        APawn => Pawn,
        AGameState => GameState,
        APlayerState => PlayerState,
        _ => Actor
    };
}
