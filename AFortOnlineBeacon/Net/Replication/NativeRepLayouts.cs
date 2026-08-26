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
            Reserved("bFailedToRespawn"),
            Reserved("bHasInitiallySpawned"),
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
            }
        })
        // Nothing past handle 34 is declared: this project never sends OutpostInventory (35),
        // LatestRewardReport (36-41, six handles - FFortRewardReport recurses into 3 FText + float +
        // TArray + bool) or anything after them, so no Cmd needs to exist for them.
    ).ToArray();

    /// <summary>
    ///     APawn's own properties, after AActor's 15. All three come straight from
    ///     APawn::PossessedBy on a real server (see AController.Possess) and none of them were being
    ///     sent before - a client could see the pawn actor but nothing tying it to its controller.
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
        }
    }).ToArray();

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
        Reserved("ReplicatedWorldTimeSeconds"), // live-probed handle 19
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
        Reserved("UniqueId", ERepPropertyKind.StructAtomic), // 25, offset 624 - FUniqueNetIdRepl is
                                            // one of the few structs RepLayout special-cases by name
                                            // as atomic (AddPropertyCmd, RepLayout.cpp:4409-4522)
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

    public static FRepLayout Get(AActor actor) => actor switch {
        AFortInventory => Inventory,
        APlayerController => PlayerController,
        AController => Controller,
        APawn => Pawn,
        AGameState => GameState,
        APlayerState => PlayerState,
        _ => Actor
    };
}
