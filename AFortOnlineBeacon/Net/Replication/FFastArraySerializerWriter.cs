namespace AFortOnlineBeacon.Net.Replication;

/// <summary>
///     Writes the payload half of a Custom Delta property whose struct is an FFastArraySerializer -
///     the mechanism real UE uses for every "fast TArray" (FFortItemList, FDelayedQuickBarActionContainer,
///     and most of Fortnite's list-shaped state).
///
///     Custom Delta properties never appear in FRepLayout's ordinary handle+value stream. A struct
///     flagged STRUCT_NetDeltaSerializeNative is excluded by SetupRepStructFlags (RepLayout.cpp:4691-4713)
///     and is instead sent as its own FIELD, using the exact same RepIndex/payload framing as an
///     RPC (UActorChannel::WriteFieldHeaderAndPayload, DataChannel.cpp:3542). Both go into the one
///     shared content-block payload alongside the RepLayout properties - see
///     FObjectReplicator::ReplicateProperties (DataReplication.cpp:1588-1591), which writes
///     RepLayout properties, then ReplicateCustomDeltaProperties, then queued RPCs into a single
///     Writer before wrapping the lot with one numPayloadBits.
///
///     The field payload itself is (FRepLayout::SendCustomDeltaProperty, RepLayout.cpp:3640-3659):
///
///         [1 bit]   bSupportsFastArrayDelta
///         [packed]  StaticArrayIndex   -- ONLY when the property's ArrayDim != 1
///         ...then the struct's own NetDeltaSerialize output.
///
///     That leading bit is easy to miss and is version-gated on the READ side:
///     FRepLayout::ReceiveCustomDeltaProperty only consumes it when
///     Connection->EngineNetworkProtocolVersion >= HISTORY_FAST_ARRAY_DELTA_STRUCT. This project
///     negotiates exactly that version (UNetConnection.DefaultEngineNetworkProtocolVersion = 11,
///     matching Fortnite Release-10.40), so the bit must be written. It is always false here:
///     sending true would opt the client into FastArrayDeltaSerialize_DeltaSerializeStructs, a
///     second, different wire format this project does not implement.
///
///     FFastArraySerializer::FastArrayDeltaSerialize's header (NetSerialization.h,
///     WriteDeltaHeader at line 870, ReadDeltaHeader at 895) is four raw int32s, then one int32 per
///     deleted element, then the changed elements:
///
///         [int32] ArrayReplicationKey
///         [int32] BaseReplicationKey
///         [int32] NumDeletes
///         [int32] NumChanged
///         [int32] DeletedID    x NumDeletes
///         [ID + item payload]  x NumChanged
/// </summary>
internal static class FFastArraySerializerWriter {
    /// <summary>
    ///     Writes a complete, well-formed delta for an array that is empty and has never been marked
    ///     dirty - no items, no deletions, no changes.
    ///
    ///     Both keys are INDEX_NONE, which is what real UE sends in this exact situation:
    ///     FFastArraySerializer's constructor initialises ArrayReplicationKey to INDEX_NONE and only
    ///     IncrementArrayReplicationKey (via MarkArrayDirty/MarkItemDirty) ever moves it off that,
    ///     while BaseReplicationKey is left at INDEX_NONE whenever there is no previous state -
    ///     which is always true for an actor channel's first replication (NetSerialization.h:1250-1282).
    ///
    ///     Note real UE deliberately still sends this header when nothing changed, rather than
    ///     skipping the field: the comment at NetSerialization.h:1290 explains clients need the
    ///     array/base key pair to detect implicit deletes. So an "empty" delta is a real message,
    ///     not a no-op, and the receiving side runs its full PostReceiveCleanup callback sequence
    ///     (PreReplicatedRemove -> PostReplicatedAdd -> PostReplicatedChange, all called
    ///     unconditionally with empty index lists) plus the property's RepNotify.
    /// </summary>
    public static void WriteEmptyDelta(FNetBitWriter payload) {
        payload.WriteBit(false); // bSupportsFastArrayDelta - see class doc; never true here.

        WriteInt32(payload, -1); // ArrayReplicationKey  (INDEX_NONE: never marked dirty)
        WriteInt32(payload, -1); // BaseReplicationKey   (INDEX_NONE: no previous state)
        WriteInt32(payload, 0);  // NumDeletes
        WriteInt32(payload, 0);  // NumChanged
    }

    /// <summary>
    ///     Writes a delta that adds every entry in <paramref name="items"/> as a changed element.
    ///
    ///     Each element is a raw int32 ReplicationID (explicitly NOT packed - NetSerialization.h:1305
    ///     comments "Dont pack this, want property to be byte aligned") followed by the item struct's
    ///     body, written by <see cref="WriteItemEntry"/>.
    ///
    ///     ArrayReplicationKey is 0 here rather than INDEX_NONE: a list that has had items added has
    ///     been through MarkItemDirty, which runs IncrementArrayReplicationKey and moves the key off
    ///     INDEX_NONE. BaseReplicationKey stays INDEX_NONE because an actor channel's first
    ///     replication has no previous state to diff against.
    /// </summary>
    public static void WriteItemListDelta(FNetBitWriter payload, IReadOnlyList<FFortItemEntry> items) {
        payload.WriteBit(false); // bSupportsFastArrayDelta - see class doc; never true here.

        WriteInt32(payload, 0);            // ArrayReplicationKey
        WriteInt32(payload, -1);           // BaseReplicationKey (INDEX_NONE: no previous state)
        WriteInt32(payload, 0);            // NumDeletes
        WriteInt32(payload, items.Count);  // NumChanged

        foreach (var item in items) {
            WriteInt32(payload, item.ReplicationId);
            WriteItemEntry(payload, item);
        }
    }

    /// <summary>
    ///     Serializes one FFortItemEntry the way FRepLayout::SerializePropertiesForStruct does
    ///     (RepLayout.cpp) - and that is deliberately NOT the handle+value+terminator stream used for
    ///     an actor's properties. For a struct, every replicated member is written unconditionally,
    ///     in order, with no handles and no terminator at all:
    ///
    ///         for (int32 i = 0; i &lt; Parents.Num(); i++)
    ///             SerializeProperties_r(Ar, Map, Parents[i].CmdStart, Parents[i].CmdEnd, ...);
    ///
    ///     which means the reader depends entirely on both sides agreeing on the member list and its
    ///     order - there is no resync point. Order is TFieldIterator order, i.e. declaration order
    ///     (UECodeGen adds properties in reverse so that Children ends up forward), and CPF_RepSkip
    ///     members are skipped. A dynamic array member writes a raw uint16 element count first
    ///     (SerializeProperties_DynamicArray_r), then that many elements - all three arrays here are
    ///     always empty, so each is just the count.
    /// </summary>
    private static unsafe void WriteItemEntry(FNetBitWriter payload, FFortItemEntry item) {
        WriteInt32(payload, item.Count);
        ((UPackageMapClient) payload.PackageMap!).SerializeObject(payload, item.ItemDefinition);

        var orderIndex = item.OrderIndex;
        payload.SerializeBits(&orderIndex, 16);

        var durability = item.Durability;
        payload.SerializeBits(&durability, 32);

        WriteInt32(payload, item.Level);
        WriteInt32(payload, item.LoadedAmmo);

        // FGuid recurses into four int32s - it is not in RepLayout's atomic special-case list.
        foreach (var part in GuidToAbcd(item.ItemGuid)) WriteInt32(payload, part);

        payload.WriteBit(item.InventoryOverflowDate);
        payload.WriteBit(item.bWasGifted);
        payload.WriteBit(item.bIsReplicatedCopy);
        payload.WriteBit(item.bIsDirty);
        payload.WriteBit(item.bUpdateStatsOnCollection);

        WriteEmptyArray(payload);  // StateValues
        ((UPackageMapClient) payload.PackageMap!).SerializeObject(payload, item.ParentInventory);
        WriteInt32(payload, item.GameplayAbilitySpecHandle); // FGameplayAbilitySpecHandle wraps one int32
        WriteEmptyArray(payload);  // AlterationInstances
        WriteEmptyArray(payload);  // GenericAttributeValues
    }

    /// <summary>
    ///     Writes AFortGameStateAthena::CurrentPlaylistInfo (an FPlaylistPropertyArray).
    ///
    ///     MEASURED, not guessed. Two live runs against a real 10.40 client, one with BasePlaylist
    ///     written before the FastArray header and one after, produced two DIFFERENT errors, and
    ///     only one reader shape explains both. Payload was 137 bits either way
    ///     (1 flag + 8 NetGUID + 4x32 header):
    ///
    ///       base_first  -> "FBitReader::SetOverflowed() called! (ReadLen: 32, Remaining: 8, Max: 137)"
    ///                      The reader took our 8 NetGUID bits as the start of ArrayReplicationKey,
    ///                      which shifted the header by one byte and made NumDeletes read as 255;
    ///                      it then tried to read the first deleted ID (32 bits) at bit 129 with 8
    ///                      left. Every number in that message is reproduced exactly by that model.
    ///       array_first -> "NetDeltaSerialize - Mismatch read" (ReceiveCustomDeltaProperty's
    ///                      Reader.GetBitsLeft() != 0 branch). The reader consumed exactly
    ///                      1 + 128 = 129 bits and left our 8 trailing NetGUID bits untouched.
    ///
    ///     So this struct's reader is the PLAIN FFastArraySerializer path and reads nothing else -
    ///     BasePlaylist is not in the custom-delta payload at all. It goes through the ordinary
    ///     RepLayout handle stream instead, at handle 155; see NativeRepLayouts.GameStateProps.
    ///     (Real UE never sends a custom-delta parent's handles - CompareProperties skips them at
    ///     RepLayout.cpp:4113/4269 - but the RECEIVE side has no such check: ReceiveProperties_r
    ///     takes any handle it is given, writes the value, and queues the parent's RepNotify, which
    ///     for this parent is OnRep_CurrentPlaylistInfo.)
    ///
    ///     PropertyOverrides is left empty: overrides are hotfix/LTM tweaks layered on the base
    ///     playlist, and the client is happy with none.
    /// </summary>
    public static void WritePlaylistPropertyArrayDelta(FNetBitWriter payload) {
        payload.WriteBit(false); // bSupportsFastArrayDelta - see class doc; never true here.

        WriteInt32(payload, 0);  // ArrayReplicationKey - the array has been marked dirty once
        WriteInt32(payload, -1); // BaseReplicationKey (INDEX_NONE: no previous state)
        WriteInt32(payload, 0);  // NumDeletes
        WriteInt32(payload, 0);  // NumChanged - no FPropertyOverride entries
    }

    /// <summary>
    ///     Writes AFortGameStateAthena::GameMemberInfoArray (FGameMemberInfoArray, ClassNetCache
    ///     field 135) with one FGameMemberInfo per player.
    ///
    ///     This is what makes the client call AFortGameStateAthena::NotifyGameMemberAdded, whose own
    ///     format strings give the whole game away:
    ///         "%s: Adding Player state with UniqueId: %s, in team: %d, and in squad: %d"
    ///         "%s: Didn't find existing player state with UniqueId: %s, in team: %d, and in squad: %d"
    ///     i.e. the array is the client's team/squad roster and it is keyed BY UNIQUE ID - which is
    ///     why APlayerState::UniqueId (handle 25) had to land first.
    ///
    ///     Unlike FPlaylistPropertyArray, this struct adds no replicated members of its own beyond
    ///     Members (OwningGameState is RepSkip), so the stock FFastArraySerializer header should be
    ///     the whole story here.
    ///
    ///     FGameMemberInfo derives from FFastArraySerializerItem, whose three members are all
    ///     RepSkip, so the item body is just its own three properties in declaration order:
    ///     SquadId (uint8, 8 bits), TeamIndex (uint8, 8 bits), MemberUniqueId (FUniqueNetIdRepl,
    ///     one atomic PropertyNetId cmd).
    /// </summary>
    public static unsafe void WriteGameMemberInfoArrayDelta(FNetBitWriter payload, IReadOnlyList<FGameMemberInfo> members) {
        payload.WriteBit(false); // bSupportsFastArrayDelta - see class doc; never true here.

        WriteInt32(payload, 0);              // ArrayReplicationKey - MarkItemDirty has run
        WriteInt32(payload, -1);             // BaseReplicationKey (INDEX_NONE: no previous state)
        WriteInt32(payload, 0);              // NumDeletes
        WriteInt32(payload, members.Count);  // NumChanged

        foreach (var member in members) {
            WriteInt32(payload, member.ReplicationId);

            var squadId = member.SquadId;
            payload.SerializeBits(&squadId, 8);

            var teamIndex = member.TeamIndex;
            payload.SerializeBits(&teamIndex, 8);

            FUniqueNetIdRepl.Write(payload, member.MemberUniqueId ?? new FUniqueNetIdRepl());
        }
    }

    /// <summary>A dynamic array member inside a struct is a raw uint16 count then the elements - see SerializeProperties_DynamicArray_r.</summary>
    private static unsafe void WriteEmptyArray(FNetBitWriter payload) {
        ushort num = 0;
        payload.SerializeBits(&num, 16);
    }

    /// <summary>FGuid's wire form is its four int32 members A,B,C,D in declaration order.</summary>
    private static int[] GuidToAbcd(Guid guid) {
        var b = guid.ToByteArray();
        return new[] {
            BitConverter.ToInt32(b, 0), BitConverter.ToInt32(b, 4),
            BitConverter.ToInt32(b, 8), BitConverter.ToInt32(b, 12)
        };
    }

    /// <summary>
    ///     FBitWriter's operator&lt;&lt;(int32) is a plain 4-byte Serialize, i.e. 32 raw little-endian
    ///     bits at the current (possibly unaligned) bit position - NOT SerializeIntPacked. The
    ///     FastArray header relies on this fixed width, so it must not be packed.
    /// </summary>
    private static unsafe void WriteInt32(FNetBitWriter payload, int value) {
        payload.SerializeBits(&value, 32);
    }
}
