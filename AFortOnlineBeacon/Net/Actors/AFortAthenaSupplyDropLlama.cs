using AFortOnlineBeacon.Core.Math;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     A supply llama - AAthenaSupplyDrop_Llama_C, five of which a match places at random points on
///     the map.
///
///     THE CLASS CHAIN IS NOT A BUILDING PIECE'S, and that is the one thing to get right here.
///     AAthenaSupplyDrop_Llama_C : AFortAthenaSupplyDrop : ABuildingGameplayActor : ABuildingActor -
///     it SHARES ABuildingActor's handles 16-37 with every wall and floor this project already
///     replicates, and then diverges: where a piece has ABuildingSMActor's TextureData at 38, a llama
///     has SpecialActorID (38), Looted (39) and FinalDestination (40). So it derives from
///     ABuildingActor here, and NativeRepLayouts gives it its own layout built from
///     BuildingActorProps' first 37 entries rather than the whole thing. Reusing BuildingActorProps
///     wholesale would send a llama a handle its ClassReps does not have, which is the
///     BunchIsError-then-silent-disconnect failure the building tool's handle 36 already cost this
///     project once.
///
///     WHAT MAKES IT WORK, all read off the Blueprint (`pakreader props` for the CDO,
///     Tools/BlueprintDump for the graph):
///
///       * `LootTableName = Loot_AthenaLlama` - a real tier group, now generated into
///         FortLootTables. It rolls 49 drops, which is why a llama is worth opening.
///       * `Looted` has an OnRep, and OnRep_Looted calls PlayLootedFX (the pinata burst and its
///         sound cue). So handle 39 IS the "it has been opened" visual, the same role
///         bAlreadySearched plays for a chest.
///       * `BlueprintCanInteract` reads Looted, so the client stops offering the prompt by itself
///         once the flag arrives - this server does not have to police repeat interactions on the
///         client side, only its own (see Search).
///       * `BlueprintOnInteract` is FUNC_BlueprintAuthorityOnly, i.e. server-side graph this project
///         cannot run. That is why the loot is dropped from NativeRpcHandlers rather than by the
///         actor.
///
///     DELIBERATELY NOT MODELLED: the FALL. The real llama spawns above its landing spot and drops,
///     driven by FinalDestination (handle 40, OnRep_FinalDestination -> OnLandingLocationChanged) and
///     a projectile movement component. This server spawns it already at rest on the ground, which
///     looks right from the moment a player sees it and skips a whole physics path that has nothing
///     else to build on. FinalDestination is left at its default so OnRep never fires.
/// </summary>
public class AFortAthenaSupplyDropLlama : ABuildingActor {

    /// <summary>
    ///     AAthenaSupplyDrop_Llama_C::Looted - wire handle 39, RepNotify. Set once, by Search.
    /// </summary>
    public bool Looted { get; private set; }

    /// <summary>
    ///     AAthenaSupplyDrop_Llama_C::FinalDestination - wire handle 40, RepNotify. Never sent; see
    ///     the class comment on why the fall is not modelled.
    /// </summary>
    public FVector FinalDestination { get; set; } = new();

    /// <summary>
    ///     Opens it, once. Same shape and same reason as ABuildingContainer.Search: a client holding
    ///     the interact key sends the RPC repeatedly, and without this guard one llama would pay out
    ///     its 49 items on every one of them.
    /// </summary>
    public bool Search() {
        if (Looted) return false;

        Looted = true;
        return true;
    }
}
