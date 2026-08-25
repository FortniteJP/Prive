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
public class AFortInventory : AActor;
