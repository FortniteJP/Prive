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
    ///     The GameplayCue that actually shows an emoji.
    ///
    ///     Found by searching every one of the 325,881 packages in the install for
    ///     `ConfigureParticleSystem` (`pakreader nameref`), the one native function on
    ///     UAthenaEmojiItemDefinition that could put a particular emoji on a particle system. Three
    ///     packages call it and all three are GameplayCue notifies: OnDisplayEmoji, OnPreviewEmoji
    ///     (the wheel) and OnDisplayChatEmoji (the squad-chat one). This is the in-world one; its
    ///     notify attaches P_Emote_Show_Emoji to the pawn's root and throws it upward.
    ///
    ///     Nothing in any Blueprint executes it - the same search finds the tag in exactly two
    ///     packages, the notify itself and the tag table - so the cue comes from the client's C++ on
    ///     the authority side, and an out-of-process server has to send it.
    /// </summary>
    private const string EmojiCueTag = "GameplayCue.Abilities.Emotes.DisplayEmoji";

    /// <summary>
    ///     0.65 SECONDS, and it is read off the animation rather than tuned by eye.
    ///
    ///     A real server does not decide when the emoji appears - the MONTAGE does. Toss_Emoji (all
    ///     153 emojis share it) carries exactly one notify, `FortPlayEmojiItem`, an absolute link at
    ///     LinkValue **0.65** into its 1.333-second segment, and it is marked
    ///     `bTriggerOnDedicatedServer: true`. That flag is the whole answer to "who fires the cue and
    ///     when": the authority plays the montage, the notify fires on it 0.65s in - as the hand
    ///     releases - and the cue goes out from there.
    ///
    ///     This server cannot play a montage, so it waits the same 0.65s instead. Sending on the
    ///     frame the emote starts is what made the first working build show the emoji EARLY, before
    ///     the throw.
    ///
    ///     Scaled by nothing, because EmoteMontagePlayRate is set to 1. EMOJI_CUE_DELAY overrides it;
    ///     0 restores the immediate send.
    /// </summary>
    private static float EmojiCueDelay =>
        float.TryParse(Environment.GetEnvironmentVariable("EMOJI_CUE_DELAY"), out var seconds)
            ? seconds
            : 0.65f;

    private readonly record struct FPendingEmojiCue(APawn Pawn, UObject EmoteAsset, float DueAt);

    private static readonly List<FPendingEmojiCue> _pendingEmojiCues = new();

    /// <summary>
    ///     Sends the emoji cues whose moment in the throw animation has arrived - the stand-in for
    ///     the montage notify this server cannot run. See <see cref="EmojiCueDelay" />.
    ///
    ///     A queued cue is DROPPED rather than sent late if the pawn stopped emoting that emoji in
    ///     the meantime: cancelling an emote interrupts the montage, so the real notify would never
    ///     have fired either. `LastReplicatedEmoteExecuted` is the same value StopEmote nulls, which
    ///     makes it the exact test - a different emoji started in between replaces the entry's asset
    ///     and correctly cancels this one.
    /// </summary>
    public static void Tick(UWorld world, float timeSeconds) {
        if (_pendingEmojiCues.Count == 0) return;

        for (var i = _pendingEmojiCues.Count - 1; i >= 0; i--) {
            var pending = _pendingEmojiCues[i];
            if (timeSeconds < pending.DueAt) continue;

            _pendingEmojiCues.RemoveAt(i);

            if (pending.Pawn.bIsDying ||
                !ReferenceEquals(pending.Pawn.LastReplicatedEmoteExecuted, pending.EmoteAsset)) {
                Console.WriteLine("FortEmoteSystem: the emoji throw was interrupted before it left the hand - " +
                                  "cue dropped, which is what the montage notify would have done.");
                continue;
            }

            // EVERY connection, not just the emoting one. This is a MULTICAST: the engine fans it
            // out to every client with the pawn's channel open, and an emoji only the thrower can
            // see is the same bug as an emote only the thrower can see. The damage cues in
            // FortDamageSystem send to ONE connection on purpose - they drive that player's own HUD
            // - which is why this does not copy their shape.
            var sent = 0;
            foreach (var connection in world.NetDriver?.ClientConnections ?? Enumerable.Empty<UNetConnection>()) {
                if (connection.FindActorChannel(pending.Pawn) is not { } pawnChannel) continue;
                pawnChannel.SendNetMulticastInvokeGameplayCueExecutedWithParams(EmojiCueTag, pending.EmoteAsset);
                sent++;
            }

            Console.WriteLine($"FortEmoteSystem: emoji cue {EmojiCueTag} -> {sent} client(s), " +
                              $"SourceObject={pending.EmoteAsset.GetFName()}" +
                              (sent == 0 ? " (nobody has the pawn's channel open - nothing will be seen)" : ""));
        }
    }

    /// <summary>
    ///     Real UE's FPredictionKey::GenerateNewPredictionKey - a process-wide counter starting at 1,
    ///     stamped bIsServerInitiated for a key the SERVER created
    ///     (FPredictionKey::CreateNewServerInitiatedKey, GameplayPrediction.cpp). The capture's very
    ///     first emote carried Current=1, which is exactly this counter's first value.
    /// </summary>
    private static short _nextServerPredictionKey = 1;

    /// <summary>
    ///     Which ability plays this cosmetic, ASKED OF THE ASSET rather than guessed from its folder.
    ///
    ///     This used to switch on the path containing /Sprays/ or /Toys/, because the item's real
    ///     UClass is not on the wire and the asset could not be read. FortEmoteAssets.Generated.cs
    ///     now carries the class AND the per-item ability for all 590 cosmetics (263 dances, 156
    ///     sprays, 18 toys, 153 EMOJIS - the last of which were absent until 2026-09-08, because
    ///     they live in a SUBFOLDER of Dances and the dump filtered on the EID_ prefix), baked out
    ///     of the paks, so both guesses can go:
    ///
    ///       * A TOY has no generic ability to fall back on - its
    ///         UAthenaToyItemDefinition::ToySpawnAbility is a different class per toy - which is why
    ///         toys were refused outright before. All 18 are now named.
    ///       * THREE dances have a bespoke UAthenaDanceItemDefinition::CustomDanceAbility
    ///         (EID_ThighSlapper, EID_VikingHorn, EID_WolfHowl) and were silently getting the
    ///         generic one instead.
    ///
    ///     An emote the table does not know still falls back to the folder rule, so a cosmetic added
    ///     later - or one the dump missed - degrades to the old behaviour rather than failing.
    /// </summary>
    private static string? AbilityPathFor(string emoteAssetPath) {
        var itemName = emoteAssetPath[(emoteAssetPath.LastIndexOf('.') + 1)..];

        if (FortEmoteAssets.For(itemName) is { } asset) {
            // A per-item ability is named as a CLASS path; the grant needs its CDO - the same
            // sibling-of-the-class rule FortWeaponActorClasses.FireAbilityFor documents.
            if (asset.CustomAbility is { } classPath) {
                var dot = classPath.LastIndexOf('.');
                return dot < 0 ? null : $"{classPath[..dot]}.Default__{classPath[(dot + 1)..]}";
            }

            return asset.Kind switch {
                FortEmoteAssets.EEmoteKind.Spray => SprayAbilityPath,
                // A toy with no ToySpawnAbility is one this server cannot play - the same refusal as
                // before, but now on the asset's own evidence rather than on its folder name.
                FortEmoteAssets.EEmoteKind.Toy => null,
                // Dance AND Emoji. UAthenaEmojiItemDefinition derives from
                // UAthenaDanceItemDefinition and plays an ordinary montage (Toss_Emoji), so an emoji
                // needs no ability of its own.
                //
                // THE SPRITE IS NOT PART OF THAT, and this comment used to say it was ("entirely
                // client-side, a UFortAnimNotify_PlayEmojiItem inside the montage"). It is a
                // GAMEPLAY CUE the SERVER executes - see EmojiCueTag below and
                // UActorChannel.SendNetMulticastInvokeGameplayCueExecutedWithParams. That is why the
                // throwing hand animated and nothing was ever thrown.
                _ => EmoteAbilityPath
            };
        }

        Console.WriteLine($"FortEmoteSystem: '{itemName}' is not in FortEmoteAssets.Generated.cs - falling " +
                          "back to the folder rule. Re-run Tools/EmoteTable/gen_emotes.py if this is a real " +
                          "10.40 cosmetic.");

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
        // THE SAME EMOTE TWICE IS THE COMMON CASE, not a corner one - which is what this comment
        // used to get wrong. It said the A -> null -> A window was "one tick, a human cannot
        // re-trigger an emote inside it", and dismissed the fix as "an out-of-band pawn push whose
        // only job is to send a value the client is about to be told to replace".
        //
        // The window is not one tick, it is one REPLICATION INTERVAL, and both writes happen inside
        // this one RPC handler no matter how long the player waited. StopEmote nulls handle 70 and
        // the line below sets it straight back, so the per-connection diff sees no change, sends
        // nothing, and no onlooker's OnRep_LastReplicatedEmoteExecuted ever fires. Pressing the same
        // emote a second time is invisible to everyone else - which is exactly what a two-client
        // test of the onlooker path reported.
        //
        // Sending the null on its own is not waste: it IS the change. A RepNotify fires on the
        // client only when the arriving value differs from the one it holds.
        var previousEmote = controller.Pawn?.LastReplicatedEmoteExecuted;

        StopEmote(controller, "a new emote started");

        if (previousEmote != null && controller.Pawn is { } emotingPawn
            && ReferenceEquals(previousEmote, UAssetRegistry.GetOrCreate(emoteAssetPath))) {
            controller.GetWorld()?.NetDriver?.FlushActorProperties(emotingPawn);
            Console.WriteLine($"FortEmoteSystem: the same emote again - pushed handle 70's null on its " +
                              "own so onlookers get a change to notify on.");
        }

        var emoteAsset = UAssetRegistry.GetOrCreate(emoteAssetPath);
        var spec = abilitySystem.GrantAbility(UAssetRegistry.GetOrCreate(abilityPath), sourceObject: emoteAsset);

        pawn.ActiveEmoteAbilityHandle = spec.Handle;

        // What every OTHER client sees. Set before the grant is flushed only so both leave on the
        // same tick; they travel on different channels (the pawn's and the PlayerState's) and are
        // independent - see APawn.LastReplicatedEmoteExecuted.
        // A MOVING emote moves because of these three, and nothing else. They were Reserved until
        // the asset values existed; now they come straight off FortEmoteAssets.Generated.cs. Only 22
        // of 263 dances set them, so almost every emote correctly leaves them at the default and
        // dances on the spot.
        if (FortEmoteAssets.For(emoteAssetPath[(emoteAssetPath.LastIndexOf('.') + 1)..]) is { } emoteAsset2) {
            pawn.bMovingEmote = emoteAsset2.bMovingEmote;
            pawn.bMovingEmoteForwardOnly = emoteAsset2.bMoveForwardOnly;
            pawn.EmoteWalkSpeed = float.IsNaN(emoteAsset2.WalkForwardSpeed) ? 0f : emoteAsset2.WalkForwardSpeed;

            if (emoteAsset2.bMovingEmote) {
                Console.WriteLine($"FortEmoteSystem: '{emoteAsset2.DisplayName}' is a MOVING emote " +
                                  $"(forwardOnly={emoteAsset2.bMoveForwardOnly}, speed={pawn.EmoteWalkSpeed}) - " +
                                  "handles 52/53/71 go out with it.");
            }
        }

        pawn.LastReplicatedEmoteExecuted = emoteAsset;

        // AN EMOJI IS THROWN FROM HERE, for the same reason a spray is painted from here: the
        // visible half is the server's job. GCNS_GM_OnDisplayEmoji is bound to this tag and reads
        // the cue's SourceObject - the emoji item definition, which is the same object the ability
        // spec above already carries - to pick which sprite goes on P_Emote_Show_Emoji.
        if (FortEmoteAssets.For(emoteAssetPath[(emoteAssetPath.LastIndexOf('.') + 1)..])
                is { Kind: FortEmoteAssets.EEmoteKind.Emoji }) {
            // QUEUED, NOT SENT - the throw has to leave the hand first. See EmojiCueDelay.
            _pendingEmojiCues.Add(new FPendingEmojiCue(
                pawn, emoteAsset, (controller.GetWorld()?.TimeSeconds ?? 0f) + EmojiCueDelay));
        }

        // A SPRAY PAINTS FROM HERE, because nothing else can. GAB_Spray_Generic's whole body is
        // behind IsServer, so the client's copy of the ability plays the montage and stops; the
        // decal is a replicated actor the authority spawns. See FortSpraySystem.
        if (abilityPath == SprayAbilityPath) FortSpraySystem.Paint(controller, emoteAsset);

        // AND THE MONTAGE, which is what everyone ELSE plays - see APawn.EmoteMontage and the
        // RepAnimMontageInfo block in NativeRepLayouts. ForcePlayBit TOGGLES rather than being set,
        // because the engine's own PlayMontage does: a repeat of the same emote has to look like a
        // change or no OnRep fires, and that is the same trap handle 70 needs a null push for.
        if (FortEmoteAssets.For(emoteAssetPath[(emoteAssetPath.LastIndexOf('.') + 1)..]) is { Montage: { Length: > 0 } montagePath }) {
            pawn.EmoteMontage = UAssetRegistry.GetOrCreate(montagePath);
            pawn.EmoteMontagePosition = 0f;

            // EXPLICIT, because the client's own default is 0 and a montage at rate zero is a
            // character standing still - see APawn.EmoteMontagePlayRate. 0.25 is the ordinary
            // montage blend-in; the struct's own default for it is 0 as well.
            pawn.EmoteMontagePlayRate = 1f;
            pawn.EmoteMontageBlendTime = 0.25f;
            pawn.EmoteMontageIsStopped = false;
            pawn.EmoteMontageSkipPositionCorrection = true;
            pawn.EmoteMontageForcePlayBit = !pawn.EmoteMontageForcePlayBit;

            Console.WriteLine($"FortEmoteSystem: onlookers get montage {montagePath[(montagePath.LastIndexOf('.') + 1)..]} " +
                              $"(ForcePlayBit now {pawn.EmoteMontageForcePlayBit})");
        } else {
            // No baked montage - a spray or a toy, which has no Animation of its own. Leave the
            // struct stopped rather than pointing at the previous emote's montage.
            pawn.EmoteMontage = null;
            pawn.EmoteMontageIsStopped = true;
        }

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
    /// <summary>
    ///     Ends whatever emote is running, from outside. Damage is the caller that matters.
    ///
    ///     TAKING A HIT CANCELS AN EMOTE IN REAL FORTNITE, and until this existed the two ends
    ///     disagreed about that: the client stopped the montage locally while the server went on
    ///     saying `IsStopped = false` with the same ForcePlayBit, so the next thing that made an
    ///     onlooker re-read the struct replayed the emote FROM ITS FIRST FRAME. The server's console
    ///     is what ruled out its own involvement - no `RepAnimMontageInfo.*` in any
    ///     ReplicateActorUpdate at the moment of damage, and no ServerPlayEmoteItem either - which
    ///     leaves a purely local cancel, and the fix is to agree with it.
    /// </summary>
    public static void CancelEmote(APlayerController controller, string reason) => StopEmote(controller, reason);

    private static void StopEmote(APlayerController controller, string reason) {
        if (controller.Pawn is not { ActiveEmoteAbilityHandle: not 0 } pawn) return;

        var handle = pawn.ActiveEmoteAbilityHandle;
        pawn.ActiveEmoteAbilityHandle = 0;
        pawn.LastReplicatedEmoteExecuted = null;

        // IsStopped is what an onlooker's OnRep_ReplicatedAnimMontage reads to stop the montage.
        // The asset stays put deliberately: the client needs to know WHICH montage stopped, and
        // nulling it in the same update would take that away.
        pawn.EmoteMontageIsStopped = true;

        // Cleared with the emote, or the pawn would keep walking at a dance's speed after it ended.
        pawn.bMovingEmote = false;
        pawn.bMovingEmoteForwardOnly = false;
        pawn.EmoteWalkSpeed = 0f;

        controller.PlayerState?.AbilitySystemComponent?.ClearAbility(handle);

        Console.WriteLine($"FortEmoteSystem.StopEmote: emote spec handle {handle} cleared ({reason})");
    }
}
