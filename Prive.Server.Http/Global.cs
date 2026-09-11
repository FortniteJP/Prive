global using K = System.Text.Json.Serialization.JsonPropertyNameAttribute;
global using static Prive.Server.Http.Global;
using MongoDB.Driver;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Prive.Server.Http;

public static class Global {
    public const string DateTimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public static readonly string CloudStorageLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Server", "CloudStorage");
    public static readonly string KeyChainLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Server", "keychain.json");
    public static readonly string BulkStatusLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Server", "bulkstatus.json");
    public static readonly string ItemShopLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Server", "itemshop.json");
    
    public static string[] KeyChain { get; private set; } = new string[0];
    public static object[] BulkStatus { get; private set; } = new object[0];
    public static object ItemShop { get; private set; } = new();
    
    /// <summary>Client-credentials tokens, keyed by the token string the client sends back.</summary>
    public static ConcurrentDictionary<string, ClientToken> ClientTokens { get; } = new();

    /// <summary>
    ///     Access tokens, keyed by the token string the client sends back.
    ///     <para>
    ///         A dictionary rather than a list for two reasons. Every authenticated request used to
    ///         find its token by scanning the whole list - and <c>List.Add</c> is not atomic, so two
    ///         logins landing together on Kestrel's threads can both write to the same slot and lose
    ///         one of the tokens. It does not throw; it just drops it, measured at over 10% with
    ///         four threads appending flat out. A dropped token is one the server has already handed
    ///         a client and can no longer recognise, so every later request from that client 401s.
    ///     </para>
    ///     <para>
    ///         Note that a concurrent scan would not have thrown either: since .NET 9 LINQ walks a
    ///         <c>List</c> through <c>CollectionsMarshal.AsSpan</c>, with no version check. It can
    ///         read a stale backing array, but never reports the problem.
    ///     </para>
    /// </summary>
    public static ConcurrentDictionary<string, AuthToken> AuthTokens { get; } = new();
    public static List<Party> Parties { get; } = new();

    public static MyDiscordRestClient DiscordRest { get; } = new();

    /// <summary>
    ///     The port Kestrel ended up on, from <c>PORT</c> or <c>ASPNETCORE_URLS</c>. Assigned once
    ///     at the top of <see cref="Program.Main"/>, before a route is mapped or an ini generated.
    /// </summary>
    public static int HttpPort { get; internal set; } = 8000;

    /// <summary>
    ///     XMPP listens one port above HTTP. This is the only place that rule is written down -
    ///     <see cref="Program"/> binds it and DefaultEngine.ini tells the client about it.
    /// </summary>
    public static int XmppPort => HttpPort + 1;

    static Global() {
        if (!Directory.Exists(CloudStorageLocation)) Directory.CreateDirectory(CloudStorageLocation);
        RefreshKeyChain();
        RefreshBulkStatus();
        RefreshItemShop();
    }

    public static string GenerateToken() {
        return Guid.NewGuid().ToString().Replace("-", "");
    }

    public static void RefreshKeyChain() {
        if (Path.GetDirectoryName(KeyChainLocation) is string dir && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(KeyChainLocation)) File.WriteAllText(KeyChainLocation, "[]");
        KeyChain = JsonSerializer.Deserialize<string[]>(File.ReadAllText(KeyChainLocation)) ?? new string[0];
    }

    public static void RefreshBulkStatus() {
        if (Path.GetDirectoryName(BulkStatusLocation) is string dir && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(BulkStatusLocation)) File.WriteAllText(BulkStatusLocation, "[]");
        BulkStatus = JsonSerializer.Deserialize<object[]>(File.ReadAllText(BulkStatusLocation)) ?? new object[0];
    }

    public static void RefreshItemShop() {
        if (Path.GetDirectoryName(ItemShopLocation) is string dir && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(ItemShopLocation)) File.WriteAllText(ItemShopLocation, "{}");
        ItemShop = JsonSerializer.Deserialize<object>(File.ReadAllText(ItemShopLocation)) ?? new();
    }

    public static ProfileChange CreateProfileChange(AthenaProfile profile) {
        var p = new FortniteProfile() {
            ProfileId = "athena",
            Id = profile.AccountId,
            AccountId = profile.AccountId
        };
        foreach (var item in Cosmetics.CosmeticItems) {
            p.Items.Add($"{item.BackendType}:{item.Id}", new() {
                TemplateId = $"{item.BackendType}:{item.Id}",
                Attributes = new() {
                    ["max_level_bonus"] = 0,
                    ["level"] = 1,
                    ["item_seen"] = true,
                    ["rnd_sel_cnt"] = 0,
                    ["xp"] = 0,
                    ["variants"] = new object[0],
                    ["favorite"] = false
                }
            });
        }
        p.Stats.Attributes.Add("favorite_backpack", profile.BackpackId);
        p.Stats.Attributes.Add("favorite_character", profile.CharacterId);
        p.Stats.Attributes.Add("favorite_glider", profile.GliderId); // HOW I EVER MISSED THIS
        p.Stats.Attributes.Add("favorite_pickaxe", profile.PickaxeId);
        p.Stats.Attributes.Add("favorite_dance", profile.Dances);
        p.Stats.Attributes.Add("favorite_itemwraps", profile.ItemWraps);
        p.Stats.Attributes.Add("favorite_loadingscreen", profile.LoadingScreenId);
        p.Stats.Attributes.Add("favorite_skydivecontrail", profile.SkyDiveContrailId);
        p.Stats.Attributes.Add("favorite_musicpack", profile.MusicPackId);
        return new() {
            Profile = p
        };
    }

    public static ProfileChange CreateProfileChange(CommonCoreProfile profile) {
        var p = new FortniteProfile() {
            ProfileId = "common_core",
            Id = profile.AccountId,
            AccountId = profile.AccountId
        };
        p.Items.Add("Currency:MtxPurchased", new() {
            TemplateId = "Currency:MtxPurchased",
            Attributes = new() {
                ["platform"] = profile.MtxPlatform
            },
            Quantity = profile.VBucks
        });
        return new() {
            Profile = p
        };
    }
}

public class ClientToken {
    public required string TokenString { get; init; }
}

public class AuthToken {
    public required string TokenString { get; init; }
    public required string RefreshTokenString { get; init; }
    public required string AccountId { get; init; }
}

/// <summary>
///     A party as the client's own JSON reader expects it. Every name here is the wire name, in
///     snake_case: without the [K] attributes these serialise as camelCase, and the client's
///     FPartyConfigInfo reader then finds no <c>sub_type</c>, <c>max_size</c> or <c>invite_ttl</c>
///     and fails the whole CreateParty.
/// </summary>
public class Party {
    [K("id")] public required string Id { get; init; }
    [K("created_at")] public DateTime CreatedAt { get; } = DateTime.UtcNow;
    [K("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Echoed back from the create request rather than rebuilt, so the joinability and
    ///     discoverability enums cannot be guessed wrong - the client is told exactly what it asked
    ///     for. <see cref="PartyConfig"/> is the fallback when the request carried no config.
    /// </summary>
    [K("config")] public required object Config { get; set; }

    [K("members")] public List<PartyMember> Members { get; } = new();
    [K("applicants")] public object[] Applicants { get; } = Array.Empty<object>();
    [K("meta")] public object Meta { get; set; } = new Dictionary<string, string>();
    [K("invites")] public object[] Invites { get; } = Array.Empty<object>();
    [K("revision")] public int Revision { get; set; }
    [K("intentions")] public object[] Intentions { get; } = Array.Empty<object>();
}

/// <summary>Only used when the create request did not carry a config of its own.</summary>
public class PartyConfig {
    [K("type")] public string Type { get; set; } = "DEFAULT";
    [K("joinability")] public string Joinability { get; set; } = "OPEN";
    [K("discoverability")] public string Discoverability { get; set; } = "ALL";
    [K("sub_type")] public string SubType { get; set; } = "default";
    [K("max_size")] public int MaxSize { get; set; } = 16;
    [K("invite_ttl")] public int InviteTtl { get; set; } = 14400;
    [K("join_confirmation")] public bool JoinConfirmation { get; set; } = true;
}

public class PartyMember {
    [K("account_id")] public required string AccountId { get; init; }
    [K("meta")] public object Meta { get; set; } = new Dictionary<string, string>();
    [K("connections")] public List<PartyMemberConnection> Connections { get; } = new();
    [K("revision")] public int Revision { get; set; }
    [K("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [K("joined_at")] public DateTime JoinedAt { get; } = DateTime.UtcNow;

    /// <summary>CAPTAIN for whoever created the party, MEMBER for everyone else.</summary>
    [K("role")] public string Role { get; set; } = "MEMBER";
}

public class PartyMemberConnection {
    [K("id")] public required string Id { get; init; }
    [K("connected_at")] public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    [K("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [K("yield_leadership")] public bool YieldLeadership { get; set; }
    [K("meta")] public object Meta { get; set; } = new Dictionary<string, string>();
}

public static class DB {
    public static MongoClient Client { get; } = new("mongodb://localhost:27017");
    public static IMongoDatabase Database { get; } = Client.GetDatabase("Prive");

    public static IMongoCollection<User> Users { get; } = Database.GetCollection<User>("Users");
    public static IMongoCollection<Friend> Friends { get; } = Database.GetCollection<Friend>("Friends");
    public static IMongoCollection<AthenaProfile> AthenaProfiles { get; } = Database.GetCollection<AthenaProfile>("AthenaProfiles");
    public static IMongoCollection<CommonCoreProfile> CommonCoreProfiles { get; } = Database.GetCollection<CommonCoreProfile>("CommonCoreProfiles");

    private static readonly Regex EmailPattern = new(".*@.*\\..*");

    public static async Task<User> GetUser(string input, string? type = null) {
        if (type is null) type = (input.Length == 32 ? "AccountId" : EmailPattern.IsMatch(input) ? "Email" : "DisplayName");
        return await Users.Find(Builders<User>.Filter.Eq(type, input.ToLowerInvariant()), new() { Collation = new("en", strength: CollationStrength.Primary) }).FirstOrDefaultAsync();
    }

    public static async Task<List<User>> GetUsers(string[] input) {
        return await Users.Find(Builders<User>.Filter.In("AccountId", input)).ToListAsync();
    }

    public static async Task<Friend> GetFriend(string accountId) => await Friends.Find(Builders<Friend>.Filter.Eq("AccountId", accountId)).FirstOrDefaultAsync();

    public static async Task<AthenaProfile> GetAthenaProfile(string accountId) => await AthenaProfiles.Find(Builders<AthenaProfile>.Filter.Eq("AccountId", accountId)).FirstOrDefaultAsync();

    public static async Task<CommonCoreProfile> GetCommonCoreProfile(string accountId) => await CommonCoreProfiles.Find(Builders<CommonCoreProfile>.Filter.Eq("AccountId", accountId)).FirstOrDefaultAsync();
}

public static class WebSocketExtension {
    public static Task SendAsync(this System.Net.WebSockets.WebSocket socket, string message) {
        var buffer = System.Text.Encoding.UTF8.GetBytes(message);
        return socket.SendAsync(buffer, System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
    }
}