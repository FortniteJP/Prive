namespace AFortOnlineBeacon.Net.Actors;

public class AController : AActor {

    /// <summary>AController::AController (Controller.cpp:42) - a controller belongs to one player and reaches nobody else.</summary>
    public AController() => bOnlyRelevantToOwner = true;
    public APawn? Pawn { get; private set; }
    public APlayerState? PlayerState { get; set; }

    /// <summary>
    ///     Port of AController::Possess plus the part of APawn::PossessedBy that matters on the wire.
    ///     Real UE's PossessedBy does three things this used to skip entirely:
    ///
    ///         SetOwner(NewController);
    ///         Controller = NewController;
    ///         if (Controller->PlayerState != NULL) { PlayerState = Controller->PlayerState; }
    ///
    ///     All three are replicated - Owner is handle 13 on every actor, PlayerState 17 and
    ///     Controller 18 on a Pawn - and none of them were being sent, so a client had no way to see
    ///     the pawn as owned by, or belonging to, its own controller.
    /// </summary>
    public virtual void Possess(APawn inPawn) {
        Pawn = inPawn;
        inPawn.SetOwner(this);
        inPawn.SetController(this);
        if (PlayerState != null) inPawn.PlayerState = PlayerState;

        GrantDefaultAbilities();
    }

    /// <summary>
    ///     The abilities every player has just by existing, granted once on possession.
    ///
    ///     Jump is the reason this exists. In Fortnite it is NOT the plain ACharacter jump: the
    ///     pawn caches a granted ability's spec handle and drives jumping through it. Read
    ///     straight out of the decrypted client - AFortPlayerPawn's constructor initialises the
    ///     handle at +0x13D4 to -1, and CancelJumpAbility (0x14195B2D0) early-outs on exactly
    ///     that -1 before looking the spec up in the ability system component at +0x648. With no
    ///     grant the handle stays -1 forever, so pressing space does nothing at all - no error,
    ///     no log line, and not even a jump flag in the move sent to the server, which is what
    ///     made this look for a long time like an input problem.
    ///
    ///     Same shape as firing, which also did nothing until its ability was granted. The list
    ///     is raider3.5's (Logic/Abilities.h ApplyAbilities), which a working injected server
    ///     hands out on spawn.
    ///
    ///     Only the NATIVE ones are here. Their CDOs resolve by path with no asset to stream -
    ///     the same trick the reload ability already uses. raider3.5 also grants Blueprint
    ///     abilities (GA_DefaultPlayer_Death, _InteractUse, _InteractSearch, GAB_Emote_Generic,
    ///     GA_TrapBuildGeneric, GA_DanceGrenade_Stun); those need real /Game/ paths and are not
    ///     granted yet.
    /// </summary>
    private static readonly string[] DefaultAbilities = {
        "/Script/FortniteGame.Default__FortGameplayAbility_Jump",
        "/Script/FortniteGame.Default__FortGameplayAbility_Sprint",
        "/Script/FortniteGame.Default__FortGameplayAbility_RangedWeapon"
    };

    private void GrantDefaultAbilities() {
        if (PlayerState?.AbilitySystemComponent is not { } abilitySystem) return;

        foreach (var path in DefaultAbilities) {
            var spec = abilitySystem.GrantAbility(UAssetRegistry.GetOrCreate(path));
            Console.WriteLine($"AController.Possess: granted {path[(path.LastIndexOf('.') + 1)..]} " +
                              $"as spec handle {spec.Handle}");
        }
    }
}