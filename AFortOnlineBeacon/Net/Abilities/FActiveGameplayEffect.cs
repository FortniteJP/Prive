namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     GameplayAbilities.FGameplayEffectSpec, as far as the wire is concerned - the definition of one
///     applied GameplayEffect.
///
///     WHY THIS EXISTS. A live client proved the HUD health bar cannot be moved by replicating the
///     health attribute alone: the value arrives exactly (GetAll), the bar's own cached ValueCurrent
///     never changes, and the whole chain was traced in the memory dump -
///     `UAthenaPlayerHitPointBarBase::SetDataSource` binds the bar's value handler to a view-model
///     delegate that is broadcast from `AFortPawn::OnPawnHealthChanged`, which Fortnite raises from
///     its DAMAGE pipeline, not from an attribute-replication callback.
///
///     The way in is GAS's own: `FActiveGameplayEffectsContainer::SetBaseAttributeValueFromReplication`
///     (GameplayEffect.cpp:2357) has two branches. With NO aggregator for the attribute it merely
///     broadcasts the raw delegates - which is the silence this server has been getting. With an
///     aggregator it goes through OnAttributeAggregatorDirty -> InternalUpdateNumericalAttribute,
///     the full notification path the HUD is built on. **An aggregator exists exactly when some
///     active GameplayEffect touches that attribute.** So replicating even ONE active effect that
///     names Health is what turns every subsequent replicated health change into a notification.
///
///     Members and their order are RepLayout's, i.e. offset order with CPF_RepSkip members absent -
///     CapturedRelevantAttributes, CapturedSourceTags, CapturedTargetTags and the three
///     bCompleted*/bDurationLocked bits are all RepSkip and never appear.
/// </summary>
public class FGameplayEffectSpecForActiveEffect {
    /// <summary>The GameplayEffect's class default object. The client reads its modifier list from HERE, not from us - we only supply the magnitudes.</summary>
    public UObject? Def;

    /// <summary>What this effect changed, and by how much. Purely informational for cues and the HUD.</summary>
    public List<FGameplayEffectModifiedAttribute> ModifiedAttributes { get; } = new();

    /// <summary>Seconds. UGameplayEffect::INFINITE_DURATION is -1, which is what an effect that should simply stay applied uses.</summary>
    public float Duration = -1.0f;

    /// <summary>Seconds between periodic executions; 0 means not periodic.</summary>
    public float Period;

    public float ChanceToApplyToTarget = 1.0f;

    /// <summary>
    ///     One evaluated magnitude per modifier the GE DEFINES, in the definition's own order. The
    ///     attribute and the operation come from the definition; only the number is ours.
    /// </summary>
    public List<float> Modifiers { get; } = new();

    public int StackCount = 1;

    public float Level = 1.0f;
}

/// <summary>
///     GameplayAbilities.FActiveGameplayEffect - one element of the AbilitySystemComponent's
///     ActiveGameplayEffects fast array.
///
///     Only three of its members replicate: Spec, PredictionKey and StartServerWorldTime.
///     CachedStartServerWorldTime, StartWorldTime and bIsInhibited are all RepSkip, and the three
///     FFastArraySerializerItem bookkeeping fields are NotReplicated - the array's own delta header
///     carries the ReplicationID instead (see FFastArraySerializerWriter).
/// </summary>
public class FActiveGameplayEffect : IFastArrayItem {
    public int ReplicationId { get; set; } = UnrealConstants.IndexNone;
    public int ReplicationKey { get; set; }

    public FGameplayEffectSpecForActiveEffect Spec { get; } = new();

    /// <summary>
    ///     Left at its default - a server-applied effect was never predicted by a client, so there
    ///     is no key to reconcile. Written by FPredictionKey.Write either way, since this is an
    ///     ordinary struct member of the item rather than an RPC parameter that could be omitted.
    /// </summary>
    public FPredictionKey PredictionKey { get; } = new();

    /// <summary>
    ///     World seconds, server clock. The client uses it to work out how far through a duration
    ///     the effect already is, so it has to be a time the client also knows - the same clock
    ///     AGameState::ReplicatedWorldTimeSeconds carries.
    /// </summary>
    public float StartServerWorldTime { get; set; }
}
