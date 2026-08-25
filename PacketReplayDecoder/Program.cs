using System.Globalization;

namespace PacketReplayDecoder;

internal static class Program {
    private static void Main(string[] args) {
        var logPath = args.Length > 0 ? args[0] : @"C:\Users\user\AppData\Local\Temp\claude\c--Users-user-Documents-Prive\058027bb-4373-4a48-a8ca-22c1b76a4a21\scratchpad\PacketProxy\bin\Debug\net10.0\packets_ProjectReboot3.0.log";
        var limit = args.Length > 1 ? int.Parse(args[1]) : int.MaxValue;
        var verbose = args.Contains("--verbose");

        Console.WriteLine($"Reading {logPath} (limit={(limit == int.MaxValue ? "none" : limit)})");

        var lines = File.ReadAllLines(logPath);
        Console.WriteLine($"{lines.Length} lines total");

        var serverRole = new RoleDecoder(isServerRole: true, verbose); // decodes C->S
        var clientRole = new RoleDecoder(isServerRole: false, verbose); // decodes S->C

        var processed = 0;
        foreach (var line in lines) {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith('#')) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;

            var indexStr = parts[0].TrimStart('#');
            if (!int.TryParse(indexStr, out var index)) continue;

            var direction = parts[1]; // "C->S" or "S->C"

            var lenPart = parts[3];
            if (!lenPart.StartsWith("len=")) continue;

            var hex = parts[4];
            byte[] bytes;
            try {
                bytes = Convert.FromHexString(hex);
            } catch {
                continue;
            }

            if (index > limit) break;

            Console.WriteLine($"---- #{index} {direction} len={bytes.Length} ----");

            if (direction == "C->S") serverRole.Feed(index, bytes);
            else if (direction == "S->C") {
                clientRole.Feed(index, bytes);

                // The server role can't validate its own connectionless handshake against a capture
                // (it would need the real server's own random per-run HMAC secret) - so it borrows
                // the same two sequence numbers straight from the client role's cookie parse instead,
                // the moment they become available.
                if (clientRole.RecoveredHandshakeSeqs is { } seqs) {
                    serverRole.ForceInitFromCookie(seqs.serverSeq, seqs.clientSeq);
                }
            }

            processed++;
        }

        Console.WriteLine($"Done. Processed {processed} packets.");
    }
}
