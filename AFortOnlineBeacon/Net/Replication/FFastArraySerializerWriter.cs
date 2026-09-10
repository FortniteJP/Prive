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
///     matching Fortnite Release-10.40), so the bit must be written.
///
///     THE BIT SELECTS BETWEEN TWO ITEM FORMATS, and which one an array uses is decided ONCE, per
///     receiving object, and never revisited (NetSerialization.h:1232-1239 sets
///     EFastArraySerializerDeltaFlags::IsUsingDeltaSerialization, and line 1070 honours it on every
///     later call). That is not a per-connection negotiation we control: the client's own
///     DemoNetDriver serialises these same arrays when it records a newly created actor, it does so
///     with the flag TRUE, and whoever touches the array first wins. Writing false and hoping to
///     get there first is the race this project lost - see [[fastarray-delta-latch]]: on one live
///     session 144 plain GameplayAbilitySpec reads succeeded and all 5 that took the struct path
///     failed, every one of them the second player's PlayerState, which is what "the first player
///     to join loses all abilities" actually was.
///
///     So the bit is written TRUE, and the struct format implemented, for every array whose owning
///     struct asks for it. Which arrays those are is not a guess: FFastArraySerializer only takes
///     the struct path when the owner called SetDeltaSerializationEnabled(true)
///     (EFastArraySerializerDeltaFlags::HasDeltaBeenRequested), and a real 10.40 client's log names
///     each one it ever took that path for. On 10.40 that list includes GameplayAbilitySpec,
///     ActiveGameplayEffect and FortItemEntry - the three this server writes items into - and does
///     NOT include GameMemberInfo, which therefore stays plain. Claiming the struct format for an
///     array the client did not request it for would be read as plain and corrupt every item.
///
///     The header is IDENTICAL in both formats (WriteDeltaHeader is shared), so an array that
///     writes no items - FPlaylistPropertyArray - is byte-for-byte the same either way.
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
    ///     Whether item bodies are written in the DELTA-STRUCT format
    ///     (FFastArraySerializer::FastArrayDeltaSerialize_DeltaSerializeStructs) - the default, and
    ///     the only format that cannot lose the latch race described in the class doc.
    ///
    ///     FASTARRAY_DELTA_STRUCT=0 reverts every array to the plain format. It exists because this
    ///     is a wire-format change to the one payload that carries abilities, effects and the
    ///     inventory at once: if the struct format is wrong, NOTHING about a player works, and the
    ///     fastest way to tell "my encoding is wrong" from "something else broke" is to put the
    ///     known-good encoding back in one run without rebuilding.
    /// </summary>
    private static bool UseDeltaStruct => Environment.GetEnvironmentVariable("FASTARRAY_DELTA_STRUCT") is not "0";

    /// <summary>
    ///     One property handle, exactly as WritePropertyHandle does it (RepLayout.cpp:1227) -
    ///     SerializeIntPacked and nothing else.
    ///
    ///     NO LEADING bDoChecksum BIT anywhere in this stream. An actor's property push needs one
    ///     (see [[replayout-checksum-bit]] - its absence cost a disconnect), but the fast-array item
    ///     path hard-codes bDoChecksum to false at RepLayout.cpp:6713/6721 and never reads it, so
    ///     writing one here would shift every handle by a bit.
    /// </summary>
    private static void WriteHandle(FNetBitWriter payload, uint handle) => payload.SerializeIntPacked(handle);

    /// <summary>
    ///     A dynamic array inside an item's handle stream: its own handle, then a RAW uint16 element
    ///     count (SendProperties_r, RepLayout.cpp:2004 - the count is deliberately not packed).
    ///     Elements follow as their own handle streams; close with <see cref="EndArray" />.
    ///
    ///     An element's handles are relative and continuous across the whole array: member j
    ///     (1-based) of element i is `i * HandlesPerElement + j`, which FRepHandleIterator::NextHandle
    ///     (RepLayout.cpp:1577) inverts by exactly that division. So the handle count per element is
    ///     a DIVISOR, not just a list length - getting it wrong renumbers every element after the
    ///     first rather than dropping a member.
    /// </summary>
    private static unsafe void BeginArray(FNetBitWriter payload, uint handle, int count) {
        WriteHandle(payload, handle);

        var num = (ushort) count;
        payload.SerializeBits(&num, 16);
    }

    /// <summary>The 0 handle that closes a dynamic array (SendProperties_r, RepLayout.cpp:2033).</summary>
    private static void EndArray(FNetBitWriter payload) => WriteHandle(payload, 0);

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
        // bSupportsFastArrayDelta. False, and it does not matter which: this writes no ITEMS, and
        // the two formats share WriteDeltaHeader, so the bytes are identical either way. All the
        // bit decides is which format the client latches for an array we never send items into.
        payload.WriteBit(false);

        WriteInt32(payload, -1); // ArrayReplicationKey  (INDEX_NONE: never marked dirty)
        WriteInt32(payload, -1); // BaseReplicationKey   (INDEX_NONE: no previous state)
        WriteInt32(payload, 0);  // NumDeletes
        WriteInt32(payload, 0);  // NumChanged
    }

    /// <summary>
    ///     The write half of FFastArraySerializer::FastArrayDeltaSerialize (NetSerialization.h:1242-1317)
    ///     against a per-connection base state: what changed since this connection was last sent
    ///     this array.
    ///
    ///     An element counts as changed when its ReplicationKey differs from the one the base state
    ///     recorded, or when its id is absent from the base state entirely (it is new). An id the
    ///     base state knows about but the array no longer contains is a delete.
    ///
    ///     Returns false when there is nothing to say, which the caller must treat as "write no
    ///     field at all" - not as "write an empty header". Real UE takes the same early-out
    ///     (ConditionalCreateNewDeltaState, NetSerialization.h:1263) whenever ArrayReplicationKey
    ///     matches the base. Note this is only valid once a base state EXISTS: the very first send
    ///     always goes out, even for an empty array, because the client needs the key pair before it
    ///     can reason about deletes at all.
    ///
    ///     <paramref name="baseState"/> is STAGED to describe what this delta just put on the wire.
    ///     The caller must call Commit() on it once the bunch is away, or Discard() if it never went -
    ///     see FNetFastTArrayBaseState.StagePending for why writing and sending must not be conflated.
    /// </summary>
    public static bool WriteDelta<T>(
        FNetBitWriter payload,
        FFastArraySerializer<T> array,
        FNetFastTArrayBaseState baseState,
        Action<FNetBitWriter, T> writeItemBody,
        Action<FNetBitWriter, T>? writeItemBodyDeltaStruct = null) where T : class, IFastArrayItem {

        var isFirstSend = baseState.ArrayReplicationKey == UnrealConstants.IndexNone && baseState.IdToKey.Count == 0;
        if (!isFirstSend && baseState.ArrayReplicationKey == array.ArrayReplicationKey) return false;

        var changed = new List<T>();
        var newIdToKey = new Dictionary<int, int>();

        foreach (var item in array.Items) {
            if (item.ReplicationId == UnrealConstants.IndexNone) {
                // Real UE calls MarkItemDirty here rather than shipping an unidentified element.
                array.MarkItemDirty(item);
            }

            newIdToKey[item.ReplicationId] = item.ReplicationKey;

            if (baseState.IdToKey.TryGetValue(item.ReplicationId, out var sentKey) && sentKey == item.ReplicationKey) continue;

            changed.Add(item);
        }

        var deleted = baseState.IdToKey.Keys.Where(id => !newIdToKey.ContainsKey(id)).ToArray();

        // Which of the two item formats this array speaks. See the class doc, and
        // FFastArraySerializerWriter.UseDeltaStruct for the switch.
        var deltaStruct = writeItemBodyDeltaStruct != null && UseDeltaStruct;

        payload.WriteBit(deltaStruct); // bSupportsFastArrayDelta

        WriteInt32(payload, array.ArrayReplicationKey);
        WriteInt32(payload, baseState.ArrayReplicationKey); // what this connection last received
        WriteInt32(payload, deleted.Length);
        WriteInt32(payload, changed.Count);

        foreach (var id in deleted) WriteInt32(payload, id);

        foreach (var item in changed) {
            WriteInt32(payload, item.ReplicationId); // raw, NOT packed - NetSerialization.h:1305

            if (!deltaStruct) {
                writeItemBody(payload, item);
                continue;
            }

            // bAnythingSent (RepLayout.cpp:6706). The write side sets it from
            // `Changelist.Num() > 1`, i.e. "this element has at least one changed property"; this
            // server never sends an element it has nothing to say about, and always sends the
            // element WHOLE, so it is always true here.
            payload.WriteBit(true);
            writeItemBodyDeltaStruct!(payload, item);

            // End of this element's handle stream. ReceiveProperties_r stops on it and
            // DeltaSerializeFastArrayProperty then insists it really was 0 - the
            // "ReceiveFastArrayItem: Invalid property terminator handle" error is this check
            // failing, which is what a wrong item LENGTH looks like from the client's side.
            WriteHandle(payload, 0);
        }

        // STAGED, NOT COMMITTED - the caller commits once the bunch carrying this is actually away.
        // See FNetFastTArrayBaseState.StagePending for what committing here used to cost.
        baseState.StagePending(array.ArrayReplicationKey, newIdToKey);

        return true;
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
    public static unsafe void WriteItemEntry(FNetBitWriter payload, FFortItemEntry item) {
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
        // bSupportsFastArrayDelta. False, and it does not matter which: this writes no ITEMS, and
        // the two formats share WriteDeltaHeader, so the bytes are identical either way. All the
        // bit decides is which format the client latches for an array we never send items into.
        payload.WriteBit(false);

        WriteInt32(payload, 0);  // ArrayReplicationKey - the array has been marked dirty once
        WriteInt32(payload, -1); // BaseReplicationKey (INDEX_NONE: no previous state)
        WriteInt32(payload, 0);  // NumDeletes
        WriteInt32(payload, 0);  // NumChanged - no FPropertyOverride entries
    }

    /// <summary>
    ///     One element body of AFortGameStateAthena::GameMemberInfoArray (FGameMemberInfoArray,
    ///     ClassNetCache field 135) - the delta header around it is written by WriteDelta.
    ///
    ///     This is what makes the client call AFortGameStateAthena::NotifyGameMemberAdded, whose own
    ///     format strings give the whole game away:
    ///         "%s: Adding Player state with UniqueId: %s, in team: %d, and in squad: %d"
    ///         "%s: Didn't find existing player state with UniqueId: %s, in team: %d, and in squad: %d"
    ///     i.e. the array is the client's team/squad roster and it is keyed BY UNIQUE ID - which is
    ///     why APlayerState::UniqueId (handle 25) had to land first.
    ///
    ///     Unlike FPlaylistPropertyArray, this struct adds no replicated members of its own beyond
    ///     Members (OwningGameState is RepSkip), so the stock FFastArraySerializer header is the
    ///     whole story here.
    ///
    ///     FGameMemberInfo derives from FFastArraySerializerItem, whose three members are all
    ///     RepSkip, so the item body is just its own three properties in declaration order:
    ///     SquadId (uint8, 8 bits), TeamIndex (uint8, 8 bits), MemberUniqueId (FUniqueNetIdRepl,
    ///     one atomic PropertyNetId cmd).
    /// </summary>
    public static unsafe void WriteGameMemberInfo(FNetBitWriter payload, FGameMemberInfo member) {
        var squadId = member.SquadId;
        payload.SerializeBits(&squadId, 8);

        var teamIndex = member.TeamIndex;
        payload.SerializeBits(&teamIndex, 8);

        FUniqueNetIdRepl.Write(payload, member.MemberUniqueId ?? new FUniqueNetIdRepl());
    }

    /// <summary>A dynamic array member inside a struct is a raw uint16 count then the elements - see SerializeProperties_DynamicArray_r.</summary>
    private static unsafe void WriteEmptyArray(FNetBitWriter payload) {
        ushort num = 0;
        payload.SerializeBits(&num, 16);
    }

    /// <summary>
    ///     The same uint16-count-then-elements shape as WriteEmptyArray, for an array whose element
    ///     is a single object reference. An empty list writes byte-for-byte what WriteEmptyArray
    ///     does, so this is a safe drop-in wherever a slot used to be hardcoded empty.
    /// </summary>
    private static unsafe void WriteObjectArray(FNetBitWriter payload, IReadOnlyList<UObject> items) {
        var num = (ushort) items.Count;
        payload.SerializeBits(&num, 16);

        var packageMap = (UPackageMapClient) payload.PackageMap!;
        foreach (var item in items) packageMap.SerializeObject(payload, item);
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
    /// <summary>
    ///     One FGameplayAbilitySpec item body - the six non-RepSkip members, in the struct's own
    ///     declaration (offset) order. Everything omitted here is RepSkip in the 10.40 SDK, i.e.
    ///     server bookkeeping the client rebuilds itself; see FGameplayAbilitySpec.
    /// </summary>
    public static void WriteAbilitySpec(FNetBitWriter payload, FGameplayAbilitySpec spec) {
        var packageMap = (UPackageMapClient) payload.PackageMap!;

        WriteInt32(payload, spec.Handle);           // FGameplayAbilitySpecHandle - one bare int32
        packageMap.SerializeObject(payload, spec.Ability);
        WriteInt32(payload, spec.Level);
        WriteInt32(payload, spec.InputID);
        packageMap.SerializeObject(payload, spec.SourceObject);

        // ReplicatedInstances. Empty for every ReplicateNo ability, which is nearly all of them -
        // those the CLIENT instances itself, in OnGiveAbility. A ReplicateYes ability is the exact
        // opposite: the client deliberately makes none and waits for this, and until it arrives the
        // ability activates on its CDO and can send no Server_* RPC at all. See
        // UGameplayAbilityInstance.
        WriteObjectArray(payload, spec.ReplicatedInstances);
    }

    /// <summary>
    ///     One FActiveGameplayEffect - an element of UAbilitySystemComponent::ActiveGameplayEffects,
    ///     and the thing that gives the client's GAS an aggregator for an attribute (see
    ///     FActiveGameplayEffect's doc comment for why that is the whole point).
    ///
    ///     Members in RepLayout order, i.e. by offset with CPF_RepSkip absent. For the item:
    ///     Spec, PredictionKey, StartServerWorldTime (CachedStartServerWorldTime, StartWorldTime and
    ///     bIsInhibited are RepSkip). For the Spec: Def, ModifiedAttributes, Duration, Period,
    ///     ChanceToApplyToTarget, DynamicGrantedTags, DynamicAssetTags, Modifiers, StackCount,
    ///     GrantedAbilitySpecs, EffectContext, Level.
    ///
    ///     Two sub-encodings are exact rather than guessed, both read out of the engine source:
    ///     an EMPTY FGameplayTagContainer is a single 1 bit and nothing else
    ///     (GameplayTagContainer.cpp:968), and an INVALID FGameplayEffectContextHandle is a single
    ///     0 bit (GameplayEffectTypes.cpp:310). The context is sent invalid deliberately - a valid
    ///     one defers to Fortnite's FFortGameplayEffectContext subclass, whose NetSerialize is
    ///     native code in the client's encrypted .text, and a guessed layout would corrupt every
    ///     field after it.
    /// </summary>
    public static unsafe void WriteActiveGameplayEffect(FNetBitWriter payload, FActiveGameplayEffect effect) {
        var packageMap = (UPackageMapClient) payload.PackageMap!;
        var spec = effect.Spec;

        packageMap.SerializeObject(payload, spec.Def);

        var modifiedCount = (ushort) spec.ModifiedAttributes.Count;
        payload.SerializeBits(&modifiedCount, 16);
        foreach (var modified in spec.ModifiedAttributes) {
            // FGameplayAttribute has no native NetSerialize, so it flattens to its three members in
            // offset order: AttributeName (0x00), Attribute (0x10), AttributeOwner (0x18).
            payload.WriteString(modified.AttributeName);
            packageMap.SerializeObject(payload, modified.Attribute);
            packageMap.SerializeObject(payload, modified.AttributeOwner);
            payload.WriteFloat(modified.TotalMagnitude);
        }

        payload.WriteFloat(spec.Duration);
        payload.WriteFloat(spec.Period);
        payload.WriteFloat(spec.ChanceToApplyToTarget);

        payload.WriteBit(true);  // DynamicGrantedTags: IsEmpty
        payload.WriteBit(true);  // DynamicAssetTags:   IsEmpty

        var modifierCount = (ushort) spec.Modifiers.Count;
        payload.SerializeBits(&modifierCount, 16);
        // FModifierSpec is one float - the EVALUATED magnitude. Which attribute it applies to and
        // with which operation comes from Def's own modifier list, in this same order.
        foreach (var magnitude in spec.Modifiers) payload.WriteFloat(magnitude);

        WriteInt32(payload, spec.StackCount);
        WriteEmptyArray(payload); // GrantedAbilitySpecs - this server grants abilities directly

        payload.WriteBit(false);  // EffectContext: ValidData = 0
        payload.WriteFloat(spec.Level);

        FPredictionKey.Write(payload, effect.PredictionKey);
        payload.WriteFloat(effect.StartServerWorldTime);
    }

    // ----------------------------------------------------------------------------------------
    // DELTA-STRUCT item bodies.
    //
    // Same VALUES as the plain writers above, in the same order, with a handle in front of each one
    // and a 0 handle at the end (written by WriteDelta). The handle numbers are FRepLayout's for the
    // array's INNER struct: InitFromProperty_r recurses into ArrayProp->Inner starting at
    // RelativeHandle 0 (RepLayout.cpp:4605), so an item's handles are 1..N over its own non-RepSkip
    // members in offset order - the SAME numbering rule as a class, restarted.
    //
    // Every number below is reproduced by `python Tools/RepHandles/rep_handles.py <Struct> --struct`
    // against the 10.40 SDK, which is the tool of record for handles; the tables here are its
    // output, not a reading of the C# classes.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    ///     One FGameplayAbilitySpec, delta-struct format. Handles:
    ///
    ///         1  Handle.Handle        int32   (FGameplayAbilitySpecHandle recurses to its one int32)
    ///         2  Ability              object
    ///         3  Level                int32
    ///         4  InputID              int32
    ///         5  SourceObject         object
    ///         6  ReplicatedInstances  TArray&lt;UGameplayAbility*&gt;, 1 handle per element
    ///
    ///     See <see cref="WriteAbilitySpec" /> for what the values mean and why ReplicatedInstances
    ///     is load-bearing for a ReplicateYes ability.
    /// </summary>
    public static void WriteAbilitySpecDeltaStruct(FNetBitWriter payload, FGameplayAbilitySpec spec) {
        var packageMap = (UPackageMapClient) payload.PackageMap!;

        WriteHandle(payload, 1);
        WriteInt32(payload, spec.Handle);

        WriteHandle(payload, 2);
        packageMap.SerializeObject(payload, spec.Ability);

        WriteHandle(payload, 3);
        WriteInt32(payload, spec.Level);

        WriteHandle(payload, 4);
        WriteInt32(payload, spec.InputID);

        WriteHandle(payload, 5);
        packageMap.SerializeObject(payload, spec.SourceObject);

        BeginArray(payload, 6, spec.ReplicatedInstances.Count);
        for (var index = 0; index < spec.ReplicatedInstances.Count; index++) {
            WriteHandle(payload, (uint) index + 1); // one handle per element: an object ref is one leaf
            packageMap.SerializeObject(payload, spec.ReplicatedInstances[index]);
        }

        EndArray(payload);
    }

    /// <summary>
    ///     One FActiveGameplayEffect, delta-struct format. Handles - note Spec is a plain struct, so
    ///     RepLayout FLATTENS it into the item's own handle space rather than giving it a handle:
    ///
    ///          1  Spec.Def                   object
    ///          2  Spec.ModifiedAttributes    TArray&lt;FGameplayEffectModifiedAttribute&gt;, 4 handles/element
    ///          3  Spec.Duration              float
    ///          4  Spec.Period                float
    ///          5  Spec.ChanceToApplyToTarget float
    ///          6  Spec.DynamicGrantedTags    FGameplayTagContainer (atomic)
    ///          7  Spec.DynamicAssetTags      FGameplayTagContainer (atomic)
    ///          8  Spec.Modifiers             TArray&lt;FModifierSpec&gt;, 1 handle/element
    ///          9  Spec.StackCount            int32
    ///         10  Spec.GrantedAbilitySpecs   TArray, always empty here
    ///         11  Spec.EffectContext         FGameplayEffectContextHandle (atomic)
    ///         12  Spec.Level                 float
    ///         13  PredictionKey              FPredictionKey (atomic)
    ///         14  StartServerWorldTime       float
    ///
    ///     The two exact sub-encodings are the ones <see cref="WriteActiveGameplayEffect" /> already
    ///     documents: an empty FGameplayTagContainer is a single 1 bit
    ///     (GameplayTagContainer.cpp:968) and an invalid FGameplayEffectContextHandle is a single
    ///     0 bit (GameplayEffectTypes.cpp:310).
    /// </summary>
    public static void WriteActiveGameplayEffectDeltaStruct(FNetBitWriter payload, FActiveGameplayEffect effect) {
        var packageMap = (UPackageMapClient) payload.PackageMap!;
        var spec = effect.Spec;

        WriteHandle(payload, 1);
        packageMap.SerializeObject(payload, spec.Def);

        BeginArray(payload, 2, spec.ModifiedAttributes.Count);
        for (var index = 0; index < spec.ModifiedAttributes.Count; index++) {
            var modified = spec.ModifiedAttributes[index];

            // 4 handles per element: FGameplayAttribute has no native NetSerialize, so it flattens
            // into its three members and TotalMagnitude follows them.
            var element = (uint) index * 4;

            WriteHandle(payload, element + 1);
            payload.WriteString(modified.AttributeName);

            WriteHandle(payload, element + 2);
            packageMap.SerializeObject(payload, modified.Attribute);

            WriteHandle(payload, element + 3);
            packageMap.SerializeObject(payload, modified.AttributeOwner);

            WriteHandle(payload, element + 4);
            payload.WriteFloat(modified.TotalMagnitude);
        }

        EndArray(payload);

        WriteHandle(payload, 3);
        payload.WriteFloat(spec.Duration);

        WriteHandle(payload, 4);
        payload.WriteFloat(spec.Period);

        WriteHandle(payload, 5);
        payload.WriteFloat(spec.ChanceToApplyToTarget);

        WriteHandle(payload, 6);
        payload.WriteBit(true); // DynamicGrantedTags: IsEmpty

        WriteHandle(payload, 7);
        payload.WriteBit(true); // DynamicAssetTags: IsEmpty

        BeginArray(payload, 8, spec.Modifiers.Count);
        for (var index = 0; index < spec.Modifiers.Count; index++) {
            WriteHandle(payload, (uint) index + 1); // FModifierSpec is one float, so one handle each
            payload.WriteFloat(spec.Modifiers[index]);
        }

        EndArray(payload);

        WriteHandle(payload, 9);
        WriteInt32(payload, spec.StackCount);

        BeginArray(payload, 10, 0); // GrantedAbilitySpecs - this server grants abilities directly
        EndArray(payload);

        WriteHandle(payload, 11);
        payload.WriteBit(false); // EffectContext: ValidData = 0

        WriteHandle(payload, 12);
        payload.WriteFloat(spec.Level);

        WriteHandle(payload, 13);
        FPredictionKey.Write(payload, effect.PredictionKey);

        WriteHandle(payload, 14);
        payload.WriteFloat(effect.StartServerWorldTime);
    }

    /// <summary>
    ///     One FActiveGameplayCue - an element of UAbilitySystemComponent::ActiveGameplayCues, and
    ///     what keeps a LOOPING gameplay cue alive on every client that can see this actor.
    ///
    ///     Three replicated members, each of them a struct with its own native NetSerialize, so each
    ///     is one atomic leaf rather than something RepLayout recurses into:
    ///
    ///         GameplayCueTag  14-bit net index          (FGameplayTag::NetSerialize)
    ///         PredictionKey   conditional, empty here   (FPredictionKey::NetSerialize)
    ///         Parameters      12 rep bits + 2 empty containers + the flagged members
    ///
    ///     bPredictivelyRemoved is UPROPERTY(NotReplicated) and never on the wire - it is the
    ///     client's own note that it already ran the Removed event predictively.
    /// </summary>
    public static void WriteActiveGameplayCue(FNetBitWriter payload, FActiveGameplayCue cue) {
        FGameplayTypes.WriteTag(payload, cue.GameplayCueTag);
        FPredictionKey.Write(payload, cue.PredictionKey);
        FGameplayTypes.WriteCueParameters(payload, (UPackageMapClient) payload.PackageMap!,
                                          cue.SourceObject, cue.Location);
    }

    /// <summary>
    ///     One FActiveGameplayCue, delta-struct format. Handles:
    ///
    ///         1  GameplayCueTag  FGameplayTag            (atomic - has a NetSerializer)
    ///         2  PredictionKey   FPredictionKey          (atomic)
    ///         3  Parameters      FGameplayCueParameters  (atomic)
    ///
    ///     THE STRUCT FORMAT IS NOT OPTIONAL FOR THIS ARRAY. The reference 10.40 client takes the
    ///     delta-struct path for ActiveGameplayCue 18,193 times in one session - more than for
    ///     GameplayAbilitySpec or ActiveGameplayEffect - so writing the plain format here would be
    ///     read as struct and corrupt the item. See the class doc on the latch.
    /// </summary>
    public static void WriteActiveGameplayCueDeltaStruct(FNetBitWriter payload, FActiveGameplayCue cue) {
        WriteHandle(payload, 1);
        FGameplayTypes.WriteTag(payload, cue.GameplayCueTag);

        WriteHandle(payload, 2);
        FPredictionKey.Write(payload, cue.PredictionKey);

        WriteHandle(payload, 3);
        FGameplayTypes.WriteCueParameters(payload, (UPackageMapClient) payload.PackageMap!,
                                          cue.SourceObject, cue.Location);
    }

    /// <summary>
    ///     One FFortItemEntry, delta-struct format. Handles:
    ///
    ///          1  Count                     int32
    ///          2  ItemDefinition            object
    ///          3  OrderIndex                int16 (16 raw bits, NOT 32)
    ///          4  Durability                float
    ///          5  Level                     int32
    ///          6  LoadedAmmo                int32
    ///        7-10 ItemGuid.A/B/C/D          int32 each - FGuid is not atomic, it flattens to four
    ///         11  inventory_overflow_date   bool
    ///         12  bWasGifted                bool
    ///         13  bIsReplicatedCopy         bool
    ///         14  bIsDirty                  bool
    ///         15  bUpdateStatsOnCollection  bool
    ///         16  StateValues               TArray, always empty here
    ///         17  ParentInventory           object (a TWeakObjectPtr is still one object ref)
    ///         18  GameplayAbilitySpecHandle.Handle  int32
    ///         19  AlterationInstances       TArray, always empty here
    ///         20  GenericAttributeValues    TArray, always empty here
    ///
    ///     The five booleans really are five handles rather than one packed byte: each is its own
    ///     leaf cmd, and each carries one bit of value behind its own handle.
    /// </summary>
    public static unsafe void WriteItemEntryDeltaStruct(FNetBitWriter payload, FFortItemEntry item) {
        var packageMap = (UPackageMapClient) payload.PackageMap!;

        WriteHandle(payload, 1);
        WriteInt32(payload, item.Count);

        WriteHandle(payload, 2);
        packageMap.SerializeObject(payload, item.ItemDefinition);

        WriteHandle(payload, 3);
        var orderIndex = item.OrderIndex;
        payload.SerializeBits(&orderIndex, 16);

        WriteHandle(payload, 4);
        var durability = item.Durability;
        payload.SerializeBits(&durability, 32);

        WriteHandle(payload, 5);
        WriteInt32(payload, item.Level);

        WriteHandle(payload, 6);
        WriteInt32(payload, item.LoadedAmmo);

        // Spelled out rather than looped so every top-level handle in this method is a literal -
        // Tools/RepHandles/verify_fastarray_delta_struct.py reads them back and checks the sequence
        // against the SDK, and a computed handle would be invisible to it.
        var guidParts = GuidToAbcd(item.ItemGuid);

        WriteHandle(payload, 7);
        WriteInt32(payload, guidParts[0]);

        WriteHandle(payload, 8);
        WriteInt32(payload, guidParts[1]);

        WriteHandle(payload, 9);
        WriteInt32(payload, guidParts[2]);

        WriteHandle(payload, 10);
        WriteInt32(payload, guidParts[3]);

        WriteHandle(payload, 11);
        payload.WriteBit(item.InventoryOverflowDate);

        WriteHandle(payload, 12);
        payload.WriteBit(item.bWasGifted);

        WriteHandle(payload, 13);
        payload.WriteBit(item.bIsReplicatedCopy);

        WriteHandle(payload, 14);
        payload.WriteBit(item.bIsDirty);

        WriteHandle(payload, 15);
        payload.WriteBit(item.bUpdateStatsOnCollection);

        BeginArray(payload, 16, 0); // StateValues
        EndArray(payload);

        WriteHandle(payload, 17);
        packageMap.SerializeObject(payload, item.ParentInventory);

        WriteHandle(payload, 18);
        WriteInt32(payload, item.GameplayAbilitySpecHandle);

        BeginArray(payload, 19, 0); // AlterationInstances
        EndArray(payload);

        BeginArray(payload, 20, 0); // GenericAttributeValues
        EndArray(payload);
    }

    private static unsafe void WriteInt32(FNetBitWriter payload, int value) {
        payload.SerializeBits(&value, 32);
    }
}
