namespace AFortOnlineBeacon.Net.Replication;

/// <summary>
///     The two members every FFastArraySerializerItem carries (NetSerialization.h). Both are
///     UPROPERTY(NotReplicated), so neither appears in the item's serialized body - ReplicationID is
///     written separately by the delta header, and ReplicationKey never leaves the server at all.
///     It exists purely so the server can tell "this element changed" from "this element is the one
///     it was last time".
/// </summary>
public interface IFastArrayItem {
    /// <summary>INDEX_NONE until MarkItemDirty assigns one.</summary>
    int ReplicationId { get; set; }

    int ReplicationKey { get; set; }
}

/// <summary>
///     Server-side half of FFastArraySerializer (NetSerialization.h) - the list plus the two
///     counters that make delta replication possible.
///
///     Nothing here touches the wire; see FFastArraySerializerWriter.WriteDelta for that. The
///     division matters because the keys are the entire protocol: an element is "changed" when its
///     ReplicationKey differs from what the connection was last sent, and the array is "unchanged"
///     as a whole when ArrayReplicationKey matches - which is the cheap early-out real UE takes
///     before it looks at any element (NetSerialization.h:1263).
/// </summary>
public sealed class FFastArraySerializer<T> where T : class, IFastArrayItem {
    public List<T> Items { get; } = new();

    /// <summary>FFastArraySerializer::ArrayReplicationKey - starts at 0 (NetSerialization.h ctor).</summary>
    public int ArrayReplicationKey { get; private set; }

    /// <summary>FFastArraySerializer::IDCounter - starts at 0, so the first assigned id is 1.</summary>
    private int _idCounter;

    public int Count => Items.Count;

    /// <summary>
    ///     FFastArraySerializer::MarkItemDirty. Must be called after adding or changing an element -
    ///     an element whose ReplicationKey did not move is indistinguishable from one that never
    ///     changed, and will simply not be sent.
    /// </summary>
    public void MarkItemDirty(T item) {
        if (item.ReplicationId == UnrealConstants.IndexNone) {
            item.ReplicationId = ++_idCounter;
            if (_idCounter == UnrealConstants.IndexNone) _idCounter++;
        }

        item.ReplicationKey++;
        MarkArrayDirty();
    }

    /// <summary>FFastArraySerializer::MarkArrayDirty - call after REMOVING an element, which has no item to dirty.</summary>
    public void MarkArrayDirty() {
        ArrayReplicationKey++;
        if (ArrayReplicationKey == UnrealConstants.IndexNone) ArrayReplicationKey++;
    }

    public void Add(T item) {
        Items.Add(item);
        MarkItemDirty(item);
    }

    public bool Remove(T item) {
        if (!Items.Remove(item)) return false;

        MarkArrayDirty();
        return true;
    }
}

/// <summary>
///     FNetFastTArrayBaseState (NetSerialization.h) - what one connection was last sent for one fast
///     array. Real UE keeps this per connection inside FObjectReplicator; here it hangs off the
///     actor channel, which is the same thing since this project has one channel per actor per
///     connection.
///
///     BaseReplicationKey being accurate is not a nicety. The client's PostReceiveCleanup deletes
///     any element whose MostRecentArrayReplicationKey falls strictly between BaseReplicationKey and
///     ArrayReplicationKey - that is how it recovers elements lost to a nak. Send a stale
///     BaseReplicationKey (INDEX_NONE, say) alongside a moved ArrayReplicationKey and the client
///     will implicitly delete every element the update did not re-send.
/// </summary>
public sealed class FNetFastTArrayBaseState {
    /// <summary>INDEX_NONE means "nothing has been sent yet", which is what the first delta reports as its base.</summary>
    public int ArrayReplicationKey { get; set; } = UnrealConstants.IndexNone;

    /// <summary>FNetFastTArrayBaseState::IDToCLMap - element ReplicationID to the ReplicationKey last sent for it.</summary>
    public Dictionary<int, int> IdToKey { get; } = new();

    /// <summary>
    ///     What the delta just WRITTEN would make this state, held until the bunch carrying it is
    ///     actually away.
    ///
    ///     WRITING IS NOT SENDING, and treating them as the same thing is unrecoverable here. This
    ///     state used to be updated inside WriteDelta, so the moment a delta was serialised the
    ///     connection was recorded as having received it - even if the bunch was then dropped
    ///     because the reliable buffer had overflowed or the channel was closing. The next pass
    ///     compares the array key against this state, finds them equal, sends nothing, and the
    ///     connection is stuck at that version FOR THE REST OF THE MATCH: a fast array has no
    ///     redundancy and no resync point, so one lost delta is permanent.
    ///
    ///     That is not hypothetical - it is the shape of "the first player to join lost every
    ///     ability the moment a second player joined", because a second connection is exactly what
    ///     puts a burst of reliable channel-open bunches in front of the ASC's delta.
    ///
    ///     The actor's ordinary properties already worked this way: UActorChannel commits its
    ///     shadow state "only after the bunch is away". This makes the fast arrays agree.
    /// </summary>
    private (int Key, Dictionary<int, int> IdToKey)? _pending;

    /// <summary>Records what a just-written delta WOULD make this state. See <see cref="Commit" />.</summary>
    public void StagePending(int arrayReplicationKey, Dictionary<int, int> idToKey) =>
        _pending = (arrayReplicationKey, idToKey);

    /// <summary>The bunch is away: what was written is now what this connection has.</summary>
    public void Commit() {
        if (_pending is not { } pending) return;

        ArrayReplicationKey = pending.Key;
        IdToKey.Clear();
        foreach (var (id, key) in pending.IdToKey) IdToKey[id] = key;
        _pending = null;
    }

    /// <summary>The bunch never went out: forget it, so the next pass writes the same delta again.</summary>
    public void Discard() => _pending = null;
}
