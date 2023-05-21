global using K = System.Text.Json.Serialization.JsonPropertyNameAttribute;
global using static Prive.Server.Http.Global;

namespace Prive.Server.Http;

public static class Global {
    public static List<ClientToken> ClientTokens { get; } = new();
    public static List<AuthToken> AuthTokens { get; } = new();
}

public class ClientToken {
    public required string Token { get; init; }
    public required string AccountId { get; init; }
}

public class AuthToken {
    public required string Token { get; init; }
    public required string AccountId { get; init; }
}