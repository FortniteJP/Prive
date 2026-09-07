using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     Resends WELCOME_MESSAGE a few times after the player joins, so that TIMING can be crossed off
///     the list.
///
///     WHY A RESEND IS THE POINT. The first attempt sent the message once, on
///     `ServerLoadingScreenDropped`, and nothing appeared. That is per-player and at join - not at
///     server start, which was the obvious thing to wonder - but "the loading screen has dropped" is
///     the CLIENT saying it can see the world, not that its HUD is built and listening. A message
///     that arrives a frame too early is indistinguishable from one that arrives on a channel nobody
///     reads, and the two want completely different fixes.
///
///     So the same pair goes out again at intervals. If none of them shows, timing is not the
///     answer and the search moves to the channel; if a later one shows, it was.
///
///     Both RPCs are sent each time, for the reason UActorChannel.SendClientTeamMessage documents:
///     one carries an FText this project has never sent before and the other an FString it has been
///     writing correctly for months, so which of them appears is itself the measurement.
/// </summary>
public static class FortWelcomeMessage {
    private static readonly List<(APlayerController Controller, float DueAt, int Remaining)> Pending = new();

    /// <summary>Seconds between resends.</summary>
    private static readonly float Interval =
        float.TryParse(Environment.GetEnvironmentVariable("WELCOME_MESSAGE_INTERVAL"), out var v) && v > 0 ? v : 6f;

    /// <summary>
    ///     How many times to repeat AFTER the immediate one. Three more over eighteen seconds is
    ///     enough to cover a HUD that builds late without becoming noise in the log.
    /// </summary>
    private static readonly int Repeats =
        int.TryParse(Environment.GetEnvironmentVariable("WELCOME_MESSAGE_REPEATS"), out var v) && v >= 0 ? v : 3;

    public static string? Text => Environment.GetEnvironmentVariable("WELCOME_MESSAGE") is { Length: > 0 } text
        ? text
        : null;

    /// <summary>Queues the resends. The caller has already sent the first pair.</summary>
    public static void Schedule(APlayerController controller, float now) {
        if (Text == null || Repeats <= 0) return;

        Pending.Add((controller, now + Interval, Repeats));
    }

    public static void Tick(UWorld world, float now) {
        if (Pending.Count == 0 || Text is not { } text) return;

        for (var i = Pending.Count - 1; i >= 0; i--) {
            var (controller, dueAt, remaining) = Pending[i];
            if (now < dueAt) continue;

            var channel = world.NetDriver?.ClientConnections
                .FirstOrDefault(c => c.PlayerController == controller)?.FindActorChannel(controller);

            if (channel == null) {
                Pending.RemoveAt(i);   // gone from the game
                continue;
            }

            // Tagged per channel - see the same pair in NativeRpcHandlers. The text reaching the
            // client's CONSOLE proved the wire works; the suffix is what says which of the two did it.
            channel.SendClientSendMessage(text + " [FText/ClientSendMessage]");
            channel.SendClientTeamMessage(controller.PlayerState, text + " [FString/ClientTeamMessage]",
                                          typeNameIndex: 0);

            Console.WriteLine($"FortWelcomeMessage: resent both message RPCs to {controller.GetFName()} " +
                              $"({remaining - 1} resend(s) left). If a LATER one shows and the first did not, " +
                              "the HUD was simply not up yet.");

            if (remaining <= 1) Pending.RemoveAt(i);
            else Pending[i] = (controller, now + Interval, remaining - 1);
        }
    }
}
