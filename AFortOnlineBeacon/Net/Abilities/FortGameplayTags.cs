namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     Tag name -> the number that goes on the wire. The table itself is generated; this is the half
///     that answers questions about it.
///
///     WHY A TABLE AT ALL. FGameplayTag::NetSerialize does not send the name. It sends a NET INDEX -
///     the tag's position in a sorted list of every tag node the game knows - as a flat 14-bit field,
///     and the two sides never exchange that list: each builds it from its own config and assumes the
///     other did the same. So a server that cannot reproduce the list cannot send a single tag, which
///     is why every tag this project ever wrote before now was the empty one.
///
///     WHERE THE NUMBERS COME FROM, and why they can be trusted: see the header of
///     FortGameplayTags.Generated.cs and the class comment on Tools/GameplayTags. The short version
///     is that the paks reproduce the client's tag tree exactly (proven against a CRC the client
///     logs) and the memory dump supplies the 480 native tags' SHAPE, which is enough to pin 12340 of
///     13502 slots without guessing at any of them.
///
///     THE TABLE IS INCOMPLETE ON PURPOSE. A tag with no derivable index is absent rather than
///     approximated, because a wrong index is not a missing effect - it is a DIFFERENT tag, silently.
///     <see cref="IndexOf" /> returns null for those and every caller has to decide what to do
///     rather than send something plausible.
/// </summary>
public static partial class FortGameplayTags {
    /// <summary>
    ///     The net index for a tag name, or null when this table cannot derive one.
    ///
    ///     Case-insensitive, like FName. Null means "do not send this tag" - not "send zero": index 0
    ///     is a real tag (the alphabetically first one), which is exactly the trap
    ///     <see cref="InvalidNetIndex" /> exists to avoid.
    /// </summary>
    public static uint? IndexOf(string tagName) =>
        Lookup.TryGetValue(tagName, out var index) ? index : null;

    /// <summary>
    ///     <see cref="IndexOf" />, but shouting once per unknown tag instead of returning null - for
    ///     the callers where a missing tag means a feature quietly does nothing and the reason would
    ///     otherwise never surface.
    /// </summary>
    public static uint IndexOrWarn(string tagName) {
        if (IndexOf(tagName) is { } index) return index;

        if (Warned.TryAdd(tagName, 0)) {
            Console.WriteLine($"FortGameplayTags: '{tagName}' has no derivable net index " +
                              "(it is one of the slots Tools/GameplayTags could not force, or it is not a tag at " +
                              "all). Sending the empty tag instead - whatever needed it will do nothing.");
        }

        return InvalidNetIndex;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     BUILT ON FIRST USE, NOT IN A FIELD INITIALIZER, and that is not a style choice.
    ///
    ///     `Table` lives in the OTHER half of this partial class, the generated one. Static field
    ///     initializers run in textual order WITHIN a class, but the order the compiler visits the
    ///     files of a partial class is not something this code gets to decide - so a
    ///     `= BuildLookup()` here can and did run while `Table` was still null. The whole type then
    ///     fails to initialize, and every call site sees a TypeInitializationException instead: the
    ///     first live emoji attempt died as "RPC ServerPlayEmoteItem failed to decode", which names
    ///     neither this class nor the real cause.
    ///
    ///     The same failure the RPC parameter tables hit once before (see VerifyRpcTables), in a
    ///     class written after it. A lazy build has no ordering to get wrong.
    /// </summary>
    private static Dictionary<string, ushort> Lookup => _lookup ??= BuildLookup();

    private static Dictionary<string, ushort>? _lookup;

    private static Dictionary<string, ushort> BuildLookup() {
        var map = new Dictionary<string, ushort>(Table.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, index) in Table) map[name] = index;

        Console.WriteLine($"FortGameplayTags: {map.Count} tag net indices loaded " +
                          $"({NetIndexBits} bits each, empty tag = {InvalidNetIndex})");
        return map;
    }
}
