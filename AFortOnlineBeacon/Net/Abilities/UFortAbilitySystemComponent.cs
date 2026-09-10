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
    ///     UAbilitySystemComponent::ActiveGameplayEffects - the other FastArraySerializer on this
    ///     component, and the one the HUD ultimately depends on.
    ///
    ///     A client that receives an active effect naming an attribute creates an AGGREGATOR for it,
    ///     and from then on every replicated change to that attribute takes
    ///     SetBaseAttributeValueFromReplication's aggregator branch - the full
    ///     OnAttributeAggregatorDirty -> InternalUpdateNumericalAttribute notification path the
    ///     health bar listens to - instead of the silent one. See FActiveGameplayEffect for the
    ///     evidence chain that led here.
    /// </summary>
    public FFastArraySerializer<FActiveGameplayEffect> ActiveGameplayEffects { get; } = new();

    /// <summary>
    ///     Adds one active GameplayEffect and marks the array dirty so it goes out.
    ///
    ///     The POINT is the side effect on the client, not the effect itself: receiving an active
    ///     effect that names an attribute makes the client's GAS create an aggregator for it, and an
    ///     attribute with an aggregator takes the loud branch of
    ///     SetBaseAttributeValueFromReplication ever after. A magnitude of 0 is therefore a perfectly
    ///     good argument - it changes no number and still builds the aggregator.
    ///
    ///     Which ATTRIBUTE that is comes from <paramref name="def"/>'s own modifier list, not from
    ///     here; this server only supplies the evaluated magnitudes, one per modifier the definition
    ///     declares, in its order.
    /// </summary>
    public FActiveGameplayEffect AddActiveGameplayEffect(UObject? def, float magnitude, float startServerWorldTime) {
        var effect = new FActiveGameplayEffect { StartServerWorldTime = startServerWorldTime };
        effect.Spec.Def = def;
        effect.Spec.Modifiers.Add(magnitude);

        ActiveGameplayEffects.Add(effect);

        Console.WriteLine($"UFortAbilitySystemComponent.AddActiveGameplayEffect: {def?.GetFName().ToString() ?? "(null def)"} " +
                          $"magnitude={magnitude} - now {ActiveGameplayEffects.Count} active effect(s)");

        return effect;
    }

    /// <summary>
    ///     UAbilitySystemComponent::ActiveGameplayCues - the third fast array on this component, and
    ///     the only cue channel that can be turned OFF again.
    ///
    ///     A cue sent through NetMulticast_InvokeGameplayCueExecuted_WithParams is a one-shot; a
    ///     LOOPING notify needs an element to exist here for as long as the effect should last, and
    ///     stops when that element goes away. See FActiveGameplayCue for the whole lifecycle and for
    ///     why this array rather than MinimalReplicationGameplayCues.
    /// </summary>
    public FFastArraySerializer<FActiveGameplayCue> ActiveGameplayCues { get; } = new();

    /// <summary>
    ///     UAbilitySystemComponent::AddGameplayCue_Internal's authority branch, minus the RPC - the
    ///     caller sends that, because only the channel knows how (see
    ///     UActorChannel.SendNetMulticastInvokeGameplayCueAddedWithParams).
    ///
    ///     Adding the SAME tag twice is refused rather than stacked. Real UE does allow duplicates
    ///     here and leans on RemoveCue deleting only the first match, but this server has exactly one
    ///     producer of continuous cues and a second copy of an aura is a leak with no way back:
    ///     nothing would remove the extra element.
    /// </summary>
    public bool AddGameplayCue(string cueTagName, UObject? sourceObject = null, FVector? location = null) {
        if (HasGameplayCue(cueTagName)) return false;

        ActiveGameplayCues.Add(new FActiveGameplayCue {
            GameplayCueTag = cueTagName, SourceObject = sourceObject, Location = location
        });

        Console.WriteLine($"UFortAbilitySystemComponent.AddGameplayCue: {cueTagName} - now " +
                          $"{ActiveGameplayCues.Count} active cue(s)");
        return true;
    }

    /// <summary>
    ///     FActiveGameplayCueContainer::RemoveCue - and the whole point of the array. Removing the
    ///     element is what makes the client run PreReplicatedRemove and therefore the notify's
    ///     Removed event; there is no RPC that can say this.
    /// </summary>
    public bool RemoveGameplayCue(string cueTagName) {
        var cue = ActiveGameplayCues.Items.FirstOrDefault(
            item => item.GameplayCueTag.Equals(cueTagName, StringComparison.OrdinalIgnoreCase));

        if (cue == null) return false;

        ActiveGameplayCues.Remove(cue);

        Console.WriteLine($"UFortAbilitySystemComponent.RemoveGameplayCue: {cueTagName} - now " +
                          $"{ActiveGameplayCues.Count} active cue(s)");
        return true;
    }

    public bool HasGameplayCue(string cueTagName) =>
        ActiveGameplayCues.Items.Any(item => item.GameplayCueTag.Equals(cueTagName, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    ///     UAbilitySystemComponent::AllReplicatedInstancedAbilities - every instanced ability that
    ///     has to reach the client as a sub-object, in grant order.
    ///
    ///     Real UE walks this in ReplicateSubobjects (AbilitySystemComponent.cpp:1490). Here it is
    ///     the channel's to-send list; UActorChannel.ReplicateAbilityInstances emits one content
    ///     block per entry and then leaves it alone, since none of them ever changes.
    /// </summary>
    public List<UObject> AllReplicatedInstancedAbilities { get; } = new();

    /// <summary>
    ///     Grants an ability. <paramref name="replicateInstance"/> mirrors the ability's own
    ///     EGameplayAbilityReplicationPolicy, which this server cannot read at runtime (it lives in
    ///     the Blueprint) and so is baked - see FortConsumables.NeedsReplicatedAbilityInstance.
    ///
    ///     GETTING IT WRONG IS NOT SYMMETRIC. Missing an instance for a ReplicateYes ability makes
    ///     that ability silently inert (the client activates on the CDO and its Server_* RPCs are
    ///     dropped locally); supplying one for a ReplicateNo ability instead REPLACES what the
    ///     client would have built for itself, and every CanActivateAbility check then runs against
    ///     an object this server invented. So the default is false, and only a positive, read-from-
    ///     the-pak answer turns it on.
    /// </summary>
    public FGameplayAbilitySpec GrantAbility(UObject abilityClass, UObject? sourceObject = null, int inputId = -1,
                                             bool replicateInstance = false) {
        var spec = new FGameplayAbilitySpec {
            Handle = _nextHandle++,
            Ability = abilityClass,
            InputID = inputId,
            SourceObject = sourceObject
        };

        // Real UE creates the instance inside GiveAbility, before OnGiveAbility and before the spec
        // is ever marked dirty (AbilitySystemComponent_Abilities.cpp:244-249) - i.e. the spec is
        // never replicated in a state where ReplicatedInstances is empty but should not be. Doing it
        // here, before Add(), keeps that ordering: this project's standing hazard is an ObjectRef
        // written before its target exists, which arrives null and is never reconsidered.
        if (replicateInstance && OwnerActor != null) {
            // "/Game/X/GA_Y.Default__GA_Y_C" is the CDO this spec's Ability points at; the INSTANCE
            // is of the class beside it, "/Game/X/GA_Y.GA_Y_C". Rebuilt from the CDO's own name
            // rather than passed in separately so the two can never disagree.
            var cdoName = abilityClass.GetFName().ToString();
            var package = abilityClass.GetOuter()?.GetFName().ToString();

            if (package != null && cdoName.StartsWith("Default__", StringComparison.Ordinal)) {
                var classPath = $"{package}.{cdoName["Default__".Length..]}";
                var instanceClass = GUClassArray.StaticClassForPath<UGameplayAbilityInstance>(classPath);

                // A path-built UClass has no FName until CreateDefaultObject gives it one, and
                // MakeUniqueObjectName names the instance after its class - so without this the
                // first instance of each class is called "None" in every log line that mentions it,
                // and the second (by which time WriteContentBlockHeader has forced the same call)
                // is not. Cosmetic, but it makes two identical objects look like two different bugs.
                instanceClass.GetDefaultObject();

                var instance = UObjectGlobals.NewObject<UGameplayAbilityInstance>(OwnerActor, instanceClass);

                if (instance != null) {
                    spec.ReplicatedInstances.Add(instance);
                    AllReplicatedInstancedAbilities.Add(instance);
                    Console.WriteLine($"UFortAbilitySystemComponent.GrantAbility: created a replicated ability " +
                                      $"instance of {classPath} for spec handle {spec.Handle} - without one the " +
                                      "client would activate this ability on its CDO and drop every Server_ RPC it sends");
                }
            } else {
                Console.WriteLine($"UFortAbilitySystemComponent.GrantAbility: '{cdoName}' does not look like a " +
                                  "Default__ CDO, so no instance class could be derived - this ability will be inert");
            }
        }

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

        // UAbilitySystemComponent::OnRemoveAbility does the same (AbilitySystemComponent_Abilities.cpp:478):
        // the instance goes out of AllReplicatedInstancedAbilities with the spec that owned it.
        // Without this the list only ever grows - and it grows on every re-EQUIP, because each equip
        // grants a fresh spec. One test session re-equipped a grenade a dozen times and left a dozen
        // live ability instances replicating for the rest of the match.
        foreach (var instance in spec.ReplicatedInstances) AllReplicatedInstancedAbilities.Remove(instance);

        ActivatableAbilities.Remove(spec);
        Console.WriteLine($"UFortAbilitySystemComponent.ClearAbility: removed spec handle {handle}, " +
                          $"{ActivatableAbilities.Count} left");
    }
}
