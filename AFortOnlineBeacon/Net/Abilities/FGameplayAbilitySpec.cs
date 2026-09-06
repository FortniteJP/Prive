namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     One granted ability - GameplayAbilities.GameplayAbilitySpec, an FFastArraySerializerItem
///     inside UAbilitySystemComponent::ActivatableAbilities (field index 3 on the component).
///
///     This is what a trigger pull actually needs: the client cannot activate anything it has not
///     been granted, and <see cref="Handle"/> is the number it names when it does - it is exactly
///     the FGameplayAbilitySpecHandle in ServerTryActivateAbility, decoded off a real
///     Project-Reboot-3.0 capture as Handle=259 repeated with prediction keys 1, 2, 3.
///
///     Only SIX of the struct's members reach the wire. Everything else is RepSkip in the 10.40 SDK
///     (ActiveCount, InputPressed, RemoveAfterActivation, PendingRemove, ActivationInfo,
///     NonReplicatedInstances, GameplayEffectHandle) - all of it server-side bookkeeping the client
///     rebuilds for itself. Getting that list wrong is not a compile error, it is a silent desync,
///     so it is transcribed straight from the SDK's own flag column rather than from the member list.
/// </summary>
public sealed class FGameplayAbilitySpec : IFastArrayItem {
    public int ReplicationId { get; set; } = -1;
    public int ReplicationKey { get; set; }

    /// <summary>
    ///     FGameplayAbilitySpecHandle - one int32, and NOT a NetSerializeNative struct, so it
    ///     serializes as its bare member (proven by ServerTryActivateAbility measuring exactly 54
    ///     bits, which only works if this costs 32).
    ///
    ///     Real UE hands these out from a global counter (FGameplayAbilitySpecHandle::GenerateNewHandle).
    ///     The value itself is opaque - all that matters is that the server and the client agree,
    ///     which they do because the server is the one that grants it.
    /// </summary>
    public int Handle { get; init; }

    /// <summary>
    ///     The UGameplayAbility CLASS default object, referenced by path - e.g.
    ///     /Game/Abilities/Weapons/Ranged/GA_Ranged_GenericDamage.GA_Ranged_GenericDamage_C, which
    ///     is what the assault rifle's WID names as its PrimaryFireAbility and what the capture
    ///     shows being exported the moment a rifle is equipped.
    /// </summary>
    public required UObject Ability { get; init; }

    public int Level { get; init; } = 1;

    /// <summary>
    ///     Which input slot activates this ability, or INDEX_NONE for abilities with no binding.
    ///     Real UE maps this through the ability's own EFortAbilityInputBinding.
    /// </summary>
    public int InputID { get; init; } = -1;

    /// <summary>
    ///     What granted the ability. For a weapon ability this is the weapon actor, which is how the
    ///     client ties the spec back to the thing in its hands.
    /// </summary>
    public UObject? SourceObject { get; set; }

    /// <summary>
    ///     FGameplayAbilitySpec::ReplicatedInstances - the sixth member, and the only one of the
    ///     struct's two instance arrays that reaches the wire: it is a plain `UPROPERTY()` while
    ///     NonReplicatedInstances immediately above it is `UPROPERTY(NotReplicated)`
    ///     (GameplayAbilitySpec.h:273-279). That asymmetry is the whole mechanism - it is how a
    ///     server-created ability instance becomes the client's GetPrimaryInstance().
    ///
    ///     Empty for almost everything, and correctly so: an ability whose ReplicationPolicy is
    ///     ReplicateNo has the client build its own instance. Only ReplicateYes abilities need one
    ///     here, and for them it is not optional - see UGameplayAbilityInstance for the full chain
    ///     and for why a grenade is inert without it.
    /// </summary>
    public List<UObject> ReplicatedInstances { get; } = new();

    /// <summary>
    ///     The prediction key the client used the last time it activated this spec. NOT on the wire -
    ///     server bookkeeping, kept because ClientEndAbility is only obeyed when the key MATCHES.
    ///
    ///     UAbilitySystemComponent::RemoteEndOrCancelAbility walks the spec's instances and ends only
    ///     the one whose GetActivationPredictionKey() equals the key in the ActivationInfo it was
    ///     sent. Send a fresh or empty key and the client silently keeps the ability running.
    /// </summary>
    public FPredictionKey? ActivationPredictionKey { get; set; }
}
