using AFortOnlineBeacon.Runtime;
namespace AFortOnlineBeacon.Net;

/// <summary>
///     Every way a PLAYER loses health, in one place - the server half of what real Fortnite spreads
///     across AFortPawn::TakeDamage, UFortHealthSet's attribute clamping and a GameplayEffect.
///
///     THIS BYPASSES GAS, and that is now known to be the whole of what is still missing - it is no
///     longer a suspicion. Health is written straight into the attribute set and replicated, and a
///     live client PROVES that arrives intact: `GetAll FortRegenHealthSet Health` on a damaged
///     player reported BaseValue/CurrentValue equal to this server's value to the last digit, and
///     the PlayerState mirror (handles 216-219) reported the same number. The client simply does not
///     REDRAW from it.
///
///     Why: the bottom-centre bar is AthenaLocalPlayerHitPointInfo -> AthenaHitPointBar_C, driven by
///     a native UAthenaPlayerViewModel whose only inputs are the events
///     `OnValueChangedWithReason(float, EFortHitPointModificationReason)` and `OnMaxValueChanged`.
///     A REASON (DamageReceived / DamageOverTime / AutoRegen / ItemRegen / InitalSet) can only come
///     from a GameplayEffect, and a real 10.40 server never replicates the player's health set at
///     all - two Project-Reboot-3.0 captures show the ONLY sub-object ever sent on the PlayerState
///     channel is the AbilitySystemComponent, whose ActiveGameplayEffects the client receives
///     thousands of times. Replicating that fast array is what remains.
///
///     Health still travels both ways this server can send it - the GAS attribute set
///     (UActorChannel.ReplicateHealthSet) and the PlayerState's plain float mirror. Keep both: the
///     mirror is what the real server fills from the pawn's GAS accessors (proven in the memory
///     dump) and it is what the team/squad HUD reads.
///
///     Damage NUMBERS are placeholders, exactly as ABuildingActor's hit points are: real per-weapon
///     damage lives in a DataTable that needs the PAK directory and the AES key.
/// </summary>
public static class FortDamageSystem {
    /// <summary>This world's share of FortDamageSystem's state - see FWorldSubsystem.</summary>
    private sealed class FDamageState : FWorldSubsystem {
        /// <summary>
        ///     Flat per-hit damage to a player, used ONLY for a weapon Tools/WeaponStats has no row for.
        ///
        ///     This used to be the damage every weapon did - a pickaxe, a sniper rifle and a point-blank
        ///     shotgun pellet all took exactly 20 off a player. The real numbers now come from the
        ///     weapon's own stat row (see FortWeaponStats), with four range breakpoints and a crit
        ///     multiplier each, so this is a fallback rather than the model. It is kept, and the caller
        ///     names the weapon in the log when it is used, because an unknown weapon should be visible
        ///     rather than silently doing assault-rifle damage.
        /// </summary>
        public float WeaponDamage => Options.Float("WEAPON_DAMAGE", 20.0f);

        /// <summary>
        ///     The GameplayEffect this server claims did the damage. The storm's is used because it is
        ///     the one the reference capture proves resolvable on a live client, and because this server
        ///     has no per-weapon effect to name; DAMAGE_EFFECT overrides it.
        ///
        ///     Nothing about it is applied - the client is being TOLD an effect executed, not asked to
        ///     run one - but the class still has to resolve, or the cue has no definition to look up.
        /// </summary>
        public UObject DamageEffectDef => UAssetRegistry.GetOrCreate(
            Options.Get("DAMAGE_EFFECT") is { Length: > 0 } path
                ? path
                : "/Game/Athena/SafeZone/GE_OutsideSafeZoneDamage.Default__GE_OutsideSafeZoneDamage_C");

        public float _nextRampAt;

        /// <summary>Alternates the HEALTH_BAR_NUDGE wobble so MaxHealth genuinely differs every time.</summary>
        public bool _nudgeUp = true;

        public readonly List<FPendingDeath> Pending = new();

        /// <summary>
        ///     How long after ClientOnPawnDied the pawn is torn off and the player becomes a spectator.
        ///
        ///     MEASURED NOW, at 0.01s. This was 4 seconds and its comment said "Not a sourced number" -
        ///     it was a guess about how long a client needs to play its own death handling. The 0906
        ///     capture's one death answers it: ClientOnPawnDied at 13:08:20.960, the pawn's channel
        ///     closing at 13:08:20.970. TEN MILLISECONDS - one frame.
        ///
        ///     Four seconds was not merely too long, it was too long IN THE WRONG DIRECTION. The old
        ///     code destroyed the body at the end of it, so the corpse stood about for four seconds and
        ///     then vanished; the real server tears it off immediately and the corpse stays for good. The
        ///     linger was compensating for the wrong teardown, which is why shortening it only makes
        ///     sense together with AActor.TearOff.
        ///
        ///     Kept as a small delay rather than folded into the same frame on purpose: this project has
        ///     paid three times for sending an RPC in the same frame as the actor change it refers to
        ///     (see [[bus-camera-mode-hypothesis]]), and one tick of separation costs nothing.
        ///     DEATH_LINGER_SECONDS still overrides it.
        /// </summary>
        public float LingerSeconds =>
            float.TryParse(Options.Get("DEATH_LINGER_SECONDS"), out var v) ? v : 0.01f;

        /// <summary>
        ///     Server-initiated prediction keys for the death activation. Its own counter rather than
        ///     FortEmoteSystem's, because the two are unrelated activations and sharing a counter would
        ///     mean one system's numbering depended on how much the other had done.
        /// </summary>
        public short _nextDeathPredictionKey = 1;
    }

    private static FDamageState StateOf(UWorld world) => world.GetSubsystem<FDamageState>();

    /// <summary>WEAPON_DAMAGE for this world - the flat fallback for a weapon with no baked stats.</summary>
    public static float FallbackWeaponDamage(UWorld world) => StateOf(world).WeaponDamage;

    /// <summary>
    ///     Applies damage to a player. Shield absorbs first, then health, and health stops at zero -
    ///     which is where the kill happens.
    ///
    ///     Returns the damage actually dealt, which is less than <paramref name="amount"/> when the
    ///     victim did not have that much left to lose.
    /// </summary>
    public static float ApplyDamage(APlayerState? victim, float amount, EDeathCause cause,
                                    APlayerState? instigator = null) {
        if (victim == null || amount <= 0.0f) return 0.0f;
        if (victim.GetWorld() is not { } world) return 0.0f;
        var state = StateOf(world);

        // A dead player takes no further damage. Without this a burst already in flight would re-run
        // the death path per bullet, and the elimination feed would fire once for each.
        if (victim.bIsDead) return 0.0f;

        if (victim.HealthSet is not { } health) {
            Console.WriteLine($"FortDamageSystem: {victim.GetFName()} has no HealthSet - nothing to damage. " +
                              "(AGameModeBase.Login builds it; a PlayerState from before that does not have one.)");
            return 0.0f;
        }

        // A HIT CANCELS AN EMOTE - see FortEmoteSystem.CancelEmote for why the server has to say so
        // rather than let the client cancel it alone.
        if (victim.GetWorld()?.NetDriver?.ClientConnections
                  .FirstOrDefault(c => c.PlayerController?.PlayerState == victim)?.PlayerController is { } emoting) {
            FortEmoteSystem.CancelEmote(emoting, "took damage");
        }

        var absorbedByShield = MathF.Min(health.CurrentShield, amount);
        health.CurrentShield -= absorbedByShield;

        var toHealth = amount - absorbedByShield;
        var dealtToHealth = MathF.Min(health.Health, toHealth);
        health.Health -= dealtToHealth;

        var dealt = absorbedByShield + dealtToHealth;

        Console.WriteLine($"FortDamageSystem: {victim.GetFName()} took {dealt} damage ({cause}) - " +
                          $"shield {health.CurrentShield}/{health.Shield}, health {health.Health}/{health.MaxHealth}" +
                          (instigator != null ? $", from {instigator.GetFName()}" : string.Empty));

        var bFatal = health.Health <= 0.0f;

        // HEALTH_BAR_NUDGE=1: also move MaxHealth by an invisible amount on every damage.
        //
        // A live experiment showed the HUD bar following a server push that changed MaxHealth AND
        // Health together, while a push that changed Health alone left it stale - the bar's maximum
        // has its own event (OnMaxValueChanged, no "reason" argument) and its current value appears
        // to be redrawn as a passenger of it. This makes every hit look like that working case. It
        // is a WORKAROUND, kept behind a knob and deliberately not on by default: the honest fix is
        // whatever raises OnValueChangedWithReason, and leaving this off keeps that question honest.
        // 0.01 is far below what the HUD renders (it prints integers), so nothing visibly lies.
        if (world.Options.Get("HEALTH_BAR_NUDGE") == "1") {
            health.MaxHealth += state._nudgeUp ? 0.01f : -0.01f;
            state._nudgeUp = !state._nudgeUp;
        }

        // The client-side half: a damage number, the hit flash, and the shield/fatal flags. Sent
        // BEFORE the kill so a fatal hit is still announced as a hit - see
        // UActorChannel.SendNetMulticastAthenaBatchedDamageCues, and note what the reference capture
        // says it is NOT (the health bar's update channel).
        SendDamageCue(victim, dealt, absorbedByShield > 0.0f, bDestroyedShield: absorbedByShield > 0.0f && health.CurrentShield <= 0.0f,
                      bBallistic: cause != EDeathCause.FallDamage && cause != EDeathCause.OutsideSafeZone, bFatal: bFatal,
                      bWeaponHit: cause != EDeathCause.OutsideSafeZone);

        if (bFatal) Kill(victim, cause, instigator);

        return dealt;
    }

    /// <summary>
    ///     Tells the client it was hit, on the victim's own pawn channel (the RPC is a multicast on
    ///     AFortPawn, and with one player "multicast" is that one connection). The hit location is
    ///     the pawn's own - this server has no per-hit impact point for a fall or the storm, and for
    ///     a weapon hit the client already drew its own impact effect locally.
    /// </summary>
    private static void SendDamageCue(APlayerState victim, float magnitude, bool bShield,
                                      bool bDestroyedShield, bool bBallistic, bool bFatal,
                                      bool bWeaponHit = true) {
        if (victim.GetOwningPawn() is not { } pawn) return;
        if (victim.GetOwningController() is not APlayerController pc) return;

        // Same connection lookup as NativeRpcHandlers.ReportDamagedBuilding - there is no
        // GetNetConnection() on a controller in this project, so the owning connection is the one
        // whose PlayerController is this one.
        var connection = pc.GetWorld()?.NetDriver?.ClientConnections
            .FirstOrDefault(candidate => candidate.PlayerController == pc);

        if (connection?.FindActorChannel(pawn) is not { } pawnChannel) return;

        // THE WEAPON HIT CUE, AND ONLY FOR A WEAPON HIT. The reference capture carries 78 of these
        // and every one is a weapon hit; across the 30 STORM-damage ticks on the local player there
        // is not a single one. Sending it every storm tick is both wrong and not free - the storm
        // ticks once a second per player for minutes, and each cue is work for the client's
        // GameplayCueManager. (A captured client hang showed it streaming GameplayCueNotify assets in
        // a flood with its game thread stuck in PhysX; that is not proven to be this, and the server
        // behaved normally in the same window, but a cue a real server never sends is worth not
        // sending.) STORM_DAMAGE_CUE=1 restores it for comparison.
        //
        // The FromSpec cue below is NOT gated: the same capture DOES send that one on a storm tick,
        // and it is what makes the health bar redraw.
        if (!bWeaponHit && victim.WorldOptions.Get("STORM_DAMAGE_CUE") != "1") {
            SendHealthChangeCue(pawnChannel, magnitude);
            return;
        }

        pawnChannel.SendNetMulticastAthenaBatchedDamageCues(
            pawn.GetActorLocation(), new FVector { X = 0.0f, Y = 0.0f, Z = 1.0f }, magnitude,
            bIsFatal: bFatal, bIsShield: bShield, bIsShieldDestroyed: bDestroyedShield,
            bIsBallistic: bBallistic, hitActor: pawn);

        // The other half of what a real server sends on a health change, and the one that carries a
        // gameplay-effect REASON rather than only a cosmetic cue. See
        // UActorChannel.SendNetMulticastInvokeGameplayCueExecutedFromSpec.
        SendHealthChangeCue(pawnChannel, magnitude);
    }

    /// <summary>
    ///     The half of a damage notification that carries a REASON - the gameplay effect that moved
    ///     the Damage meta attribute - and therefore the half that makes the health bar redraw. Sent
    ///     for every kind of damage, the storm included; see SendDamageCue for what is not.
    /// </summary>
    private static void SendHealthChangeCue(Channels.Actor.UActorChannel pawnChannel, float magnitude) {
        if (pawnChannel.Actor?.GetWorld() is not { } world) return;
        var state = StateOf(world);

var spec = new FGameplayEffectSpecForRPC { Def = state.DamageEffectDef };
        spec.ModifiedAttributes.Add(new FGameplayEffectModifiedAttribute {
            // The DAMAGE meta attribute, not Health - that is what the reference capture's client
            // resolves out of this RPC, and it is how Fortnite models a hit: a GE moves Damage and
            // UFortRegenHealthSet::PostGameplayEffectExecute turns it into lost health.
            AttributeName = "Damage",
            Attribute = UAssetRegistry.GetOrCreate(DamageAttributePath),
            AttributeOwner = UAssetRegistry.GetOrCreate(HealthSetClassPath),
            TotalMagnitude = magnitude
        });

        pawnChannel.SendNetMulticastInvokeGameplayCueExecutedFromSpec(spec);
    }

    /// <summary>
    ///     The UProperty this server names as the modified attribute, and the class it lives on.
    ///     Both are stably-named native objects, so they export by path like any other static
    ///     reference; the reference capture shows a real client resolving exactly these two.
    /// </summary>
    private const string DamageAttributePath = "/Script/FortniteGame.FortHealthSet:Damage";

    private const string HealthSetClassPath = "/Script/FortniteGame.FortHealthSet";

    /// <summary>
    ///     A DIAGNOSTIC, off unless HEALTH_DEBUG_RAMP=1, and it exists to answer one question that
    ///     nothing else can: does ANY attribute change this server pushes mid-match reach the HUD,
    ///     or only Health?
    ///
    ///     Every few seconds it raises MaxHealth (and Health with it) by a visible step. The bar has
    ///     a SEPARATE event for its maximum - AthenaHitPointBar_C::OnMaxValueChanged, distinct from
    ///     OnValueChangedWithReason - so watching the bar's MAX is a second, independent probe of
    ///     the same notification chain:
    ///
    ///       max grows on screen -> attribute pushes DO notify, and something is specific to Health
    ///       nothing moves      -> the whole attribute-change chain is silent client-side, and the
    ///                             answer is upstream of any single attribute
    ///
    ///     Delete this once the question is settled; it has no gameplay purpose.
    /// </summary>
    public static void DebugRamp(UWorld world, float now) {
        var state = StateOf(world);

        var mode = world.Options.Get("HEALTH_DEBUG_RAMP");
        if (string.IsNullOrEmpty(mode)) return;
        if (now < state._nextRampAt) return;
        state._nextRampAt = now + world.Options.Float("HEALTH_DEBUG_RAMP_PERIOD", 3.0f);

        foreach (var connection in world.NetDriver?.ClientConnections ?? Enumerable.Empty<UNetConnection>()) {
            if (connection.PlayerController?.PlayerState is not { HealthSet: { } set } playerState) continue;

            switch (mode) {
                // HEALTH ONLY, and downward - the shape a real hit has. If the bar follows this, a
                // plain Health push does notify and the damage path is at fault; if it does not
                // while "max" below still moves the bar, then only MaxHealth notifies and the
                // current value is a passenger that gets redrawn alongside it.
                case "health":
                    set.Health -= 10.0f;
                    if (set.Health <= 0.0f) set.Health = set.MaxHealth;
                    break;

                // MAX ONLY. The bar has a separate event for it (OnMaxValueChanged, no "reason"
                // parameter), which is the leading suspect for why "both" moved the bar at all.
                case "max":
                    set.MaxHealth += 25.0f;
                    break;

                // Both, the original probe: this one DID move the bar live, which is what split the
                // question into the two cases above.
                default:
                    set.MaxHealth += 25.0f;
                    set.Health = set.MaxHealth;
                    break;
            }

            Console.WriteLine($"FortDamageSystem.DebugRamp[{mode}]: {playerState.GetFName()} " +
                              $"health {set.Health}/{set.MaxHealth} - watch the HUD bar");
        }
    }

    /// <summary>
    ///     Heals, with the same clamping in reverse. Nothing calls this yet - consumables are not
    ///     modelled - but the shield half of it is what a Small Shield Potion will need, and having
    ///     exactly one place that writes the health set is the point of this class.
    /// </summary>
    public static void Heal(APlayerState? victim, float health = 0.0f, float shield = 0.0f) {
        if (victim is not { bIsDead: false, HealthSet: { } set }) return;

        if (health > 0.0f) set.Health = MathF.Min(set.Health + health, set.MaxHealth);
        if (shield > 0.0f) set.CurrentShield = MathF.Min(set.CurrentShield + shield, set.Shield);

        Console.WriteLine($"FortDamageSystem: {victim.GetFName()} healed - " +
                          $"health {set.Health}/{set.MaxHealth}, shield {set.CurrentShield}/{set.Shield}");
    }

    /// <summary>
    ///     What a player's death consists of on this server, which is three things a client reads
    ///     independently:
    ///
    ///       * the health attribute at zero - both the GAS set and the PlayerState mirror,
    ///       * AFortPawn::bIsDying (handle 47) on the pawn, which the client's own death handling
    ///         runs off,
    ///       * FDeathInfo (handles 258-262) on the PlayerState, which the elimination feed and the
    ///         death screen read.
    ///
    ///     NONE OF WHICH ACTUALLY KILLS ANYBODY, which is what the first live test found: at 0 HP
    ///     the player could not shoot and could not build, and was otherwise alive and walking about.
    ///     Two of the three above are inert on the client, and for the same reason:
    ///
    ///       * `AFortPawn::bIsDying` HAS NO OnRep. The SDK header lists OnRep_IsDBNO,
    ///         OnRep_IsKnockedBack and a dozen more right beside it and nothing at all for bIsDying,
    ///         so it arrives correctly and runs nothing.
    ///       * FDeathInfo is read by the elimination FEED and the death SCREEN once they are up. It
    ///         does not put them up.
    ///
    ///     What was doing the visible half was bMarkedAlive: it is the client's own answer to "am I
    ///     alive" and it locally gates jumping and building, which is exactly the half-dead state
    ///     that was reported.
    ///
    ///     The part that actually kills is AFortPlayerController::ClientOnPawnDied, driven from
    ///     <see cref="Tick"/> a tick later - see UActorChannel.SendClientOnPawnDied. Kill() only sets
    ///     state; nothing here touches a channel, because every object reference in the death report
    ///     has to exist on the client first and this class has no way to know that.
    /// </summary>
    public static void Kill(APlayerState victim, EDeathCause cause, APlayerState? instigator = null) {
        if (victim.GetWorld() is not { } world) return;
        var state = StateOf(world);

        if (victim.bIsDead) return;

        victim.bIsDead = true;
        if (victim.HealthSet is { } set) {
            set.Health = 0.0f;
            set.CurrentShield = 0.0f;
        }

        var victimPawn = victim.GetOwningPawn();
        var killerPawn = instigator?.GetOwningPawn();

        if (victimPawn != null) victimPawn.bIsDying = true;

        victim.DeathInfoFinisherOrDowner = killerPawn;
        victim.DeathInfoDBNO = false;
        victim.DeathInfoCause = (byte) cause;
        victim.DeathInfoDistance = DistanceBetween(victimPawn, killerPawn);
        victim.DeathInfoInitialized = true;

        if (victim.GetOwningController() is APlayerController controller) {
            controller.bMarkedAlive = false;
            state.Pending.Add(new FPendingDeath(controller, victimPawn, killerPawn, instigator, float.NaN));

            // One fewer player alive. This is what the HUD's "N left" reads (handle 116), and it is
            // also the number the match-over check will want when there is one.
            //
            // AND WHERE THIS PLAYER FINISHED, taken BEFORE the decrement because the count still
            // includes them: die when six are left and you placed sixth. Handle 233 was Reserved(...)
            // until now, so every death this server has reported carried a placement of zero - see
            // NativeRepLayouts' entry for it, and note this is a lead on the missing death screen
            // rather than a confirmed fix.
            if (controller.GetWorld()?.GameState is { } gameState) {
                victim.Place = Math.Max(1, gameState.PlayersLeft);
                if (gameState.PlayersLeft > 0) gameState.PlayersLeft--;
            }
        }

        Console.WriteLine($"FortDamageSystem: {victim.GetFName()} was ELIMINATED ({cause})" +
                          (instigator != null
                              ? $" by {instigator.GetFName()} at {victim.DeathInfoDistance / 100.0f:F0}m"
                              : string.Empty));
    }

    /// <summary>
    ///     A death waiting for the client to be told about it. <see cref="ReportedAt"/> is NaN until
    ///     ClientOnPawnDied has gone out, and the world time it went out at afterwards.
    /// </summary>
    private sealed record FPendingDeath(APlayerController Controller, APawn? Pawn, APawn? KillerPawn,
                                        APlayerState? Killer, float ReportedAt) {
        public float ReportedAt { get; set; } = ReportedAt;
    }

    /// <summary>
    ///     Finishes every death that <see cref="Kill"/> started. Two beats, both deferred for reasons
    ///     this project has hit before:
    ///
    ///     1. SEND ClientOnPawnDied, but not before the killer's pawn and PlayerState have channels
    ///        on this connection. An object reference that is not resolvable on the client arrives as
    ///        null - the same trap that sent the battle bus's ClientSetViewTarget at an aircraft the
    ///        client had never heard of. Waiting a tick costs nothing and removes the whole class.
    ///
    ///     2. THEN, after DEATH_LINGER_SECONDS, take the pawn away and leave the player spectating.
    ///        The teardown order is the one the battle bus already proved: UnequipCurrentWeapon
    ///        FIRST (it reaches the ability system through Controller-&gt;PlayerState, so after
    ///        UnPossess it would silently skip clearing the weapon's ability specs), then UnPossess,
    ///        then Destroy. The camera goes to the killer's pawn when there is one, which is what a
    ///        real match does; with no killer there is nothing sensible to look at, so the pawn stays
    ///        and only control is taken away - a standing body is a much smaller lie than a camera
    ///        bound to a destroyed actor.
    /// </summary>
    public static void Tick(UWorld world, float now) {
        var worldState = StateOf(world);

        if (worldState.Pending.Count == 0) return;

        for (var i = worldState.Pending.Count - 1; i >= 0; i--) {
            var death = worldState.Pending[i];
            if (world.NetDriver?.ClientConnections.FirstOrDefault(c => c.PlayerController == death.Controller)
                is not { } connection) {
                worldState.Pending.RemoveAt(i);   // gone from the game entirely
                continue;
            }

            if (connection.FindActorChannel(death.Controller) is not { } pcChannel) continue;

            if (float.IsNaN(death.ReportedAt)) {
                // Every reference has to be resolvable, or it lands as null - see above.
                if (death.KillerPawn != null && connection.FindActorChannel(death.KillerPawn) == null) continue;
                if (death.Killer != null && connection.FindActorChannel(death.Killer) == null) continue;

                // THE DEATH ABILITY FIRST, which is the order the capture uses: the activation goes
                // out in the same frame, a few milliseconds AHEAD of ClientOnPawnDied.
                ActivateDeathAbility(world, death.Controller);

                pcChannel.SendClientOnPawnDied(death.Killer, death.KillerPawn, death.KillerPawn,
                                               lethalDamage: death.Controller.PlayerState?.HealthSet?.MaxHealth ?? 100f);

                // THE ELIMINATION FEED, 1 ms after ClientOnPawnDied in the capture and sent in the
                // same breath here for the same reason the death report is: both name the killer's
                // PlayerState, and the guard above has already established that it is resolvable on
                // this connection. See SendClientReceiveKillNotification for the signature's source.
                pcChannel.SendClientReceiveKillNotification(death.Killer, death.Controller.PlayerState);

                death.ReportedAt = now;

                Console.WriteLine($"FortDamageSystem: sent ClientOnPawnDied to {death.Controller.GetFName()} " +
                                  $"(killer={(death.Killer == null ? "none" : death.Killer.GetFName().ToString())})");
                continue;
            }

            if (now < death.ReportedAt + worldState.LingerSeconds) continue;
            worldState.Pending.RemoveAt(i);

            if (death.Pawn == null || world.Options.Get("DEATH_KEEP_BODY") is "1") continue;

            // THE BODY IS TORN OFF, NOT DESTROYED - measured, and it is the whole difference between
            // a corpse and a body that pops out of existence. The 0906 capture's one death reads:
            //
            //     13:08:20.960  Sent RPC: ...::ClientOnPawnDied
            //     13:08:20.970  UActorChannel::Close: ChIndex: 7, PlayerPawn_Athena_C_2147462182,
            //                   Reason: TearOff
            //
            // Ten milliseconds later, with reason TearOff. See AActor.TearOff for what the client
            // does with each of the three close reasons. DEATH_TEAR_OFF=0 restores the destroy.
            var tearOff = world.Options.Get("DEATH_TEAR_OFF") is not "0";

            // WHERE THE CAMERA GOES. The killer's pawn is what a real match uses; failing that, any
            // other living player, which is what a real match falls back to when the killer has
            // already left. With NEITHER - the solo case, and the one a storm or fall death in
            // testing actually hits - no view target is sent at all, and UE's own
            // APlayerController::TickActor handles a view target that has gone away by falling back
            // to the controller, which sits where the player died. That is a death cam looking at
            // the place of death, which is the right thing anyway.
            var spectate = death.KillerPawn ?? world.NetDriver?.ClientConnections
                .Where(c => c != connection && c.PlayerController?.PlayerState is { bIsDead: false })
                .Select(c => c.PlayerController!.Pawn)
                .FirstOrDefault(p => p != null);

            // The teardown order the battle bus already proved - see the doc comment.
            death.Pawn.UnequipCurrentWeapon();
            death.Controller.UnPossess();

            if (tearOff) death.Pawn.TearOff();
            else death.Pawn.Destroy();

            if (death.Controller.PlayerState?.AbilitySystemComponent is { } asc) asc.AvatarActor = null;

            // NO ClientGotoState - and that is a correction, not an omission. This sent
            // ClientGotoState(322 = NAME_Spectating) on the reasoning that a spectator has no pawn,
            // which the battle bus does need. A real DEATH does not: sixty thousand log lines after
            // the capture's death contain no ClientGotoState of any kind. The client leaves its own
            // playing state off the back of ClientOnPawnDied - it answers within 126 ms with
            // ServerClientPawnLoaded and ServerClientIsReadyToRespawn without ever being told to.
            // DEATH_GOTO_STATE=1 sends it again for comparison.
            if (world.Options.Get("DEATH_GOTO_STATE") is "1") pcChannel.SendClientGotoState(322);

            if (spectate != null) pcChannel.SendClientSetViewTarget(spectate);

            Console.WriteLine($"FortDamageSystem: {death.Controller.GetFName()}'s body was " +
                              $"{(tearOff ? "torn off (the corpse stays)" : "destroyed")}; now spectating " +
                              $"{(spectate == null ? "where it died" : spectate.GetFName().ToString())}");
        }
    }

    /// <summary>
    ///     Tells the client to run GA_DefaultPlayer_Death on itself.
    ///
    ///     THE LAST STRUCTURAL DIFFERENCE between this server's death and a real one. The 0906
    ///     capture's death frame carries `ClientActivateAbilitySucceedWithEventData` [147.9 bytes]
    ///     just before ClientOnPawnDied, and this server has never activated any client ability for
    ///     a death at all. Everything else in that frame is now matched: the report, the kill
    ///     notification, the view target, the tear-off.
    ///
    ///     IT IS WORTH TRYING BECAUSE OF WHAT THE CLIENT STOPS DOING. After a death here the client
    ///     sends ServerClientPawnLoaded and then nothing; the capture's client goes on to
    ///     ServerClientIsReadyToRespawn 126 ms later. Something in its death flow never starts, and
    ///     an unactivated death ability is the one candidate left standing.
    ///
    ///     THE PLAIN VARIANT, NOT WithEventData, deliberately. `SendClientActivateAbilitySucceed`
    ///     already works on this server - it is what makes emotes play - whereas the event-data
    ///     variant needs an FGameplayEventData payload nothing here can write yet. If the ability
    ///     runs without a payload this is the whole fix; if it does not, that is a real answer too,
    ///     and a much smaller thing to have found out than to have built the payload first.
    ///     DEATH_ABILITY=0 skips it.
    /// </summary>
    private static void ActivateDeathAbility(UWorld world, APlayerController controller) {
        var state = StateOf(world);

        if (world.Options.Get("DEATH_ABILITY") is "0") return;
        if (controller.PlayerState is not { AbilitySystemComponent: { } abilitySystem } playerState) return;

        var spec = abilitySystem.ActivatableAbilities.Items
            .FirstOrDefault(s => s.Ability.GetFName().ToString()
                .Contains("GA_DefaultPlayer_Death", StringComparison.OrdinalIgnoreCase));

        if (spec == null) {
            Console.WriteLine("FortDamageSystem: no GA_DefaultPlayer_Death spec granted, so there is no death " +
                              "ability to activate - see AController.DefaultAbilities.");
            return;
        }

        var predictionKey = new FPredictionKey {
            bValidKeyForConnection = true,
            bIsServerInitiated = true,
            Current = state._nextDeathPredictionKey++
        };

        // WITH EVENT DATA, because the client said so. The plain variant reached the ability and
        // the ability refused it: "expects event data but none is being supplied. Use Activate
        // Ability instead of Activate Ability From Event." DEATH_ABILITY_EVENT_DATA=0 sends the
        // plain one again, which is what reproduces that warning.
        if (world.Options.Get("DEATH_ABILITY_EVENT_DATA") is "0") {
            world.NetDriver?.SendClientActivateAbilitySucceed(playerState, abilitySystem, spec.Handle, predictionKey);
        } else {
            world.NetDriver?.SendClientActivateAbilitySucceedWithEventData(
                playerState, abilitySystem, spec.Handle, predictionKey,
                instigator: controller.Pawn, target: controller.Pawn);
        }

        Console.WriteLine($"FortDamageSystem: activated GA_DefaultPlayer_Death on {controller.GetFName()} " +
                          $"as spec handle {spec.Handle} (server prediction key {predictionKey.Current})");
    }

    /// <summary>Straight-line distance between two pawns, or 0 when there is no killer to measure to.</summary>
    private static float DistanceBetween(APawn? victim, APawn? killer) {
        if (victim == null || killer == null) return 0.0f;

        var from = victim.GetActorLocation();
        var to = killer.GetActorLocation();
        var dx = from.X - to.X;
        var dy = from.Y - to.Y;
        var dz = from.Z - to.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
