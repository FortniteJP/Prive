using System.Text.RegularExpressions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;

// Builds the table that turns "what the client says it hit" into "what that is worth".
//
// A reported hit names an object PATH - ".../Sublevel_X2Y3.PersistentLevel.Tree_Pine_169" - and
// nothing else. That instance name is AUTHORED, not derived: the class Athena_Tree_Pine_3_C shows up
// in Athena's maps as Tree_Pine_N, Tree_Medium_N, Tree_N and Athena_Tree_Pine_N, with no rule
// connecting any of them back to the class. An external server cannot ask the running game what it
// just hit, so the join has to be made from the shipped maps, once, offline:
//
//     pass 1   every sublevel .umap        ->  (instance name stem, actor class)
//     pass 2   every class named by pass 1 ->  its CDO's ResourceType and its
//                                              BuildingResourceAmountOverride.RowName
//
// TWO properties, because they answer two different questions and reading only one gets it wrong.
// The row name looks like it says everything - "ResourceWoodMedium" - but it is the AMOUNT curve,
// and the resource comes from ResourceType. Checking the two against each other across all 2147
// classes settled it: they DISAGREE on 1006 of the 1958 that carry both. Tiered_Chest_Oak_Athena_C
// is row=PropWoodHigh, ResourceType=Metal; an oak chest pays out metal. And the largest group of
// all, the LDBuilding* rows that name no resource whatsoever, are Athena's prefab walls and floors -
// they carry an explicit Stone or Metal or Wood and would have been written off as worthless.
//
// So: ResourceType decides WHAT, the row name's tier decides HOW MUCH. Where a class does not
// override ResourceType (189 of them) the row name's own family is the fallback, which is exactly
// the tree case - ResourceWoodLow with the engine default of Wood.
//
// Stems are not unique across classes, but the collisions are benign: every class sharing the stem
// "Tree_Pine" is a tree and every class sharing "Prop_Rocks" is a rock, so they agree on the
// resource even where they differ on the amount tier. Stems whose classes genuinely disagree are
// printed rather than silently resolved.

if (args.Length < 2) {
    Console.WriteLine("Usage: HarvestTable <PaksDirectory> <AesKeyHex> [MapPrefix] [OutputCsPath] [EGame]");
    Console.WriteLine();
    Console.WriteLine("  MapPrefix     default FortniteGame/Content/Athena/Maps/");
    Console.WriteLine("  OutputCsPath  default AFortOnlineBeacon/Net/Actors/FortHarvestResources.Generated.cs");
    return 1;
}

var paksDir = args[0];
var aesKey = args[1];
var mapPrefix = args.Length > 2 ? args[2] : "FortniteGame/Content/Athena/Maps/";
var outPath = args.Length > 3 ? args[3] : "AFortOnlineBeacon/Net/Actors/FortHarvestResources.Generated.cs";
var gameVersion = args.Length > 4 ? Enum.Parse<EGame>(args[4]) : EGame.GAME_UE4_23;

if (!Directory.Exists(paksDir)) {
    Console.WriteLine($"PAK directory not found: {paksDir}");
    return 1;
}

var provider = new DefaultFileProvider(paksDir, SearchOption.TopDirectoryOnly, new VersionContainer(gameVersion));
provider.Initialize();

foreach (var vfs in provider.UnloadedVfs.ToList()) {
    provider.SubmitKey(vfs.EncryptionKeyGuid, new FAesKey(aesKey));
}

Console.WriteLine($"Mounted files: {provider.Files.Count}");

// ---------------------------------------------------------------- pass 1: maps -> stem -> classes

var umaps = provider.Files.Keys
    .Where(f => f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)
             && f.StartsWith(mapPrefix, StringComparison.OrdinalIgnoreCase))
    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
    .ToList();

Console.WriteLine($"pass 1: scanning {umaps.Count} .umap under '{mapPrefix}'");

var stemClasses = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
var scanned = 0;

foreach (var path in umaps) {
    List<UObject> exports;
    try {
        exports = provider.LoadPackageObjects(path).ToList();
    } catch {
        continue;
    }

    foreach (var export in exports) {
        // Blueprint actor classes only. Components and plain engine actors carry no harvest data,
        // and every resource-bearing prop in Athena is a Blueprint.
        if (!export.ExportType.EndsWith("_C", StringComparison.Ordinal)) continue;

        var stem = Stem(export.Name);
        if (stem.Length == 0) continue;

        if (!stemClasses.TryGetValue(stem, out var set)) {
            stemClasses[stem] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        set.Add(export.ExportType);
    }

    if (++scanned % 50 == 0) Console.WriteLine($"  {scanned}/{umaps.Count} maps, {stemClasses.Count} stems so far");
}

var allClasses = stemClasses.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
Console.WriteLine($"pass 1 done: {stemClasses.Count} stems over {allClasses.Count} distinct classes");

// ------------------------------------------------------- pass 2: class CDO -> resource row name

// A Blueprint's generated class "X_C" lives in the asset "X.uasset". Indexing every mounted asset by
// basename finds it without depending on how a map's import table spells the package path.
var byBaseName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

foreach (var f in provider.Files.Keys) {
    if (!f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;

    var name = Path.GetFileNameWithoutExtension(f);
    if (!byBaseName.TryGetValue(name, out var list)) byBaseName[name] = list = new List<string>();
    list.Add(f);
}

Console.WriteLine($"pass 2: reading {allClasses.Count} class CDOs");

var classRow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var classResource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var classTier = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var inferredFromRow = 0;
var noResource = 0;
var notFound = 0;
var done = 0;

foreach (var className in allClasses) {
    if (++done % 250 == 0) Console.WriteLine($"  {done}/{allClasses.Count} classes, {classRow.Count} carry a resource row");

    var assetName = className[..^2]; // strip the "_C"
    if (!byBaseName.TryGetValue(assetName, out var candidates)) {
        notFound++;
        continue;
    }

    foreach (var candidate in candidates) {
        List<UObject> exports;
        try {
            exports = provider.LoadPackageObjects(candidate).ToList();
        } catch {
            continue;
        }

        var cdo = exports.FirstOrDefault(e =>
            e.Name.Equals($"Default__{className}", StringComparison.OrdinalIgnoreCase));

        if (cdo == null) continue;

        // FCurveTableRowHandle. Only the RowName matters - the table it indexes is a project default
        // that never varies per prop.
        var handle = cdo.GetOrDefault<FStructFallback?>("BuildingResourceAmountOverride", null);
        var rowName = handle?.GetOrDefault<FName>("RowName").Text;

        if (!string.IsNullOrEmpty(rowName) && !rowName.Equals("None", StringComparison.Ordinal)) {
            classRow[className] = rowName;

            // ABuildingActor::ResourceType - the actual resource. A cooked asset serialises only
            // overrides, so an absent value means the class default; the row name's own family is
            // the best evidence of what that default is, and for the classes that omit it the row
            // always names one (trees: ResourceWoodLow, with the engine default of Wood).
            var resourceType = cdo.GetOrDefault<FName>("ResourceType").Text;
            var resource = FamilyOfResourceType(resourceType);

            if (resource == "None") {
                resource = Family(rowName);
                if (resource != "None") inferredFromRow++;
            }

            if (resource != "None") {
                classResource[className] = resource;
                classTier[className] = Tier(rowName);
            } else {
                noResource++;
            }
        }

        break;
    }
}

Console.WriteLine($"pass 2 done: {classResource.Count} classes yield a resource, {notFound} class assets not found");
Console.WriteLine($"  {classResource.Count - inferredFromRow} took it from an explicit ResourceType, " +
                  $"{inferredFromRow} fell back to the row name's own family");
Console.WriteLine($"  {noResource} classes carry a resource row but name no resource at all");

var stemRow = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var tierConflicts = 0;
var typeConflicts = new List<string>();

foreach (var (stem, classes) in stemClasses) {
    var known = classes.Where(c => classResource.ContainsKey(c)).ToList();
    if (known.Count == 0) continue;

    // Count votes rather than taking whichever entry a hash set yields first: HashSet order is not
    // stable between runs, and picking the first silently made "Car_Pickup" - a truck - give wood.
    var resourceVotes = known
        .GroupBy(c => classResource[c], StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    if (resourceVotes.Count > 1) {
        typeConflicts.Add($"{stem}: {string.Join(", ", resourceVotes.OrderByDescending(v => v.Value).Select(v => $"{v.Key} x{v.Value}"))}");
    }

    // Most classes wins; ties break alphabetically so the generated file is byte-stable.
    var resource = resourceVotes
        .OrderByDescending(v => v.Value)
        .ThenBy(v => v.Key, StringComparer.Ordinal)
        .First().Key;

    var tierVotes = known
        .Where(c => classResource[c].Equals(resource, StringComparison.OrdinalIgnoreCase))
        .GroupBy(c => classTier[c], StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    if (tierVotes.Count > 1) tierConflicts++;

    var tier = tierVotes
        .OrderByDescending(v => v.Value)
        .ThenBy(v => v.Key, StringComparer.Ordinal)
        .First().Key;

    // The RAW row name too, not just its tier bucket - Tier() only keeps the LAST word
    // ("ResourceWoodLow" -> "Low"), throwing away which curve row actually decided the amount. The
    // curve table baked below is keyed by the full row name, so this is what makes a real lookup
    // possible instead of only the coarse tier bucket.
    var rowVotes = known
        .Where(c => classResource[c].Equals(resource, StringComparison.OrdinalIgnoreCase))
        .GroupBy(c => classRow[c], StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    var rowName = rowVotes
        .OrderByDescending(v => v.Value)
        .ThenBy(v => v.Key, StringComparer.Ordinal)
        .First().Key;

    stemRow[stem] = $"{resource}|{tier}|{rowName}";
}

Console.WriteLine($"{stemRow.Count} stems resolve to a resource");
Console.WriteLine($"  {tierConflicts} stems disagreed only on the amount tier (harmless - same resource)");
Console.WriteLine($"  {typeConflicts.Count} stems disagreed on the RESOURCE ITSELF (one of them is wrong):");
foreach (var c in typeConflicts.Take(25)) Console.WriteLine($"    {c}");

var rowCounts = stemRow.Values
    .GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
    .OrderByDescending(g => g.Count());

Console.WriteLine("resource|tier distribution:");
foreach (var g in rowCounts) Console.WriteLine($"  {g.Count(),5}  {g.Key}");

// --------------------------------------------------------- pass 3: the curve table's REAL amounts
//
// Erbium (one of PriveDev's reimplemented/injected 10.40-era servers, BuildingSMActor.cpp's
// OnDamageServer hook) shows the real formula: a hit's resource count is
// CurveTable.FindCurve(RowName).Eval(0) / (Actor.MaxHealth / Damage) - the curve's value at time 0
// is a full-break total, scaled down by how much of the piece's health this one hit actually took.
// This project has no real per-class MaxHealth yet (ABuildingActor.BaseHitPointsFor is still a
// placeholder - see there), so the division can't be reproduced faithfully; what CAN be baked
// exactly is the curve's own Eval(0) per row, which is real data and gives correct RELATIVE
// proportions between rows even before the HP side is solved. FortHarvestResources.RowAmounts
// is consulted first and TierAmounts is kept as a fallback for any row this table does not cover.
const string resourceRatesPath = "FortniteGame/Content/Balance/DataTables/ResourceRates.uasset";
var rowAmounts = new SortedDictionary<string, float>(StringComparer.OrdinalIgnoreCase);

try {
    var curveTable = provider.LoadPackage(resourceRatesPath).GetExports().OfType<UCurveTable>().FirstOrDefault();
    if (curveTable == null) {
        Console.WriteLine($"pass 3: '{resourceRatesPath}' has no UCurveTable export - RowAmounts will be empty");
    } else {
        foreach (var rowName in curveTable.RowMap.Keys) {
            if (!curveTable.TryFindCurve(rowName, out var curve, bWarnIfNotFound: false) || curve == null) continue;
            rowAmounts[rowName.Text] = curve.Eval(0f);
        }

        Console.WriteLine($"pass 3: baked {rowAmounts.Count} row(s) from '{resourceRatesPath}'");
    }
} catch (Exception ex) {
    Console.WriteLine($"pass 3: failed to read '{resourceRatesPath}' - {ex.GetType().Name}: {ex.Message}");
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

using (var writer = new StreamWriter(outPath)) {
    writer.WriteLine("// <auto-generated>");
    writer.WriteLine("//     Generated by Tools/HarvestTable from the 10.40 paks. Do not edit by hand.");
    writer.WriteLine($"//     {stemRow.Count} placed-actor name stems -> \"<resource>|<amount tier>|<curve row name>\",");
    writer.WriteLine($"//     read off {classResource.Count} Blueprint CDOs found across {umaps.Count} Athena sublevels,");
    writer.WriteLine($"//     plus {rowAmounts.Count} row -> real Eval(0) amount from ResourceRates.uasset (see FortHarvestResources.RowAmounts' doc comment for what this number does and does not mean).");
    writer.WriteLine("// </auto-generated>");
    writer.WriteLine();
    writer.WriteLine("namespace AFortOnlineBeacon.Net.Actors;");
    writer.WriteLine();
    writer.WriteLine("internal static partial class FortHarvestResources {");
    writer.WriteLine("    private static readonly Dictionary<string, string> StemYields = new(StringComparer.OrdinalIgnoreCase) {");

    foreach (var (stem, row) in stemRow) writer.WriteLine($"        [\"{stem}\"] = \"{row}\",");

    writer.WriteLine("    };");
    writer.WriteLine();
    writer.WriteLine("    private static readonly Dictionary<string, float> RowAmounts = new(StringComparer.OrdinalIgnoreCase) {");

    foreach (var (rowName, amount) in rowAmounts) {
        writer.WriteLine($"        [\"{rowName}\"] = {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}f,");
    }

    writer.WriteLine("    };");
    writer.WriteLine("}");
}

Console.WriteLine($"wrote {outPath}");
return 0;

// "Tree_Pine_169" -> "Tree_Pine". A placed actor's name is an authored base plus an instance number,
// and the number is the only part that differs between two copies of the same thing. Some names run
// the number straight on with no separator ("Car_Pickup10"), so both spellings are trimmed.
static string Stem(string name) => Regex.Replace(name, @"_?\d+$", "").TrimEnd('_');

// Which resource a row name names, ignoring its amount tier. Must agree with
// FortHarvestResources.ItemForRow on the server, which reads the same row names the same way.
static string Family(string rowName) {
    if (rowName.Contains("Wood", StringComparison.OrdinalIgnoreCase)) return "Wood";
    if (rowName.Contains("Stone", StringComparison.OrdinalIgnoreCase)) return "Stone";
    if (rowName.Contains("Metal", StringComparison.OrdinalIgnoreCase)) return "Metal";

    return "None"; // ResourceNone, LDBuildingNormal, LDBuildingThick - see FortHarvestResources
}

// EFortResourceType::Metal -> "Metal". Absent means the class did not override it.
static string FamilyOfResourceType(string? resourceType) {
    if (string.IsNullOrEmpty(resourceType)) return "None";
    if (resourceType.Contains("Wood", StringComparison.OrdinalIgnoreCase)) return "Wood";
    if (resourceType.Contains("Stone", StringComparison.OrdinalIgnoreCase)) return "Stone";
    if (resourceType.Contains("Metal", StringComparison.OrdinalIgnoreCase)) return "Metal";

    return "None";
}

// The amount tier a row name ends with. LDBuildingNormal and LDBuildingThick are the prefab
// building pieces and carry their own two tiers.
static string Tier(string rowName) {
    foreach (var tier in new[] { "VeryHigh", "High", "Medium", "Low", "Normal", "Thick" }) {
        if (rowName.EndsWith(tier, StringComparison.OrdinalIgnoreCase)) return tier;
    }

    return "Medium";
}
