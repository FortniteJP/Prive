using AFortOnlineBeacon.Net.Actors;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     Reports, out loud and repeatedly, that no client has ever pressed jump - together with every
///     piece of state a jump is known to depend on.
///
///     WHY THIS EXISTS RATHER THAN JUST THE EXISTING PROBE. APawn.TrackMoveFlags already prints
///     "client move flag JumpPressed (0x01) seen for the first time" when the client jumps, and its
///     ABSENCE is the diagnosis - but an absence is invisible in a log, it has to be noticed, and
///     nobody reading a few thousand lines notices a line that is not there. This turns the absence
///     into a line that IS there, carrying the answer with it.
///
///     WHAT IT PRINTS AND WHY EACH ONE. Jump has been chased three times on this project and every
///     cause found so far is in here, so one line separates them (see [[afortonlinebeacon-status]],
///     the 2026-08-28 entries):
///
///       * `jumpAbility`  - jump IS a gameplay ability on 10.40
///         (/Script/FortniteGame.FortGameplayAbility_Jump). Before it was granted the client simply
///         could not jump, with no complaint anywhere. If this says MISSING, that is the answer.
///       * `bMarkedAlive` - AFortPlayerControllerAthena handle 75, the client's own "am I alive",
///         and it locally gates BOTH jumping and building. False on a client never told otherwise,
///         and cleared by FortDamageSystem.Kill - so a player who died (storm damage on the warmup
///         island, say) looks exactly like a player who cannot jump.
///       * `bIsDying`, `phase`, `matchState` - the other states that were suspected and cleared;
///         cheap to print and they close those doors for good.
///       * `moveFlags` - which input bits have EVER arrived. Crouch (0x02) arriving while jump
///         (0x01) never does is what proved the input system was alive and jump specifically
///         refused; no bits at all would be a different problem entirely.
///
///     Stops permanently the moment any client presses jump, so a working server prints this at most
///     a handful of times. JUMP_DIAGNOSTICS=0 turns it off.
/// </summary>
public static class JumpDiagnostics {

    /// <summary>This world's share of JumpDiagnostics's state - see FWorldSubsystem.</summary>
    private sealed class FJumpDiagnosticsState : FWorldSubsystem {
        public bool Enabled => Options.Get("JUMP_DIAGNOSTICS") is not "0";

        public float IntervalSeconds =>
            float.TryParse(Options.Get("JUMP_DIAGNOSTICS_INTERVAL"), out var value) ? value : 10f;

        public float _nextReport;

        public bool _everJumped;
    }

    private static FJumpDiagnosticsState StateOf(UWorld world) => world.GetSubsystem<FJumpDiagnosticsState>();

    public static void Tick(UWorld world, float now) {
        var worldState = StateOf(world);

        if (!worldState.Enabled || worldState._everJumped || now < worldState._nextReport) return;
        worldState._nextReport = now + worldState.IntervalSeconds;

        if (world.NetDriver is not { } netDriver) return;

        foreach (var connection in netDriver.ClientConnections) {
            if (connection.PlayerController is not { } pc) continue;
            if (pc.Pawn is not { } pawn) continue;

            // Bit 0 of FSavedMove_Character's compressed flags. Seeing it once ends this for good:
            // it means ACharacter::Jump() ran, so whatever is wrong after that is on this side.
            if ((pawn.SeenMoveFlags & 0x01) != 0) {
                worldState._everJumped = true;
                return;
            }

            var abilities = pc.PlayerState?.AbilitySystemComponent?.ActivatableAbilities.Items;
            var jumpGranted = abilities?.Any(spec =>
                spec.Ability?.GetFName().ToString().Contains("Jump", StringComparison.OrdinalIgnoreCase) == true) ?? false;

            var granted = abilities == null
                ? "(no ability system component)"
                : string.Join(", ", abilities.Select((spec, i) =>
                    $"{i}:{spec.Ability?.GetFName().ToString() ?? "?"}"));

            Console.WriteLine(
                $"JumpDiagnostics: no client has pressed JUMP yet ({now:F0}s). " +
                $"jumpAbility={(jumpGranted ? "granted" : "MISSING - this is the cause")}, " +
                $"bMarkedAlive={pc.bMarkedAlive}{(pc.bMarkedAlive ? "" : " <- DEAD, jump and building are gated client-side")}, " +
                $"bIsDying={pawn.bIsDying}, " +
                // The descent/spectator states, added after the user reported walking THROUGH
                // buildings and props and being unable to pick items up. Those three symptoms plus
                // "cannot jump" are ONE symptom - the pawn has no collision - and every replicated
                // state that can put a Fortnite pawn into a no-collision state is here. bInAircraft
                // and the skydive flags are the bus's; a player still flagged as aboard or mid-drop
                // is exactly a player who passes through the world.
                $"bInAircraft={pc.PlayerState?.bInAircraft.ToString() ?? "-"}, " +
                $"bIsSkydiving={pawn.bIsSkydiving}, bIsParachuteOpen={pawn.bIsParachuteOpen}, " +
                $"bIsSkydivingFromBus={pawn.bIsSkydivingFromBus}, " +
                // If this disagrees, the controller is driving a pawn it does not officially
                // possess - the shape a stuck NAME_Spectating leaves behind.
                $"possessed={(pawn.Controller == pc ? "yes" : "NO - controller/pawn disagree")}, " +
                $"moveFlags=0x{pawn.SeenMoveFlags:X2} ({DescribeFlags(pawn.SeenMoveFlags)}), " +
                $"movementMode={pawn.LastClientMovementMode?.ToString() ?? "(never reported)"}, " +
                $"phase={world.GameState?.GamePhase.ToString() ?? "(no GameState)"}, " +
                $"matchState={world.GameState?.MatchState.ToString() ?? "-"}, " +
                $"abilities=[{granted}]");
        }
    }

    private static string DescribeFlags(byte flags) {
        if (flags == 0) return "none have EVER arrived - the moves carry no input at all";

        var names = new[] {
            "JumpPressed", "WantsToCrouch", "Reserved_1", "Reserved_2",
            "Custom_0", "Custom_1", "Custom_2", "Custom_3"
        };

        return string.Join("|", Enumerable.Range(0, 8).Where(bit => (flags & (1 << bit)) != 0).Select(bit => names[bit]));
    }
}
