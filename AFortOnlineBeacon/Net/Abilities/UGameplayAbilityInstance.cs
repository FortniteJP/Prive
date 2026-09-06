using AFortOnlineBeacon.Core.Objects;

namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     One instanced UGameplayAbility living on a player - the object
///     UAbilitySystemComponent::CreateNewInstanceOfAbility makes, replicated to its owner as a
///     sub-object so the client has something to activate ON.
///
///     WHY THIS HAS TO EXIST, in the engine's own words. An ability whose ReplicationPolicy is
///     ReplicateYes gets NO client-side instance: OnGiveAbility creates one only for ReplicateNo
///     (AbilitySystemComponent_Abilities.cpp:383), because a replicating ability is supposed to
///     arrive FROM the server. With none, the client's FGameplayAbilitySpec::GetPrimaryInstance()
///     returns null, InternalTryActivateAbility falls through to its final branch and activates on
///     the CDO (:1382), and UGameplayAbility::GetFunctionCallspace returns Local for a CDO (:92) -
///     so every Server_* RPC that ability tries to send is run locally and discarded.
///
///     That is not an abstract concern: it is precisely why a grenade did nothing. The throw is
///     GA_Athena_Grenade_WithTrajectory_C, ReplicateYes, and the projectile is spawned by the
///     SERVER in answer to that ability's own Server_SpawnProjectile(Location, Direction) - a
///     FUNC_Net | FUNC_NetReliable | FUNC_NetServer Blueprint RPC. No instance, no RPC, no grenade;
///     the animation plays either way, which is what made it look like a projectile problem.
///
///     The split is visible in the data and matches what this server already does live: every
///     healing consumable is ReplicateNo (and works today with no instance at all), every grenade is
///     ReplicateYes (and does not). See FortConsumables.NeedReplicatedAbilityInstance.
///
///     NOTHING IS SENT FOR IT. The client needs the object to EXIST and to have the right class -
///     that is all a content block header carries anyway (its NetGUID plus, because the name is not
///     stable, its class). The ability's own state is rebuilt client-side by GAS; this server has no
///     ability state to send and would not know how to serialise a Blueprint's variables if it did.
///
///     The class comes from GUClassArray.StaticClassForPath, exactly as weapons and vehicles do, so
///     the same GA_*_C path always exports as one NetGUID no matter which lookup reaches it first.
/// </summary>
public sealed class UGameplayAbilityInstance : UObject {
    /// <summary>
    ///     Runtime-created, so its name is not stable and it takes a DYNAMIC NetGUID - which is what
    ///     is wanted. But UObject::IsSupportedForNetworking is IsFullNameStableForNetworking, so
    ///     without this override FNetGUIDCache::SupportsObject refuses it, the reference goes out as
    ///     the invalid guid 0, and the client's spec points at nothing. The ASC hit exactly this and
    ///     the failure is silent on both ends - see UFortAbilitySystemComponent's own note.
    /// </summary>
    public override bool IsSupportedForNetworking() => true;
}
