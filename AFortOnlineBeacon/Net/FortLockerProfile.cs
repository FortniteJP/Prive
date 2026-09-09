using AFortOnlineBeacon.Core.Objects;
using AFortOnlineBeacon.Net.Actors;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AFortOnlineBeacon.Net;

/// <summary>
///     What a player has equipped in the locker, read straight out of the MongoDB the HTTP/MCP server
///     writes - outfit, back bling, harvesting tool and glider.
///
///     WHY THE DATABASE AND NOT THE WIRE. Nothing the game client sends this server names a cosmetic.
///     The locker lives entirely in the MCP profile: the client PUTs its choice to
///     Prive.Server.Http's SetCosmeticLockerSlot / EquipBattleRoyaleCustomization, which stores it in
///     the `AthenaProfiles` collection, and the game server is simply expected to know. A real
///     Fortnite server is told by the matchmaking/party service; this project has neither, and the
///     two processes already share a machine and a database, so reading it directly is the shortest
///     honest path. It is READ-ONLY here - the beacon never writes a profile.
///
///     DEGRADES, NEVER REFUSES. Mongo being down, absent, or holding no profile for this account all
///     end the same way: null, a line in the console, and the caller keeps the default loadout. A
///     player joining without their skin is a worse game; a player unable to join is no game at all.
///     Every call is bounded by a short server-selection timeout for the same reason - a login must
///     not block on a database.
/// </summary>
internal static class FortLockerProfile {
    /// <summary>MONGO_URL overrides it; the default is the same local instance Prive.Server.Http uses.</summary>
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("MONGO_URL") is { Length: > 0 } url ? url : "mongodb://localhost:27017";

    /// <summary>LOCKER_FROM_DB=0 pins every player to the built-in default loadout.</summary>
    private static bool Enabled => Environment.GetEnvironmentVariable("LOCKER_FROM_DB") is not "0";

    private static IMongoCollection<BsonDocument>? _profiles;
    private static bool _connectFailed;

    private static IMongoCollection<BsonDocument>? Profiles() {
        if (_profiles != null || _connectFailed) return _profiles;

        try {
            var settings = MongoClientSettings.FromConnectionString(ConnectionString);

            // A login runs on the net thread. Without these the driver waits 30 seconds for a server
            // it is never going to find, and the client times out long before that.
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            settings.ConnectTimeout = TimeSpan.FromSeconds(2);
            settings.SocketTimeout = TimeSpan.FromSeconds(2);

            _profiles = new MongoClient(settings).GetDatabase("Prive").GetCollection<BsonDocument>("AthenaProfiles");
        } catch (Exception ex) {
            _connectFailed = true;
            Console.WriteLine($"FortLockerProfile: cannot reach MongoDB at '{ConnectionString}' " +
                              $"({ex.GetType().Name}: {ex.Message}) - every player gets the default loadout.");
        }

        return _profiles;
    }

    /// <summary>
    ///     One player's equipped cosmetics, already resolved to the assets the wire wants.
    ///     Every field is null when that slot is empty or unresolvable, and the caller leaves its own
    ///     default in place for those.
    /// </summary>
    internal readonly record struct FLockerLoadout(
        string? HeroTypePath,
        string?[] CharacterParts,
        string? BackpackPartPath,
        string? PickaxeWeaponPath,
        string? GliderItemPath,
        string Description);

    /// <summary>
    ///     Reads the profile for one account id (the 32-hex `Contents` of the FUniqueNetIdRepl the
    ///     client logged in with) and resolves each id through FortCosmeticLoadout.
    /// </summary>
    public static FLockerLoadout? For(string accountId) {
        if (!Enabled || string.IsNullOrEmpty(accountId)) return null;
        if (Profiles() is not { } profiles) return null;

        BsonDocument? profile;
        try {
            profile = profiles.Find(Builders<BsonDocument>.Filter.Eq("AccountId", accountId)).FirstOrDefault();
        } catch (Exception ex) {
            Console.WriteLine($"FortLockerProfile: query for '{accountId}' failed " +
                              $"({ex.GetType().Name}: {ex.Message}) - using the default loadout.");
            return null;
        }

        if (profile == null) {
            Console.WriteLine($"FortLockerProfile: no AthenaProfile for '{accountId}' - using the default loadout.");
            return null;
        }

        var character = ItemName(profile, "CharacterId");
        var backpack = ItemName(profile, "BackpackId");
        var pickaxe = ItemName(profile, "PickaxeId");
        var glider = ItemName(profile, "GliderId");

        var parts = new string?[6];
        string? heroType = null;

        if (character != null && FortCosmeticLoadout.Characters.TryGetValue(character, out var outfit)) {
            heroType = outfit.HeroTypePath;
            Array.Copy(outfit.Parts, parts, Math.Min(outfit.Parts.Length, parts.Length));
        } else if (character != null) {
            Console.WriteLine($"FortLockerProfile: '{character}' is not in FortCosmeticLoadout - re-run " +
                              "Tools/CosmeticLoadout if it is a real 10.40 outfit.");
        }

        string? backpackPart = null;
        if (backpack != null && FortCosmeticLoadout.Backpacks.TryGetValue(backpack, out var bling)) {
            backpackPart = bling.PartPath;
        }

        string? pickaxeWeapon = null;
        if (pickaxe != null && FortCosmeticLoadout.Pickaxes.TryGetValue(pickaxe, out var tool)) {
            pickaxeWeapon = tool.WeaponPath;
        }

        string? gliderItem = null;
        if (glider != null && FortCosmeticLoadout.Gliders.TryGetValue(glider, out var wing)) {
            gliderItem = wing;
        }

        return new FLockerLoadout(heroType, parts, backpackPart, pickaxeWeapon, gliderItem,
            $"character={character ?? "-"} backpack={backpack ?? "-"} pickaxe={pickaxe ?? "-"} glider={glider ?? "-"}");
    }

    /// <summary>
    ///     "AthenaCharacter:CID_028_Athena_Commando_F" -> "CID_028_Athena_Commando_F".
    ///
    ///     The backend type in front of the colon is thrown away on purpose: it is a statement about
    ///     the CLIENT's AssetManager rather than about the item (see
    ///     Prive.Server.Http/Models/Cosmetics.cs, where getting it wrong made every spray invisible),
    ///     and the four folders here are unambiguous by id prefix anyway. Case is not preserved
    ///     either - the MCP path lowercases ids on some routes - so every lookup table this feeds is
    ///     OrdinalIgnoreCase.
    /// </summary>
    private static string? ItemName(BsonDocument profile, string field) {
        if (!profile.TryGetValue(field, out var value) || value.IsBsonNull) return null;

        var text = value.AsString;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var colon = text.IndexOf(':');
        var name = colon < 0 ? text : text[(colon + 1)..];
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    ///     Applies a loadout to the PlayerState this server is about to replicate: hero type, the six
    ///     character-part slots, and the flags that say which of them were actually sent.
    ///
    ///     The BACK BLING WINS SLOT 3 over whatever the outfit brought, which is the game's own rule -
    ///     an equipped back bling replaces an outfit's built-in one - and an empty back-bling slot
    ///     keeps NoBackpack rather than leaving the slot null, because the client warns forever about
    ///     a customization that never completed.
    /// </summary>
    public static void Apply(APlayerState playerState, FLockerLoadout loadout) {
        if (loadout.HeroTypePath is { } hero) playerState.HeroType = UAssetRegistry.GetOrCreate(hero);

        for (var slot = 0; slot < loadout.CharacterParts.Length && slot < playerState.CharacterParts.Length; slot++) {
            if (loadout.CharacterParts[slot] is { } part) {
                playerState.CharacterParts[slot] = UAssetRegistry.GetOrCreate(part);
            }
        }

        if (loadout.BackpackPartPath is { } backpack) {
            playerState.CharacterParts[(int) EFortCustomPartType.Backpack] = UAssetRegistry.GetOrCreate(backpack);
        }

        byte flags = 0;
        for (var slot = 0; slot < playerState.CharacterParts.Length; slot++) {
            if (playerState.CharacterParts[slot] != null) flags |= (byte) (1 << slot);
        }

        playerState.WasPartReplicatedFlags = flags;
    }
}
