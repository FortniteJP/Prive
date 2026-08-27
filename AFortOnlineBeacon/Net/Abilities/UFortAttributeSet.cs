namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     A GameplayAbilities UAttributeSet, as far as the wire is concerned - which is very little.
///
///     This holds no attributes and never will. The client's own AFortPlayerState constructor
///     already builds all ten sets as default subobjects with their real defaults (its log prints
///     WalkSpeed 200, RunSpeed 410, SprintSpeed 550), and because they are stably named the server
///     references each by PATH: outer plus name, no class, no values. All this type exists for is to
///     be a name the server can point at from UAbilitySystemComponent::SpawnedAttributes.
///
///     The override is the whole point of having a type at all.
///     UAttributeSet::IsSupportedForNetworking (AttributeSet.cpp:327) returns true unconditionally,
///     and it has to: an attribute set's outer is a runtime-spawned actor, so
///     IsFullNameStableForNetworking is false, and FNetGUIDCache::SupportsObject would otherwise
///     refuse it a NetGUID and every reference would go out as the invalid guid 0. That exact
///     mistake already cost a full test round on the AbilitySystemComponent itself.
/// </summary>
public class UFortAttributeSet : UObject {
    public override bool IsSupportedForNetworking() => true;
}
