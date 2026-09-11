using AFortOnlineBeacon.Core.Math;
using AFortOnlineBeacon.Core.Names;
using AFortOnlineBeacon.Core.Objects;
using AFortOnlineBeacon.Net.Abilities;
using AFortOnlineBeacon.Net.Actors;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     WEARING the Sneaky Snowman - its SECOND button, `GA_Athena_Apply_SneakySnowman`.
///
///     The ability is the Bush's, reskinned (its tags are `Abilities.Generic.Athena.Bush` and
///     `Gameplay.Action.Player.InterruptibleHealingItem`, its stat `use_item_bush`), and what it does
///     when its 3.5-second montage triggers is, from its bytecode:
///
///         K2_CommitAbility                       - the cost: one Sneaky Snowman
///         BP_ApplyGameplayEffectToOwner          - GE_Athena_SneakySnowman: infinite, owned tags
///                                                  Athena.Item.Snowman + Athena.AI.IgnoreAll, cue
///                                                  GameplayCue.Athena.Applied.Item.Snowman
///         [IsDedicatedServer only]
///         BeginDeferredActorSpawnFromClass       - Athena_Player_SneakySnowman_C, Instigator = wearer
///         K2_AttachToComponent(mesh, "pelvis")   - the snowman the player is now inside
///
///     THE COSTUME IS A SERVER ACTOR and that is the whole of why this file exists: the client's copy
///     of the ability skips the spawn, so a server that cannot run the Blueprint has to do it. Until
///     now it did not - and the button was not even pressable, because the item's secondary ability
///     was never granted and AFortWeapon.SecondaryAbilitySpecHandle was never sent.
///
///     The costume (`BuildingGameplayActorBalloon`, like the Bush and the Balloons) carries its own
///     rules on the client side - team, a crouch check - and one on the server this file reproduces:
///     it breaks after `MaxHealth` (AthenaSneakySnowman.Defaults.FortHealthSet.MaxHealth = 100)
///     damage, which the building damage path already delivers because it IS a building actor.
///
///     Timed rather than instant, unlike the healing consumables: the player sees 3.5 seconds of
///     animation before the snowman appears, and a snowman that popped on at the first frame would
///     be the visible lie. A cancel (a weapon swap mid-animation) drops it; nothing waits on the
///     client's END message, so an ability the client never ends still lands.
/// </summary>
internal static class FortSnowmanDisguise {
    private const string ItemName = "Athena_SneakySnowman";

    private const string CostumeClassPath =
        "/Game/Athena/Items/Consumables/SneakySnowman/Athena_Player_SneakySnowman.Athena_Player_SneakySnowman_C";

    /// <summary>`GA_Athena_Apply_SneakySnowman.TriggerDuration`.</summary>
    private const float TriggerDuration = 3.5f;

    /// <summary>`AthenaSneakySnowman.Defaults.FortHealthSet.MaxHealth`, via the costume's MaxHealth.</summary>
    private const int CostumeMaxHealth = 100;

    /// <summary>GCN_Athena_SneakySnowman_C - a FortGameplayCueNotify_Loop, so Added and later Removed.</summary>
    private const string WornCue = "GameplayCue.Athena.Applied.Item.Snowman";

    private sealed record FPendingWear(APlayerState PlayerState, APawn Pawn, AFortWeapon Weapon, int Handle, float DueAt);

    private sealed record FWorn(APlayerState PlayerState, APawn Pawn, AFortDeployedActor Costume);

    private sealed class FState : FWorldSubsystem {
        public readonly List<FPendingWear> Pending = new();
        public readonly List<FWorn> Worn = new();
    }

    private static FState StateOf(UWorld world) => world.GetSubsystem<FState>();

    /// <summary>
    ///     Starts putting the snowman on, if this spec is the Sneaky Snowman's secondary ability.
    ///     Returns false for anything else so the caller's other paths run.
    /// </summary>
    public static bool TryBegin(APlayerState playerState, FGameplayAbilitySpec spec) {
        if (spec.SourceObject is not AFortWeapon { WeaponData: { } definition } weapon) return false;
        if (!definition.GetFName().ToString().Equals(ItemName, StringComparison.OrdinalIgnoreCase)) return false;
        if (spec.Handle != weapon.SecondaryAbilitySpecHandle) return false;
        if (playerState.GetOwningPawn() is not { } pawn || pawn.GetWorld() is not { } world) return true;

        var state = StateOf(world);

        // One at a time. The real ability's K2_CanActivateAbility refuses a player who already
        // carries Athena.Item.Snowman; a second costume on the same pelvis is not something the item
        // can produce.
        if (state.Worn.Any(worn => worn.Pawn == pawn) || state.Pending.Any(pending => pending.Pawn == pawn)) {
            Console.WriteLine($"FortSnowmanDisguise: {pawn.GetFName()} is already a snowman - ignoring.");
            return true;
        }

        state.Pending.Add(new FPendingWear(playerState, pawn, weapon, spec.Handle, world.TimeSeconds + TriggerDuration));
        Console.WriteLine($"FortSnowmanDisguise: {pawn.GetFName()} is putting on a snowman - on in {TriggerDuration:F1}s.");
        return true;
    }

    /// <summary>The use was cancelled before it triggered: nothing is put on and nothing is spent.</summary>
    public static void Cancel(APlayerState playerState, int handle) {
        if (playerState.GetWorld() is not { } world) return;

        var removed = StateOf(world).Pending.RemoveAll(p => p.PlayerState == playerState && p.Handle == handle);
        if (removed > 0) Console.WriteLine($"FortSnowmanDisguise: {playerState.GetFName()} cancelled putting on the snowman.");
    }

    public static void Tick(UWorld world, float now) {
        var state = StateOf(world);

        for (var i = state.Pending.Count - 1; i >= 0; i--) {
            if (now < state.Pending[i].DueAt) continue;

            var pending = state.Pending[i];
            state.Pending.RemoveAt(i);
            PutOn(world, state, pending);
        }

        for (var i = state.Worn.Count - 1; i >= 0; i--) {
            var worn = state.Worn[i];

            // BROKEN (the building damage path destroyed it) or the wearer is gone: take it off.
            // A costume left attached to a dead pawn would be detached on the client and stand
            // there on its own.
            var wearerGone = worn.Pawn.IsPendingKillPending() || worn.PlayerState.bIsDead;
            if (worn.Costume.IsPendingKillPending() || worn.Costume.bDestroyed || wearerGone) {
                state.Worn.RemoveAt(i);
                TakeOff(world, worn, wearerGone ? "its wearer is gone" : "it was broken");
                continue;
            }

            // The server's copy follows the wearer too. The CLIENT's is carried by the attachment;
            // this one is what explosions and relevancy measure from, and left at the spot the
            // player put it on it would be immune to a grenade thrown at where they are now.
            worn.Costume.SetActorLocation(worn.Pawn.GetActorLocation());
        }
    }

    private static void PutOn(UWorld world, FState state, FPendingWear pending) {
        if (pending.Pawn.IsPendingKillPending() || pending.PlayerState.bIsDead) return;

        AFortDeployedActor? costume;
        try {
            costume = world.SpawnActor<AFortDeployedActor>(
                GUClassArray.StaticClassForPath<AFortDeployedActor>(CostumeClassPath),
                new FActorSpawnParameters { ObjectFlags = EObjectFlags.RF_Transient, Owner = pending.Pawn });
        } catch (Exception ex) {
            Console.WriteLine($"FortSnowmanDisguise: could not spawn the costume - {ex.Message}");
            return;
        }

        if (costume == null) return;

        costume.DeployedClassPath = CostumeClassPath;
        costume.SetActorLocation(pending.Pawn.GetActorLocation());
        costume.SetInstigator(pending.Pawn);
        costume.SetOwner(pending.Pawn);
        costume.InitializeLevelActorHitPoints(CostumeMaxHealth);

        // VISIBLE, which its CDO is not - see AActor.bHidden.
        costume.bHidden = false;
        costume.bReplicateVisibility = true;
        costume.bReplicateInstigator = true;

        // ON THE WEARER. The real one goes to the mesh's "pelvis" socket; AttachComponent stays null
        // here, so the client attaches to the pawn's root - the capsule, whose centre sits within a
        // few units of the pelvis - and the socket name, not found on a capsule, falls back to its
        // origin.
        //
        // RelativeScale3D MUST be one. FRepAttachment's is ForceInit - zero - and the client applies
        // it to the root on attach, so an unset one shrinks the snowman to nothing.
        costume.AttachSocket = new FName("pelvis");
        costume.AttachRelativeScale3D = new FVector { X = 1f, Y = 1f, Z = 1f };
        costume.AttachParent = pending.Pawn;

        costume.SetRole(ENetRole.ROLE_Authority);
        costume.SetReplicates(true);

        state.Worn.Add(new FWorn(pending.PlayerState, pending.Pawn, costume));

        // The cost, the same way every consumable pays it.
        FortConsumableSystem.ConsumeOne(pending.PlayerState, pending.Weapon, "Sneaky Snowman");

        ShowWornCue(world, pending.PlayerState, pending.Pawn, true);

        Console.WriteLine($"FortSnowmanDisguise: {pending.Pawn.GetFName()} is now a snowman ({costume.GetFName()}, " +
                          $"{CostumeMaxHealth} HP).");
    }

    private static void TakeOff(UWorld world, FWorn worn, string why) {
        if (!worn.Costume.IsPendingKillPending()) worn.Costume.Destroy();
        ShowWornCue(world, worn.PlayerState, worn.Pawn, false);
        Console.WriteLine($"FortSnowmanDisguise: {worn.Pawn.GetFName()}'s snowman came off - {why}.");
    }

    /// <summary>
    ///     GE_Athena_SneakySnowman's cue, on while the snowman is worn: an element in the wearer's
    ///     ActiveGameplayCues plus the Added RPC, and later the element going away - the same pair
    ///     the low-gravity aura and the air strike's marker use. See FActiveGameplayCue.
    /// </summary>
    private static void ShowWornCue(UWorld world, APlayerState playerState, APawn pawn, bool on) {
        if (playerState.AbilitySystemComponent is not { } abilitySystem) return;

        if (on) {
            if (!abilitySystem.AddGameplayCue(WornCue)) return;

            foreach (var connection in world.NetDriver?.ClientConnections ?? Enumerable.Empty<UNetConnection>()) {
                if (connection.FindActorChannel(pawn) is { } channel)
                    channel.SendNetMulticastInvokeGameplayCueAddedWithParams(WornCue, null);
            }
        } else if (!abilitySystem.RemoveGameplayCue(WornCue)) {
            return;
        }

        world.NetDriver?.FlushAbilitySystemComponent(playerState);
    }
}
