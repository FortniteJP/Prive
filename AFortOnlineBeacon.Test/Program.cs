using System.Text;
using AFortOnlineBeacon.Core;
using AFortOnlineBeacon.Net;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Test;

class Program {
    private const float TickRate = (1000.0f / 60.0f) / 1000.0f;
    private static readonly PeriodicTimer Tick = new PeriodicTimer(TimeSpan.FromSeconds(TickRate));

    static async Task<int> Main(string[] args) {
        //StartPacketCapture();
        //StartConsoleLog();

        var worldUrl = new FUrl {
            Map = "/Game/Athena/Maps/Athena_Terrain",
            Port = 20000
        };
        
        await using (var world = new FortniteWorld()) {
            world.SetGameInstance(new UGameInstance());
            world.SetGameMode(worldUrl);
            world.InitializeActorsForPlay(worldUrl, true);
            world.Listen();
        
            while (await Tick.WaitForNextTickAsync()) world.Tick(TickRate);
        }
        return 0;
    }

    // Dumps every UDP packet (in and out) to a plain-text log next to the executable, so a
    // capture can be handed over for offline analysis without needing Wireshark on this machine.
    private static void StartPacketCapture() {
        var captureDir = Path.Combine(AppContext.BaseDirectory, "Captures");
        Directory.CreateDirectory(captureDir);

        var capturePath = Path.Combine(captureDir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        var writer = new StreamWriter(capturePath, append: false) { AutoFlush = true };
        var writeLock = new object();

        PacketCapture.OnPacket += (direction, address, data) => {
            lock (writeLock) {
                writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {(direction == EPacketDirection.Incoming ? "IN " : "OUT")} {address} ({data.Length} bytes)");
                writer.WriteLine(Convert.ToHexString(data));
                writer.WriteLine();
            }
        };

        Console.WriteLine($"Packet capture: {capturePath}");
    }

    // Mirrors all Console.WriteLine output (the diagnostic lines sprinkled through the handshake/
    // connection code) to a file, so a run's console output can be handed over alongside the packet
    // capture without needing to remember to redirect stdout manually.
    private static void StartConsoleLog() {
        var logDir = Path.Combine(AppContext.BaseDirectory, "Captures");
        Directory.CreateDirectory(logDir);

        var logPath = Path.Combine(logDir, $"console_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        var fileWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };

        Console.SetOut(new TeeTextWriter(Console.Out, fileWriter));
        Console.SetError(new TeeTextWriter(Console.Error, fileWriter));

        // The default unhandled-exception message goes to stderr just before the process dies -
        // mirroring Console.Error above should already catch it, but hook this too as a fallback
        // in case the runtime tears down stdio before that message is flushed.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => {
            fileWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] UNHANDLED EXCEPTION (terminating: {e.IsTerminating})");
            fileWriter.WriteLine(e.ExceptionObject);
        };

        Console.WriteLine($"Console log: {logPath}");
    }

    private sealed class TeeTextWriter(TextWriter first, TextWriter second) : TextWriter {
        public override Encoding Encoding => first.Encoding;

        public override void Write(char value) {
            first.Write(value);
            second.Write(value);
        }

        public override void Write(string? value) {
            first.Write(value);
            second.Write(value);
        }

        public override void WriteLine(string? value) {
            first.WriteLine(value);
            second.WriteLine(value);
        }
    }
}
