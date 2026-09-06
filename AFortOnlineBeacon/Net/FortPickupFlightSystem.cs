using AFortOnlineBeacon.Net.Actors;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     Holds a collected pickup alive for the length of its flight, so the client has something to
///     animate into the player's hands - and holds the ITEM back for the same length, so it lands in
///     the inventory when the animation lands rather than the instant the key was pressed.
///
///     WHY A DELAY IS THE WHOLE FEATURE. `ServerHandlePickup` used to set bPickedUp and call
///     Destroy() on the same line. The client's own RPC carries InFlyTime and InStartDirection - it
///     is asking for an arc - and the server threw both away and removed the actor immediately, so
///     the item simply blinked out. The four properties that describe the flight
///     (PickupLocationData.PickupTarget / FlyTime / StartDirection / bPlayPickupSound, handles 38,
///     43, 44 and 47) cannot do anything for an actor that is already gone.
///
///     THE GRANT BELONGS AT THE END OF THE FLIGHT, not at the start, and that is the real game's
///     structure rather than a preference. Fortnite has a native `AFortPickup::CompletePickupAnimation`,
///     and it is where the inventory work happens: PR3.0 hooks exactly that function to do its
///     stacking, swapping and overflow (FortPickup.cpp:309), while its `ServerHandlePickupHook`
///     (FortPlayerPawn.cpp:331) does nothing but publish the flight and set bPickedUp. So this
///     server's `ServerHandlePickup` handler now splits the same way: it decides UP FRONT whether
///     the pickup may happen at all - a full inventory has to leave the item on the ground before
///     anything flies, because there is no way to un-fly an item - and hands the actual grant here
///     to run when the arc finishes.
///
///     Shaped like FortDamageSystem's own deferred teardown, for the same reason: a list plus a tick
///     is all that is needed, and it keeps the RPC handler free of timing. The grant travels as a
///     callback rather than as data so that ownership stays where it reads correctly - this file
///     owns WHEN, and NativeRpcHandlers still owns WHAT.
/// </summary>
public static class FortPickupFlightSystem {
    private static readonly List<(AFortPickup Pickup, APawn Target, float LandsAt, Action OnComplete)> InFlight = new();

    /// <summary>
    ///     How long a pickup flies, in seconds.
    ///
    ///     NOT THE CLIENT'S NUMBER, which is what this used to echo. The client sends InFlyTime and
    ///     the value it asks for animates at Save The World's pace - reported as "same speed as STW,
    ///     slow". The real answer is 0.40s, and it is not a guess: PR3.0 discards the client's
    ///     InFlyTime in both of its pickup hooks and writes 0.40f (FortPlayerPawn.cpp:352 and 128),
    ///     and calls ServerHandlePickup with 0.40f again from its two internal callers. Someone
    ///     there clearly looked at the incoming value - the line printing it is still in the source,
    ///     commented out - and decided against it.
    ///
    ///     PICKUP_FLY_TIME overrides it. The number is the game's rather than an asset's, so it is
    ///     worth being able to change without a rebuild.
    /// </summary>
    private static readonly float FlightSeconds =
        float.TryParse(Environment.GetEnvironmentVariable("PICKUP_FLY_TIME"), out var seconds) && seconds > 0f
            ? Math.Min(seconds, MaxFlightSeconds)
            : 0.40f;

    /// <summary>
    ///     A ceiling on how long a pickup may be held back.
    ///
    ///     This guarded untrusted input while the flight time was the CLIENT's number - a client
    ///     asking for a thousand seconds would have left the actor standing in the world for that
    ///     long, visible to everyone else. The server chooses the time now, so the ceiling only
    ///     bounds PICKUP_FLY_TIME, and it stays because the delay now also holds back the ITEM: a
    ///     mistyped knob would be a player pressing pickup and getting nothing for a minute.
    /// </summary>
    private const float MaxFlightSeconds = 2f;

    /// <summary>
    ///     Starts the flight: publishes it to the client and queues the grant-then-destroy.
    ///
    ///     The dormancy flush is NOT the no-op the one in ServerHandlePickup was documented as being.
    ///     That one was harmless because the destroy followed on the next line, so the wake never got
    ///     a turn; here the actor deliberately survives the tick, so the wake DOES get its turn and
    ///     the flight properties actually go out. Same call, opposite significance - which is exactly
    ///     what that comment warned would eventually matter.
    /// </summary>
    public static void Begin(AFortPickup pickup, APawn target, FVector startDirection,
                             bool playSound, float now, Action onComplete) {
        pickup.PickupTarget = target;
        pickup.ItemOwner = target;
        pickup.FlyTime = FlightSeconds;
        pickup.StartDirection = startDirection;
        pickup.bPlayPickupSound = playSound;
        pickup.TossState = EFortPickupTossState.InProgress;
        pickup.bPickedUp = true;

        pickup.FlushNetDormancy();

        InFlight.Add((pickup, target, now + FlightSeconds, onComplete));
    }

    /// <summary>
    ///     Grants and destroys each pickup whose flight has finished.
    ///
    ///     A TARGET THAT DIED MID-FLIGHT still gets its actor cleaned up, but not the item: 0.40s is
    ///     long enough to be shot in, and handing an item to a dead player's inventory would put it
    ///     somewhere nobody can reach it or drop it. Losing it is what the player saw happen anyway.
    /// </summary>
    public static void Tick(UWorld world, float now) {
        if (InFlight.Count == 0) return;

        for (var i = InFlight.Count - 1; i >= 0; i--) {
            var (pickup, target, landsAt, onComplete) = InFlight[i];
            if (now < landsAt) continue;

            InFlight.RemoveAt(i);

            if (target.IsPendingKillPending()) {
                Console.WriteLine("FortPickupFlightSystem: the pawn a pickup was flying to is gone, dropping the grant");
            } else {
                onComplete();
            }

            pickup.Destroy();
        }
    }
}
