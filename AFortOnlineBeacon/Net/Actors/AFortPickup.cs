namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     A dropped item lying in the world - what a real server spawns when a player drops something,
///     and what the client walks over to pick it back up.
///
///     Modelled on /Script/FortniteGame.FortPickupAthena (AFortPickupAthena : AFortPickup : AActor).
///     AFortPickupAthena adds no replicated properties of its own, so the whole layout comes from
///     AFortPickup - see NativeRepLayouts.PickupProps for the 56 handles.
///
///     This is the FIRST actor in this project spawned during a match rather than during login, so
///     it is also what UNetDriver.ServerReplicateActors' dynamic channel opening exists for: no
///     login-time code opens a channel for it, and none can - it does not exist yet when the player
///     joins.
///
///     The item itself rides in PrimaryPickupItemEntry, an FFortItemEntry that RepLayout flattens
///     into one handle per member (17-36) rather than sending as a struct body - unlike the same
///     struct inside AFortInventory::Inventory, where the FastArray writes it with no handles at
///     all. Same struct, two different framings, decided by whether the parent is a custom delta.
/// </summary>
public class AFortPickup : AActor {
    /// <summary>
    ///     Always relevant: a pickup on the ground belongs to no one, and this project has no
    ///     distance culling to decide who is close enough to see it.
    /// </summary>
    public AFortPickup() => bAlwaysRelevant = true;

    /// <summary>AFortPickup::PrimaryPickupItemEntry (0x0270, Net + RepNotify) - handles 17-36.</summary>
    public FFortItemEntry? PrimaryPickupItemEntry { get; set; }

    /// <summary>
    ///     AFortPickup::PickupLocationData.TossState (handle 46), an EFortPickupTossState.
    ///
    ///     This is what was missing when the client rendered a dropped weapon but offered no way to
    ///     pick it up: nothing was sent for it, so the client read NotTossed = 0 and had an item
    ///     that had never finished being thrown. AtRest is the state a pickup lying on the ground is
    ///     supposed to be in.
    /// </summary>
    public EFortPickupTossState TossState { get; set; } = EFortPickupTossState.AtRest;

    /// <summary>
    ///     PickupLocationData's three FVector_NetQuantize10 positions (handles 41, 42, 45). With no
    ///     toss to simulate they are all just where the item came to rest, which is also the actor's
    ///     own location - kept as one property so a real toss has somewhere to diverge later.
    /// </summary>
    public FVector RestLocation { get; set; } = new();

    /// <summary>AFortPickup::bPickedUp (0x0438, Net + RepNotify) - handle 49.</summary>
    public bool bPickedUp { get; set; }

    /// <summary>AFortPickup::bTossedFromContainer (0x043A, Net + RepNotify) - handle 50. False: a player dropped this.</summary>
    public bool bTossedFromContainer { get; set; }

    /// <summary>AFortPickup::bServerStoppedSimulation (0x043D, Net + RepNotify) - handle 53. True means "it has come to rest where I told you", which is all this server can honestly claim: there is no projectile movement simulation here.</summary>
    public bool bServerStoppedSimulation { get; set; } = true;
}

/// <summary>Enum FortniteGame.EFortPickupTossState, 10.40. _MAX = 3, so the wire width is CeilLogTwo(3) = 2 bits.</summary>
public enum EFortPickupTossState : byte {
    NotTossed = 0,
    InProgress = 1,
    AtRest = 2,
    EFortPickupTossState_MAX = 3
}
