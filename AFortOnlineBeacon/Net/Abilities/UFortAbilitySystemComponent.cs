namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     /Script/FortniteGame.FortAbilitySystemComponentAthena - the first replicated COMPONENT this
///     project has ever sent, and the object every part of firing hangs off.
///
///     It is not an actor, so it does not get a channel of its own. It rides on its owner's actor
///     channel inside a **sub-object content block** (UActorChannel::WriteContentBlockHeader /
///     ReadContentBlockHeader), which is exactly what a live Project-Reboot-3.0 capture shows: 96 of
///     them on one channel, all naming the same dynamic NetGUID.
///
///     Two DIFFERENT stability rules apply to it, and conflating them cost a full test round:
///     - `IsFullNameStableForNetworking` (walks the outer chain) decides static vs dynamic GUID.
///       The outer is a runtime-spawned PlayerState, so the GUID is DYNAMIC - matching the capture,
///       where the player's ASC is the even guid 584.
///     - `IsNameStableForNetworking` (this object only) decides both whether the path is exported
///       and what the content block's "stably named" bit says. A default subobject is stable, so
///       that bit must be 1, which tells the client to RESOLVE its own existing component instead
///       of constructing a new one. Sending 0 makes the client build a second, orphaned ASC while
///       AFortPlayerState::AbilitySystemComponent - not a replicated pointer - keeps pointing at the
///       original, and every granted ability lands somewhere nothing reads. Silently.
///
///     Wire identity: the field index space is what matters, and it is fully derived from the 10.40
///     SDK - UObject(0) + UActorComponent(2) + UGameplayTasksComponent(1) +
///     UAbilitySystemComponent(47) + UFortAbilitySystemComponent(3) = GetMaxIndex 53, so every field
///     index on this component is a 6-bit bounded int. The capture confirms it to the bit: a
///     ServerSetReplicatedTargetData block was 2879 bits = 6 (index 45) + 16 (packed length) + 2857.
///     UFortAbilitySystemComponentAthena adds no net fields of its own, so the Athena subclass and
///     its parent share one index space - which is why picking between them cannot go wrong here.
/// </summary>
public class UFortAbilitySystemComponent : UObject {
    /// <summary>
    ///     UActorComponent::IsSupportedForNetworking (ActorComponent.cpp:1677) -
    ///     `GetIsReplicated() || IsNameStableForNetworking()`. A replicated component is networkable
    ///     even though its full path is not stable, and without this it is not: FNetGUIDCache's
    ///     SupportsObject refuses it, GetOrAssignNetGUID hands back the invalid guid 0, and the
    ///     content block names an object the client has never heard of. That is exactly what a real
    ///     client reported - "Stably named sub-object not found ... Component: " with the component
    ///     path EMPTY, because guid 0 resolves to nothing to print.
    /// </summary>
    public override bool IsSupportedForNetworking() => true;

    /// <summary>
    ///     The owning actor, kept so the replication pass can find the channel this component's
    ///     content block belongs in. Real UE reaches this through the component's Outer.
    /// </summary>
    public AActor? OwnerActor { get; set; }

    /// <summary>
    ///     UAbilitySystemComponent::AvatarActor - wire handle 8. The actor this component acts
    ///     THROUGH, i.e. the pawn, as opposed to the one that owns it (the PlayerState).
    ///
    ///     Fortnite's walk speed comes from GameplayAttributes, and those only reach a pawn's
    ///     CharacterMovement once InitAbilityActorInfo has run - which needs this. A client missing
    ///     it has perfectly healthy attributes bound to nothing: WalkSpeed 200 and RunSpeed 410 in
    ///     its own log, and a measured velocity clamped at exactly 1.0 uu/s.
    /// </summary>
    public AActor? AvatarActor { get; set; }

    /// <summary>
    ///     UAbilitySystemComponent::ActivatableAbilities - field index 3, and a
    ///     FastArraySerializer (FGameplayAbilitySpecContainer derives from it), so it travels as a
    ///     ClassNetCache-indexed custom delta rather than through any handle stream. That is the
    ///     same machinery the inventory already uses and has been live-confirmed on.
    ///
    ///     Nothing the client can fire exists until something lands in here: the handle in
    ///     ServerTryActivateAbility indexes this array.
    /// </summary>
    public FFastArraySerializer<FGameplayAbilitySpec> ActivatableAbilities { get; } = new();

    /// <summary>
    ///     UAbilitySystemComponent::SpawnedAttributes - wire handle 4, and the reason a player could
    ///     only crawl. Fortnite reads walk speed through the ASC
    ///     (GetNumericAttribute -> GetAttributeSubobject), which searches THIS array; an empty one
    ///     means every attribute reads as zero no matter how healthy the sets themselves are.
    ///
    ///     The sets do not need creating or filling. They are default subobjects of the PlayerState,
    ///     so the client already constructed them with their real defaults - its own log prints
    ///     WalkSpeed 200 and RunSpeed 410 - and being stably named they are referenced by PATH, with
    ///     no class and no values on the wire. This array is purely the introduction.
    /// </summary>
    public List<UObject> SpawnedAttributes { get; } = new();

    /// <summary>
    ///     Real UE's FGameplayAbilitySpecHandle::GenerateNewHandle - a process-wide counter. The
    ///     value is opaque; all that matters is that the server hands out a distinct one per spec
    ///     and the client echoes it back. Starts at 1 so 0 never means "granted".
    /// </summary>
    private static int _nextHandle = 1;

    public FGameplayAbilitySpec GrantAbility(UObject abilityClass, UObject? sourceObject = null, int inputId = -1) {
        var spec = new FGameplayAbilitySpec {
            Handle = _nextHandle++,
            Ability = abilityClass,
            InputID = inputId,
            SourceObject = sourceObject
        };

        // Add() runs MarkItemDirty, which assigns the ReplicationID and moves the array key - never
        // set either by hand, or the next item collides with this one.
        ActivatableAbilities.Add(spec);
        return spec;
    }

    /// <summary>
    ///     UAbilitySystemComponent::ClearAbility - removes a granted spec. Remove() moves the array
    ///     replication key, so the next delta carries this as an explicit delete.
    /// </summary>
    public void ClearAbility(int handle) {
        var spec = ActivatableAbilities.Items.FirstOrDefault(item => item.Handle == handle);
        if (spec == null) return;

        ActivatableAbilities.Remove(spec);
        Console.WriteLine($"UFortAbilitySystemComponent.ClearAbility: removed spec handle {handle}, " +
                          $"{ActivatableAbilities.Count} left");
    }
}
