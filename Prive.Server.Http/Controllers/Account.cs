using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace Prive.Server.Http.Controllers;

[ApiController]
public class AccountController : ControllerBase {
    [HttpPost("/api/oauth/token")] [NoAuth]
    public async Task<object> OAuthToken([FromBody] OAuthTokenRequest request) {
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

        switch (request.GrantType) {
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
                var exchangeCodeRequest = request as OAuthExchangeCodeRequest;
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
                var refreshTokenRequest = request as OAuthRefreshTokenRequest;
                if (refreshTokenRequest is null) {
                    Response.StatusCode = 400;
                    return EpicError.Create(
                        "error.com.epicgames.common.oauth.invalid_request", 1013,
                        "refresh_token is required.",
                        "com.epicgames.account.public", "prod"
                    );
                }

                var refreshToken = AuthTokens.FirstOrDefault(x => x.RefreshTokenString == refreshTokenRequest.RefreshToken);
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
                var passwordRequest = request as OAuthPasswordRequest;
                if (passwordRequest is null) {
                    Response.StatusCode = 400;
                    return EpicError.Create(
                        "error.com.epicgames.common.oauth.invalid_request", 1013,
                        "username and password are required.",
                        "com.epicgames.account.public", "prod"
                    );
                }

                user = await DB.GetUser(passwordRequest.Username);
                if (user is null || passwordRequest.Password != user.Password) {
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
                    $"Unsupported grant type: {request.GrantType}",
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
}

public class OAuthTokenRequest {
    [K("grant_type")] public required string GrantType { get; set; }
}

public class OAuthExchangeCodeRequest : OAuthTokenRequest {
    [K("exchange_code")] public required string ExchangeCode { get; set; }
}

public class OAuthRefreshTokenRequest : OAuthTokenRequest {
    [K("refresh_token")] public required string RefreshToken { get; set; }
}

public class OAuthPasswordRequest : OAuthTokenRequest {
    [K("username")] public required string Username { get; set; }
    [K("password")] public required string Password { get; set; }
}