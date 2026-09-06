namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     ENetDormancy (Actor.h) - reduced to the two values this server has a use for. See
///     AActor.NetDormancy.
/// </summary>
public enum ENetDormancy {
    /// <summary>DORM_Awake - replicate normally.</summary>
    Awake,

    /// <summary>
    ///     DORM_DormantAll - nothing will change again; close the channels and stop diffing. The
    ///     client keeps the actor.
    /// </summary>
    DormantAll
}
