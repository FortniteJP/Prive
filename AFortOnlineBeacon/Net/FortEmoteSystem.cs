namespace AFortOnlineBeacon.Net;

/// <summary>
///     Playing an emote - the server half of AFortPlayerController::ServerPlayEmoteItem.
///
///     Real Fortnite does this with one native call, UAbilitySystemComponent::GiveAbilityAndActivateOnce,
///     which every injected reference server (Project-Reboot-3.0, Erbium, Nebula, Magnesium) simply
///     invokes by address. Nothing here can call it, so this reproduces what that call PUTS ON THE
///     WIRE, which a real Project-Reboot-3.0 capture spells out end to end
///     (PriveDev\PacketCaptures / PacketProxy, packet #2979, client log 19:58:18.231-.232):
///
///         1. the emote spec is added to UAbilitySystemComponent::ActivatableAbilities -
///            Ability = /Game/Abilities/Emotes/GAB_Emote_Generic.Default__GAB_Emote_Generic_C,
///            SourceObject = the emote asset the client just named (EID_KPopDance03 in the capture),
///            both exported by PATH because neither is anything the server spawned;
///         2. UAbilitySystemComponent::ClientActivateAbilitySucceed(Handle, PredictionKey) on the
///            PlayerState's channel, with a SERVER-INITIATED prediction key.
///
///     The client's log then reads "GAB_Emote_Generic_C_2147474546 Activated" and streams the
///     montage in. Order does not matter: ClientActivateAbilitySucceed arrived FIRST in that capture,
///     and UAbilitySystemComponent::ClientActivateAbilitySucceedWithEventData_Implementation parks an
///     activation whose handle it cannot find yet in PendingServerActivatedAbilities and retries it
///     from OnRep_ActivateAbilities. The grant is still flushed first here, because it costs nothing.
///
///     Why the client can be trusted to run the ability at all: GAB_Emote_Generic's NetExecutionPolicy
///     is ServerInitiated, not LocalPredicted. That is what makes ClientActivateAbilitySucceed take
///     the "we haven't already executed this ability, so kick it off" branch (AbilitySystemComponent_Abilities.cpp:1764)
///     instead of looking for a locally predicted instance that a server-started emote never has.
///     The capture proves it: the ability activates on a key it never predicted.
///
///     What this does NOT do, and cannot: read the emote ASSET. bMovingEmote, bMoveForwardOnly and
///     WalkForwardSpeed are properties of the UAthenaDanceItemDefinition, which lives in an encrypted
///     PAK this server has no reader for - the reference servers copy them off the loaded asset in
///     the client's own memory. So a moving emote (Electro Swing, Ride the Pony) dances on the spot
///     here instead of walking. Everything else about it is identical.
/// </summary>
public static class FortEmoteSystem {
    /// <summary>
    ///     The generic emote ability's class default object, by path - confirmed verbatim on the wire
    ///     (capture export guid 9089, "RegisterNetGUIDFromPath_Client: NetGUID: 9089, PathName:
    ///     Default__GAB_Emote_Generic_C, OuterGUID: 9091").
    ///
    ///     The CDO, not the class: FGameplayAbilitySpec::Ability is a UGameplayAbility* and every
    ///     reference server passes `Class->DefaultObject`. Naming the _C class instead would give the
    ///     client an object of the wrong type for that pointer.
    /// </summary>
    private const string EmoteAbilityPath = "/Game/Abilities/Emotes/GAB_Emote_Generic.Default__GAB_Emote_Generic_C";

    /// <summary>Sprays go through the same RPC on 10.40 (there is no ServerPlaySprayItem in the ClassNetCache) but a different ability.</summary>
    private const string SprayAbilityPath = "/Game/Abilities/Sprays/GAB_Spray_Generic.Default__GAB_Spray_Generic_C";

    /// <summary>
    ///     Real UE's FPredictionKey::GenerateNewPredictionKey - a process-wide counter starting at 1,
    ///     stamped bIsServerInitiated for a key the SERVER created
    ///     (FPredictionKey::CreateNewServerInitiatedKey, GameplayPrediction.cpp). The capture's very
    ///     first emote carried Current=1, which is exactly this counter's first value.
    /// </summary>
    private static short _nextServerPredictionKey = 1;

    /// <summary>
    ///     Which ability plays this cosmetic. A real server switches on the asset's CLASS
    ///     (UAthenaSprayItemDefinition / UAthenaToyItemDefinition / UAthenaDanceItemDefinition); the
    ///     class is not on the wire, so this switches on the asset's own folder, which is what names
    ///     the class in practice - /Sprays/SPID_*, /Toys/TOY_*, /Dances/ and /VictoryPoses/ and
    ///     /ConsumableEmotes/ EID_*.
    ///
    ///     A toy returns null on purpose. Its ability is not a fixed asset at all: it is the
    ///     TSoftClassPtr UAthenaToyItemDefinition::ToySpawnAbility, i.e. a per-toy value stored INSIDE
    ///     the asset, so there is nothing to name without reading it. Guessing one would grant the
    ///     wrong ability rather than none.
    /// </summary>
    private static string? AbilityPathFor(string emoteAssetPath) {
        if (emoteAssetPath.Contains("/Sprays/", StringComparison.OrdinalIgnoreCase)) return SprayAbilityPath;
        if (emoteAssetPath.Contains("/Toys/", StringComparison.OrdinalIgnoreCase)) return null;
        return EmoteAbilityPath;
    }

    /// <summary>
    ///     AFortPlayerController::ServerPlayEmoteItem's body. <paramref name="emoteAssetPath"/> is the
    ///     emote asset's package path - either exported on the wire (the first time this client names
    ///     it) or recovered from the NetGUID it sent instead (every time after). See
    ///     ERpcParamKind.AssetPath for why both cases exist and what breaks if only one is handled.
    /// </summary>
    public static void PlayEmoteItem(APlayerController controller, string emoteAssetPath) {
        if (controller.Pawn is not { } pawn) {
            Console.WriteLine("FortEmoteSystem.PlayEmoteItem: no pawn to emote on, ignoring");
            return;
        }

        if (controller.PlayerState is not { AbilitySystemComponent: { } abilitySystem } playerState) {
            Console.WriteLine("FortEmoteSystem.PlayEmoteItem: the player has no AbilitySystemComponent, ignoring");
            return;
        }

        // Dead players do not dance. Real Fortnite blocks this through the ability's own
        // CanActivateAbility tag requirements, which nothing here can run.
        if (pawn.bIsDying) {
            Console.WriteLine("FortEmoteSystem.PlayEmoteItem: the pawn is dead, ignoring");
            return;
        }

        var abilityPath = AbilityPathFor(emoteAssetPath);
        if (abilityPath == null) {
            Console.WriteLine($"FortEmoteSystem.PlayEmoteItem: '{emoteAssetPath}' is a toy - its ability is " +
                              "ToySpawnAbility inside the asset, which this server cannot read. Ignoring.");
            return;
        }

        // Emoting again while already emoting is ordinary (the player picks a different wheel slot).
        // The old spec has to go first: real UE's GiveAbilityAndActivateOnce sets RemoveAfterActivation
        // on the spec it grants, so a second emote never stacks on the first.
        //
        // Note this nulls LastReplicatedEmoteExecuted and the line below immediately sets it again,
        // so a replay of the SAME emote inside one replication interval collapses to no change and
        // no RepNotify for onlookers. That is a real gap and deliberately not papered over: the
        // window is one tick, a human cannot re-trigger an emote inside it, and closing it would
        // mean forcing an out-of-band pawn push whose only job is to send a value the client is
        // about to be told to replace.
        StopEmote(controller, "a new emote started");

        var emoteAsset = UAssetRegistry.GetOrCreate(emoteAssetPath);
        var spec = abilitySystem.GrantAbility(UAssetRegistry.GetOrCreate(abilityPath), sourceObject: emoteAsset);

        pawn.ActiveEmoteAbilityHandle = spec.Handle;

        // What every OTHER client sees. Set before the grant is flushed only so both leave on the
        // same tick; they travel on different channels (the pawn's and the PlayerState's) and are
        // independent - see APawn.LastReplicatedEmoteExecuted.
        pawn.LastReplicatedEmoteExecuted = emoteAsset;

        var netDriver = controller.GetWorld()?.NetDriver;
        if (netDriver == null) return;

        // Push ActivatableAbilities NOW rather than waiting for the next replication tick. Not
        // required - the client parks an activation it cannot resolve yet and retries it from
        // OnRep_ActivateAbilities, which is exactly what happened in the reference capture - but it
        // spares the emote a round trip's worth of latency for one out-of-band call.
        netDriver.FlushAbilitySystemComponent(playerState);

        var predictionKey = new FPredictionKey {
            bValidKeyForConnection = true,
            bIsServerInitiated = true,
            Current = _nextServerPredictionKey++
        };

        netDriver.SendClientActivateAbilitySucceed(playerState, abilitySystem, spec.Handle, predictionKey);

        Console.WriteLine($"FortEmoteSystem.PlayEmoteItem: {playerState.GetFName()} plays '{emoteAssetPath}' " +
                          $"via {abilityPath[(abilityPath.LastIndexOf('.') + 1)..]} as spec handle {spec.Handle} " +
                          $"(server prediction key {predictionKey.Current})");
    }

    /// <summary>
    ///     The emote stopped. The client is what decides that - it sends ServerCancelAbility when the
    ///     player moves/jumps/shoots out of the emote and ServerEndAbility when the montage simply
    ///     runs out (UAbilitySystemComponent::ReplicateEndOrCancelAbility takes the client branch for
    ///     a ServerInitiated ability). The reference capture shows ServerCancelAbility arriving
    ///     [12.1 bytes] immediately before the client logs "GAB_Emote_Generic_C EndAbility".
    ///
    ///     Returns true when the handle really was the emote, so the caller can tell an emote ending
    ///     from any other ability ending.
    /// </summary>
    public static bool OnAbilityEnded(APlayerController controller, int abilityHandle, string source) {
        if (controller.Pawn is not { } pawn) return false;
        if (abilityHandle == 0 || pawn.ActiveEmoteAbilityHandle != abilityHandle) return false;

        StopEmote(controller, source);
        return true;
    }

    /// <summary>
    ///     Clears the emote: the granted spec goes away (real UE's RemoveAfterActivation) and
    ///     LastReplicatedEmoteExecuted goes back to null so other clients stop the montage - and so
    ///     that playing the SAME emote again is a genuine change, which a RepNotify needs.
    ///
    ///     Both are ordinary dirty-state changes; the next replication tick carries them. There is
    ///     deliberately no ClientEndAbility sent back: the client is the one that ended it.
    /// </summary>
    private static void StopEmote(APlayerController controller, string reason) {
        if (controller.Pawn is not { ActiveEmoteAbilityHandle: not 0 } pawn) return;

        var handle = pawn.ActiveEmoteAbilityHandle;
        pawn.ActiveEmoteAbilityHandle = 0;
        pawn.LastReplicatedEmoteExecuted = null;

        controller.PlayerState?.AbilitySystemComponent?.ClearAbility(handle);

        Console.WriteLine($"FortEmoteSystem.StopEmote: emote spec handle {handle} cleared ({reason})");
    }
}
