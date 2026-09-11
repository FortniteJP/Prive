namespace Prive.Server.Http.Routes;

/// <summary>
///     The one-liner services: content pages, telemetry, lightswitch, the waiting room and the
///     handful of odds and ends the client will not start without. Each was its own controller file.
/// </summary>
public static class MiscRoutes {
    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/content/api/pages/fortnite-game", () => new object()).NoAuth();

        // Telemetry. The client posts a lot of it and does not look at the response.
        app.MapPost("/datarouter/api/v1/public/data", () => new object()).NoAuth();

        app.MapGet("/lightswitch/api/service/bulk/status", () => BulkStatus);

        app.MapGet("/waitingroom/api/waitingroom", () => Results.NoContent()).NoAuth();

        app.MapPost("/api/v1/user/setting", () => new {});
        app.MapGet("/socialban/api/public/v1/{accountId}", () => Results.NoContent());
        app.MapGet("/socialban/api/public/v1/{accountId}/ban", () => Results.NoContent());
    }
}
