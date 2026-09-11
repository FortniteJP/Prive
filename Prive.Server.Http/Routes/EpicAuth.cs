namespace Prive.Server.Http.Routes;

/// <summary>
///     The only middleware this server needs. Every endpoint requires a bearer token from
///     <see cref="Global.AuthTokens"/> or <see cref="Global.ClientTokens"/> unless it is marked
///     <c>.NoAuth()</c>, and every request gets one log line - the express
///     <c>app.use((req, res, next) =&gt; ...)</c> equivalent, one for one.
/// </summary>
public static class EpicAuth {
    /// <summary>Lets an endpoint be reached without an Authorization header.</summary>
    public static TBuilder NoAuth<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new NoAuthAttribute());

    /// <summary>
    ///     <see cref="NoAuth"/> in Debug builds only - some endpoints are left open locally so the
    ///     client can be poked at with curl, but stay authenticated in Release.
    /// </summary>
    public static TBuilder NoAuthInDebug<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
#if DEBUG
        => builder.NoAuth();
#else
        => builder;
#endif

    public static IApplicationBuilder UseEpicAuth(this IApplicationBuilder app)
        => app.Use(async (HttpContext ctx, Func<Task> next) => {
            if (RequiresAuth(ctx) && !TryAuthenticate(ctx)) {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(EpicError.Create(
                    "errors.com.epicgames.common.authentication_failed", 1032,
                    $"Authentication failed for {ctx.Request.Path.Value}",
                    "com.epicgames.fortnite", "prod", new[] { ctx.Request.Path.Value ?? "" }
                ));
            } else await next();
            Console.WriteLine($"{ctx.Response.StatusCode} {ctx.Request.Method} {ctx.Request.Path.Value}");
        });

    /// <summary>
    ///     No endpoint matched means the fallback (or nothing) will answer, and the fallback is
    ///     <see cref="NoAuth"/> anyway - a 404 must not turn into a 401.
    /// </summary>
    static bool RequiresAuth(HttpContext ctx)
        => ctx.GetEndpoint() is Endpoint endpoint && endpoint.Metadata.GetMetadata<NoAuthAttribute>() is null;

    /// <summary>
    ///     Stores whichever token matched in <see cref="HttpContext.Items"/> - both keys are always
    ///     written, one of them as null, because handlers pattern-match on the type they want.
    /// </summary>
    static bool TryAuthenticate(HttpContext ctx) {
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var value)) return false;
        var parts = value.ToString().Split(" ");
        if (parts.Length < 2) return false;
        var authToken = AuthTokens.GetValueOrDefault(parts[1]);
        var clientToken = ClientTokens.GetValueOrDefault(parts[1]);
        if (authToken is null && clientToken is null) return false;
        ctx.Items["AuthToken"] = authToken;
        ctx.Items["ClientToken"] = clientToken;
        return true;
    }
}
