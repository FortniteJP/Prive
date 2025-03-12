using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("account")]
public class AccountController : ControllerBase {
    [HttpPost("api/oauth/token")] [NoAuth]
    public async Task<object> OAuthToken([FromForm] OAuthTokenRequest request) {
        string clientId;
        User user;

        try {
            clientId = Encoding.UTF8.GetString(Convert.FromBase64String(Request.Headers["Authorization"].ToString().Split(' ')[1])).Split(':')[0];
        } catch {
            Response.StatusCode = 400;
            return EpicError.Create(
                "error.com.epicgames.common.oauth.invalid_client", 1011,
                "It appears that your Authorization header may be invalid or not present, please verify that you are sending the correct headers.",
                "com.epicgames.account.public", "prod"
            );
        }

        switch (request.grant_type) {
            case "client_credentials":
                var token = GenerateToken();
                ClientTokens.Add(new() { TokenString = token });
                return new {
                    access_token = token,
                    expires_in = 14400,
                    expires_at = DateTime.UtcNow.AddSeconds(14400).ToString(DateTimeFormat),
                    token_type = "bearer",
                    client_id = clientId,
                    internal_client = true,
                    client_service = "fortnite",
                };
            case "exchange_code":
                var exchangeCodeRequest = request.To<OAuthExchangeCodeRequest>();
                if (exchangeCodeRequest is null) {
                    Response.StatusCode = 400;
                    return EpicError.Create(
                        "error.com.epicgames.common.oauth.invalid_request", 1013,
                        "exchange_code is required.",
                        "com.epicgames.account.public", "prod"
                    );
                }
                Response.StatusCode = 501;
                return EpicError.Create(
                    "error.com.epicgames.common.oauth.unsupported_grant_type", 0,
                    "exchange_code is not supported yet.",
                    "com.epicgames.account.public", "prod"
                );
            case "refresh_token":
                var refreshTokenRequest = request.To<OAuthRefreshTokenRequest>();
                if (refreshTokenRequest is null) {
                    Response.StatusCode = 400;
                    return EpicError.Create(
                        "error.com.epicgames.common.oauth.invalid_request", 1013,
                        "refresh_token is required.",
                        "com.epicgames.account.public", "prod"
                    );
                }

                var refreshToken = AuthTokens.FirstOrDefault(x => x.RefreshTokenString == refreshTokenRequest.refresh_token);
                if (refreshToken is null) {
                    Response.StatusCode = 400;
                    return EpicError.Create(
                        "error.com.epicgames.account.auth_token.invalid_refresh_token", 18036,
                        "Sorry the refresh token you provided is invalid.",
                        "com.epicgames.account.public", "prod"
                    );
                }

                user = await DB.GetUser(refreshToken.AccountId);
                break;
            case "password":
                var passwordRequest = request.To<OAuthPasswordRequest>();
                if (passwordRequest is null) {
                    Response.StatusCode = 400;
                    return EpicError.Create(
                        "error.com.epicgames.common.oauth.invalid_request", 1013,
                        "username and password are required.",
                        "com.epicgames.account.public", "prod"
                    );
                }

                user = await DB.GetUser(passwordRequest.username);
                if (user is null || passwordRequest.password != user.Password) {
                    Response.StatusCode = 400;
                    return EpicError.Create(
                        "error.com.epicgames.account.invalid_account_credentials", 18031,
                        "Sorry the account credentials you are using are invalid.",
                        "com.epicgames.account.public", "prod"
                    );
                }
                break;
            default:
                Response.StatusCode = 400;
                return EpicError.Create(
                    "error.com.epicgames.common.oauth.unsupported_grant_type", 1016,
                    $"Unsupported grant type: {request.grant_type}",
                    "com.epicgames.account.public", "prod"
                );
        }

        var tokenString = GenerateToken();
        var refreshTokenString = GenerateToken();

        AuthTokens.Add(new() {
            TokenString = tokenString,
            RefreshTokenString = refreshTokenString,
            AccountId = user.AccountId,
        });

        return new {
            access_token = tokenString,
            expires_in = 28800,
            expires_at = DateTime.UtcNow.AddSeconds(28800).ToString(DateTimeFormat),
            token_type = "bearer",
            refresh_token = refreshTokenString,
            refresh_expires = 115200,
            refresh_expires_at = DateTime.UtcNow.AddSeconds(115200).ToString(DateTimeFormat),
            account_id = user.AccountId,
            client_id = clientId,
            internal_client = true,
            client_service = "fortnite",
            scope = new object[0],
            displayName = user.DisplayName,
            app = "fortnite",
            in_app_id = user.AccountId,
        };
    }

    [HttpDelete("api/oauth/sessions/kill/{accessTokenString}")]
    public object? OAuthSessionsKillToken() {
        var accessTokenString = Request.RouteValues["accessTokenString"]?.ToString();
        var authToken = AuthTokens.FirstOrDefault(x => x.TokenString == accessTokenString);
        var clientToken = ClientTokens.FirstOrDefault(x => x.TokenString == accessTokenString);

        if (authToken is null && clientToken is null) {
            Console.WriteLine($"AuthToken: {authToken?.TokenString ?? "NULL"}, ClientToken: {clientToken?.TokenString ?? "NULL"}");
            Response.StatusCode = 404;
            return EpicError.Create(
                "errors.com.epicgames.account.auth_token.unknown_oauth_session", 18051,
                $"Sorry we could not find the auth session '{accessTokenString}'",
                "com.epicgames.account.public", "prod", new[] { accessTokenString ?? "" }
            );
        }

        if (authToken is not null) {
            AuthTokens.Remove(authToken);
            // Remove player from party
        }

        if (clientToken is not null) ClientTokens.Remove(clientToken);
        
        Response.StatusCode = 204;
        return null;
    }

    [HttpDelete("api/oauth/sessions/kill")]
    public object? OAuthSessionsKill() {
        Response.StatusCode = 204;
        return null;
    }

    [HttpGet("api/public/account/{accountId}")]
    public async Task<object> PublicAccount() {
        var accountId = Request.RouteValues["accountId"]?.ToString();
        var user = await DB.GetUser(accountId ?? "");
        if (user is null) {
            Response.StatusCode = 404;
            return EpicError.Create(
                "errors.com.epicgames.account.account_not_found", 18007,
                $"Sorry we couldn't find an account for {accountId}",
                "com.epicgames.account.public", "prod"
            );
        }

        return new {
            id = user.AccountId,
            displayName = user.DisplayName,
            externalAuths = new {}
        };
    }

    [HttpGet("api/public/account/{accountId}/externalAuths")]
    public object PublicAccountExternalAuths() {
        return new {};
    }

    [HttpGet("api/public/account/displayName/{displayName}")]
    public async Task<object> PublicAccountDisplayName() {
        var displayName = Request.RouteValues["displayName"]?.ToString();
        var user = await DB.GetUser(displayName ?? "");

        if (user is null) {
            Response.StatusCode = 404;
            return EpicError.Create(
                "errors.com.epicgames.account.account_not_found", 18007,
                $"Sorry we couldn't find an account for {displayName}",
                "com.epicgames.account.public", "prod"
            );
        }

        return new {
            id = user.AccountId,
            displayName = user.DisplayName,
            externalAuths = new {}
        };
    }

    [HttpGet("api/public/account")]
    public async Task<object> PublicAccountMultiple() {
        var accountIds = (string[])Request.Query["accountId"].ToArray()!;

        if (accountIds.Length > 100 || accountIds.Length == 0) {
            Response.StatusCode = 400;
            return EpicError.Create(
                "errors.com.epicgames.account.invalid_account_id_count", 18066,
                "Sorry, the number of account id should be at least one and not more than 100.",
                "com.epicgames.account.public", "prod"
            );
        }

        var users = await DB.GetUsers(accountIds);

        return users.Aggregate(new List<object>(), (acc, user) => {
            acc.Add(new {
                id = user.AccountId,
                displayName = user.DisplayName,
                externalAuths = new {}
            });
            return acc;
        });
    }

    [HttpGet("api/oauth/verify")]
    public async Task<object> OAuthVerify() {
        var token = AuthTokens.FirstOrDefault(x => x.TokenString == Request.Headers["Authorization"].First()?.Split(" ")[1]);

        if (token is null) {
            Response.StatusCode = 404;
            return EpicError.Create(
                "errors.com.epicgames.common.not_found", 1004,
                "Sorry the resource you were trying to find could not be found",
                "com.epicgames.account.public"
            );
        }

        var user = await DB.GetUser(token.AccountId);

        return new {
            access_token = token.TokenString,
            expires_in = 28800,
            expires_at = DateTime.UtcNow.AddSeconds(28800).ToString(DateTimeFormat),
            token_type = "bearer",
            refresh_token = token.RefreshTokenString,
            refresh_expires = 115200,
            refresh_expires_at = DateTime.UtcNow.AddSeconds(115200).ToString(DateTimeFormat),
            account_id = user.AccountId,
            // client_id = "",
            internal_client = true,
            client_service = "fortnite",
            scope = new object[0],
            displayName = user.DisplayName,
            app = "fortnite",
            in_app_id = user.AccountId,
        };
    }
}

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