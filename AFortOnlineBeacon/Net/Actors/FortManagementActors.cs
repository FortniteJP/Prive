namespace AFortOnlineBeacon.Net.Actors;

// -------------------------------------------------------------------------------------------------
// The six "management" actors a real 10.40 server spawns and this one never did.
//
// They are grouped in one file because they are one FINDING rather than six: the PR3.0 capture
// (PriveDev/PacketProxy/decoded_new.txt) shows all six opened within twenty packets of each other,
// during the same burst of channel opens that carries the GameState, and four of the six send
// NOTHING but Role and RemoteRole:
//
//   #302 ChIndex=3  Default__FortPropertyOverrideReplShared     53 bits  (RemoteRole, Owner, Role)
//   #318 ChIndex=7  Default__FortPoiManager                    121 bits  (+ its POI grid)
//   #320 ChIndex=11 Default__FortSpecialActorReplicationInfo    29 bits  (RemoteRole, Role)
//   #321 ChIndex=17 Default__FortVolumeManager_BP_C             53 bits  (RemoteRole, Owner, Role)
//   #321 ChIndex=18 Default__FortClientAnnouncementManager      29 bits  (RemoteRole, Role)
//   #328 ChIndex=36 Default__FortTeamPrivateInfo              1271 bits  (+ its per-team roster)
//
// That is the whole point of them: on the wire they are nearly empty, and what they are FOR is
// being pointed at. Each one fills in a replicated reference the client dereferences before doing
// something - AFortGameStateAthena::PoiManager (32), AnnouncementManager (38), SpecialActorData
// (105), ReplOverrideData (106), VolumeManager (185), and AFortPlayerState::PlayerTeamPrivate (69).
// A null there is the project's recurring failure mode: the client's code path simply returns and
// logs nothing (the same shape as the null BroadcastRemoteClientInfo that silently ate every
// ServerSetPlayerBuildableClass - see AFortBroadcastRemoteClientInfo).
//
// All six handles were derived with `python Tools/RepHandles/rep_handles.py <Class>`, which
// reproduces every live-probed handle this project has - see [[rep_handle_derivation]].
//
// WHAT IS DELIBERATELY NOT MODELLED. Their own properties are left Reserved. For four of the six
// that is exactly what the real server does. The other two carry real content this server has no
// source for yet:
//
//   * AFortPoiManager sends FortPoiGridInfo (handles 16-25: a world-space grid) plus
//     PoiTagContainerTable (26), one FGameplayTagContainer per grid cell - that table is how the
//     HUD names the location you are standing in. Building it needs the map's POI volumes, which
//     Tools/MapActorDump could supply but nothing reads yet.
//   * AFortTeamPrivateInfo sends RepData (16), a FastArraySerializer roster of the squad. With
//     solo play that roster is one entry, and the squad list is already fed by the GameState's
//     GameMemberInfoArray, so it buys nothing today.
//
// Both are marked TODO rather than guessed at: an empty container is a valid state the client
// handles, a wrong grid is not.
// -------------------------------------------------------------------------------------------------

/// <summary>
///     Base for the management actors - AInfo (no transform of its own; the capture confirms it,
///     every one of the six spawns with bSerializeLocation=False) that is relevant to everyone.
///
///     bAlwaysRelevant is not an AInfo default, and it has to be set: these are referenced by the
///     GameState, which every client has, so every client needs the actor the reference points at.
/// </summary>
public abstract class AFortManagementActor : AInfo {
    protected AFortManagementActor() => bAlwaysRelevant = true;
}

/// <summary>
///     AFortPoiManager - AFortGameState::PoiManager, wire handle 32.
///
///     Owns the world's point-of-interest grid: a coarse 2D grid over the map, plus one
///     FGameplayTagContainer per cell naming the POI that cell belongs to. Client code that wants
///     "which named location is this actor in" goes through here.
///
///     TODO: FortPoiGridInfo (16-25) and PoiTagContainerTable (26). The real server sends them - the
///     capture's 121-bit block is those, where the four empty managers below use 29 - so this actor
///     existing is necessary but not sufficient for POI names to appear.
/// </summary>
public class AFortPoiManager : AFortManagementActor {}

/// <summary>
///     AFortVolumeManager - AFortGameStateAthena::VolumeManager, wire handle 185.
///
///     The only one of the six that is a BLUEPRINT rather than a native class: the capture exports
///     `/Game/Athena/BuildingActors/FortVolumeManager_BP.Default__FortVolumeManager_BP_C`. It
///     therefore relies on the same MustBeMappedGuids machinery the TimeOfDayManager and the battle
///     bus do, so the client can finish streaming the class before the bunch is applied - see
///     GUClassArray's TODM_BR note for what happens without it.
///
///     Tracks which players are inside which gameplay volumes (VolumeActivePlayers, handle 16 - a
///     FastArraySerializer). Left empty: this server has no volume queries at all, so there is
///     nothing truthful to put in it.
/// </summary>
public class AFortVolumeManager : AFortManagementActor {}

/// <summary>
///     AFortClientAnnouncementManager - AFortGameState::AnnouncementManager, wire handle 38.
///
///     The queue behind the on-screen announcements ("Storm is closing in", eliminations). It holds
///     an array of AFortClientAnnouncement actors (handle 16); the manager is what the client asks
///     for that list, so with it null there is nowhere for an announcement to be posted.
///
///     Sent empty, which is exactly what the real server's 29-bit block is - the array only fills
///     when something is announced.
/// </summary>
public class AFortClientAnnouncementManager : AFortManagementActor {}

/// <summary>
///     AFortSpecialActorReplicationInfo - AFortGameStateAthena::SpecialActorData, wire handle 105.
///
///     A side-channel list of "special" actors (handle 16, SpecialActorRepList) that need to reach
///     clients without a channel each. Empty here, and empty on the real server too at this point
///     in the match - its block is 29 bits, i.e. Role and RemoteRole and nothing else.
/// </summary>
public class AFortSpecialActorReplicationInfo : AFortManagementActor {}

/// <summary>
///     AFortPropertyOverrideReplShared - AFortGameStateAthena::ReplOverrideData, wire handle 106.
///
///     Carries the playlist's property overrides (handle 17, PropertyOverridesRepl) to clients - the
///     replicated half of what CurrentPlaylistInfo.PropertyOverrides is on the GameState. This
///     server sends no overrides, so the array stays empty; the actor exists so the reference
///     resolves.
///
///     First of the six to open in the capture (ChIndex=3), before even the GameState's own burst.
/// </summary>
public class AFortPropertyOverrideReplShared : AFortManagementActor {}

/// <summary>
///     AFortTeamPrivateInfo - AFortPlayerState::PlayerTeamPrivate, wire handle 69.
///
///     Per-TEAM rather than per-player: everyone on a team points at the same actor, and it carries
///     the things only that team may see (teammate health, positions, damage numbers). The real
///     server's block is the largest of the six at 1271 bits, all of it RepData (handle 16).
///
///     One instance per team index, handed out by AGameModeBase.GetOrCreateTeamPrivateInfo. In solo
///     that means one per player, which is correct rather than wasteful - a solo player IS their own
///     team, and the client resolving the reference is the point.
///
///     TODO: RepData. Needs the FastArraySerializer plumbing FFortItemList already has, and a real
///     source of teammate state, which only matters once squads do.
/// </summary>
public class AFortTeamPrivateInfo : AFortManagementActor {

    /// <summary>Which team this one belongs to - not replicated, it is how the server indexes them.</summary>
    public byte TeamIndex { get; set; }

    /// <summary>
    ///     The one place in this project where relevancy is a real decision rather than "everyone".
    ///
    ///     "Private" is the actor's entire purpose: it exists to carry what only teammates may see,
    ///     so sending it to the other team would be the exact leak it was designed to prevent. That
    ///     is invisible today - the actor is empty and solo play puts everyone on a team of one -
    ///     which is precisely why it is written now: once RepData is filled in, the wrong answer
    ///     here would be a wallhack rather than a bug.
    ///
    ///     Note this OVERRIDES the base's bAlwaysRelevant rather than clearing it. Leaving the flag
    ///     set matters for the other half of the question: a teammate should get this actor whether
    ///     or not they can see it, so the answer is "always, for my team", not "when nearby".
    /// </summary>
    public override bool IsNetRelevantFor(AActor? realViewer, FVector? srcLocation = null) =>
        realViewer switch {
            APlayerController { PlayerState: { } viewerState } => viewerState.TeamIndex == TeamIndex,
            // No viewer, or one with no PlayerState yet - fall back to the base answer rather than
            // guessing. An extra empty actor is recoverable; a missing one during login is not.
            _ => base.IsNetRelevantFor(realViewer, srcLocation)
        };
}
