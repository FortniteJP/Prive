namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Minimal placeholder for AFortPlayerController::WorldInventory's real target class. A live
///     client capture (2026-08-24) showed AFortPlayerController::ClientRestart_Implementation
///     refuses to finish possessing a pawn while this property resolves to null
///     ("ClientRestart_Implementation failed because WorldInventory is invalid") - the client keeps
///     retrying via ServerCheckClientPossessionReliable instead of ever sending
///     ServerAcknowledgePossession.
///
///     This was first (wrongly) modeled as a plain UObject default subobject, sent via a
///     sub-object content block - the real client rejected that ("Sub-object cannot be actor
///     class"), which is itself the proof that /Script/FortniteGame.FortInventory is really an
///     ACTOR class, not a UObject subobject. So WorldInventory needs its own actor channel exactly
///     like AGameState/APlayerState do (see AGameModeBase.Login), and gets referenced from
///     APlayerController the same simple way AController.PlayerState already does - a plain
///     ERepPropertyKind.ObjectRef Cmd (see NativeRepLayouts.PlayerControllerProps), no export-path
///     machinery needed since a dynamically-spawned actor is never name-stable.
/// </summary>
public class AFortInventory : AActor {
    /// <summary>
    ///     EFortInventoryType - handle 16, the first property after AActor's 15. Always World for
    ///     the actor behind AFortPlayerController::WorldInventory (Outpost is what OutpostInventory,
    ///     handle 35 on the PlayerController, would use - not spawned by this project).
    /// </summary>
    public EFortInventoryType InventoryType { get; set; } = EFortInventoryType.World;

    /// <summary>
    ///     AFortInventory::Inventory (FFortItemList) - handle 17, but it never travels through
    ///     FRepLayout: FFortItemList derives from FFastArraySerializer, making it a Custom Delta
    ///     property sent as its own RepIndex-addressed field (see
    ///     UActorChannel.WriteCustomDeltaProperties / FFastArraySerializerWriter).
    ///
    ///     A real 10.40 server's opening inventory, recovered from a Project-Reboot-3.0 packet
    ///     capture (packet #253), is the harvesting pickaxe plus the four building pieces and the
    ///     edit tool - precisely what fills an Athena quickbar, which is why this is what the
    ///     client's "Quickbars are invalid" stall is waiting on.
    /// </summary>
    public List<FFortItemEntry> Inventory { get; } = new();
}
