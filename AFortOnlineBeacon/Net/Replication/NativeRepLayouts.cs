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
        Reserved("Owner"), // live-probed handle 13
        new() {
            // Not swapped on send either - see RemoteRole above. Live-probed handle 14.
            Name = "Role",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = (int) ENetRole.ROLE_MAX,
            GetByteValue = obj => (byte) ((AActor) obj).Role
        },
        Reserved("Instigator") // live-probed handle 15
    };

    private static readonly FRepPropertyDef[] ControllerProps = ActorProps.Concat(new[] {
        new() {
            // Handle 16 (ActorProps' 15 + 1) - not independently live-probed, but PlayerController's
            // own TargetViewRotation landing on the predicted handle 18 (16+Pawn17+TargetViewRotation18)
            // corroborates this base.
            Name = "PlayerState",
            Kind = ERepPropertyKind.ObjectRef,
            GetObjectValue = obj => ((AController) obj).PlayerState
        },
        Reserved("Pawn") // handle 17
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
        }.Concat(Enumerable.Range(23, 50 - 23 + 1).Select(h => Reserved($"__unidentified_{h}"))) // handles 23-50 (TEMP: testing handle 51 - see conversation history, handle 50 kept producing "Invalid property terminator handle" even with a bit-perfect, self-terminating packed-int guid write)
        .Append(new FRepPropertyDef {
            // TEMP diagnostic (2026-08-25), round 2. Round 1 sent a SECOND reference to the
            // PlayerController's own PlayerState here (an object already proven to round-trip clean
            // elsewhere via this exact ObjectRef mechanism) instead of WorldInventory - the identical
            // class of "Invalid property terminator handle" error still fired (just a different
            // garbage handle value: 2, vs 3/4 for the earlier WorldInventory/PlayerState variants).
            // That rules out the referenced object/class entirely - the bug is POSITIONAL. Per the
            // AttachmentReplication precedent this session (one dump entry that was actually 6 wire
            // handles), the leading hypothesis is that handle 50 is really a DynamicArray Cmd on the
            // real client, not a flat scalar/object slot - a plain packed-int write there would
            // desync catastrophically regardless of its value, matching everything observed so far.
            // This sends the minimal, self-terminating "empty array" encoding instead (see
            // EmptyDynamicArrayProbe) to test that theory without needing to know the array's real
            // element type. If this makes the error disappear, handle 50 really is an array, and
            // WorldInventory needs to be looked for elsewhere (either right after this array's true
            // end, or somewhere in the still-unidentified 23-48 block).
            Name = "WorldInventory",
            Kind = ERepPropertyKind.EmptyDynamicArrayProbe
        }) // handle 51 (TEMP test - empty-array probe, NOT actually WorldInventory right now)
        // A prior round also appended a "__trailingDummy" Bool here at handle 52 (to test whether
        // WorldInventory being the LAST property mattered). REMOVED (2026-08-25): handle 52 is
        // live-probe-CONFIRMED to be the real OverriddenBackpackSize property, not a free slot - a
        // 1-bit dummy there was colliding with whatever OverriddenBackpackSize's real (probably
        // wider) type actually is, producing a brand new desync unrelated to WorldInventory that
        // cascaded all the way to a "ReadHandle=63 / Property=bDisplayNPCNumbers / BunchIsError"
        // failure. The array-probe fix at handle 51 above already resolved the ORIGINAL "Invalid
        // property terminator handle" failure (confirmed: that error class disappeared entirely once
        // the empty-array encoding replaced the plain ObjectRef write) - this payload should now just
        // terminate cleanly right after WorldInventory's slot.
    ).ToArray();

    private static readonly FRepPropertyDef[] PawnProps = ActorProps.Concat(new[] {
        Reserved("RemoteViewPitch"),
        Reserved("PlayerState"),
        Reserved("Controller")
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
            Name = "MatchState",
            Kind = ERepPropertyKind.Name,
            GetNameValue = obj => ((AGameState) obj).MatchState
        }
    }).ToArray();

    /// <summary>
    ///     APlayerState : AInfo : AActor (AInfo adds nothing, same as AGameState above) - its own 11
    ///     props (Score..PlayerNamePrivate) are all reserved/unpopulated, handles 16-26 following the
    ///     corrected ActorProps base. AFortPlayerState's own props start right after, but NOT in
    ///     declaration order: a UEDumper dump (still trustworthy here since none of these specific
    ///     properties are arrays) showed bIsGameSessionOwner, bIsWorldDataOwner, bHasFinishedLoading
    ///     immediately preceding bHasStartedPlaying, with bIsGameSessionAdmin/bIsReadyToContinue
    ///     coming AFTER it instead. Not independently re-verified via live probe this round (only
    ///     the ActorProps base offset was ever actually wrong - see ActorProps), but re-derived here
    ///     against the corrected base (16, not 11). Everything AFortPlayerState declares after
    ///     bHasStartedPlaying, and all of FortPlayerStateZone/PvP/Athena, is deliberately not
    ///     declared - same reasoning as GameStateProps/PlayerControllerProps.
    /// </summary>
    private static readonly FRepPropertyDef[] PlayerStateProps = ActorProps.Concat(new[] {
        Reserved("Score"),
        Reserved("PlayerID"),
        Reserved("Ping"),
        Reserved("bIsSpectator"),
        Reserved("bOnlySpectator"),
        Reserved("bIsABot"),
        Reserved("bIsInactive"),
        Reserved("bFromPreviousLevel"),
        Reserved("StartTime"),
        Reserved("UniqueId"),
        Reserved("PlayerNamePrivate"),
        Reserved("bIsGameSessionOwner"),
        Reserved("bIsWorldDataOwner"),
        Reserved("bHasFinishedLoading"),
        new() {
            Name = "bHasStartedPlaying",
            Kind = ERepPropertyKind.Bool,
            GetByteValue = obj => (byte) (((APlayerState) obj).bHasStartedPlaying ? 1 : 0)
        }
    }).ToArray();

    public static readonly FRepLayout Actor = new(ActorProps);
    public static readonly FRepLayout Controller = new(ControllerProps);
    public static readonly FRepLayout PlayerController = new(PlayerControllerProps);
    public static readonly FRepLayout Pawn = new(PawnProps);
    public static readonly FRepLayout GameState = new(GameStateProps);
    public static readonly FRepLayout PlayerState = new(PlayerStateProps);

    public static FRepLayout Get(AActor actor) => actor switch {
        APlayerController => PlayerController,
        AController => Controller,
        APawn => Pawn,
        AGameState => GameState,
        APlayerState => PlayerState,
        _ => Actor
    };
}
