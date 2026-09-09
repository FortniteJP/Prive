using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;

namespace GameplayTags;

/// <summary>
///     Rebuilds the client's FGameplayTag NET INDEX table, offline, and PROVES it is right.
///
///     WHY THIS EXISTS. A gameplay tag does not go on the wire as a string: FGameplayTag::NetSerialize
///     writes a 13-bit NET INDEX, which is nothing but the tag's position in a list both sides build
///     independently at startup. Until now this server could only ever send index 0 - the invalid tag -
///     which is why FGameplayTypes.WriteTag has never been called with anything else. Every feature
///     whose whole payload IS a tag was therefore out of reach: GameplayCues (the emoji sprite, every
///     hit and footstep effect), tag containers that actually contain something, and gameplay events
///     with a real EventTag.
///
///     WHAT MAKES IT SAFE TO DERIVE. UGameplayTagsManager::ConstructNetIndex (GameplayTagsManager.cpp:438)
///     is completely deterministic: take every tag NODE - which is every tag plus every ancestor
///     prefix, so "A.B.C" also contributes "A" and "A.B" - sort them by complete tag name, and the
///     index is the position. There is no negotiation and nothing is sent; the client simply assumes
///     the server did the same thing. Get the SET or the ORDER wrong by one and every tag is silently
///     off by one from that point on.
///
///     AND WHAT MAKES IT VERIFIABLE, which is the part that matters. The same function accumulates a
///     CRC32 over the sorted list and logs it:
///
///         LogGameplayTags: NetworkGameplayTagNodeIndexHash is 79b9274c
///
///     Both the client and the reference Project-Reboot-3.0 server print exactly that (four separate
///     captures in PriveDev/PacketProxy). So this tool does not have to be trusted - it recomputes
///     that hash from the paks and refuses to emit a table unless it matches. A single missing tag,
///     one extra, or one pair out of order changes the hash completely.
///
///     WHERE THE TAGS COME FROM. FortniteGame/Config/DefaultGameplayTags.ini (cooked into the paks,
///     not on disk) carries 4203 +GameplayTagList entries and names 37 +GameplayTagTableList data
///     tables; ConstructGameplayTagTree feeds both into the same node map. There is no
///     CommonlyReplicatedTags list in 10.40's ini, so the "put the common indices up front" reorder
///     in ConstructNetIndex is a no-op here - if a future version adds one, this tool must too.
///
///     Configuration matches Tools/PakReader so no key lands in the repo:
///         FORTNITE_PAKS     directory holding pakchunk*.pak
///         FORTNITE_AES_KEY  0x-prefixed 32-byte key
/// </summary>
public static class Program {
    private const string DefaultPaks = @"C:\Users\user\Documents\10.40\FortniteGame\Content\Paks";
    private const string TagsIni = "FortniteGame/Config/DefaultGameplayTags.ini";

    /// <summary>FPaths::ProjectConfigDir()/Tags - the "Extra tags" directory. See the loop in Main.</summary>
    private const string ExtraTagsDir = "FortniteGame/Config/Tags/";

    /// <summary>
    ///     The hash of the client's LIVE tag tree - the one the wire uses - as both the client and
    ///     the reference server log it. The `fromlog` route has to hit this exactly.
    /// </summary>
    private const uint LiveTreeHash = 0x79b9274c;

    /// <summary>
    ///     The hash of the client's FIRST tag tree, which is the one the paks alone describe, and the
    ///     check that makes the whole alignment sound.
    ///
    ///     The client builds the tree TWICE: once at InitializeManager from the ini and the data
    ///     tables, and again at PostEngineInit after every module has added its NATIVE tags
    ///     (DoneAddingNativeTags, GameplayTagsManager.cpp:1738). It logs a hash each time, and both
    ///     appear in all four captures. Matching this one proves the pak-derived node set is EXACTLY
    ///     the client's first tree - which in turn proves it is a SUBSET of the live tree, since the
    ///     second build repeats the first from the same config and only adds to it. That subset
    ///     relation is what licenses <see cref="Align" />: our tree is the client's with nodes
    ///     removed, never with nodes changed.
    /// </summary>
    private const uint FirstTreeHash = 0xea79cd36;

    /// <summary>
    ///     What the CLIENT's own UGameplayTagsManager holds, read straight out of the memory dump:
    ///     NetworkGameplayTagNodeIndex is a TArray of 13502 nodes sitting 16 bytes before the
    ///     NetworkGameplayTagNodeIndexHash field, and each node's NetIndex member is its own position
    ///     in it (checked at 0, 1, 2 and 13501). Printed alongside the derived count because a size
    ///     mismatch says WHICH WAY the tag set is wrong, which the hash alone never does.
    /// </summary>
    private const int ClientNodeCount = 13502;

    /// <summary>
    ///     The client's tag TREE, extracted from the memory dump by PriveDev/dumpwork/tagtree.py -
    ///     parent index, child count and depth per node, in net index order. Not the names: the
    ///     FName pool is encoded in shipping Fortnite, so the strings are unreadable while the
    ///     structure is not. TAGS_CLIENT_TREE overrides the path.
    /// </summary>
    private static string ClientTreePath =>
        Environment.GetEnvironmentVariable("TAGS_CLIENT_TREE") is { Length: > 0 } path
            ? path
            : @"C:\Users\user\Documents\PriveDev\clienttagtree.json";

    public static int Main(string[] args) {
        if (args.Length > 0 && args[0].Equals("fromlog", StringComparison.OrdinalIgnoreCase)) {
            return args.Length > 1 ? FromLog(args[1]) : Usage();
        }

        var paks = Environment.GetEnvironmentVariable("FORTNITE_PAKS") is { Length: > 0 } p ? p : DefaultPaks;
        var key = Environment.GetEnvironmentVariable("FORTNITE_AES_KEY");
        if (string.IsNullOrWhiteSpace(key)) {
            Console.Error.WriteLine("FORTNITE_AES_KEY is not set - nothing can be decrypted without it.");
            return 2;
        }

        var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE4_23));
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(key));
        provider.PostMount();
        Console.Error.WriteLine($"gameplaytags: mounted {provider.Files.Count} files from {paks}");

        // The node map is keyed the way FName is - case-insensitively - so a tag that differs from
        // another only in case is the SAME node, and the casing that survives is whichever was seen
        // first. Casing never reaches the wire (the hash lowercases and the sort ignores case), so
        // this only has to be consistent, not authoritative.
        var nodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tables = new List<string>();

        var fromIni = 0;

        // The DEFAULT list, then the EXTRA per-feature lists. ConstructGameplayTagTree reads
        // ProjectConfigDir()/Tags/*.ini after the default one (GameplayTagsManager.cpp:222) and feeds
        // them into the same node map, and 10.40 keeps five real files there - AthenaQuestTags,
        // CreativeTags, GamepadActionNameTags, LootTags, MessageChannelTags. Missing them was worth
        // 1299 nodes: the first derived table came out 12203 long against the client's own 13502.
        var iniFiles = new List<string> { TagsIni };
        iniFiles.AddRange(provider.Files.Keys
            .Where(f => f.StartsWith(ExtraTagsDir, StringComparison.OrdinalIgnoreCase) &&
                        f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal));

        Console.Error.WriteLine($"gameplaytags: {iniFiles.Count} tag ini file(s)");

        foreach (var raw in iniFiles.SelectMany(
                     f => System.Text.Encoding.UTF8.GetString(provider.SaveAsset(f)).Split('\n'))) {
            // The '+' is the ini ARRAY-APPEND marker, and only the default file uses it: the five
            // Config/Tags files write a bare `GameplayTagList=` because each is loaded on its own into
            // a fresh UGameplayTagsList (LoadConfig, GameplayTagsManager.cpp:265) rather than merged
            // into one shared array. Dropping it here reads both forms; requiring it read none of the
            // 1299 nodes those files hold, while still reporting "6 tag ini file(s)" - which is
            // exactly the shape of a bug a count alone would never show.
            var line = raw.Trim().TrimStart('+');

            if (line.StartsWith("GameplayTagList=", StringComparison.OrdinalIgnoreCase)) {
                if (Between(line, "Tag=\"", "\"") is { Length: > 0 } tag) { AddWithParents(nodes, tag); fromIni++; }
            } else if (line.StartsWith("GameplayTagTableList=", StringComparison.OrdinalIgnoreCase)) {
                tables.Add(line["GameplayTagTableList=".Length..].Trim());
            } else if (line.StartsWith("GameplayTags=", StringComparison.OrdinalIgnoreCase)) {
                // The deprecated flat form. ConstructGameplayTagTree still folds it in (line 195), so
                // this reads it too even though 10.40's ini happens not to use it.
                AddWithParents(nodes, line["GameplayTags=".Length..].Trim().Trim('"'));
                fromIni++;
            } else if (line.StartsWith("CommonlyReplicatedTags=", StringComparison.OrdinalIgnoreCase)) {
                // Would need the reorder loop in ConstructNetIndex. 10.40 has none; shout if that changes.
                Console.Error.WriteLine("gameplaytags: CommonlyReplicatedTags is set and this tool does NOT " +
                                        "implement the reorder - every index below would be wrong.");
                return 1;
            }
        }

        var beforeTables = nodes.Count;
        var fromTables = 0;

        foreach (var table in tables) {
            // "/Game/Balance/GameplayTags/WeaponTags.WeaponTags" -> the package path, dropping the
            // object name after the dot. /Game/ is FortniteGame/Content/ in a cooked build.
            var path = table.Split('.')[0];
            if (path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) {
                path = "FortniteGame/Content/" + path["/Game/".Length..];
            }

            if (!provider.TryLoadPackage(path, out var package)) {
                Console.Error.WriteLine($"gameplaytags: MISSING tag table {path} - the hash cannot match without it.");
                continue;
            }

            var rows = 0;
            foreach (var export in package.GetExports()) {
                if (export is not UDataTable data) continue;
                foreach (var row in data.RowMap.Values) {
                    var tag = row.GetOrDefault<FName>("Tag").Text;
                    // A default FName reads as the STRING "None", not as null - the same trap that
                    // silently dropped every character head in Tools/CosmeticLoadout.
                    if (string.IsNullOrEmpty(tag) || tag.Equals("None", StringComparison.Ordinal)) continue;
                    AddWithParents(nodes, tag);
                    rows++;
                }
            }

            fromTables += rows;
        }

        Console.Error.WriteLine($"gameplaytags: {fromIni} ini tags + {fromTables} table rows from {tables.Count} " +
                                $"tables -> {nodes.Count} nodes ({nodes.Count - beforeTables} of them table-only)");

        var ordered = nodes.Values.ToArray();
        Array.Sort(ordered, CompareLikeFName);

        uint hash = 0;
        foreach (var name in ordered) hash = StrCrc32(name.ToLowerInvariant(), hash);

        Console.Error.WriteLine($"gameplaytags: first-tree hash is {hash:x} (client logs {FirstTreeHash:x}), " +
                                $"{ordered.Length} nodes (the client's LIVE tree holds {ClientNodeCount})");

        if (Environment.GetEnvironmentVariable("TAGS_DUMP") is { Length: > 0 } dump) {
            File.WriteAllLines(dump, ordered);
            Console.Error.WriteLine($"gameplaytags: wrote the {ordered.Length} names to {dump}");
        }

        if (hash != FirstTreeHash) {
            Console.Error.WriteLine("gameplaytags: HASH MISMATCH against the client's own first tree - the node set " +
                                    "or its order is wrong, so nothing below it can be trusted. Nothing written.");
            return 1;
        }

        return Align(ordered);
    }

    private static int Usage() {
        Console.Error.WriteLine("usage: gameplaytags                 derive the table from the paks (needs FORTNITE_AES_KEY)");
        Console.Error.WriteLine("       gameplaytags fromlog <log>   read it out of a client log - see the class comment");
        return 2;
    }

    /// <summary>
    ///     The table straight from the CLIENT'S OWN MOUTH, which is the route that finishes the job.
    ///
    ///     ConstructNetIndex logs every single assignment - "Assigning NetIndex (%d) to Tag (%s)" -
    ///     behind one console variable (GameplayTagsManager.cpp:437, `GameplayTags.PrintNetIndiceAssignment`).
    ///     Turn it on and the client writes its whole tag table, in order, to FortniteGame.log:
    ///
    ///         [ConsoleVariables]
    ///         GameplayTags.PrintNetIndiceAssignment=1
    ///
    ///     WHY THIS IS NEEDED AT ALL, when the pak route reproduces the tree exactly. It reproduces
    ///     the FIRST of the two trees the client builds. UGameplayTagsManager::DoneAddingNativeTags
    ///     runs at PostEngineInit, lets every module add NATIVE tags through
    ///     `AddNativeGameplayTag` - C++ string literals, present in no pak and no config file - and
    ///     then throws the tree away and rebuilds it (GameplayTagsManager.cpp:1738). That second
    ///     tree is the one the wire uses. The two are visible as the two different hashes the client
    ///     logs at startup, and the pak route matches the first (0xea79cd36) exactly while being 480
    ///     nodes short of the second (0x79b9274c). Those 480 are the native tags.
    ///
    ///     The log is still CHECKED, not trusted: same CRC, same node count. A log from a different
    ///     build, or one truncated mid-table, fails here rather than in a match.
    /// </summary>
    private static int FromLog(string path) {
        if (!File.Exists(path)) {
            Console.Error.WriteLine($"gameplaytags: no such log: {path}");
            return 2;
        }

        // "LogGameplayTags: Assigning NetIndex (1234) to Tag (Some.Tag.Name)"
        var line = new System.Text.RegularExpressions.Regex(
            @"Assigning NetIndex \((\d+)\) to Tag \((.+)\)\s*$");

        var byIndex = new SortedDictionary<int, string>();
        var duplicates = 0;

        foreach (var text in File.ReadLines(path)) {
            var m = line.Match(text);
            if (!m.Success) continue;

            var index = int.Parse(m.Groups[1].Value);
            var name = m.Groups[2].Value;

            // A log can hold the assignment TWICE - the client builds the tree twice, and if the
            // cvar was set early enough both passes are in there. The second pass is the live one,
            // so a later line wins.
            if (byIndex.ContainsKey(index)) duplicates++;
            byIndex[index] = name;
        }

        if (byIndex.Count == 0) {
            Console.Error.WriteLine("gameplaytags: not one 'Assigning NetIndex' line in that log - the cvar " +
                                    "GameplayTags.PrintNetIndiceAssignment was not set when the client started.");
            return 1;
        }

        if (byIndex.Keys.First() != 0 || byIndex.Keys.Last() != byIndex.Count - 1) {
            Console.Error.WriteLine($"gameplaytags: the log's indices are not 0..{byIndex.Count - 1} " +
                                    $"(first {byIndex.Keys.First()}, last {byIndex.Keys.Last()}) - it is truncated.");
            return 1;
        }

        var ordered = byIndex.Values.ToArray();
        Console.Error.WriteLine($"gameplaytags: {ordered.Length} assignments from {path}" +
                                (duplicates > 0 ? $" ({duplicates} re-assigned by a second tree build - later wins)" : ""));

        uint hash = 0;
        foreach (var name in ordered) hash = StrCrc32(name.ToLowerInvariant(), hash);

        Console.Error.WriteLine($"gameplaytags: hash {hash:x} (client logs {LiveTreeHash:x}), " +
                                $"{ordered.Length} nodes (client holds {ClientNodeCount})");

        if (hash != LiveTreeHash) {
            Console.Error.WriteLine("gameplaytags: HASH MISMATCH - this log is not from the build this server " +
                                    "talks to, or the table in it is incomplete. Nothing written.");
            return 1;
        }

        // Straight through: in the log EVERY index is known, so nothing needs deriving and the
        // whole table is emitted rather than the ~91% the structural alignment can force.
        var pinned = new List<(string Name, int Index)>();
        for (var i = 0; i < ordered.Length; i++) pinned.Add((ordered[i], i));

        Emit(pinned, $"read from the client's own log, {Path.GetFileName(path)}");
        return 0;
    }

    /// <summary>
    ///     Turn "our 13022 names, in order" plus "the client's 13502-slot tree shape" into REAL NET
    ///     INDICES, for every name whose slot is forced.
    ///
    ///     THE IDEA. The sorted node array is a trie PREORDER, not just a sorted list: '.' is 0x2E,
    ///     below every alphanumeric, so "A.B.C" always sorts before "A.BC" and a node's whole subtree
    ///     is a CONTIGUOUS run. That means the client's array, read through the parent pointers in
    ///     the dump, gives an exact subtree SIZE for every slot - and our own tree gives a subtree
    ///     size for every name.
    ///
    ///     Our tree is the client's with the native tags removed (see <see cref="FirstTreeHash" />
    ///     for why that is proven rather than assumed), so under any given parent our children are a
    ///     SUBSEQUENCE of the client's children, order-preserving because both are sorted the same
    ///     way, and a match is only possible where the client's subtree is at least as big as ours.
    ///     Enumerating every such subsequence would be exponential; instead this asks, per child, the
    ///     only question that matters: is there more than one slot it could take in ANY valid
    ///     assignment? Two linear passes answer that - a left pass for "can the children before me
    ///     fit before slot p" and a right pass for "can the children after me fit after it" - and a
    ///     child with exactly one surviving slot is PINNED: every valid alignment agrees on it, so
    ///     the index is derived, not guessed.
    ///
    ///     Where several siblings are interchangeable (a row of leaves with an extra native tag
    ///     somewhere among them) nothing is pinned, and this emits nothing for them rather than a
    ///     plausible-looking guess. That is ~9% of the table; `fromlog` fills in the rest.
    /// </summary>
    private static int Align(string[] ours) {
        if (!File.Exists(ClientTreePath)) {
            Console.Error.WriteLine($"gameplaytags: no client tree at {ClientTreePath} - run " +
                                    "PriveDev/dumpwork/tagtree.py to extract it from the memory dump, or use " +
                                    "`gameplaytags fromlog <log>` which needs neither. Nothing written.");
            return 1;
        }

        var client = System.Text.Json.JsonSerializer.Deserialize<ClientNode[]>(File.ReadAllText(ClientTreePath))!;
        Console.Error.WriteLine($"gameplaytags: client tree {ClientTreePath}: {client.Length} nodes");

        if (client.Length != ClientNodeCount) {
            Console.Error.WriteLine($"gameplaytags: that tree has {client.Length} nodes, not {ClientNodeCount} - it is " +
                                    "from a different build than the one this tool's constants describe.");
            return 1;
        }

        // Children of each client slot, already in net index (= alphabetical) order.
        var clientKids = new Dictionary<int, List<int>>();
        foreach (var n in client) {
            if (!clientKids.TryGetValue(n.parent, out var list)) clientKids[n.parent] = list = new List<int>();
            list.Add(n.i);
        }

        // Subtree sizes, bottom up - the array is a preorder, so a child always has a higher index.
        var clientSize = new int[client.Length];
        for (var i = client.Length - 1; i >= 0; i--) {
            clientSize[i] = 1;
            if (clientKids.TryGetValue(i, out var kids)) {
                foreach (var c in kids) clientSize[i] += clientSize[c];
            }
        }

        // The same two things for our own tree, keyed by name.
        var ourKids = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var ourRoots = new List<string>();
        foreach (var name in ours) {
            var dot = name.LastIndexOf('.');
            if (dot < 0) { ourRoots.Add(name); continue; }
            var parent = name[..dot];
            if (!ourKids.TryGetValue(parent, out var list)) ourKids[parent] = list = new List<string>();
            list.Add(name);
        }

        var ourSize = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = ours.Length - 1; i >= 0; i--) {
            var size = 1;
            if (ourKids.TryGetValue(ours[i], out var kids)) {
                foreach (var c in kids) size += ourSize[c];
            }

            ourSize[ours[i]] = size;
        }

        var pinned = new List<(string Name, int Index)>();
        var ambiguous = 0;

        void Walk(int clientParent, List<string> mine) {
            if (mine.Count == 0) return;
            var slots = clientKids.TryGetValue(clientParent, out var cs) ? cs : new List<int>();

            var options = Slots(slots, mine, clientSize, ourSize);
            for (var j = 0; j < mine.Count; j++) {
                if (options[j].Count != 1) { ambiguous++; continue; }

                var index = slots[options[j][0]];
                pinned.Add((mine[j], index));
                if (ourKids.TryGetValue(mine[j], out var kids)) Walk(index, kids);
            }
        }

        Walk(-1, ourRoots);

        Console.Error.WriteLine($"gameplaytags: pinned {pinned.Count} of {ours.Length} names to exact net indices " +
                                $"({ambiguous} could sit in more than one slot; their subtrees are skipped too)");

        pinned.Sort((a, b) => a.Index.CompareTo(b.Index));
        Emit(pinned, "derived: the paks give the tree, the memory dump gives the client's slots");
        return 0;
    }

    /// <summary>
    ///     Per child, every slot it could occupy in SOME valid order-preserving assignment. One entry
    ///     means the assignment is forced there. See <see cref="Align" /> for why two linear passes
    ///     answer this without enumerating assignments.
    /// </summary>
    private static List<int>[] Slots(List<int> slots, List<string> mine,
                                     int[] clientSize, Dictionary<string, int> ourSize) {
        int n = slots.Count, m = mine.Count;
        var fits = new bool[m][];
        for (var j = 0; j < m; j++) {
            fits[j] = new bool[n];
            for (var p = 0; p < n; p++) fits[j][p] = clientSize[slots[p]] >= ourSize[mine[j]];
        }

        var left = new bool[m][];   // left[j][p]: children 0..j placeable with j at p
        var right = new bool[m][];  // right[j][p]: children j..m-1 placeable with j at p
        for (var j = 0; j < m; j++) { left[j] = new bool[n]; right[j] = new bool[n]; }

        for (var p = 0; p < n; p++) left[0][p] = fits[0][p];
        for (var j = 1; j < m; j++) {
            var any = false;
            for (var p = 0; p < n; p++) {
                if (p > 0) any |= left[j - 1][p - 1];
                left[j][p] = fits[j][p] && any;
            }
        }

        for (var p = 0; p < n; p++) right[m - 1][p] = fits[m - 1][p];
        for (var j = m - 2; j >= 0; j--) {
            var any = false;
            for (var p = n - 1; p >= 0; p--) {
                if (p < n - 1) any |= right[j + 1][p + 1];
                right[j][p] = fits[j][p] && any;
            }
        }

        var options = new List<int>[m];
        for (var j = 0; j < m; j++) {
            options[j] = new List<int>();
            for (var p = 0; p < n; p++) {
                if (left[j][p] && right[j][p]) options[j].Add(p);
            }
        }

        return options;
    }

    /// <summary>One node of the client tree as PriveDev/dumpwork/tagtree.py writes it.</summary>
    private sealed class ClientNode {
        public int i { get; set; }
        public int parent { get; set; }
        public int nchild { get; set; }
        public int depth { get; set; }
    }
    /// <summary>
    ///     Adds a tag AND every ancestor prefix, because that is what a node is.
    ///     UGameplayTagsManager::InsertTagIntoNodeArray walks "A.B.C" one segment at a time and
    ///     creates a node for each level, so "A" and "A.B" are real, indexable tags even when nothing
    ///     ever declares them. Forgetting this loses a third of the table.
    /// </summary>
    private static void AddWithParents(Dictionary<string, string> nodes, string tag) {
        for (var dot = tag.IndexOf('.'); ; dot = tag.IndexOf('.', dot + 1)) {
            var prefix = dot < 0 ? tag : tag[..dot];
            if (!nodes.ContainsKey(prefix)) nodes[prefix] = prefix;
            if (dot < 0) return;
        }
    }

    /// <summary>
    ///     FName's own ordering, which is NOT StringComparer.OrdinalIgnoreCase.
    ///
    ///     CompareDifferentIdsAlphabetically (UnrealNames.cpp:920) is Strnicmp over the shorter
    ///     length, then the length difference - and Strnicmp compares LOWERCASED characters, while
    ///     .NET's OrdinalIgnoreCase compares UPPERCASED ones. Every character between 'Z' and 'a'
    ///     flips: '_' (0x5F) sorts AFTER 'B' (0x42) when uppercasing but BEFORE 'b' (0x62) when
    ///     lowercasing. Gameplay tag names are full of underscores ("Athena.Emote_Dance"), so using
    ///     the .NET comparer here would misorder a large chunk of the table - and the hash is what
    ///     would catch it.
    /// </summary>
    private static int CompareLikeFName(string a, string b) {
        var min = Math.Min(a.Length, b.Length);
        for (var i = 0; i < min; i++) {
            int ca = char.ToLowerInvariant(a[i]), cb = char.ToLowerInvariant(b[i]);
            if (ca != cb) return ca - cb;
        }

        return a.Length - b.Length;
    }

    /// <summary>
    ///     FCrc::StrCrc32 (Crc.h:35) - a standard reflected CRC-32 (poly 0xEDB88320), but fed FOUR
    ///     bytes per character rather than the string's bytes: the engine deliberately treats every
    ///     char as 32-bit "so equivalent strings with different character types agree". For a 16-bit
    ///     TCHAR that means low byte, high byte, 0, 0.
    /// </summary>
    private static uint StrCrc32(string s, uint crc) {
        crc = ~crc;
        foreach (var c in s) {
            uint ch = c;
            for (var b = 0; b < 4; b++) {
                crc = (crc >> 8) ^ Table[(crc ^ ch) & 0xFF];
                ch >>= 8;
            }
        }

        return ~crc;
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable() {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++) {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        return table;
    }

    private static string? Between(string s, string open, string close) {
        var start = s.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        var end = s.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : s[start..end];
    }

    private static void Emit(List<(string Name, int Index)> pinned, string provenance) {
        var target = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "AFortOnlineBeacon", "Net", "Abilities", "FortGameplayTags.Generated.cs"));

        using var w = new StreamWriter(target, false, new System.Text.UTF8Encoding(true));

        w.WriteLine("// <auto-generated> Tools/GameplayTags - do not edit by hand. </auto-generated>");
        w.WriteLine("//");
        w.WriteLine("// A gameplay tag's NET INDEX: what FGameplayTag::NetSerialize actually puts on the wire, as a");
        w.WriteLine($"// flat {NetIndexBits}-bit field. The index is the tag's position in the client's sorted node table, which");
        w.WriteLine("// both sides build from their own config and never exchange - so it is derived, never negotiated.");
        w.WriteLine("//");
        w.WriteLine($"// {provenance}.");
        w.WriteLine($"// {pinned.Count} tag(s) of the client's {ClientNodeCount}; anything absent here has no derivable");
        w.WriteLine("// index and must not be sent - see FortGameplayTags.IndexOf.");
        w.WriteLine();
        w.WriteLine("namespace AFortOnlineBeacon.Net.Abilities;");
        w.WriteLine();
        w.WriteLine("public static partial class FortGameplayTags {");
        w.WriteLine("    /// <summary>NetIndexTrueBitNum = CeilToInt(Log2(NodeCount + 1)) - a tag's width on the wire.</summary>");
        w.WriteLine($"    public const int NetIndexBits = {NetIndexBits};");
        w.WriteLine();
        w.WriteLine("    /// <summary>UGameplayTagsManager::InvalidTagNetIndex = NodeCount + 1: the EMPTY tag.</summary>");
        w.WriteLine($"    public const uint InvalidNetIndex = {ClientNodeCount + 1};");
        w.WriteLine();
        w.WriteLine("    /// <summary>The client's whole node count, for the record.</summary>");
        w.WriteLine($"    public const int NodeCount = {ClientNodeCount};");
        w.WriteLine();
        w.WriteLine("    private static readonly (string Name, ushort Index)[] Table = {");

        foreach (var (name, index) in pinned) w.WriteLine($"        (\"{name}\", {index}),");

        w.WriteLine("    };");
        w.WriteLine("}");

        Console.Error.WriteLine($"gameplaytags: wrote {pinned.Count} tag(s) to {target}");
    }

    /// <summary>Log2(13503) rounded up. Constant because ClientNodeCount is.</summary>
    private static readonly int NetIndexBits = (int) Math.Ceiling(Math.Log2(ClientNodeCount + 1));
}
