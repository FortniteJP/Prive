namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     GameplayAbilities.FActiveGameplayCue - one element of UAbilitySystemComponent's
///     ActiveGameplayCues fast array, and the ONLY way a gameplay cue can be turned off again.
///
///     WHY IT HAD TO EXIST. Every cue this server sent until now was a one-shot: the
///     NetMulticast_InvokeGameplayCueExecuted_WithParams RPC, which fires an effect and forgets it.
///     That is all a FortGameplayCueNotify_Simple wants, and it is nothing at all to a
///     FortGameplayCueNotify_**Looping** - `GCN_Athena_LowGravity_C`, the shockwave's aura, answers
///     only to Added/WhileActive/Removed, so an Executed for it was a silent no-op. There is no
///     "cue removed" RPC anywhere in the ASC's net cache to pair with an Added, either: removal is
///     not a message, it is the DISAPPEARANCE of an element from this array.
///
///     So the lifecycle real UE uses, and this server now copies (AbilitySystemComponent.cpp:1144,
///     confirmed line-for-line in the PR3.0 capture around a shield potion):
///
///         add     -> ActiveGameplayCues gains an element  -> client PostReplicatedAdd  -> WhileActive
///                    ...plus NetMulticast_InvokeGameplayCueAdded_WithParams -> OnActive
///         remove  -> the element is deleted from the array -> client PreReplicatedRemove -> Removed
///
///     ACTIVEGAMEPLAYCUES, NOT MINIMALREPLICATIONGAMEPLAYCUES, and the difference decides whether
///     the thrown player sees their own effect: MinimalReplicationGameplayCues is
///     DOREPLIFETIME_CONDITION(..., COND_SkipOwner) (AbilitySystemComponent.cpp:1451) and exists so
///     an owner in Mixed replication mode is not shown the same cue twice - it never reaches the one
///     player who most needs to see the aura. ActiveGameplayCues is unconditional. Its client-side
///     NetDeltaSerialize also refuses to run in some replication modes and this one's never does.
///
///     Members are RepLayout's three: GameplayCueTag, PredictionKey, Parameters. bPredictivelyRemoved
///     is UPROPERTY(NotReplicated) and the FFastArraySerializerItem bookkeeping is carried by the
///     array's own delta header - see FFastArraySerializerWriter.
/// </summary>
public sealed class FActiveGameplayCue : IFastArrayItem {
    public int ReplicationId { get; set; } = UnrealConstants.IndexNone;
    public int ReplicationKey { get; set; }

    /// <summary>The cue tag, by name; it goes on the wire as its 14-bit net index (see FortGameplayTags).</summary>
    public required string GameplayCueTag { get; init; }

    /// <summary>
    ///     Left at its default. The client's PostReplicatedAdd only skips the cue when the key is a
    ///     LOCAL client key (GameplayCueInterface.cpp:181) - i.e. one the client itself predicted -
    ///     so a server-originated cue must not claim one, and an empty key is the plainest way to
    ///     say that.
    /// </summary>
    public FPredictionKey PredictionKey { get; } = new();

    /// <summary>
    ///     FGameplayCueParameters::SourceObject. Null for the low-gravity aura, which reads nothing
    ///     off its cue.
    /// </summary>
    public UObject? SourceObject { get; init; }

    /// <summary>
    ///     FGameplayCueParameters::Location - WHERE the cue happens, when that is not simply the
    ///     actor it is attached to.
    ///
    ///     The air strike needs it: its marker and its rocket rain belong to a patch of ground, and
    ///     the cue rides the thrower's ability system because that is what has a replicated cue list.
    ///     Without a location the whole strike would draw itself on the player.
    /// </summary>
    public FVector? Location { get; init; }
}
