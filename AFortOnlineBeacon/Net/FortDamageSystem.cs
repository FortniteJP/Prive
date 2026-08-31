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
    private static float Env(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    /// <summary>
    ///     Flat per-hit damage to a player, standing in for the weapon's own stat row - the same
    ///     placeholder arrangement, and the same eventual fix, as NativeRpcHandlers'
    ///     BuildingDamagePerHit. 20 is a mid-tier assault rifle body shot, so five hits kill a
    ///     full-health player carrying no shield.
    /// </summary>
    public static float WeaponDamage => Env("WEAPON_DAMAGE", 20.0f);

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

        // A dead player takes no further damage. Without this a burst already in flight would re-run
        // the death path per bullet, and the elimination feed would fire once for each.
        if (victim.bIsDead) return 0.0f;

        if (victim.HealthSet is not { } health) {
            Console.WriteLine($"FortDamageSystem: {victim.GetFName()} has no HealthSet - nothing to damage. " +
                              "(AGameModeBase.Login builds it; a PlayerState from before that does not have one.)");
            return 0.0f;
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
        if (Environment.GetEnvironmentVariable("HEALTH_BAR_NUDGE") == "1") {
            health.MaxHealth += _nudgeUp ? 0.01f : -0.01f;
            _nudgeUp = !_nudgeUp;
        }

        // The client-side half: a damage number, the hit flash, and the shield/fatal flags. Sent
        // BEFORE the kill so a fatal hit is still announced as a hit - see
        // UActorChannel.SendNetMulticastAthenaBatchedDamageCues, and note what the reference capture
        // says it is NOT (the health bar's update channel).
        SendDamageCue(victim, dealt, absorbedByShield > 0.0f, bDestroyedShield: absorbedByShield > 0.0f && health.CurrentShield <= 0.0f,
                      bBallistic: cause != EDeathCause.FallDamage && cause != EDeathCause.OutsideSafeZone, bFatal: bFatal);

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
                                      bool bDestroyedShield, bool bBallistic, bool bFatal) {
        if (victim.GetOwningPawn() is not { } pawn) return;
        if (victim.GetOwningController() is not APlayerController pc) return;

        // Same connection lookup as NativeRpcHandlers.ReportDamagedBuilding - there is no
        // GetNetConnection() on a controller in this project, so the owning connection is the one
        // whose PlayerController is this one.
        var connection = pc.GetWorld()?.NetDriver?.ClientConnections
            .FirstOrDefault(candidate => candidate.PlayerController == pc);

        if (connection?.FindActorChannel(pawn) is not { } pawnChannel) return;

        pawnChannel.SendNetMulticastAthenaBatchedDamageCues(
            pawn.GetActorLocation(), new FVector { X = 0.0f, Y = 0.0f, Z = 1.0f }, magnitude,
            bIsFatal: bFatal, bIsShield: bShield, bIsShieldDestroyed: bDestroyedShield,
            bIsBallistic: bBallistic, hitActor: pawn);

        // The other half of what a real server sends on a health change, and the one that carries a
        // gameplay-effect REASON rather than only a cosmetic cue. See
        // UActorChannel.SendNetMulticastInvokeGameplayCueExecutedFromSpec.
        var spec = new FGameplayEffectSpecForRPC { Def = DamageEffectDef };
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
    ///     The GameplayEffect this server claims did the damage. The storm's is used because it is
    ///     the one the reference capture proves resolvable on a live client, and because this server
    ///     has no per-weapon effect to name; DAMAGE_EFFECT overrides it.
    ///
    ///     Nothing about it is applied - the client is being TOLD an effect executed, not asked to
    ///     run one - but the class still has to resolve, or the cue has no definition to look up.
    /// </summary>
    private static UObject DamageEffectDef => UAssetRegistry.GetOrCreate(
        Environment.GetEnvironmentVariable("DAMAGE_EFFECT") is { Length: > 0 } path
            ? path
            : "/Game/Athena/SafeZone/GE_OutsideSafeZoneDamage.Default__GE_OutsideSafeZoneDamage_C");

    private static float _nextRampAt;

    /// <summary>Alternates the HEALTH_BAR_NUDGE wobble so MaxHealth genuinely differs every time.</summary>
    private static bool _nudgeUp = true;

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
        var mode = Environment.GetEnvironmentVariable("HEALTH_DEBUG_RAMP");
        if (string.IsNullOrEmpty(mode)) return;
        if (now < _nextRampAt) return;
        _nextRampAt = now + Env("HEALTH_DEBUG_RAMP_PERIOD", 3.0f);

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
    ///     The pawn is NOT destroyed. A dead Battle Royale player becomes a spectator, and this
    ///     project has no spectator path yet; destroying the pawn out from under a client whose view
    ///     is still attached to it would be a worse lie than leaving a dead body standing.
    ///
    ///     bMarkedAlive is cleared for the same reason it was set at spawn: it is the client's own
    ///     answer to "am I alive", and it locally gates jumping and building.
    /// </summary>
    public static void Kill(APlayerState victim, EDeathCause cause, APlayerState? instigator = null) {
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

        if (victim.GetOwningController() is APlayerController controller) controller.bMarkedAlive = false;

        Console.WriteLine($"FortDamageSystem: {victim.GetFName()} was ELIMINATED ({cause})" +
                          (instigator != null
                              ? $" by {instigator.GetFName()} at {victim.DeathInfoDistance / 100.0f:F0}m"
                              : string.Empty));
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
