namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     AFortBroadcastRemoteClientInfo - a per-player actor whose replicated fields broadcast that
///     player's current building/interaction state to OTHER clients (what building class they're
///     holding, whether they're editing, a chat bubble, ...). Referenced from
///     AFortPlayerControllerAthena::BroadcastRemoteClientInfo (a Net ObjectRef property - see
///     NativeRepLayouts.PlayerControllerProps) and spawned per-player in AGameModeBase.Login,
///     mirroring AFortInventory's own pattern (its own actor/channel, not a subobject - the same
///     "one actor per player" shape).
///
///     Found 2026-08-29 from a working Project-Reboot-3.0 client log: the instant a player equips a
///     building tool, it calls BroadcastRemoteClientInfo-&gt;ServerSetPlayerBuildableClass(BuildableClass)
///     - right after the ghost preview mesh updates (itself purely client-local, confirmed by the
///     same log) and right before the "Fort_Build_BluePrint_Select_Cue" sound cue. AFortOnlineBeacon
///     never spawned this actor nor set the PlayerController's reference to it, so on this server
///     that call always finds a null reference and is silently skipped client-side - one more
///     candidate in the "client silently refuses to progress" pattern this project keeps finding.
///
///     Only the fields this project actually uses are modeled (bActive, RemoteBuildableClass - the
///     ServerSetPlayerBuildableClass target). Everything else (chat entry, weakspot data, respawn
///     timer, poi tag, event score, map markers, edit-tile data) is Reserved-only - see
///     NativeRepLayouts.BroadcastRemoteClientInfoProps.
/// </summary>
public class AFortBroadcastRemoteClientInfo : AActor {
    public bool bActive { get; set; }

    /// <summary>
    ///     AFortBroadcastRemoteClientInfo::RemoteBuildableClass - handle 20 (confirmed by
    ///     `python Tools/RepHandles/rep_handles.py AFortBroadcastRemoteClientInfo`). Set by
    ///     ServerSetPlayerBuildableClass (see NativeRpcHandlers) to whatever this server resolved
    ///     the client's class reference to - may be null if the class was never exported to us.
    /// </summary>
    public UClass? RemoteBuildableClass { get; set; }
}
