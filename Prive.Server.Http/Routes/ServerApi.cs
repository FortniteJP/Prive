using MongoDB.Driver;
using System.Net;
using System.Text.Json;

namespace Prive.Server.Http.Routes;

/// <summary>
///     The control surface Prive.Server.WebUI and the Discord bot drive. Everything here is
///     <c>.NoAuth()</c> - there is no Epic token involved - and gated on
///     <see cref="IsFromAuthorized"/> instead.
///     <para>
///         The match-control endpoints (safe zone, infinite ammo, start bus, restart, set port,
///         time to go, shutdown, execute test) are gone: every one of them was a single call into
///         <c>ServerInstanceCommunicator</c>, which is not being used any more.
///     </para>
/// </summary>
public static class ServerApiRoutes {
    public static readonly string BaseDllLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prive.Launcher");
    public static readonly string ClientNativeDllLocation = Path.Combine(BaseDllLocation, "Prive.Client.Native.dll");
    public static readonly string ServerNativeDllLocation = Path.Combine(BaseDllLocation, "Prive.Server.Native.dll");
    public static readonly string ShippingLocation = @"D:\Documents\Fortnite\10.4\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe" is var p && File.Exists(p) ? p : @"C:\Users\User\AppData\Local\Prive.Launcher\v10.40\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"; // Hardcoded

    public static ServerInstance? Instance { get => Program.Instance; set => Program.Instance = value; }

    public static string IP = Dns.GetHostEntry("xthe.org").AddressList.First().ToString();

    public static void Map(IEndpointRouteBuilder app) {
        var api = app.MapGroup("/serverapi");

        api.MapGet("/ip", () => IP).NoAuth();
        api.MapGet("/activeplayers", GetActivePlayers).NoAuth();
        api.MapGet("/discord/{discordAccountId}", DiscordAccountId).NoAuth();
        api.MapPost("/start", Start).NoAuth();
        api.MapPost("/stop", Stop).NoAuth();
        api.MapPost("/createuser", (Delegate)CreateUser).NoAuth();
    }

    /// <summary>
    ///     Behind the reverse proxy the only caller allowed to drive the match is the box the game
    ///     server runs on. A request with no X-Forwarded-For at all came in locally, so it passes.
    /// </summary>
    public static bool IsFromAuthorized(HttpContext ctx)
        => !ctx.Request.Headers.ContainsKey("X-Forwarded-For") || ctx.Request.Headers["X-Forwarded-For"].Select(x => x?.Split(", ")).Any(x => x?.Any(x => x == IP) ?? false);

    /// <summary>
    ///     Only counts players still queueing. It used to add the players left in the live match,
    ///     via the communicator, but that call always landed in its own catch block.
    /// </summary>
    static int GetActivePlayers()
        => MatchMakingRoutes.MatchMakingManagerSolo.QueuedCount
         + MatchMakingRoutes.MatchMakingManagerLateGameSolo.QueuedCount;

    static IResult Start(HttpContext ctx) {
        if (!IsFromAuthorized(ctx)) return Results.Unauthorized();
        Console.WriteLine("Start posted");
        Instance?.Kill();
        Instance = new ServerInstance(ShippingLocation);
        Instance.Launch();
        Instance.InjectDll(ClientNativeDllLocation);
        // have to wait because required things does not load instantly
        Task.Run(async () => await Instance.WaitForLogAndInjectDll(line => line.Contains("LogHotfixManager: Display: Update State CheckingForPatch -> CheckingForHotfix"), ServerNativeDllLocation));
        return Results.NoContent();
    }

    static IResult Stop(HttpContext ctx) {
        if (!IsFromAuthorized(ctx)) return Results.Unauthorized();
        Console.WriteLine("Stop posted");
        Program.Instance?.Kill();
        return Results.NoContent();
    }

    static async Task<IResult> CreateUser(HttpContext ctx) {
        if (!IsFromAuthorized(ctx)) return Results.Unauthorized();
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? throw new Exception("Failed to deserialize body");
        var username = d["username"];
        var password = d["password"];
        var discord = d["discord"];
        if ((await DB.GetUser($"{username}@fortnite.day", "Email")) is not null) return Results.Conflict();
        var user = new User() {
            Email = $"{username}@fortnite.day",
            Password = password,
            DisplayName = username,
            AccountId = Guid.NewGuid().ToString().Replace("-", ""),
            DiscordAccountId = discord
        };
        await DB.Users.InsertOneAsync(user);
        return Results.NoContent();
    }

    static async Task<IResult> DiscordAccountId(HttpContext ctx, string discordAccountId) {
        if (!IsFromAuthorized(ctx)) return Results.Unauthorized();
        try {
            var user = await DB.Users.Find(Builders<User>.Filter.Eq("DiscordAccountId", discordAccountId)).FirstOrDefaultAsync();
            if (user is null) return Results.NotFound();
            else return Results.Ok(user);
        } catch {}
        return Results.NotFound();
    }
}
