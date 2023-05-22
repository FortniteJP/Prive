global using K = System.Text.Json.Serialization.JsonPropertyNameAttribute;
global using static Prive.Server.Http.Global;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Prive.Server.Http;

public static class Global {
    public const string DateTimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    
    public static List<ClientToken> ClientTokens { get; } = new();
    public static List<AuthToken> AuthTokens { get; } = new();

    public static string GenerateToken() {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
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