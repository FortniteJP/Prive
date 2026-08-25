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
}
