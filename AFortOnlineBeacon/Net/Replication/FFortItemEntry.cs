namespace AFortOnlineBeacon.Net.Replication;

/// <summary>
///     One entry of AFortInventory::Inventory (an FFortItemList, i.e. a FastArraySerializer).
///
///     Only the properties that actually replicate are modelled. FRepLayout::InitFromStruct walks a
///     struct's properties with TFieldIterator and skips exactly those flagged CPF_RepSkip, so
///     FFortItemEntry's PreviousCount / AlterationDefinitions / ItemSource / GiftingInfo are all
///     out (Dumper-7 shows "RepSkip" on each), as are the three fields it inherits from
///     FFastArraySerializerItem - ReplicationID / ReplicationKey / MostRecentArrayReplicationKey are
///     declared UPROPERTY(NotReplicated) in NetSerialization.h.
///
///     ReplicationID is still meaningful on the wire, just not as a struct member: the FastArray
///     header writes it separately, once per changed element, as a raw int32 before the element's
///     body (NetSerialization.h:1300-1313, "Dont pack this, want property to be byte aligned").
/// </summary>
public sealed class FFortItemEntry {
    /// <summary>The element's FastArray ReplicationID - written by the delta header, not by the struct body. Must be unique and non-negative.</summary>
    public required int ReplicationId { get; init; }

    public required UObject ItemDefinition { get; init; }
    public int Count { get; init; } = 1;
    public short OrderIndex { get; init; }
    public float Durability { get; init; }
    public int Level { get; init; }
    public int LoadedAmmo { get; init; }

    /// <summary>FGuid is NOT one of the structs RepLayout special-cases as atomic, so it recurses into its four int32s (A/B/C/D).</summary>
    public Guid ItemGuid { get; init; } = Guid.NewGuid();

    public bool InventoryOverflowDate { get; init; }
    public bool bWasGifted { get; init; }
    public bool bIsReplicatedCopy { get; init; }
    public bool bIsDirty { get; init; }
    public bool bUpdateStatsOnCollection { get; init; }

    /// <summary>TWeakObjectPtr&lt;AFortInventory&gt; - a UWeakObjectProperty, which AddPropertyCmd still maps to an ordinary PropertyObject cmd.</summary>
    public UObject? ParentInventory { get; set; }

    public int GameplayAbilitySpecHandle { get; init; }
}
