using AFortOnlineBeacon.Runtime;
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
    ///     DISTANCE-CULLED, as of 2026-09-04. This used to be bAlwaysRelevant with the comment
    ///     "a pickup on the ground belongs to no one, and this project has no distance culling to
    ///     decide who is close enough to see it" - and that second half is no longer true
    ///     (AActor.IsNetRelevantFor now has the real tail), so the flag came off.
    ///
    ///     It matters more here than anywhere else in the project. The floor-loot generator finds
    ///     2895 spawners across the whole Athena map; every one of them was getting a channel on
    ///     every connection, in an untimed trickle bounded only by NEWLY_RELEVANT_PER_TICK. The map
    ///     is over 200000 units across and this cull radius is 15000, so the overwhelming majority
    ///     of them are now simply never opened for a given player - and the ones that are, are the
    ///     ones that player could actually walk up to.
    ///
    ///     That trickle is not a hypothetical cost. Rounds 144-147 proved that WHAT LANDS IN THE
    ///     POSSESSION WINDOW breaks jumping and collision (see UNetDriver.OpenChannelsForNewlyRelevantActors),
    ///     and this is by far the largest source of channel opens in the whole server.
    ///
    ///     The radius is the engine default (AActor::NetCullDistanceSquared = 225000000, i.e. 15000
    ///     units / 150 m), not a Fortnite-specific figure - AFortPickupAthena's real value lives in
    ///     its native CDO and has not been read out of the dump yet. 150 m is generous for a small
    ///     object on the ground, which is the right way to be wrong here.
    ///     PICKUP_CULL_DISTANCE overrides it in UNITS (not squared); NET_CULL=0 disables culling
    ///     everywhere and restores exactly the old behaviour.
    /// </summary>
    /// <summary>PICKUP_CULL_DISTANCE, from this world's options - see AActor.NetCullDistanceKnob.</summary>
    protected override string? NetCullDistanceKnob => "PICKUP_CULL_DISTANCE";

    public AFortPickup() {

        // DORMANT ONCE SENT - and pickups are the reason dormancy was worth building. The floor-loot
        // generator finds 2895 spawners; a pickup that has settled never changes again, and every
        // tick spent diffing one is spent for the rest of the match.
        //
        // Only safe now that a destroy can reach a client WITHOUT a channel: a pickup IS destroyed
        // when it is taken, and before UActorChannel.SendDestructionInfo existed that would have left
        // it standing on every client forever. See UNetDriver.NotifyActorDestroyed.
        //
        // NativeRpcHandlers flushes dormancy before setting bPickedUp, though in practice the
        // destroy on the very next line wins the race and the removal travels as a destruction info
        // instead - see the comment there. The flush is kept for the property change that is not
        // followed by a destroy.
        SetNetDormancy(ENetDormancy.DormantAll);
    }

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
    ///     PickupLocationData.LootFinalPosition and FinalTossRestLocation (handles 42 and 45) - where
    ///     the item ends up. For a pickup that is simply placed, this is also its actor location.
    /// </summary>
    public FVector RestLocation { get; set; } = new();

    /// <summary>
    ///     PickupLocationData.LootInitialPosition (handle 41) - where a toss STARTS.
    ///
    ///     THE THREE POSITIONS USED TO BE ONE. All of 41, 42 and 45 returned RestLocation, on the
    ///     honest grounds that with no toss to simulate they really were the same point, and the
    ///     comment there said it was "kept as one property so a real toss has somewhere to diverge
    ///     later". There is a real toss now (FortPickupToss's streamed mode), and the client reads
    ///     these: it calls `AFortPickup::SetupForMovementCompToss` off them and complained in its own
    ///     log the moment the data was inconsistent.
    ///
    ///     DEFAULTS TO RestLocation RATHER THAN TO ZERO, and that is the point of the backing field.
    ///     Three places spawn pickups - a player's drop, a container's loot and the floor-loot
    ///     generator - and only the first has any reason to think about a toss. Making this a plain
    ///     auto-property meant the other two would quietly report a toss that began at the WORLD
    ///     ORIGIN, which is precisely the shape of bug this session has spent all day on: a property
    ///     that is silent, plausible and wrong. Now a caller that never heard of tossing gets the
    ///     right answer for free.
    /// </summary>
    public FVector TossStartLocation {
        get => _tossStartLocation ?? RestLocation;
        set => _tossStartLocation = value;
    }

    private FVector? _tossStartLocation;

    /// <summary>AFortPickup::bPickedUp (0x0438, Net + RepNotify) - handle 49.</summary>
    public bool bPickedUp { get; set; }

    /// <summary>AFortPickup::bTossedFromContainer (0x043A, Net + RepNotify) - handle 50. False: a player dropped this.</summary>
    public bool bTossedFromContainer { get; set; }

    /// <summary>
    ///     AFortPickup::PickupLocationData.PickupTarget (handle 38) - the pawn this item is flying
    ///     TO, and the whole reason the client animates a pickup instead of the actor just
    ///     vanishing. Null on a pickup lying in the world.
    ///
    ///     The client sends the flight itself in ServerHandlePickup(InFlyTime, InStartDirection),
    ///     which this server already decoded and threw away: it destroyed the actor on the spot, so
    ///     there was never anything left to fly. Setting these four and holding the destroy for
    ///     FlyTime is the whole feature.
    /// </summary>
    public APawn? PickupTarget { get; set; }

    /// <summary>
    ///     AFortPickup::PickupLocationData.ItemOwner (handle 40) - who the item belongs to for the
    ///     duration of the flight. PR3.0 sets this alongside PickupTarget in both of its pickup
    ///     hooks, so it is part of the same set rather than something a toss uses on its own.
    /// </summary>
    public APawn? ItemOwner { get; set; }

    /// <summary>
    ///     AFortPickup::PickupLocationData.FlyTime (handle 43) - how long the arc takes.
    ///
    ///     The SERVER's number, not the client's: see FortPickupFlightSystem.FlightSeconds for why
    ///     the client's own InFlyTime is discarded, and where 0.40s comes from.
    /// </summary>
    public float FlyTime { get; set; }

    /// <summary>
    ///     AFortPickup::PickupLocationData.StartDirection (handle 44) - which way the item sets off.
    ///     An FVector_NetQuantizeNormal: SerializeFixedVector&lt;1, 16&gt;, i.e. each component as a
    ///     16-bit fixed-point value over [-1, 1], NOT one of the packed-vector encodings.
    /// </summary>
    public FVector StartDirection { get; set; } = new();

    /// <summary>AFortPickup::PickupLocationData.bPlayPickupSound (handle 47) - the client's own request, echoed back.</summary>
    public bool bPlayPickupSound { get; set; }

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
