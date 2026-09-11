using AFortOnlineBeacon.Runtime;
using AFortOnlineBeacon.Net;

namespace AFortOnlineBeacon.Core;

/// <summary>
///     The GameplayAbilities wire types this server needs, read out of UE 4.23's own source.
///
///     WHY THEY EXIST NOW: the client asked for them, in as many words. An in-match death produced
///
///         LogAbilitySystem: GA_DefaultPlayer_Death_C_2147464760 Activated
///         LogFortAbility: Warning: Ability ... expects event data but none is being supplied.
///                         Use Activate Ability instead of Activate Ability From Event.
///         LogAbilitySystem: GA_DefaultPlayer_Death_C_2147464760 EndAbility
///
///     The death ability is authored with "Activate Ability From Event" and cannot run without an
///     `FGameplayEventData`, so the capture's `ClientActivateAbilitySucceedWithEventData` was never
///     the optional variant it looked like.
/// </summary>
public static class FGameplayTypes {
    /// <summary>
    ///     An EMPTY FGameplayTagContainer: one bit, SET.
    ///
    ///     `FGameplayTagContainer::NetSerialize` (GameplayTagContainer.cpp:968) opens with "1st bit
    ///     to indicate empty tag container or not (empty tag containers are frequently replicated).
    ///     Early out if empty." A zero there means NOT empty and commits the reader to a count field
    ///     of `NumBitsForContainerSize` bits and then that many tags.
    /// </summary>
    public static void WriteEmptyTagContainer(FBitWriter writer) => writer.WriteBit(true);

    /// <summary>
    ///     An FGameplayTag, as its net index.
    ///
    ///     FOURTEEN BITS FLAT, and the flatness is the part worth explaining.
    ///     `FGameplayTag::NetSerialize_Packed` normally writes a first segment plus a "more" bit -
    ///     but `SerializeTagNetIndexPacked` (GameplayTagContainer.cpp:42) short-circuits to a plain
    ///     `MaxBits` field when `NetIndexFirstBitSegment >= MaxBits`, and
    ///     `UGameplayTagsManager::ConstructNetIndex` forces exactly that with
    ///     `NetIndexFirstBitSegment = FMath::Min(NetIndexFirstBitSegment, NetIndexTrueBitNum)`
    ///     (GameplayTagsManager.cpp:473). So the packed path never actually packs, and a tag is one
    ///     field of `NetIndexTrueBitNum` bits.
    ///
    ///     WHERE 14 COMES FROM, AND IT IS MEASURED NOW RATHER THAN ESTIMATED.
    ///     `NetIndexTrueBitNum = CeilToInt(Log2(NodeCount + 1))`, so the whole question is the node
    ///     count - every tag plus every ancestor prefix, since "A.B.C" also contributes "A" and
    ///     "A.B". THE CLIENT'S OWN COUNT IS 13502, read straight out of the memory dump:
    ///     UGameplayTagsManager keeps `NetworkGameplayTagNodeIndex` (a TArray of node pointers)
    ///     immediately before `NetworkGameplayTagNodeIndexHash`, and searching the dump for that
    ///     hash's known value (0x79b9274c, which both the client and the reference server log at
    ///     startup) lands on exactly one place with a TArray beside it: 13502 entries, each node's
    ///     own NetIndex member equal to its position (checked at 0, 1, 2 and 13501).
    ///     Log2(13503) = 13.72, so FOURTEEN.
    ///
    ///     THIS WAS 13 AND THAT WAS WRONG - an estimate from the ini's 4203 tags alone, which misses
    ///     the 37 tag DataTables and the five Config/Tags/*.ini files (Tools/GameplayTags now reads
    ///     all of them and reproduces the client's tag tree to the hash). Everything this server has
    ///     ever sent through WriteTag was therefore one bit short, and a short field does not just
    ///     lose the tag - it shifts every following bit of that RPC. The only caller so far is
    ///     WriteEventData, i.e. ClientActivateAbilitySucceedWithEventData, which is the death
    ///     ability's payload.
    ///
    ///     `TAG_NET_INDEX_BITS` still overrides it, but 14 is no longer a guess.
    /// </summary>
    public static unsafe void WriteTag(FBitWriter writer, uint netIndex) {
        var value = netIndex;
        writer.SerializeInt(&value, 1u << TagNetIndexBits);
    }

    /// <summary>
    ///     The same thing BY NAME, which is what every caller actually has.
    ///     An unknown tag degrades to the empty one and says so once - see FortGameplayTags.IndexOrWarn.
    /// </summary>
    public static void WriteTag(FBitWriter writer, string tagName) =>
        WriteTag(writer, Net.Abilities.FortGameplayTags.IndexOrWarn(tagName));

    /// <summary>See <see cref="WriteTag" />. TAG_NET_INDEX_BITS overrides it.</summary>
    private static readonly int TagNetIndexBits =
        int.TryParse(FBeaconProcess.Options.Get("TAG_NET_INDEX_BITS"), out var bits) && bits > 0
            ? bits
            : Net.Abilities.FortGameplayTags.NetIndexBits;

    /// <summary>
    ///     The EMPTY tag on the wire: `UGameplayTagsManager::InvalidTagNetIndex`, which is
    ///     NodeCount + 1 = **13503**.
    ///
    ///     THIS WAS 0 AND THAT WAS A REAL BUG, not a harmless placeholder. Index 0 is not "no tag" -
    ///     it is the FIRST TAG in the sorted table, a perfectly real one. The saving half of
    ///     `FGameplayTag::NetSerialize_Packed` (GameplayTagContainer.cpp:1285) writes
    ///     `GetNetIndexFromTag(*this)`, and that returns `InvalidTagNetIndex` - NodeCount + 1 - for a
    ///     tag it cannot find, which is exactly the empty tag's case. The reading half
    ///     (`GetTagNameFromNetIndex`) turns anything >= NodeCount into NAME_None and everything below
    ///     it into a real tag. So writing 0 told the client "the first tag in your table", silently.
    ///
    ///     The INVALID_TAGNETINDEX macro (MAX_uint16) is a different constant and belongs to the
    ///     REPLAY path only, where the index is a packed int into a net field export group rather
    ///     than a bounded field. That is what the 0 here was conflating.
    ///
    ///     13503 comes from the client's own node count - see <see cref="WriteTag" /> for how it was
    ///     measured out of the memory dump - so it moves if the tag table ever does.
    /// </summary>
    public const uint InvalidTagNetIndex = Net.Abilities.FortGameplayTags.InvalidNetIndex;

    /// <summary>
    ///     An INVALID FGameplayEffectContextHandle: one bit, CLEAR.
    ///     `FGameplayEffectContextHandle::NetSerialize` (GameplayEffectTypes.cpp:310) writes
    ///     `Data.IsValid()` as one bit and returns when it is false.
    /// </summary>
    public static void WriteEmptyEffectContext(FBitWriter writer) => writer.WriteBit(false);

    /// <summary>
    ///     FGameplayEventData, in its declaration order (GameplayAbilityTypes.h:266).
    ///
    ///     NOT a native NetSerialize struct, so as an RPC parameter it recurses into its members and
    ///     each writes itself - the tag fields through their own NetSerialize, the object references
    ///     through the package map, the float flat.
    ///
    ///     WHAT IS ACTUALLY BEING SENT is a payload that EXISTS rather than one that describes the
    ///     death. The client's complaint was "none is being supplied", so presence is the first
    ///     hypothesis and the cheap one; Instigator and Target are filled in because they cost
    ///     nothing and are the two fields a death ability would most plausibly read. If it turns out
    ///     to need a real EventTag, that is a tag-table problem and a much larger one - see WriteTag.
    /// </summary>
    public static void WriteEventData(FBitWriter writer, UPackageMapClient packageMap,
                                      Net.Actors.AActor? instigator, Net.Actors.AActor? target,
                                      float magnitude) {
        WriteTag(writer, InvalidTagNetIndex);          // EventTag

        packageMap.SerializeObject(writer, instigator); // Instigator
        packageMap.SerializeObject(writer, target);     // Target
        packageMap.SerializeObject(writer, null);       // OptionalObject
        packageMap.SerializeObject(writer, null);       // OptionalObject2

        WriteEmptyEffectContext(writer);                // ContextHandle
        WriteEmptyTagContainer(writer);                 // InstigatorTags
        WriteEmptyTagContainer(writer);                 // TargetTags

        writer.WriteFloat(magnitude);                   // EventMagnitude
    }

    /// <summary>
    ///     FGameplayCueParameters, exactly as its native NetSerialize writes it
    ///     (GameplayEffectTypes.cpp:788) - and in ONE place, because three different senders now
    ///     need the identical bytes: the Executed RPC, the Added RPC, and the FActiveGameplayCue
    ///     items inside the ASC's replicated cue list.
    ///
    ///     Twelve RepFlag bits say which members follow; low to high they are NormalizedMagnitude,
    ///     RawMagnitude, EffectContext, Location, Normal, Instigator, EffectCauser, SourceObject,
    ///     TargetAttachComponent, PhysMaterial, GELevel, AbilityLevel. Then BOTH tag containers
    ///     unconditionally - the source comments that out of the flags itself, "empty tag containers
    ///     are frequently replicated" - and then the flagged members in that same order.
    ///
    ///     Only SourceObject is ever carried here, and only when there is one. Every member left out
    ///     is one the reader fills with the value this server would have sent anyway: magnitudes 0,
    ///     levels 1, no context. A cue notify that reads more than its own source object would need
    ///     this extended, and the flag bit is the whole of that change.
    /// </summary>
    public static unsafe void WriteCueParameters(FBitWriter writer, UPackageMapClient packageMap,
                                                 Objects.UObject? sourceObject,
                                                 Math.FVector? location = null) {
        const int repLocation = 3;
        const int repSourceObject = 7;

        var repBits = (ushort) ((sourceObject != null ? 1 << repSourceObject : 0)
                              | (location != null ? 1 << repLocation : 0));
        writer.SerializeBits(&repBits, 12);

        WriteEmptyTagContainer(writer);   // AggregatedSourceTags
        WriteEmptyTagContainer(writer);   // AggregatedTargetTags

        // MEMBERS FOLLOW IN FLAG ORDER, not in the order the caller happens to care about them -
        // Location is bit 3 and SourceObject bit 7, so Location goes first however it was passed.
        //
        // FVector_NetQuantize10, which is the type the member is DECLARED as
        // (GameplayEffectTypes.h:969) and not a choice: scale 10, 24 bits per component. Sending
        // three plain floats would be read as a quantised vector and land somewhere absurd.
        if (location is { } where) where.NetSerializeWriteQuantized(writer, 10, 24);

        if (sourceObject != null) packageMap.SerializeObject(writer, sourceObject);
    }
}
