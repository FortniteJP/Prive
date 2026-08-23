using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;

if (args.Length < 3) {
    Console.WriteLine("Usage: OodleDictionaryExtractor <PaksDirectory> <AesKeyHex> <OutputDirectory> [EGame version, e.g. GAME_UE4_23]");
    Console.WriteLine("  Extracts FortniteGame/Content/Oodle/*.udic (network compression dictionaries) from a client's .pak files.");
    Console.WriteLine("  Example: OodleDictionaryExtractor \"C:\\Fortnite\\FortniteGame\\Content\\Paks\" 0x3ff2... \"C:\\out\" GAME_UE4_23");
    return 1;
}

var paksDir = args[0];
var aesKey = args[1];
var outDir = args[2];
var gameVersion = args.Length > 3 ? Enum.Parse<EGame>(args[3]) : EGame.GAME_UE4_23;

if (!Directory.Exists(paksDir)) {
    Console.WriteLine($"PAK directory not found: {paksDir}");
    return 1;
}

Directory.CreateDirectory(outDir);

var provider = new DefaultFileProvider(paksDir, SearchOption.TopDirectoryOnly, new VersionContainer(gameVersion));
provider.Initialize();

foreach (var vfs in provider.UnloadedVfs.ToList()) {
    provider.SubmitKey(vfs.EncryptionKeyGuid, new FAesKey(aesKey));
}

Console.WriteLine($"Mounted files: {provider.Files.Count}");

var matches = provider.Files.Keys.Where(f => f.EndsWith(".udic", StringComparison.OrdinalIgnoreCase)).ToList();

Console.WriteLine($"Found {matches.Count} .udic file(s):");
foreach (var m in matches) Console.WriteLine($"  {m}");

var savedCount = 0;

foreach (var udicPath in matches) {
    if (provider.TrySaveAsset(udicPath, out var data)) {
        var outPath = Path.Combine(outDir, Path.GetFileName(udicPath));
        File.WriteAllBytes(outPath, data);
        Console.WriteLine($"Saved {outPath} ({data.Length} bytes)");
        savedCount++;
    } else {
        Console.WriteLine($"Failed to extract {udicPath}");
    }
}

Console.WriteLine($"Done. Saved {savedCount}/{matches.Count} dictionary file(s) to {outDir}");

return savedCount == matches.Count ? 0 : 1;
