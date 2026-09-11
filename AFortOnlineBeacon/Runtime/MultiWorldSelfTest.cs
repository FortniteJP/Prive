using AFortOnlineBeacon.Core.Math;
using AFortOnlineBeacon.Core.Objects;
using AFortOnlineBeacon.Net;
using AFortOnlineBeacon.Net.Actors;

namespace AFortOnlineBeacon.Runtime;

/// <summary>
///     The property the whole world-scoping refactor exists for: SEVERAL WORLDS IN ONE PROCESS, each
///     with its own configuration and its own match state, and nothing leaking between them.
///
///     Every check here is one of the ways it used to be false. Configuration came from the process
///     environment, so two worlds could not differ; match state lived in statics, so the second world
///     would inherit the first one's grenades, storm phase and structural grid; and the shared object
///     model was written from whichever thread got there first, which two worlds ticking on two
///     threads would eventually corrupt.
///
///     Needs no client, no map and no paks: <c>--multiworld-selftest</c>, alongside the others.
/// </summary>
public static class MultiWorldSelfTest {
    private sealed class TestWorld : UWorld { }

    /// <summary>A subsystem that counts its own initialisation, to prove "once per world".</summary>
    private sealed class FProbeSubsystem : FWorldSubsystem {
        public int Initialized;
        public string? SeenSeed;

        protected internal override void Initialize() {
            Initialized++;
            SeenSeed = Options.Get("SAFEZONE_SEED");
        }
    }

    public static bool RunSelfTest() {
        var passed = true;

        void Check(bool condition, string what) {
            Console.WriteLine($"  {(condition ? "ok  " : "FAIL")}  {what}");
            if (!condition) passed = false;
        }

        Console.WriteLine("MultiWorld self-test:");

        // ---------------------------------------------------------------- configuration
        var solo = new TestWorld {
            Options = FBeaconOptions.Empty.With("SAFEZONE_SEED", "111").With("INVENTORY_SLOTS", "5")
        };
        var lateGame = new TestWorld {
            Options = FBeaconOptions.Empty.With("SAFEZONE_SEED", "222").With("INVENTORY_SLOTS", "6")
        };

        Check(solo.Options.Get("SAFEZONE_SEED") == "111" && lateGame.Options.Get("SAFEZONE_SEED") == "222",
              "two worlds carry different values for the same knob");
        Check(solo.Options.Get("NOT_A_KNOB") == null, "an unset knob reads as null, like an unset variable did");
        Check(new TestWorld().Options.Get("PATH") == Environment.GetEnvironmentVariable("PATH"),
              "a world given no options takes the process environment - run-beacon.ps1 keeps working");

        var layered = solo.Options.With("SAFEZONE_SEED", null);
        Check(layered.Get("SAFEZONE_SEED") == null && solo.Options.Get("SAFEZONE_SEED") == "111",
              "With() copies: overriding one world's options leaves the original untouched");

        // ---------------------------------------------------------------- subsystems
        var soloProbe = solo.GetSubsystem<FProbeSubsystem>();
        var lateProbe = lateGame.GetSubsystem<FProbeSubsystem>();

        Check(ReferenceEquals(soloProbe, solo.GetSubsystem<FProbeSubsystem>()),
              "a world hands out ONE instance of a subsystem");
        Check(!ReferenceEquals(soloProbe, lateProbe), "two worlds get two instances");
        Check(soloProbe.Initialized == 1 && lateProbe.Initialized == 1, "Initialize runs exactly once per world");
        Check(ReferenceEquals(soloProbe.World, solo) && ReferenceEquals(lateProbe.World, lateGame),
              "each subsystem knows its own world");
        Check(soloProbe.SeenSeed == "111" && lateProbe.SeenSeed == "222",
              "a subsystem initialises from ITS world's options, not the process's");

        Check(!ReferenceEquals(BuildingStructuralSupportSystem.Of(solo), BuildingStructuralSupportSystem.Of(lateGame)),
              "the structural support grid is per world");

        // ---------------------------------------------------------------- actors see their own world
        var soloActor = solo.SpawnActor<AActor>(GUClassArray.StaticClass<AActor>(), new FActorSpawnParameters());
        var lateActor = lateGame.SpawnActor<AActor>(GUClassArray.StaticClass<AActor>(), new FActorSpawnParameters());

        Check(soloActor.GetWorld() == solo && lateActor.GetWorld() == lateGame, "a spawned actor belongs to its world");
        Check(soloActor.WorldOptions.Get("INVENTORY_SLOTS") == "5" && lateActor.WorldOptions.Get("INVENTORY_SLOTS") == "6",
              "an actor reads its OWN world's knobs");

        // ---------------------------------------------------------------- two worlds, two threads
        // The shared object model - FName pool, class array, unique-name counters - is written by
        // every spawn, and used to be unlocked. A Dictionary written from two threads does not
        // reliably THROW: its usual failure is quieter - a resize races, an entry is lost, a counter
        // starts again from zero - and the observable is then a DUPLICATE actor name inside one
        // world. So this counts those, and releases both threads at once off a barrier so they
        // genuinely overlap. (Checked by removing the lock: this is the check that fails.)
        const int perWorld = 20000;
        var soloNames = new List<string>(perWorld);
        var lateNames = new List<string>(perWorld);
        Exception? failure = null;
        using var start = new Barrier(2);

        void Spawn(UWorld world, List<string> names) {
            try {
                start.SignalAndWait();
                for (var i = 0; i < perWorld; i++) {
                    // A fresh class per 500 spawns, so the counter dictionary keeps GROWING - its
                    // resizes are where an unsynchronised writer loses entries.
                    var clazz = GUClassArray.StaticClassForPath<AActor>($"/Game/SelfTest/C{i / 500}.C{i / 500}_C");
                    names.Add(world.SpawnActor<AActor>(clazz, new FActorSpawnParameters()).GetFName().ToString()
                              + "@" + (i / 500));
                }
            } catch (Exception ex) {
                failure ??= ex;
            }
        }

        var a = new Thread(() => Spawn(solo, soloNames));
        var b = new Thread(() => Spawn(lateGame, lateNames));
        a.Start();
        b.Start();
        a.Join();
        b.Join();

        Check(failure == null, "two worlds spawning concurrently on two threads raise nothing" +
                               (failure == null ? "" : $" (got {failure.GetType().Name}: {failure.Message})"));
        var soloDupes = soloNames.Count - soloNames.Distinct().Count();
        var lateDupes = lateNames.Count - lateNames.Distinct().Count();
        Check(soloNames.Count == perWorld && lateNames.Count == perWorld, $"both worlds spawned all {perWorld} actors");
        Check(soloDupes == 0 && lateDupes == 0,
              $"no actor name is handed out twice within a world ({soloDupes} + {lateDupes} duplicates)");

        // ---------------------------------------------------------------- process-level configuration
        _ = FBeaconProcess.Options;
        var refused = false;
        try {
            FBeaconProcess.Configure(FBeaconOptions.Empty);
        } catch (InvalidOperationException) {
            refused = true;
        }
        Check(refused, "process configuration cannot be changed after something has read it");

        Console.WriteLine(passed ? "MultiWorld self-test: all checks passed." : "MultiWorld self-test: FAILURES above.");
        return passed;
    }
}
