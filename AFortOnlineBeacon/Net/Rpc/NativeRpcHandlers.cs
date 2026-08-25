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
            Console.WriteLine($"NativeRpcHandlers: {name} on {actor.GetFName()}");
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
                Console.WriteLine($"NativeRpcHandlers: ServerSetSpectatorLocation on {pc.GetFName()} Loc={pc.LastSpectatorSyncLocation} Rot={pc.LastSpectatorSyncRotation}");
            }
        ),
        ["ServerSetSpectatorWaiting"] = new FRpcDef(
            "ServerSetSpectatorWaiting",
            new[] { new FRpcParamDef("bWaiting", ERpcParamKind.Bool) },
            (actor, values) => Console.WriteLine($"NativeRpcHandlers: ServerSetSpectatorWaiting on {actor.GetFName()} bWaiting={values[0]}")
        ),
        ["ServerChangeName"] = new FRpcDef(
            "ServerChangeName",
            new[] { new FRpcParamDef("S", ERpcParamKind.String) },
            (actor, values) => Console.WriteLine($"NativeRpcHandlers: ServerChangeName on {actor.GetFName()} S={values[0]}")
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
        ["ServerToggleAILogging"] = NoParams("ServerToggleAILogging")
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
                if (values[2] is not FVector clientLoc) return;

                actor.SetActorLocation(clientLoc);

                var view = values[5] is uint v ? FRotator.FromPackedView(v) : null;
                Console.WriteLine($"NativeRpcHandlers: ServerMoveNoBase on {actor.GetFName()} TimeStamp={values[0]} ClientLoc={clientLoc} CompressedMoveFlags={values[3]} ClientRoll={values[4]} View={view} ClientMovementMode={values[6]}");
            }
        )
    };

    public static Dictionary<string, FRpcDef>? Get(AActor actor) => actor switch {
        APlayerController => PlayerControllerRpcs,
        APawn => PawnRpcs,
        _ => null
    };
}
