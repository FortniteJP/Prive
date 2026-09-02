namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Simplified port of AGameStateBase - just enough to replicate bReplicatedHasBegunPlay, the
///     property real UE's OnRep_ReplicatedHasBegunPlay (GameStateBase.cpp) uses to call
///     UWorld::NotifyBeginPlay/NotifyMatchStarted client-side, which is what a client's loading
///     screen is waiting on before it dismisses. Real UE only ever flips this true once
///     AGameModeBase::StartMatch actually runs; this project doesn't have a warmup/countdown flow
///     yet, so it's set true as soon as this actor is spawned - see AGameModeBase.InitGameState.
/// </summary>
public class AGameState : AInfo {

    /// <summary>AGameStateBase::AGameStateBase (GameStateBase.cpp:23).</summary>
    public AGameState() => bAlwaysRelevant = true;
    /// <summary>
    ///     AFortGameStateBase::FortTimeOfDayManager - wire handle 22, an ObjectRef. Athena's
    ///     loading screen refuses to drop while this is null ("Waiting for time of day manager"),
    ///     and the client cannot fill it in itself - see AFortTimeOfDayManager.
    /// </summary>
    public AFortTimeOfDayManager? FortTimeOfDayManager { get; set; }

    public bool bReplicatedHasBegunPlay { get; set; }

    /// <summary>
    ///     AGameStateBase::MatchState (GameStateBase.cpp) - the standard UE match-state machine
    ///     (EnteringMap -> WaitingToStart -> InProgress -> ...) that OnRep_MatchState uses to drive
    ///     HandleMatchIsWaitingToStart/HandleMatchHasStarted client-side. A real PR3.0 client capture
    ///     (2026-08-24) showed this transitioning to InProgress a few seconds BEFORE the loading
    ///     screen actually dismissed and the client sent ServerLoadingScreenDropped - bHasBegunPlay
    ///     alone was not enough. Defaults to the real engine's own default value.
    /// </summary>
    public FName MatchState { get; set; } = new("EnteringMap");

    // ------------------------------------------------------------------------------------------
    // AFortGameStateAthena. Handle numbers below are derived by Tools/RepHandles/rep_handles.py
    // from the Dumper-7 10.40 SDK, reproducing UClass::SetUpRuntimeReplicationData +
    // FRepLayout::InitFromProperty_r exactly. That derivation independently reproduces every
    // live-probed handle this project already had (AActor 1-15, AGameStateBase 16-19, MatchState
    // 20, FortTimeOfDayManager 22, and all of APlayerState 16-50), which is what makes the
    // Athena-range numbers below trustworthy without a probe of their own.
    //
    // The values themselves mirror what raider3.5 (Logic/Game.h, OnReadyToStartMatch) sets on a
    // real injected 10.40 server - that is a known-good client-facing configuration for a match
    // that skips the battle bus entirely.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    ///     AGameStateBase::ReplicatedWorldTimeSeconds - wire handle 19, live-probed. The server's
    ///     clock, and the only thing that lets the client convert its own time into the server's:
    ///     AGameStateBase::OnRep_ReplicatedWorldTimeSeconds computes
    ///     ServerWorldTimeSecondsDelta = ReplicatedWorldTimeSeconds - World->GetTimeSeconds(), and
    ///     GetServerWorldTimeSeconds() is what every deadline the server sends - WarmupCountdownEndTime,
    ///     AircraftStartTime - is meant to be compared against. Until this replicated, the client was
    ///     measuring those against a clock that started when IT loaded the map.
    ///
    ///     This is also the first property in the project to be replicated CONTINUOUSLY rather than
    ///     once at join, so it is what proves UNetDriver.ServerReplicateActors actually works.
    /// </summary>
    public float ReplicatedWorldTimeSeconds { get; set; }

    /// <summary>
    ///     AGameStateBase::UpdateServerTimeSeconds (GameStateBase.cpp:147). Real UE runs this on a
    ///     repeating timer at ServerWorldTimeSecondsUpdateFrequency (5 seconds, GameStateBase.cpp:33)
    ///     rather than every frame - the client interpolates in between using its own clock, so the
    ///     replicated value only has to correct the drift.
    /// </summary>
    public void UpdateServerTimeSeconds() {
        var world = GetWorld();
        if (world != null) ReplicatedWorldTimeSeconds = world.TimeSeconds;
    }

    /// <summary>AGameStateBase::ServerWorldTimeSecondsUpdateFrequency - GameStateBase.cpp:33.</summary>
    public const float ServerWorldTimeSecondsUpdateFrequency = 5.0f;

    /// <summary>AFortGameStateAthena::WarmupCountdownStartTime - wire handle 109.</summary>
    public float WarmupCountdownStartTime { get; set; }

    /// <summary>AFortGameStateAthena::WarmupCountdownEndTime - wire handle 110. Pushed far into the
    /// future so the client never counts down out of warmup on its own.</summary>
    public float WarmupCountdownEndTime { get; set; } = 99999.9f;

    /// <summary>AFortGameStateAthena::AircraftStartTime - wire handle 111. Likewise far future;
    /// with <see cref="bGameModeWillSkipAircraft"/> set the client should never wait on it.</summary>
    public float AircraftStartTime { get; set; } = 9999.9f;

    /// <summary>AFortGameStateAthena::TotalPlayers - wire handle 115.</summary>
    public int TotalPlayers { get; set; } = 1;

    /// <summary>AFortGameStateAthena::PlayersLeft - wire handle 116.</summary>
    public int PlayersLeft { get; set; } = 1;

    /// <summary>
    ///     AFortGameState::TeamCount - wire handle 29. Solo gives every player their own team, so
    ///     this tracks the highest team index handed out (see AGameModeBase's team assignment) rather
    ///     than being a fixed playlist constant.
    /// </summary>
    public int TeamCount { get; set; } = 1;

    /// <summary>
    ///     AFortGameStateAthena::Aircrafts - wire handle 159. How the client FINDS the battle bus:
    ///     AFortAthenaAircraft.AircraftIndex indexes into this. Empty unless the aircraft phase is
    ///     switched on (AIRCRAFT_ENABLED, see AGameModeBase).
    /// </summary>
    public List<UObject> Aircrafts { get; } = new();

    /// <summary>
    ///     AFortGameStateAthena::bAircraftIsLocked - wire handle 160. While set, the client will not
    ///     even send ServerAttemptAircraftJump, so this - not any server-side check - is what
    ///     actually keeps players aboard before the drop window opens.
    /// </summary>
    public bool bAircraftIsLocked { get; set; }

    /// <summary>
    ///     AFortGameStateAthena::SafeZoneIndicator - wire handle 149, and how the client FINDS the
    ///     storm circle (it has an OnRep, and the map/minimap hang off it). Null until
    ///     FortSafeZoneSystem spawns one.
    /// </summary>
    public AFortSafeZoneIndicator? SafeZoneIndicator { get; set; }

    /// <summary>AFortGameStateAthena::SafeZonesStartTime - wire handle 112. When the FIRST circle starts closing.</summary>
    public float SafeZonesStartTime { get; set; }

    /// <summary>AFortGameStateAthena::SafeZonePhase - wire handle 157. Which circle the match is on; the HUD prints it.</summary>
    public byte SafeZonePhase { get; set; }

    /// <summary>AFortGameStateAthena::CurrentPlaylistId - wire handle 148. 2 is Playlist_DefaultSolo.</summary>
    public int CurrentPlaylistId { get; set; } = 2;

    /// <summary>
    ///     AFortGameStateAthena::GamePhase - wire handle 152. A UEnumProperty over EAthenaGamePhase,
    ///     so UEnumProperty::NetSerializeItem writes CeilLogTwo64(GetMaxEnumValue()) bits; the 10.40
    ///     enum ends at EAthenaGamePhase_MAX = 7, i.e. 3 bits.
    /// </summary>
    public EAthenaGamePhase GamePhase { get; set; } = EAthenaGamePhase.Warmup;

    /// <summary>
    ///     AFortGameStateAthena::bGameModeWillSkipAircraft - wire handle 156. Tells the client this
    ///     match has no battle bus at all, so it must not wait to be put into one.
    /// </summary>
    public bool bGameModeWillSkipAircraft { get; set; } = true;

    /// <summary>
    ///     AFortGameStateAthena::CurrentPlaylistInfo.BasePlaylist - a UFortPlaylistAthena asset.
    ///
    ///     CurrentPlaylistInfo (FPlaylistPropertyArray) derives from FFastArraySerializer, so it is
    ///     a Custom Delta property: it consumes RepLayout handles 154/155 but is NEVER sent through
    ///     the handle stream. It goes out as its own ClassNetCache-indexed field, exactly like an
    ///     RPC - see UActorChannel.WriteCustomDeltaProperties.
    ///
    ///     This is the property the whole in-match UI hangs off. In a working 10.40 capture the
    ///     client goes OnRep_CurrentPlaylistInfo -> LoadCurrentPlaylistData ->
    ///     OnPlaylistDataLoadCompleted -> UI state InGame_BR, all within one second; until then its
    ///     UI state is Invalid, which is one of the Athena loading screen's own listed reasons for
    ///     refusing to drop.
    /// </summary>
    /// <summary>
    ///     AFortGameState::WorldManager - wire handle 39.
    ///
    ///     THIS IS THE LOADING-SCREEN GATE. Disassembling the decrypted
    ///     UFortUIManagerWidget_NUI::NativeTick out of a live client memory dump
    ///     (Tools/BinXref/dumpxref.py) shows its in-game branch reading, before it will pick any
    ///     InGame_* state at all:
    ///
    ///         WorldManager = World->GameState->WorldManager;      // GameState+0x390
    ///         if (WorldManager == nullptr)                 -> leave the UI state alone
    ///         if (WorldManager->WorldManagerState != 5)    -> leave the UI state alone
    ///
    ///     (WorldManager+0x2C8 is EFortWorldManagerState, and 5 is WMS_Running.) With the state left
    ///     at Invalid the Athena loading screen refuses to drop - "The UI is not ready yet
    ///     (UIManagerWidget->GetCurrentUIState() == Invalid)".
    ///
    ///     WorldManagerState is NOT replicated, so the client's own actor has to reach WMS_Running by
    ///     itself; all the server owes it is this pointer. Athena_Terrain ships the actor already
    ///     placed - FortWorldManager "DO_NOT_DELETE_FortWorldManager" in the persistent level - so
    ///     this is a path reference to the client's own instance, not something we spawn.
    /// </summary>
    public UObject? WorldManager { get; set; }

    public UObject? BasePlaylist { get; set; }

    /// <summary>
    ///     AFortGameStateAthena::GameMemberInfoArray - a Custom Delta (FastArraySerializer) roster of
    ///     every player's team and squad, keyed by unique id. Receiving it is what makes the client
    ///     log "NotifyGameMemberAdded: Adding Player state with UniqueId: ..., in team: N, and in
    ///     squad: N" - one of the last client-side events the working 10.40 capture has and this
    ///     server did not.
    /// </summary>
    public FFastArraySerializer<FGameMemberInfo> GameMemberInfoArray { get; } = new();
}

/// <summary>
///     Enum FortniteGame.EAthenaGamePhase, 10.40. Values (including Count/_MAX) are verbatim from
///     the SDK dump because <see cref="AGameState.GamePhase"/>'s wire width is CeilLogTwo of the
///     highest one.
/// </summary>
public enum EAthenaGamePhase : byte {
    None = 0,
    Setup = 1,
    Warmup = 2,
    Aircraft = 3,
    SafeZones = 4,
    EndGame = 5,
    Count = 6,
    EAthenaGamePhase_MAX = 7
}
