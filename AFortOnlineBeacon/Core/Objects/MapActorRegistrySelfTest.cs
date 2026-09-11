using AFortOnlineBeacon.Net.Actors;
using AFortOnlineBeacon.Runtime;

namespace AFortOnlineBeacon.Core.Objects;

/// <summary>
///     The invariant that lets more than one world share a process: a map-actor stand-in belongs to
///     ONE world, an immutable asset is shared by all of them.
///     <para>
///         Both halves matter and they pull in opposite directions. Stand-ins carry match state -
///         ABuildingContainer.bAlreadySearched is literally whether that chest has been looted - so
///         sharing them would let one match see another's loot, and would hand a second match the
///         first one's already-open chests. Plain assets carry nothing, and are shared on purpose:
///         FNetGUIDCache is keyed by object identity, so one instance per path is what keeps a
///         path-exported reference stable.
///     </para>
///     <para>
///         Needs no client, no map and no paks, so it runs as
///         <c>--mapactors-selftest</c> alongside the others.
///     </para>
/// </summary>
public static class MapActorRegistrySelfTest {
    private sealed class TestWorld : UWorld { }

    private const string ChestPath =
        "/Game/Athena/Maps/Athena_Terrain.Athena_Terrain:PersistentLevel.Tiered_Chest_Athena_C_1";

    public static bool RunSelfTest() {
        var passed = true;

        void Check(bool condition, string what) {
            Console.WriteLine($"  {(condition ? "ok  " : "FAIL")}  {what}");
            if (!condition) passed = false;
        }

        var matchOne = new TestWorld();
        var matchTwo = new TestWorld();

        var chestInOne = matchOne.MapActors.GetOrCreate<ABuildingContainer>(ChestPath);
        var chestInTwo = matchTwo.MapActors.GetOrCreate<ABuildingContainer>(ChestPath);

        Check(!ReferenceEquals(chestInOne, chestInTwo),
            "the same chest path in two worlds is two different actors");

        Check(ReferenceEquals(chestInOne, matchOne.MapActors.GetOrCreate<ABuildingContainer>(ChestPath)),
            "asking twice within one world returns the same actor");

        // The point of all of it: looting in one match must not loot in the other.
        chestInOne.Search();
        Check(chestInOne.bAlreadySearched, "looting the chest marks it searched in its own world");
        Check(!chestInTwo.bAlreadySearched, "and leaves the other world's chest untouched");

        // A world that has just been created is a fresh match, so nothing carries over.
        var matchThree = new TestWorld();
        Check(!matchThree.MapActors.GetOrCreate<ABuildingContainer>(ChestPath).bAlreadySearched,
            "a newly created world starts with the chest unlooted");

        // Both are still name-stable for networking, which is what makes the path exportable at
        // all: real UE's UObject::IsNameStableForNetworking is the RF_WasLoaded case here.
        Check(chestInOne.GetFlags().HasFlag(EObjectFlags.RF_WasLoaded)
            && chestInTwo.GetFlags().HasFlag(EObjectFlags.RF_WasLoaded),
            "both stand-ins keep RF_WasLoaded, so both stay name-stable for networking");

        Console.WriteLine(passed
            ? "MapActorRegistry self-test: all checks passed."
            : "MapActorRegistry self-test: FAILURES above.");
        return passed;
    }
}
