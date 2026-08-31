namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     One entry of <see cref="FGameplayEffectSpecForRPC.ModifiedAttributes"/> - "this attribute
///     moved by this much", and the only part of a gameplay-cue RPC that carries a NUMBER the HUD
///     can use.
///
///     FGameplayAttribute has no native NetSerialize, so RepLayout recurses it into its three
///     members in offset order: AttributeName (FString, 0x00), Attribute (UProperty*, 0x10) and
///     AttributeOwner (UStruct*, 0x18). Both object references are stably-named native objects, so
///     they travel as path exports - which is exactly what a real server's traffic shows the client
///     resolving: "InternalLoadObject loaded StructProperty /Script/FortniteGame.FortHealthSet:Damage"
///     followed by "Class /Script/FortniteGame.FortHealthSet".
/// </summary>
public class FGameplayEffectModifiedAttribute {
    /// <summary>The attribute's display name. Fortnite sends the property's own name, e.g. "Damage".</summary>
    public string AttributeName = string.Empty;

    /// <summary>The UProperty object itself, e.g. /Script/FortniteGame.FortHealthSet:Damage.</summary>
    public UObject? Attribute;

    /// <summary>The class the property lives on, e.g. /Script/FortniteGame.FortHealthSet.</summary>
    public UObject? AttributeOwner;

    /// <summary>How much the attribute moved. For damage this is the damage dealt, positive.</summary>
    public float TotalMagnitude;
}

/// <summary>
///     GameplayAbilities.FGameplayEffectSpecForRPC - the compact, RPC-friendly form of a
///     GameplayEffect spec, and the payload of
///     <see cref="UActorChannel.SendNetMulticastInvokeGameplayCueExecutedFromSpec"/>.
///
///     WHY THIS EXISTS HERE. A real 10.40 server, at the exact instant a player's health bar moves,
///     sends TWO things in one packet: the attribute-set property update (which this project already
///     sends) and `NetMulticast_InvokeGameplayCueExecuted_FromSpec` on the pawn carrying this struct.
///     The bar itself is `AthenaHitPointBar_C`, whose only inputs are
///     `OnValueChangedWithReason(float, EFortHitPointModificationReason)` and `OnMaxValueChanged` -
///     it needs a REASON, and a reason only exists where a GameplayEffect does. This struct is the
///     smallest thing on the wire that carries one.
///
///     Seven members, none RepSkip, so RepLayout flattens all of them in offset order:
///
///         Def                   UGameplayEffect*        object ref (the GE's class default object)
///         ModifiedAttributes    TArray&lt;...&gt;             uint16 count, then each element's leaves
///         EffectContext         FGameplayEffectContextHandle   atomic - one validity bit, then the
///                                                              context (see below)
///         AggregatedSourceTags  FGameplayTagContainer   atomic - ONE bit when empty
///         AggregatedTargetTags  FGameplayTagContainer   atomic - ONE bit when empty
///         Level                 float
///         AbilityLevel          float
///
///     THE CONTEXT IS DELIBERATELY SENT AS INVALID (a single 0 bit). Its handle's NetSerialize
///     writes a validity bit and then defers to whatever context struct
///     UAbilitySystemGlobals::AllocGameplayEffectContext hands back - which in Fortnite is
///     FFortGameplayEffectContext, a subclass with eleven extra members (bIsFatalHit,
///     KnockbackMagnitude, EffectDirection*, ...) whose NetSerialize is native code in the client's
///     encrypted .text. Guessing its layout would silently shorten this payload and corrupt every
///     field after it; an invalid context is a legal, exactly-known encoding. If the cue turns out
///     to need an instigator, that is the next thing to work out, not something to guess now.
/// </summary>
public class FGameplayEffectSpecForRPC {
    /// <summary>The GameplayEffect's CDO - e.g. /Game/Athena/SafeZone/GE_OutsideSafeZoneDamage.Default__GE_OutsideSafeZoneDamage_C.</summary>
    public UObject? Def;

    public List<FGameplayEffectModifiedAttribute> ModifiedAttributes { get; } = new();

    /// <summary>Effect level. 1 is what an ordinary applied effect carries.</summary>
    public float Level = 1.0f;

    /// <summary>Ability level. -1 is GAS's "no ability was involved".</summary>
    public float AbilityLevel = -1.0f;
}
