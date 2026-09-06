namespace AFortOnlineBeacon.Net;

/// <summary>
///     Using a healing consumable - a Small Shield Potion, a Bandage, a Med Kit.
///
///     HOW A CONSUMABLE WORKS IN FORTNITE, which is the thing worth knowing: it is a WEAPON. The
///     item is a FortWeaponRangedItemDefinition with a WeaponActorClass (the potion the pawn holds)
///     and a PrimaryFireAbility (GA_Athena_ShieldSmall_C), so equipping and firing one runs through
///     exactly the same path as an assault rifle. Nothing about the flow is consumable-specific:
///     APawn.EquipInventoryItem already grants the item's fire ability and stores the spec handle,
///     and the client already activates it by that handle through ServerTryActivateAbility.
///
///     So the only thing missing was ever the two facts in FortConsumables.Generated.cs - which
///     actor and which ability an Athena_ShieldSmall maps to - and the effect below. That is why
///     this file is short: the machinery was all built for weapons.
///
///     ONE DELIBERATE DEVIATION, and it is visible in play. The real ability applies its effect at
///     the END of the use montage (TriggerDuration, 2.03 s for a mini shield) via a gameplay task,
///     and an interrupted use heals nothing. This server has no ability INSTANCES and no montage
///     tasks - it can only see the client's activation - so the heal lands immediately and an
///     interrupted use still heals. TriggerDuration is carried in the table for whenever there is
///     something to gate on; see the TODO at the bottom.
/// </summary>
internal static class FortConsumableSystem {
    /// <summary>
    ///     Applies a consumable's effect if this ability spec is a consumable's, and consumes one
    ///     from the stack. Returns false for any other ability, so the weapon path is untouched.
    /// </summary>
    public static bool TryUse(APlayerState playerState, FGameplayAbilitySpec spec) {
        // The spec's SourceObject is the weapon actor the grant was made for
        // (APawn.EquipInventoryItem -> GrantAbility(fireAbility, weapon)), and the weapon names the
        // item definition it was built from in WeaponData. That chain is what identifies WHICH
        // consumable without trusting anything the client sent beyond the spec handle.
        if (spec.SourceObject is not AFortWeapon { WeaponData: { } definition } weapon) return false;

        var itemName = definition.GetFName().ToString();
        if (FortConsumables.EffectFor(itemName) is not { } effect) return false;

        if (playerState is not { bIsDead: false, HealthSet: { } set }) return false;

        // Slurp Juice heals nothing on use - it starts a 75-tick drip. Its own ability carries no
        // amounts at all; they come from the GameplayEffect chain it grants (see
        // Tools/ConsumableTable/gen_consumables.py's OVER_TIME).
        if (!float.IsNaN(effect.OverTimeTotal)) {
            StartOverTime(playerState, effect);
            ConsumeOne(playerState, weapon, effect.DisplayName);
            return true;
        }

        // Cap first, then amount. A cap is a FRACTION of the pool's maximum, read from the ability's
        // own Row_*Cap curve row - a mini shield gives 25 shields but stops at 0.5 * MaxShield,
        // which is why two of them fill a fresh player and a third does nothing.
        var health = Grant(effect.HealsHealth, set.Health, set.MaxHealth, effect.HealthAmount, effect.HealthCapFraction);
        var shield = Grant(effect.HealsShields, set.CurrentShield, set.Shield, effect.ShieldAmount, effect.ShieldCapFraction);

        if (health <= 0f && shield <= 0f) {
            // Athena.Ability.Failed.ShieldSmallLimit - the real ability refuses here and the item is
            // NOT consumed. This server has no ClientActivateAbilityFailed, so the client keeps the
            // prediction it already played and briefly shows a use it did not get; the SERVER state
            // is the correct one, which is the half that matters.
            Console.WriteLine($"FortConsumableSystem: {playerState.GetFName()} used {effect.DisplayName} " +
                              $"with nothing to heal (health {set.Health}/{set.MaxHealth}, " +
                              $"shield {set.CurrentShield}/{set.Shield}) - refusing, item not consumed");
            return true;
        }

        FortDamageSystem.Heal(playerState, health, shield);

        Console.WriteLine($"FortConsumableSystem: {playerState.GetFName()} used {effect.DisplayName} " +
                          $"(+{health} health, +{shield} shield) - health {set.Health}/{set.MaxHealth}, " +
                          $"shield {set.CurrentShield}/{set.Shield}");

        ConsumeOne(playerState, weapon, effect.DisplayName);
        return true;
    }

    /// <summary>
    ///     How much of a pool this use actually adds: nothing if the ability does not touch it,
    ///     otherwise the amount clamped to the cap (or the maximum when there is no cap) and never
    ///     negative - a player already above a cap gains zero rather than losing anything.
    /// </summary>
    private static float Grant(bool heals, float current, float max, float amount, float capFraction) {
        if (!heals || float.IsNaN(amount)) return 0f;

        var ceiling = float.IsNaN(capFraction) ? max : max * capFraction;
        return MathF.Max(0f, MathF.Min(current + amount, ceiling) - current);
    }

    /// <summary>
    ///     Takes one off the stack - AbilityCosts[0] is EFortAbilityCostSource::AmmoPrimary with
    ///     CostValue 1 on every one of these abilities. An emptied stack takes the weapon out of the
    ///     player's hands too, because the weapon actor is keyed to its inventory row by
    ///     ItemEntryGuid and that row is about to stop existing - the same rule
    ///     ServerAttemptInventoryDrop follows.
    ///
    ///     Shared with FortProjectileSystem: a thrown grenade is consumed by exactly the same rule
    ///     (its ability carries the same AbilityCosts entry), and having two copies of "take one off
    ///     the stack, unequip when the stack is gone" is how the two would drift apart.
    /// </summary>
    internal static void ConsumeOne(APlayerState playerState, AFortWeapon weapon, string displayName) {
        if (playerState.GetOwningController() is not APlayerController { WorldInventory: { } inventory } controller) return;

        var entry = inventory.Inventory.Items.FirstOrDefault(item => item.ItemGuid == weapon.ItemEntryGuid);
        if (entry == null) return;

        entry.Count--;

        if (entry.Count > 0) {
            inventory.Inventory.MarkItemDirty(entry);
            Console.WriteLine($"FortConsumableSystem: {entry.Count} x {displayName} left, " +
                              $"ArrayReplicationKey={inventory.Inventory.ArrayReplicationKey}");
            return;
        }

        inventory.Inventory.Remove(entry);
        controller.Pawn?.UnequipCurrentWeapon();
        Console.WriteLine($"FortConsumableSystem: last {displayName} used up - row removed and unequipped, " +
                          $"ArrayReplicationKey={inventory.Inventory.ArrayReplicationKey}");
    }

    /// <summary>
    ///     One running heal-over-time. Slurp Juice is the only consumable that has one in 10.40.
    /// </summary>
    private sealed class FHealOverTime {
        public required APlayerState Target;
        public required string DisplayName;
        public required float PerTick;
        public required float TickRate;
        public required float Remaining;
        public float NextTickTime;
    }

    private static readonly List<FHealOverTime> Running = new();

    private static void StartOverTime(APlayerState playerState, FortConsumables.FConsumableEffect effect) {
        // Re-drinking REPLACES rather than stacks: GE_Athena_PurpleStuff_C is StackLimitCount 1.
        Running.RemoveAll(entry => entry.Target == playerState && entry.DisplayName == effect.DisplayName);

        Running.Add(new FHealOverTime {
            Target = playerState,
            DisplayName = effect.DisplayName,
            PerTick = effect.OverTimePerTick,
            TickRate = effect.OverTimeTickRate,
            Remaining = effect.OverTimeTotal,
            NextTickTime = (playerState.GetWorld()?.TimeSeconds ?? 0f) + effect.OverTimeTickRate
        });

        Console.WriteLine($"FortConsumableSystem: {playerState.GetFName()} drank {effect.DisplayName} - " +
                          $"{effect.OverTimeTotal} effective health at {effect.OverTimePerTick} every " +
                          $"{effect.OverTimeTickRate}s ({effect.OverTimeTotal / effect.OverTimePerTick * effect.OverTimeTickRate:F1}s total)");
    }

    /// <summary>
    ///     Advances every running heal-over-time. Called from AGameModeBase.TickPhases, which is the
    ///     one per-frame hook this server has with a world clock already in hand.
    ///
    ///     EACH TICK IS ONE UNIT OF *EFFECTIVE HEALTH*, spent on health while there is health missing
    ///     and on shield otherwise. That routing is not invented: the granted ability ticks two
    ///     separate effects, GE_Athena_PurpleStuff_Health (which modifies FortHealthSet:Healing) and
    ///     GE_Athena_PurpleStuff_Shields (FortHealthSet:CurrentShield), and BOTH read the same
    ///     per-tick curve row - so the choice between them is per tick, and health-first is the
    ///     order the game plays. What is NOT modelled is the Blueprint's own condition for picking
    ///     one, which is bytecode; this is the reading that matches both the assets and how the item
    ///     behaves.
    /// </summary>
    public static void Tick(float now) {
        for (var i = Running.Count - 1; i >= 0; i--) {
            var entry = Running[i];

            if (entry.Target is not { bIsDead: false, HealthSet: { } set }) {
                Running.RemoveAt(i);
                continue;
            }

            if (now < entry.NextTickTime) continue;
            entry.NextTickTime = now + entry.TickRate;

            var step = MathF.Min(entry.PerTick, entry.Remaining);
            var toHealth = MathF.Min(step, set.MaxHealth - set.Health);
            var toShield = MathF.Min(step - toHealth, set.Shield - set.CurrentShield);

            if (toHealth <= 0f && toShield <= 0f) {
                // Full on both - the drip has nowhere to go, so it is over. Real Fortnite keeps the
                // effect running and wastes the ticks; ending it early only differs if the player
                // takes damage mid-drink, and stopping is the conservative half of that.
                Console.WriteLine($"FortConsumableSystem: {entry.Target.GetFName()}'s {entry.DisplayName} " +
                                  $"ended with {entry.Remaining} unused - already full");
                Running.RemoveAt(i);
                continue;
            }

            FortDamageSystem.Heal(entry.Target, toHealth, toShield);
            entry.Remaining -= toHealth + toShield;

            if (entry.Remaining > 0f) continue;

            Console.WriteLine($"FortConsumableSystem: {entry.Target.GetFName()}'s {entry.DisplayName} finished - " +
                              $"health {set.Health}/{set.MaxHealth}, shield {set.CurrentShield}/{set.Shield}");
            Running.RemoveAt(i);
        }
    }

    // TODO: gate on FConsumableEffect.TriggerDuration. The real ability heals when the montage
    // finishes and heals nothing if it is interrupted; both need a server-side notion of "this
    // activation is still running", which means either a per-activation timer on the tick or
    // trusting the client's ServerEndAbility / ServerCancelAbility pair to tell the two apart.
    // The second is cheaper and is already decoded (NativeRpcHandlers.OnAbilityEndReported) - it was
    // not used here because an ability the client never ends would then never heal at all, and a
    // heal that lands early is a far smaller error than one that never lands.
}
