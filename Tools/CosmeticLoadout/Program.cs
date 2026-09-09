using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;

// Emits FortCosmeticLoadout.Generated.cs - what the server has to REPLICATE to make a locker choice
// visible, resolved from the shipped 10.40 assets.
//
// The locker stores an id ("AthenaCharacter:CID_028_Athena_Commando_F"). Nothing on the wire takes an
// id: AFortPlayerState replicates CharacterParts[6] as six object references to CustomCharacterPart
// assets, and CosmeticLoadout takes the item definitions themselves. Getting from one to the other is
// a chain of soft references THROUGH THREE ASSETS, none of which a server outside the game can follow
// at runtime - so it is followed once, here:
//
//     CID_028_Athena_Commando_F  (UAthenaCharacterItemDefinition)
//       .HeroDefinition      -> HID_028_Athena_Commando_F        (UFortHeroType)
//       .Specializations[0]  -> HS_028_Athena_Commando_Athena_F  (UFortHeroSpecialization)
//       .CharacterParts[]    -> CP_028_Athena_Body, F_MED_ASN_Sarah_Head_02_ATH, Hat_F_Commando_08_V01
//
// and that last list is UNORDERED - which slot each part occupies is a property ON THE PART
// (CustomCharacterPart::CharacterPartType), so every part has to be opened too. A part list is not a
// slot list, and treating it as one puts a head in the body slot.
//
// The other three are one hop each:
//     BID_*      .CharacterParts[]  -> the backpack part
//     Pickaxe_*  .WeaponDefinition  -> the WID the harvesting tool actually is
//     Glider_*                      -> no hop; CosmeticLoadout.Glider IS the item definition
//
// Same bake-it pattern as Tools/HarvestTable and Tools/EmoteTable. The AES key and pak directory come
// in as arguments so neither ever lands in the repo.

if (args.Length < 2) {
    Console.WriteLine("Usage: CosmeticLoadout <PaksDirectory> <AesKeyHex> [OutputCsPath] [EGame]");
    return 1;
}

var paksDir = args[0];
var aesKey = args[1];
var outPath = args.Length > 2 ? args[2] : "AFortOnlineBeacon/Net/Actors/FortCosmeticLoadout.Generated.cs";
var gameVersion = args.Length > 3 ? Enum.Parse<EGame>(args[3]) : EGame.GAME_UE4_23;

if (!Directory.Exists(paksDir)) {
    Console.WriteLine($"PAK directory not found: {paksDir}");
    return 1;
}

var provider = new DefaultFileProvider(paksDir, SearchOption.TopDirectoryOnly, new VersionContainer(gameVersion));
provider.Initialize();
foreach (var vfs in provider.UnloadedVfs.ToList()) provider.SubmitKey(vfs.EncryptionKeyGuid, new FAesKey(aesKey));

Console.WriteLine($"Mounted files: {provider.Files.Count}");

const string cosmetics = "FortniteGame/Content/Athena/Items/Cosmetics/";

// ---------------------------------------------------------------- helpers

/// <summary>Every .uasset directly under one cosmetics folder, as (item name, game path).</summary>
List<(string Name, string Path)> Folder(string sub) => provider.Files.Keys
    .Where(f => f.StartsWith(cosmetics + sub, StringComparison.OrdinalIgnoreCase)
             && f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
    .Select(f => (Name: Path.GetFileNameWithoutExtension(f), Path: f))
    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
    .ToList();

/// <summary>The package's single top-level export - a cosmetic asset is one object plus its inners.</summary>
UObject? MainExport(string assetPath, string name) {
    try {
        return provider.LoadPackageObjects(assetPath)
            .FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    } catch {
        return null;
    }
}

/// <summary>"/Game/..." for an ObjectProperty, an FSoftObjectPath, or an entry of a soft array.</summary>
string? GamePath(object? value) {
    var text = value switch {
        null => null,
        FPackageIndex index => index.ResolvedObject?.GetPathName(),
        FSoftObjectPath soft => soft.AssetPathName.Text,
        _ => value.ToString()
    };

    if (string.IsNullOrEmpty(text) || text.Equals("None", StringComparison.Ordinal)) return null;

    // CUE4Parse hands back "FortniteGame/Content/X.Y" for a resolved import and "/Game/X.Y" for a
    // soft path. The client only ever sees /Game/, which is also what UAssetRegistry expects.
    if (text.StartsWith("FortniteGame/Content/", StringComparison.Ordinal)) {
        text = "/Game/" + text["FortniteGame/Content/".Length..];
    }

    // A resolved import has no ".Object" suffix; every path this server exports needs one.
    var lastSlash = text.LastIndexOf('/');
    if (text.IndexOf('.', lastSlash + 1) < 0) text += "." + text[(lastSlash + 1)..];

    return text.StartsWith("/Game/", StringComparison.Ordinal) ? text : null;
}

/// <summary>The soft-object array a hero specialization and a backpack both use for their parts.</summary>
List<string> SoftArray(UObject obj, string property) {
    var result = new List<string>();
    if (!obj.TryGetValue<object[]>(out var raw, property)) return result;

    foreach (var entry in raw) {
        if (GamePath(entry) is { } path) result.Add(path);
    }

    return result;
}

// EFortCustomPartType, and the reason every part asset has to be opened. Values from the SDK; the
// server indexes AFortPlayerState::CharacterParts with exactly these.
var slotNames = new[] { "Head", "Body", "Hat", "Backpack", "Charm", "Face" };

var partSlotCache = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

int? SlotOf(string partPath) {
    if (partSlotCache.TryGetValue(partPath, out var cached)) return cached;
    partSlotCache[partPath] = null;

    // "/Game/X/Y.Y" -> "FortniteGame/Content/X/Y.uasset"
    var dot = partPath.LastIndexOf('.');
    var package = dot < 0 ? partPath : partPath[..dot];
    var name = package[(package.LastIndexOf('/') + 1)..];
    var asset = "FortniteGame/Content/" + package["/Game/".Length..] + ".uasset";

    if (MainExport(asset, name) is not { } part) return null;

    var type = part.GetOrDefault<FName>("CharacterPartType").Text;

    // "None" AS WELL AS EMPTY, and that is not defensiveness - a default FName's Text is the STRING
    // "None", so an absent property reads as a type name that matches no slot and the part gets
    // dropped. A cooked asset omits default-valued properties and EFortCustomPartType's default is
    // Head(0), so the parts that leave it unset are precisely the HEADS: the first version of this
    // resolved every outfit's body and hat and no head at all.
    if (string.IsNullOrEmpty(type) || type.Equals("None", StringComparison.Ordinal)) {
        partSlotCache[partPath] = 0;
        return 0;
    }

    var bare = type.Contains("::", StringComparison.Ordinal) ? type[(type.IndexOf("::", StringComparison.Ordinal) + 2)..] : type;
    var slot = Array.FindIndex(slotNames, s => s.Equals(bare, StringComparison.OrdinalIgnoreCase));
    partSlotCache[partPath] = slot < 0 ? null : slot;
    return slot < 0 ? null : slot;
}

// ---------------------------------------------------------------- characters

var characters = new SortedDictionary<string, (string Item, string? Hero, string?[] Parts)>(StringComparer.OrdinalIgnoreCase);
var noHero = 0;
var noSpec = 0;

foreach (var (name, path) in Folder("Characters/")) {
    if (MainExport(path, name) is not { } cid) continue;

    var itemPath = GamePath($"/Game/Athena/Items/Cosmetics/Characters/{name}")!;
    var heroPath = GamePath(cid.GetOrDefault<FPackageIndex?>("HeroDefinition", null));
    var parts = new string?[slotNames.Length];

    if (heroPath == null) {
        noHero++;
    } else {
        var heroDot = heroPath.LastIndexOf('.');
        var heroPackage = heroPath[..heroDot];
        var heroName = heroPackage[(heroPackage.LastIndexOf('/') + 1)..];
        var heroAsset = "FortniteGame/Content/" + heroPackage["/Game/".Length..] + ".uasset";

        if (MainExport(heroAsset, heroName) is { } hero) {
            var specs = SoftArray(hero, "Specializations");
            if (specs.Count == 0) {
                noSpec++;
            } else {
                var specDot = specs[0].LastIndexOf('.');
                var specPackage = specs[0][..specDot];
                var specName = specPackage[(specPackage.LastIndexOf('/') + 1)..];
                var specAsset = "FortniteGame/Content/" + specPackage["/Game/".Length..] + ".uasset";

                if (MainExport(specAsset, specName) is { } spec) {
                    foreach (var part in SoftArray(spec, "CharacterParts")) {
                        if (SlotOf(part) is { } slot) parts[slot] = part;
                    }
                }
            }
        }
    }

    characters[name] = (itemPath, heroPath, parts);
}

Console.WriteLine($"characters: {characters.Count} " +
                  $"({characters.Count(c => c.Value.Parts[1] != null)} resolved a BODY part, " +
                  $"{noHero} name no HeroDefinition, {noSpec} name no Specializations)");

// ---------------------------------------------------------------- backpacks, pickaxes, gliders

var backpacks = new SortedDictionary<string, (string Item, string? Part)>(StringComparer.OrdinalIgnoreCase);

foreach (var (name, path) in Folder("Backpacks/")) {
    if (MainExport(path, name) is not { } bid) continue;

    string? part = null;
    foreach (var candidate in SoftArray(bid, "CharacterParts")) {
        // Take the one that really is a Backpack, not merely the first: a few back blings carry a
        // second part (a charm, or a head override) and slot 3 is the one the wire cares about.
        if (SlotOf(candidate) == 3) { part = candidate; break; }
        part ??= candidate;
    }

    backpacks[name] = (GamePath($"/Game/Athena/Items/Cosmetics/Backpacks/{name}")!, part);
}

var pickaxes = new SortedDictionary<string, (string Item, string? Weapon)>(StringComparer.OrdinalIgnoreCase);

foreach (var (name, path) in Folder("Pickaxes/")) {
    if (MainExport(path, name) is not { } pickaxe) continue;

    pickaxes[name] = (GamePath($"/Game/Athena/Items/Cosmetics/Pickaxes/{name}")!,
                      GamePath(pickaxe.GetOrDefault<FPackageIndex?>("WeaponDefinition", null)));
}

var gliders = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

foreach (var (name, _) in Folder("Gliders/")) {
    gliders[name] = GamePath($"/Game/Athena/Items/Cosmetics/Gliders/{name}")!;
}

Console.WriteLine($"backpacks: {backpacks.Count} ({backpacks.Count(b => b.Value.Part != null)} with a part), " +
                  $"pickaxes: {pickaxes.Count} ({pickaxes.Count(p => p.Value.Weapon != null)} with a weapon), " +
                  $"gliders: {gliders.Count}");

// ---------------------------------------------------------------- emit

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

static string Quote(string? value) => value == null ? "null" : $"\"{value}\"";

using (var writer = new StreamWriter(outPath)) {
    writer.WriteLine("// <auto-generated> Tools/CosmeticLoadout - do not edit by hand.");
    writer.WriteLine("//");
    writer.WriteLine("// What a locker choice has to become before it can go on the wire, resolved out of the 10.40");
    writer.WriteLine("// paks. See Tools/CosmeticLoadout/Program.cs for the three-asset chain behind a character, and");
    writer.WriteLine("// for why every character part has to be opened rather than taken in array order.");
    writer.WriteLine("//");
    writer.WriteLine($"// {characters.Count} characters, {backpacks.Count} backpacks, {pickaxes.Count} pickaxes, {gliders.Count} gliders.");
    writer.WriteLine();
    writer.WriteLine("namespace AFortOnlineBeacon.Net.Actors;");
    writer.WriteLine();
    writer.WriteLine("internal static class FortCosmeticLoadout {");
    writer.WriteLine("    /// <summary>One outfit: its own item definition, its hero type, and its parts indexed by EFortCustomPartType.</summary>");
    writer.WriteLine("    internal readonly record struct FCharacter(string ItemPath, string? HeroTypePath, string?[] Parts);");
    writer.WriteLine();
    writer.WriteLine("    public static readonly Dictionary<string, FCharacter> Characters = new(StringComparer.OrdinalIgnoreCase) {");
    foreach (var (name, value) in characters) {
        var parts = string.Join(", ", value.Parts.Select(Quote));
        writer.WriteLine($"        [\"{name}\"] = new(\"{value.Item}\", {Quote(value.Hero)}, new string?[] {{ {parts} }}),");
    }

    writer.WriteLine("    };");
    writer.WriteLine();
    writer.WriteLine("    /// <summary>Back bling -> its item definition and the CustomCharacterPart that goes in slot 3.</summary>");
    writer.WriteLine("    public static readonly Dictionary<string, (string ItemPath, string? PartPath)> Backpacks = new(StringComparer.OrdinalIgnoreCase) {");
    foreach (var (name, value) in backpacks) writer.WriteLine($"        [\"{name}\"] = (\"{value.Item}\", {Quote(value.Part)}),");
    writer.WriteLine("    };");
    writer.WriteLine();
    writer.WriteLine("    /// <summary>Harvesting tool -> its item definition and the WID the inventory actually holds.</summary>");
    writer.WriteLine("    public static readonly Dictionary<string, (string ItemPath, string? WeaponPath)> Pickaxes = new(StringComparer.OrdinalIgnoreCase) {");
    foreach (var (name, value) in pickaxes) writer.WriteLine($"        [\"{name}\"] = (\"{value.Item}\", {Quote(value.Weapon)}),");
    writer.WriteLine("    };");
    writer.WriteLine();
    writer.WriteLine("    /// <summary>Glider -> its item definition. No hop: CosmeticLoadout.Glider (pawn handle 145) IS this object.</summary>");
    writer.WriteLine("    public static readonly Dictionary<string, string> Gliders = new(StringComparer.OrdinalIgnoreCase) {");
    foreach (var (name, value) in gliders) writer.WriteLine($"        [\"{name}\"] = \"{value}\",");
    writer.WriteLine("    };");
    writer.WriteLine("}");
}

Console.WriteLine($"wrote {outPath}");
return 0;
