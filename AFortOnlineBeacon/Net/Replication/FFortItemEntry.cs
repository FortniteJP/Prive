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
public sealed class FFortItemEntry : IFastArrayItem {
    /// <summary>The element's FastArray ReplicationID - written by the delta header, not by the struct body. INDEX_NONE until FFastArraySerializer.MarkItemDirty hands one out.</summary>
    public int ReplicationId { get; set; } = UnrealConstants.IndexNone;

    /// <summary>FFastArraySerializerItem::ReplicationKey - never leaves the server; it is what makes "this item changed" decidable.</summary>
    public int ReplicationKey { get; set; }

    public required UObject ItemDefinition { get; init; }

    /// <summary>Settable: a partial drop or a stack merge changes this in place, then MarkItemDirty sends it.</summary>
    public int Count { get; set; } = 1;

    /// <summary>
    ///     The quickbar slot this item claims. **INDEX_NONE, not 0** - 0 is a real slot, and handing
    ///     every item slot 0 is what left the client refusing a pickup with "inventory full" while
    ///     holding a single item. -1 means "no opinion, place it yourself", which is what a real
    ///     server sends: Erbium's FortInventory.cpp MakeItemEntry ends with
    ///     `if (ItemEntry->HasOrderIndex()) ItemEntry->OrderIndex = -1;`.
    /// </summary>
    public short OrderIndex { get; init; } = -1;

    /// <summary>1.0, i.e. undamaged (Erbium: `ItemEntry->Durability = 1.f`). 0 is a broken item.</summary>
    public float Durability { get; init; } = 1.0f;

    public int Level { get; init; }
    /// <summary>
    ///     Settable: firing empties the magazine, and the inventory row is where that has to be
    ///     recorded - the weapon actor is destroyed and rebuilt on every swap, so an ammo count kept
    ///     only there would silently refill itself.
    /// </summary>
    public int LoadedAmmo { get; set; }

    /// <summary>FGuid is NOT one of the structs RepLayout special-cases as atomic, so it recurses into its four int32s (A/B/C/D).</summary>
    public Guid ItemGuid { get; init; } = Guid.NewGuid();

    public bool InventoryOverflowDate { get; init; }
    public bool bWasGifted { get; init; }
    public bool bIsReplicatedCopy { get; init; }
    public bool bIsDirty { get; init; }
    public bool bUpdateStatsOnCollection { get; init; }

    /// <summary>
    ///     TWeakObjectPtr&lt;AFortInventory&gt; - a UWeakObjectProperty, which AddPropertyCmd still maps
    ///     to an ordinary PropertyObject cmd. Left NULL: the item's owner is already implied by
    ///     which inventory's fast array carries it, and a real server sends none here
    ///     (Erbium: `ItemEntry->ParentInventory.ObjectIndex = -1`).
    /// </summary>
    public UObject? ParentInventory { get; set; }

    /// <summary>
    ///     FGameplayAbilitySpecHandle, which wraps one int32. INDEX_NONE, not 0 - 0 is a VALID
    ///     ability spec handle, so zero claims the item is bound to whatever ability got handle 0
    ///     (Erbium: `ItemEntry->GameplayAbilitySpecHandle = FGameplayAbilitySpecHandle(-1)`).
    /// </summary>
    public int GameplayAbilitySpecHandle { get; init; } = -1;
}
