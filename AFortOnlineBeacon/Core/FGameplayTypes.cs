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
    ///     THIRTEEN BITS FLAT, and the flatness is the part worth explaining.
    ///     `FGameplayTag::NetSerialize_Packed` normally writes a first segment plus a "more" bit -
    ///     but `SerializeTagNetIndexPacked` (GameplayTagContainer.cpp:42) short-circuits to a plain
    ///     `MaxBits` field when `NetIndexFirstBitSegment >= MaxBits`, and
    ///     `UGameplayTagsManager::ConstructNetIndex` forces exactly that with
    ///     `NetIndexFirstBitSegment = FMath::Min(NetIndexFirstBitSegment, NetIndexTrueBitNum)`
    ///     (GameplayTagsManager.cpp:473). So the packed path never actually packs, and a tag is one
    ///     field of `NetIndexTrueBitNum` bits.
    ///
    ///     WHERE 13 COMES FROM. `NetIndexTrueBitNum = CeilToInt(Log2(NodeCount + 1))`.
    ///     Fortnite's `DefaultGameplayTags.ini` carries **4203** tags, which expand to **6061**
    ///     hierarchy nodes (every "A.B.C" also contributes "A" and "A.B"), and 6062 needs 13 bits.
    ///     The band for 13 is 4096..8191, so there is room for ~2100 more nodes from the 37 tag
    ///     DataTables before it would become 14 - and those tables are largely the source the ini
    ///     was imported from (`ImportTagsFromConfig=True`), so they overlap rather than add.
    ///
    ///     IT IS AN ESTIMATE, and `TAG_NET_INDEX_BITS` exists because of that. The one number that
    ///     would settle it exactly is the node count, and neither log prints it - both the client
    ///     and the reference server print only `NetworkGameplayTagNodeIndexHash is 79b9274c`, which
    ///     confirms the two agree without saying how many. If tag-bearing traffic misbehaves, 12 and
    ///     14 are the only other candidates worth trying.
    /// </summary>
    public static unsafe void WriteTag(FBitWriter writer, uint netIndex) {
        var value = netIndex;
        writer.SerializeInt(&value, 1u << TagNetIndexBits);
    }

    /// <summary>See <see cref="WriteTag" />. TAG_NET_INDEX_BITS overrides it.</summary>
    private static readonly int TagNetIndexBits =
        int.TryParse(Environment.GetEnvironmentVariable("TAG_NET_INDEX_BITS"), out var bits) && bits > 0
            ? bits
            : 13;

    /// <summary>
    ///     INVALID_TAGNETINDEX - the empty tag. Zero, and it is the engine's own constant rather
    ///     than `UGameplayTagsManager::InvalidTagNetIndex`, which is NodeCount+1 and therefore a
    ///     number only the tag table knows. `NetSerialize_Packed` writes 0 for anything it cannot
    ///     name, precisely so the two sides need not agree on that.
    /// </summary>
    public const uint InvalidTagNetIndex = 0;

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
}
