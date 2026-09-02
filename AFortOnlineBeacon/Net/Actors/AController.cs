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
    ///     AController::UnPossess, reduced the same way Possess is.
    ///
    ///     Needed because the battle bus DESTROYS the warmup pawn (see
    ///     AGameModeBase.TickBoarding): a controller left pointing at a destroyed pawn would keep
    ///     replicating Pawn/Controller references to an actor whose channel has closed.
    /// </summary>
    public virtual void UnPossess() {
        if (Pawn is not { } outgoing) return;

        outgoing.SetController(null);
        outgoing.SetOwner(null);
        Pawn = null;
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
    ///     HOLDING TO SEARCH A CHEST IS AN ABILITY, NOT AN INPUT. That is why doors worked for
    ///     rounds while chests did not: a door is a TAP, which the client sends as a plain
    ///     ServerAttemptInteract(InteractType=1), but a hold runs
    ///     GA_DefaultPlayer_InteractSearch and the RPC is sent from INSIDE it when it completes.
    ///     With no spec granted there is nothing to activate, so the hold never starts, no RPC is
    ///     ever sent, and the server sees nothing at all - the same silent shape as the jump bug
    ///     above. A working server's traffic says it outright (PacketProxy PR3.0 client log):
    ///
    ///         LogNetFastTArray: New! Element ID: 5. (Default__GA_DefaultPlayer_InteractSearch_C)
    ///         ...later, on a chest...
    ///         LogAbilitySystem: GA_DefaultPlayer_InteractSearch_C_2147477235 Activated
    ///         LogAbilitySystem: GA_DefaultPlayer_InteractSearch_C_2147477235 EndAbility
    ///         LogNetTraffic:    Sent RPC: ...InteractionComp::ServerAttemptInteract [135.4 bytes]
    ///
    ///     A Blueprint ability's CDO needs its real /Game/ package path rather than a /Script/
    ///     one, which is the only reason these were left out before; the paths below are copied
    ///     verbatim out of that log's RegisterNetGUIDFromPath_Client lines rather than guessed,
    ///     because a path the client cannot resolve has disconnected it before.
    ///
    ///     The full list that log shows a real server granting, in order, is: Jump, Sprint,
    ///     GA_DefaultPlayer_Death, _InteractUse, _InteractSearch, _PetOtherPet, GAB_AthenaDBNO,
    ///     GAB_AthenaDBNORevive, GA_Athena{Enter,Exit,In}Vehicle, GA_Rift_Athena_Skydive,
    ///     GA_Athena_Slurp_OLD, GA_Athena_Grenade_Rethrow, GAB_SurfaceChange,
    ///     GA_SiphonEffect_OnKillGrant, GA_Athena_ZipLine_Smash, GA_Athena_SCMachine_Passive,
    ///     GA_DudeBro_Vent, GA_Vehicle_ExitHoldEvent, GAB_Melee_ImpactCombo_Athena. Only the one
    ///     that the reported symptom needs is added here - GA_DefaultPlayer_InteractUse in
    ///     particular is deliberately NOT granted, because tap interaction (doors, doorbells)
    ///     already works through the plain RPC and granting its ability could reroute it.
    /// </summary>
    private static readonly string[] DefaultAbilities = {
        "/Script/FortniteGame.Default__FortGameplayAbility_Jump",
        "/Script/FortniteGame.Default__FortGameplayAbility_Sprint",
        "/Script/FortniteGame.Default__FortGameplayAbility_RangedWeapon",
        "/Game/Abilities/Player/Generic/Traits/DefaultPlayer/GA_DefaultPlayer_InteractSearch.Default__GA_DefaultPlayer_InteractSearch_C",
        // Jumping out of the battle bus crashes the client about a second into the skydive, and
        // this is id 12 of the 21 abilities a real server grants - none of 3..21 were granted here.
        // A missing ability normally means "nothing happens" rather than a crash, so this is a
        // reasonable try rather than a diagnosis.
        "/Game/Athena/Items/ForagedItems/Rift/GA_Rift_Athena_Skydive.Default__GA_Rift_Athena_Skydive_C"
        //
        // The rest of that list, with paths verified out of the capture's
        // RegisterNetGUID_Client lines, ready to add if they turn out to matter:
        //   /Game/Abilities/Player/Generic/Traits/DefaultPlayer/GA_DefaultPlayer_Death
        //   /Game/Abilities/Player/Generic/Traits/DefaultPlayer/GA_DefaultPlayer_PetOtherPet
        //   /Game/Abilities/NPC/Generic/GAB_AthenaDBNO
        //   /Game/Abilities/NPC/Generic/GAB_AthenaDBNORevive
        //   /Game/Athena/Environments/Blueprints/SurfaceEffects/GAB_SurfaceChange
        //   /Game/Athena/Items/Consumables/Grenade/GA_Athena_Grenade_Rethrow
        //   /Game/Athena/Items/Weapons/Abilities/GAB_Melee_ImpactCombo_Athena  (already granted with the pickaxe)
    };

    /// <summary>
    ///     Set once the default abilities have been handed out, so a SECOND Possess does not hand
    ///     out a second set. That became reachable the moment leaving the battle bus started
    ///     spawning a fresh pawn and possessing it: every grant appends to ActivatableAbilities, so
    ///     re-granting would leave the client with two specs per ability and two handles it could
    ///     activate.
    /// </summary>
    private bool _defaultAbilitiesGranted;

    private void GrantDefaultAbilities() {
        if (_defaultAbilitiesGranted) return;
        _defaultAbilitiesGranted = true;

        if (PlayerState?.AbilitySystemComponent is not { } abilitySystem) return;

        foreach (var path in DefaultAbilities) {
            var spec = abilitySystem.GrantAbility(UAssetRegistry.GetOrCreate(path));
            Console.WriteLine($"AController.Possess: granted {path[(path.LastIndexOf('.') + 1)..]} " +
                              $"as spec handle {spec.Handle}");
        }
    }
}