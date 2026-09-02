namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Simplified port of APlayerState/AFortPlayerState - just enough to replicate
///     bHasStartedPlaying, the other half (alongside AFortPlayerController::
///     bHasServerFinishedLoading) of the Fortnite-specific loading-screen-dismissal gate a real
///     working server (PriveDev/Project-Reboot-3.0) identifies in a disabled workaround comment.
///     Real UE spawns one of these automatically per-connection via AController::InitPlayerState -
///     see AGameModeBase.Login, which does the same here.
/// </summary>
public class APlayerState : AInfo {
    /// <summary>
    ///     AFortPlayerState::AbilitySystemComponent (0x08C0). The pawn borrows this one rather than
    ///     owning its own - AFortPlayerPawn carries a bInitAbilitySystemComponentFromPlayerState
    ///     bit - so the PlayerState is where a granted ability has to live to survive a respawn.
    ///
    ///     Replicated as a SUB-OBJECT on this actor's channel, not as a property: components have no
    ///     channel of their own. See UActorChannel.ReplicateAbilitySystemComponent.
    /// </summary>
    public UFortAbilitySystemComponent? AbilitySystemComponent { get; set; }

    /// <summary>
    ///     The one attribute set this server sends values for. Kept here rather than dug back out of
    ///     SpawnedAttributes so the replication pass does not have to search a list every tick.
    /// </summary>
    public UFortMovementSet? MovementSet { get; set; }

    /// <summary>
    ///     The same shortcut for the stamina set. Held for the same reason MovementSet is: the
    ///     replication pass would otherwise search SpawnedAttributes every tick.
    /// </summary>
    public UFortPlayerAttrSet? PlayerAttrSet { get; set; }

    /// <summary>
    ///     UFortHealthSet - the player's health and shield, and the thing every damage path in this
    ///     project ultimately writes to. Held here for the same reason the two sets above are, and
    ///     additionally because the PlayerState's own mirror floats (wire handles 216-219) read
    ///     straight off it: one source of truth, two ways onto the wire. See FortDamageSystem.
    /// </summary>
    public UFortHealthSet? HealthSet { get; set; }

    /// <summary>
    ///     AFortPlayerStateAthena::DeathInfo (handles 258-262) - who killed this player and how, as
    ///     the elimination feed and the death screen read it. All five members are held as plain
    ///     fields rather than a struct, because that is exactly how they travel: RepLayout recursed
    ///     FDeathInfo into one handle per member, so there is no struct on the wire to mirror.
    ///
    ///     DeathInfoInitialized is the one that matters - an uninitialised DeathInfo is the default
    ///     every living player has.
    /// </summary>
    public AActor? DeathInfoFinisherOrDowner { get; set; }

    /// <summary>Downed-but-not-out. Always false here - solo has no DBNO state to be in.</summary>
    public bool DeathInfoDBNO { get; set; }

    /// <summary>EDeathCause. See FortDamageSystem.EDeathCause for the values this project uses.</summary>
    public byte DeathInfoCause { get; set; } = (byte) EDeathCause.Unspecified;

    /// <summary>How far away the killer was, in centimetres as everything else in UE is.</summary>
    public float DeathInfoDistance { get; set; }

    /// <summary>The flag that makes the other four count.</summary>
    public bool DeathInfoInitialized { get; set; }

    /// <summary>
    ///     True once this player has been killed. Nothing on the wire - it is what keeps a dead
    ///     player from being damaged, healed or killed a second time, and what the game mode reads
    ///     when deciding whether anyone is still alive.
    /// </summary>
    public bool bIsDead { get; set; }

    /// <summary>
    ///     APlayerState::GetOwningController - `Cast&lt;AController&gt;(GetOwner())`. Real UE sets
    ///     that owner in AController::InitPlayerState; this project does the same in
    ///     AGameModeBase.Login, which is also what makes the PlayerState replicate to the right
    ///     connection.
    /// </summary>
    public AController? GetOwningController() => Owner as AController;

    /// <summary>The pawn this player state's controller is possessing, if any.</summary>
    public APawn? GetOwningPawn() => GetOwningController()?.Pawn;


    /// <summary>APlayerState::APlayerState (PlayerState.cpp:22) - every client needs every player's state (scoreboard, teams).</summary>
    public APlayerState() => bAlwaysRelevant = true;
    /// <summary>
    ///     AFortPlayerState::bHasFinishedLoading - wire handle 29, the bit immediately before
    ///     bHasStartedPlaying (both live in the bitfield byte at 0x0390: BitIndex 2 and 3, and
    ///     "bHasFinishedLoading" sorts first under FCompareUFieldOffsets' name tie-break). Unlike
    ///     bHasStartedPlaying it has no RepNotify - the client just reads the value.
    ///
    ///     Every working Fortnite server implementation sets this and bHasStartedPlaying together;
    ///     this project had the handle reserved but never sent it.
    /// </summary>
    public bool bHasFinishedLoading { get; set; }

    /// <summary>
    ///     AFortPlayerStateAthena::TeamIndex - wire handle 230, a plain uint8 (8 bits).
    ///
    ///     Athena reserves team numbers 0-2, so real players start at 3: the one working 10.40
    ///     capture we have logs "NotifyGameMemberAdded: Adding Player state with UniqueId: ...,
    ///     in team: 3, and in squad: 0" on the client just before its UI state becomes InGame_BR.
    ///     The default 0 is not a legal Athena team at all.
    /// </summary>
    /// <summary>
    ///     APlayerState::UniqueId - wire handle 25. The client sends its own id in NMT_Login and
    ///     this hands it straight back, which is how every id-keyed system on the client finds this
    ///     player: the working 10.40 capture logs "HandleZonePlayerStateInitialized: [MCP:5d56...]"
    ///     and "NotifyGameMemberAdded: Adding Player state with UniqueId: MCP:5d56..., in team: 3",
    ///     both of which look a PlayerState up BY ID. Until this landed the client's PlayerState had
    ///     no id at all, so no such lookup could ever match.
    /// </summary>
    public FUniqueNetIdRepl? UniqueId { get; set; }

    public byte TeamIndex { get; set; } = 3;

    /// <summary>AFortPlayerStateAthena::SquadId - wire handle 248, a plain uint8. 0 for solo.</summary>
    public byte SquadId { get; set; }

    /// <summary>
    ///     AFortPlayerStateAthena::bInAircraft - wire handle 252. Set while the player is aboard the
    ///     battle bus, cleared by ServerAttemptAircraftJump (or when the drop window closes). The
    ///     client gates the HUD, the pawn's visibility and the jump prompt on it.
    /// </summary>
    public bool bInAircraft { get; set; }

    public bool bHasStartedPlaying { get; set; }

    /// <summary>
    ///     APlayerState::PlayerNamePrivate - wire handle 26, an FString leaf.
    ///
    ///     Worth sending on its own evidence: the client's stall message ends
    ///     "waiting to finish restarting for " with an EMPTY trailing format argument, which is
    ///     player-name shaped - this project had never replicated a name at all, so the client's
    ///     PlayerState carried none.
    /// </summary>
    public string PlayerNamePrivate { get; set; } = "Player";

    /// <summary>
    ///     AFortPlayerState::HeroId - wire handle 39, an FString (StrProperty at offset 968).
    ///
    ///     This is the property the client was actually stuck on, and it named itself:
    ///     `LogFortCustomization: AFortPlayerState::InitializeHero failed. FortPC: …,
    ///     FortPC->PlayerState: …, HeroId: ` - with the HeroId printed empty. InitializeHero is what
    ///     sets the hero up client-side, and the quickbars come out of that, which is why
    ///     ClientRestart_Implementation kept bailing with "Quickbars are invalid" no matter how much
    ///     inventory or HeroType we sent.
    ///
    ///     The value is an McpProfile item instance id - 32 uppercase hex characters. A real
    ///     Project-Reboot-3.0 capture sends `B1F06E544F8B1DAA4893F48EF011260C` in the very same
    ///     packet (#250) that exports HeroType's asset. A fresh Guid in that format is used here
    ///     rather than replaying that literal id, since it identifies a profile item and nothing on
    ///     our side needs it to match anything.
    /// </summary>
    public string HeroId { get; set; } = Guid.NewGuid().ToString("N").ToUpperInvariant();

    /// <summary>
    ///     AFortPlayerState::HeroType (UFortHeroType*) - wire handle 40, a plain ObjectRef.
    ///
    ///     Left null for now. It points at a STATIC asset (Erbium uses
    ///     /Game/Athena/Heroes/HID_Commando_Athena_01.HID_Commando_Athena_01), so unlike every
    ///     object reference this project sends today it needs a NetGUID exported WITH its path
    ///     rather than a dynamically-spawned actor's guid. That machinery exists and is already
    ///     exercised for class default objects (UPackageMapClient.ExportNetGUID /
    ///     InternalWriteObject's bHasPath branch), but nothing yet models a plain asset object to
    ///     hand it. Athena's quickbars are built from the hero loadout, which makes this the
    ///     leading remaining candidate for the "Quickbars are invalid" stall.
    /// </summary>
    public UObject? HeroType { get; set; }

    /// <summary>
    ///     AFortPlayerState::CharacterData.WasPartReplicatedFlags - wire handle 49, a plain uint8
    ///     (8 bits, no enum). A bitmask over EFortCustomPartType, i.e. bit (1 &lt;&lt; PartType) per slot
    ///     this server actually replicated.
    /// </summary>
    public byte WasPartReplicatedFlags { get; set; }

    /// <summary>
    ///     AFortPlayerState::CharacterData.Parts[6] - wire handles 50-55, one ObjectRef each (a
    ///     C-array member gets one handle per element). Indexed by EFortCustomPartType:
    ///     Head=0, Body=1, Hat=2, Backpack=3, Charm=4, Face=5.
    ///
    ///     These are what the client's customization pass consumes. Wiring the pawn to its
    ///     PlayerState (see AController.Possess) is what first made the client actually look for
    ///     them, at which point it started warning
    ///     "Customization for PlayerPawn_Athena_C_… still hasn't completed after N secs" - it was
    ///     waiting on parts nothing ever sent.
    /// </summary>
    public UObject?[] CharacterParts { get; } = new UObject?[6];
}
