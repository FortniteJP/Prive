global using K = System.Text.Json.Serialization.JsonPropertyNameAttribute;
global using static Prive.Server.Http.Global;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Prive.Server.Http;

public static class Global {
    public const string DateTimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    
    public static readonly string CloudStorageLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Server/CloudStorage");
    public static readonly string KeyChainLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Server/keychain.json");
    public static readonly string BulkStatusLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Server/bulkstatus.json");
    
    public static string[] KeyChain { get; private set; } = new string[0];
    public static object[] BulkStatus { get; private set; } = new object[0];
    
    public static List<ClientToken> ClientTokens { get; } = new();
    public static List<AuthToken> AuthTokens { get; } = new();
    public static List<Party> Parties { get; } = new();
    public static List<XMPPClient> XMPPClients { get; } = new();

    static Global() {
        RefreshKeyChain();
        RefreshBulkStatus();
    }

    public static string GenerateToken() {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    public static void RefreshKeyChain() {
        if (Path.GetDirectoryName(CloudStorageLocation) is string dir && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(KeyChainLocation)) File.WriteAllText(KeyChainLocation, "[]");
        KeyChain = JsonSerializer.Deserialize<string[]>(File.ReadAllText(KeyChainLocation)) ?? new string[0];
    }

    public static void RefreshBulkStatus() {
        if (Path.GetDirectoryName(CloudStorageLocation) is string dir && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(BulkStatusLocation)) File.WriteAllText(BulkStatusLocation, "[]");
        BulkStatus = JsonSerializer.Deserialize<object[]>(File.ReadAllText(BulkStatusLocation)) ?? new object[0];
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

public class Party {
    public required string PartyId { get; init; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; private set; }
    public int Revision { get; private set; }
    public PartyConfig Config { get; private set; } = new();
    public PartyMember[] Members { get; private set; } = new PartyMember[16];
    public Dictionary<string, object> Meta { get; private set; } = new();
}

public class PartyConfig {
    public string Type { get; set; } = "DEFAULT";
    public string Joinability { get; set; } = "OPEN";
    public string Discoverability { get; set; } = "ALL";
    public string SubType { get; set; } = "default";
    public int MaxSize { get; set; } = 16;
    public int InviteTTL { get; set; } = 14400;
    public bool JoinConfirmation { get; set; } = true;
}

public class PartyMember {
    public required string AccountId { get; init; }
    public Dictionary<string, object> Meta { get; set; } = new();
    public List<PartyMemberConnection> Connections { get; set; } = new();
    public int Revision { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime JoinedAt { get; } = DateTime.UtcNow;
    public string Role { get; set; } = "MEMBER";
}

public class PartyMemberConnection {
    public required string Id { get; init; }
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; private set; }
    public bool YieldLeadership { get; set; }
    public Dictionary<string, object> Meta { get; set; } = new();
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
        return await Users.Find(Builders<User>.Filter.Eq(type, input.ToLowerInvariant())).FirstOrDefaultAsync();
    }

    public static async Task<List<User>> GetUsers(string[] input) {
        return await Users.Find(Builders<User>.Filter.In("AccountId", input)).ToListAsync();
    }

    public static async Task<Friend> GetFriend(string accountId) => await Friends.Find(Builders<Friend>.Filter.Eq("AccountId", accountId)).FirstOrDefaultAsync();

    public static async Task<AthenaProfile> GetAthenaProfile(string accountId) => await AthenaProfiles.Find(Builders<AthenaProfile>.Filter.Eq("AccountId", accountId)).FirstOrDefaultAsync();

    public static async Task<CommonCoreProfile> GetCommonCoreProfile(string accountId) => await CommonCoreProfiles.Find(Builders<CommonCoreProfile>.Filter.Eq("AccountId", accountId)).FirstOrDefaultAsync();
}