using System.Text.Json;

namespace Prive.Server.Http;

public class OAuthTokenRequest {
    // I want these to be camel case
    public required string grant_type { get; set; }
    public virtual string? refresh_token { get; set; }
    public virtual string? exchange_code { get; set; }
    public virtual string? username { get; set; }
    public virtual string? password { get; set; }

    public T? To<T>() where T : OAuthTokenRequest => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(this));
}

#pragma warning disable CS8765

public class OAuthExchangeCodeRequest : OAuthTokenRequest {
    public override required string exchange_code { get; set; }
}

public class OAuthRefreshTokenRequest : OAuthTokenRequest {
    public override required string refresh_token { get; set; }
}

public class OAuthPasswordRequest : OAuthTokenRequest {
    public override required string username { get; set; }
    public override required string password { get; set; }
}

#pragma warning restore CS8765
