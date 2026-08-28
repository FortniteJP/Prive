using AFortOnlineBeacon.Core.Objects;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     Turns a reported hit into wood, stone or metal.
///
///     The client tells this server what it hit as an object path and nothing more:
///     ".../Sublevel_X2Y3.PersistentLevel.Tree_Pine_169". An injected server would simply ask the
///     actor (ABuildingSMActor knows its own resource type); an external one has never heard of that
///     actor and never will - it lives in a streaming sublevel this server does not load.
///
///     So the answer is baked ahead of time from the shipped maps: every placed Blueprint actor in
///     Athena joined to its class, and every class to its CDO's ResourceType and amount tier. See
///     Tools/HarvestTable, which also documents why it takes TWO properties and not one.
///
///     One known soft spot, 90 classes of 2048: those carry no ResourceType of their own, so the
///     generator falls back to the family named by their amount curve. That is right for the case it
///     was built for (a tree with no override and a ResourceWoodLow curve) and wrong wherever a class
///     inherits its ResourceType from a PARENT Blueprint instead - a cooked child never re-serialises
///     an inherited value, so the property is simply absent. `Car_Pickup` coming out as wood rather
///     than metal is the visible symptom. Fixing it means walking the SuperStruct chain in the
///     generator; the other 1958 classes state their resource outright and are unaffected.
///
///     What this deliberately does NOT do is trust the hit. The client picked the target, the
///     position and the moment; a real server re-traces all of that. Nothing here is safe against a
///     modified client, and it should not be mistaken for something that is - see <see cref="Grant"/>.
/// </summary>
internal static partial class FortHarvestResources {
    /// <summary>
    ///     The three Athena resources, as their item definitions. Ordinary path-exported assets like
    ///     every other item this server names (see UAssetRegistry): the client already has them.
    /// </summary>
    private static readonly Dictionary<string, string> ItemPaths = new(StringComparer.OrdinalIgnoreCase) {
        ["Wood"] = "/Game/Items/ResourcePickups/WoodItemData.WoodItemData",
        ["Stone"] = "/Game/Items/ResourcePickups/StoneItemData.StoneItemData",
        ["Metal"] = "/Game/Items/ResourcePickups/MetalItemData.MetalItemData"
    };

    /// <summary>
    ///     How much one hit yields, per amount tier.
    ///
    ///     These numbers are the one thing in this file that is NOT ground truth. The tiers are real
    ///     - they come from the amount curve each Blueprint names - but the curve table itself has
    ///     not been tracked down, so only the ORDERING here is derived and the values are chosen to
    ///     feel like Chapter 1. Tools/HarvestTable already has the pak mount and could resolve the
    ///     real curve in the same pass whenever the numbers start to matter.
    /// </summary>
    private static readonly Dictionary<string, int> TierAmounts = new(StringComparer.OrdinalIgnoreCase) {
        ["Low"] = 2,
        ["Medium"] = 3,
        ["High"] = 5,
        ["VeryHigh"] = 7,
        ["Normal"] = 3, // LDBuildingNormal - a prefab building wall or floor
        ["Thick"] = 5   // LDBuildingThick - the same, reinforced
    };

    /// <summary>Fortnite's per-resource stack cap.</summary>
    private const int MaxResourceStack = 999;

    private static readonly HashSet<string> UnknownStemsSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Resolves a hit actor's path to the resource it yields, or null if it yields none - which
    ///     is most of the world: terrain, roads, and anything with no BuildingResourceAmountOverride
    ///     at all.
    /// </summary>
    public static (string ItemPath, int Amount)? ResolveHit(string hitActorPath) {
        if (string.IsNullOrEmpty(hitActorPath)) return null;

        // The path is Package.Level.ActorName - only the last segment is the placed actor.
        var actorName = hitActorPath[(hitActorPath.LastIndexOf('.') + 1)..];
        var stem = Stem(actorName);
        if (stem.Length == 0) return null;

        if (!StemYields.TryGetValue(stem, out var yield)) {
            // Not an error - most things are not harvestable. Logged once per stem, capped, because
            // the list of stems that SHOULD have resolved is the only way a gap in the generated
            // table ever becomes visible.
            if (UnknownStemsSeen.Add(stem) && UnknownStemsSeen.Count <= 40) {
                Console.WriteLine($"FortHarvestResources: '{stem}' yields no resource (not in the generated table)");
            }

            return null;
        }

        // "Wood|Medium" - resource then amount tier, the two properties that decide the answer.
        var split = yield.IndexOf('|');
        var resource = split < 0 ? yield : yield[..split];
        var tier = split < 0 ? "Medium" : yield[(split + 1)..];

        if (!ItemPaths.TryGetValue(resource, out var itemPath)) return null;

        return (itemPath, TierAmounts.GetValueOrDefault(tier, 3));
    }

    /// <summary>
    ///     Adds the resource to the player's inventory, stacking onto what is already there.
    ///
    ///     Stacking matters for more than tidiness: the client shows resources as one counter per
    ///     type, and a second WoodItemData row would replicate fine and display wrong. A stack that
    ///     is already full is left completely alone, so no replication key moves and no delta is sent
    ///     for a hit that changed nothing.
    ///
    ///     This is the point where a real server would have re-traced the shot. It has not been, and
    ///     this grants on the client's word alone: a modified client can ask for resources it never
    ///     earned. Acceptable for a private server, and not for anything else.
    /// </summary>
    public static void Grant(APlayerController controller, string itemPath, int amount) {
        if (controller.WorldInventory is not { } inventory) return;

        var definition = UAssetRegistry.GetOrCreate(itemPath);
        var existing = inventory.Inventory.Items.FirstOrDefault(item => item.ItemDefinition == definition);

        if (existing == null) {
            inventory.Inventory.Add(new FFortItemEntry {
                ItemDefinition = definition,
                Count = Math.Min(amount, MaxResourceStack)
            });

            Console.WriteLine($"FortHarvestResources: granted {amount} x {definition.GetFName()} (new stack), " +
                              $"ArrayReplicationKey={inventory.Inventory.ArrayReplicationKey}");
            return;
        }

        if (existing.Count >= MaxResourceStack) return;

        existing.Count = Math.Min(existing.Count + amount, MaxResourceStack);
        inventory.Inventory.MarkItemDirty(existing);

        Console.WriteLine($"FortHarvestResources: granted {amount} x {definition.GetFName()} -> {existing.Count}, " +
                          $"ArrayReplicationKey={inventory.Inventory.ArrayReplicationKey}");
    }

    /// <summary>
    ///     "Tree_Pine_169" -> "Tree_Pine". Must stay identical to Tools/HarvestTable's own Stem() or
    ///     the generated keys stop matching what is looked up in them.
    /// </summary>
    private static string Stem(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"_?\d+$", "").TrimEnd('_');
}
