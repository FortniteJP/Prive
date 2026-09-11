using Prive.Server.Http.Xmpp;
using System.Text;

namespace Prive.Server.Http.Routes;

public static class AccountRoutes {
    public static void Map(IEndpointRouteBuilder app) {
        var account = app.MapGroup("/account");

        // The (Delegate) casts are load-bearing: a handler whose only parameter is HttpContext is
        // also convertible to RequestDelegate, and that overload of MapX throws the return value
        // away, answering with an empty body. ASP0016 is an error in the csproj to catch this.
        account.MapPost("/api/oauth/token", (Delegate)OAuthToken).NoAuth();
        account.MapDelete("/api/oauth/sessions/kill/{accessTokenString}", OAuthSessionsKillToken);
        account.MapDelete("/api/oauth/sessions/kill", OAuthSessionsKill);
        account.MapGet("/api/oauth/verify", (Delegate)OAuthVerify);
        account.MapGet("/api/public/account", (Delegate)PublicAccountMultiple);
        account.MapGet("/api/public/account/{accountId}", PublicAccount);
        account.MapGet("/api/public/account/{accountId}/externalAuths", PublicAccountExternalAuths);
        account.MapGet("/api/public/account/displayName/{displayName}", PublicAccountDisplayName);
    }

    static async Task<object> OAuthToken(HttpContext ctx) {
        string clientId;
        User user;

        try {
            clientId = Encoding.UTF8.GetString(Convert.FromBase64String(ctx.Request.Headers["Authorization"].ToString().Split(' ')[1])).Split(':')[0];
        } catch {
            ctx.Response.StatusCode = 400;
            return EpicError.Create(
                "error.com.epicgames.common.oauth.invalid_client", 1011,
                "It appears that your Authorization header may be invalid or not present, please verify that you are sending the correct headers.",
                "com.epicgames.account.public", "prod"
            );
        }

        // The client posts application/x-www-form-urlencoded. Reading the form here is shorter than
        // a [FromForm] model binder and sidesteps minimal API's antiforgery requirement entirely.
        var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync() : FormCollection.Empty;
        var request = new OAuthTokenRequest() {
            grant_type = form["grant_type"].ToString(),
            refresh_token = form["refresh_token"],
            exchange_code = form["exchange_code"],
            username = form["username"],
            password = form["password"]
        };

        switch (request.grant_type) {
            case "client_credentials":
                var token = GenerateToken();
                ClientTokens[token] = new() { TokenString = token };
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
                    ctx.Response.StatusCode = 400;
                    return EpicError.Create(
                        "error.com.epicgames.common.oauth.invalid_request", 1013,
                        "exchange_code is required.",
                        "com.epicgames.account.public", "prod"
                    );
                }
                ctx.Response.StatusCode = 501;
                return EpicError.Create(
                    "error.com.epicgames.common.oauth.unsupported_grant_type", 0,
                    "exchange_code is not supported yet.",
                    "com.epicgames.account.public", "prod"
                );
            case "refresh_token":
                var refreshTokenRequest = request.To<OAuthRefreshTokenRequest>();
                if (refreshTokenRequest is null) {
                    ctx.Response.StatusCode = 400;
                    return EpicError.Create(
                        "error.com.epicgames.common.oauth.invalid_request", 1013,
                        "refresh_token is required.",
                        "com.epicgames.account.public", "prod"
                    );
                }

                // The one lookup that is not by token string, so it still has to scan. Values is
                // a snapshot, which is what makes scanning it safe.
                var refreshToken = AuthTokens.Values.FirstOrDefault(x => x.RefreshTokenString == refreshTokenRequest.refresh_token);
                if (refreshToken is null) {
                    ctx.Response.StatusCode = 400;
                    return EpicError.Create(
                        "errors.com.epicgames.account.auth_token.invalid_refresh_token", 18036,
                        "Sorry the refresh token you provided is invalid.",
                        "com.epicgames.account.public", "prod"
                    );
                }

                user = await DB.GetUser(refreshToken.AccountId);
                break;
            case "password":
                var passwordRequest = request.To<OAuthPasswordRequest>();
                if (passwordRequest is null) {
                    ctx.Response.StatusCode = 400;
                    return EpicError.Create(
                        "error.com.epicgames.common.oauth.invalid_request", 1013,
                        "username and password are required.",
                        "com.epicgames.account.public", "prod"
                    );
                }

                user = await DB.GetUser(passwordRequest.username);
                if (user is null || passwordRequest.password != user.Password) {
                    ctx.Response.StatusCode = 400;
                    return EpicError.Create(
                        "error.com.epicgames.account.invalid_account_credentials", 18031,
                        "Sorry the account credentials you are using are invalid.",
                        "com.epicgames.account.public", "prod"
                    );
                }
                break;
            default:
                ctx.Response.StatusCode = 400;
                return EpicError.Create(
                    "error.com.epicgames.common.oauth.unsupported_grant_type", 1016,
                    $"Unsupported grant type: {request.grant_type}",
                    "com.epicgames.account.public", "prod"
                );
        }

        var tokenString = GenerateToken();
        var refreshTokenString = GenerateToken();

        AuthTokens[tokenString] = new() {
            TokenString = tokenString,
            RefreshTokenString = refreshTokenString,
            AccountId = user.AccountId,
        };

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

    static object OAuthSessionsKillToken(HttpContext ctx, string accessTokenString) {
        var authToken = AuthTokens.GetValueOrDefault(accessTokenString);
        var clientToken = ClientTokens.GetValueOrDefault(accessTokenString);

        if (authToken is null && clientToken is null) {
            Console.WriteLine($"AuthToken: {authToken?.TokenString ?? "NULL"}, ClientToken: {clientToken?.TokenString ?? "NULL"}");
            ctx.Response.StatusCode = 404;
            return EpicError.Create(
                "errors.com.epicgames.account.auth_token.unknown_oauth_session", 18051,
                $"Sorry we could not find the auth session '{accessTokenString}'",
                "com.epicgames.account.public", "prod", new[] { accessTokenString }
            );
        }

        if (authToken is not null) {
            AuthTokens.TryRemove(authToken.TokenString, out _);
            // The XMPP session authenticated with this token has to go too. It holds the account
            // id, and one connection per account is the rule, so leaving it would lock the client
            // out of its own next login.
            XmppClients.DisconnectToken(authToken.TokenString);
            // Remove player from party
        }

        if (clientToken is not null) ClientTokens.TryRemove(clientToken.TokenString, out _);

        return Results.NoContent();
    }

    static IResult OAuthSessionsKill() => Results.NoContent();

    static async Task<object> PublicAccount(HttpContext ctx, string accountId) {
        var user = await DB.GetUser(accountId);
        if (user is null) {
            ctx.Response.StatusCode = 404;
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

    static object PublicAccountExternalAuths() => new {};

    static async Task<object> PublicAccountDisplayName(HttpContext ctx, string displayName) {
        var user = await DB.GetUser(displayName);

        if (user is null) {
            ctx.Response.StatusCode = 404;
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

    static async Task<object> PublicAccountMultiple(HttpContext ctx) {
        var accountIds = (string[])ctx.Request.Query["accountId"].ToArray()!;

        if (accountIds.Length > 100 || accountIds.Length == 0) {
            ctx.Response.StatusCode = 400;
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

    static async Task<object> OAuthVerify(HttpContext ctx) {
        // The middleware already matched the bearer token and put it here, so there is no need
        // to pull the header apart again. A client-credentials token leaves this null, which is
        // the 404 below - same as before.
        var token = ctx.Items["AuthToken"] as AuthToken;

        if (token is null) {
            ctx.Response.StatusCode = 404;
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
