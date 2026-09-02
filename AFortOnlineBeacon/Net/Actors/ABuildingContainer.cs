namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     A chest or ammo box - ABuildingContainer, which derives from ABuildingSMActor and so inherits
///     every handle this project already knows for a building piece, adding its own at 69-77.
///
///     THESE ARE MAP ACTORS, NOT SPAWNED ONES. A chest already exists on the client, placed in a
///     streaming sublevel this server never loads. So the same trick destructible scenery uses applies
///     (see NativeRpcHandlers.DamageLevelActor): the client names the actor by PATH, this server
///     builds a stably-named stand-in for that path with UAssetRegistry.GetOrCreateSubObject, registers
///     it for replication, and from then on it can push properties at an actor it never created.
///
///     Which is all that "opening" a chest is on the wire: set <see cref="bAlreadySearched"/> (handle
///     71, which has an OnRep_bAlreadySearched on the client and swaps in the opened mesh) and bump
///     <see cref="SearchAnimationCount"/> (handle 76) to play the animation. The loot is separate -
///     ordinary AFortPickups, spawned the same way a dropped item is.
/// </summary>
public class ABuildingContainer : ABuildingActor {
    /// <summary>
    ///     ABuildingContainer::bAlreadySearched - wire handle 71. Has an OnRep on the client, which is
    ///     what swaps the closed mesh for the opened one. This is the whole visual state of a looted
    ///     chest.
    /// </summary>
    public bool bAlreadySearched { get; private set; }

    /// <summary>
    ///     ABuildingContainer::SearchBounceData.SearchAnimationCount - wire handle 76. FSearchBounceData
    ///     has no native NetSerialize, so RepLayout recurses into it and its two members take handles
    ///     75 and 76 rather than the struct taking one. Bumping the count is what triggers the open
    ///     animation; the client watches for a CHANGE, not a value.
    /// </summary>
    public uint SearchAnimationCount { get; private set; }

    /// <summary>ABuildingContainer::ReplicatedLootTier - wire handle 70.</summary>
    public int ReplicatedLootTier { get; set; }

    /// <summary>
    ///     Opens it, once. Returns false if it was already open, which is the guard that stops a
    ///     client spamming the interact key from emptying a chest repeatedly - the same shape as
    ///     ABuildingActor.MarkDestroyed's "first time only" return.
    /// </summary>
    public bool Search() {
        if (bAlreadySearched) return false;

        bAlreadySearched = true;
        SearchAnimationCount++;
        return true;
    }
}
