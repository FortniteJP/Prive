using Prive.Server.Http.Routes;
using Prive.Server.Http.Xmpp;

namespace Prive.Server.Http;

public class Program {
    public static ServerInstance? Instance { get; set; }

    public static void Main(string[] args) {
        var (host, port, urls) = Listen();
        // Before anything is mapped or any ini generated - DefaultEngine.ini reads XmppPort off it.
        if (port > 0) HttpPort = port;

        // CreateSlimBuilder instead of CreateBuilder: no IIS integration, no static web assets, no
        // HTTPS (TLS is terminated by the reverse proxy) - just Kestrel, config and routing. Nothing
        // here needs dependency injection, so there is no service registration at all.
        var builder = WebApplication.CreateSlimBuilder(args);

        // The one service registration there is, and it earns its place: the client cannot parse a
        // timestamp with more than three fractional digits, which is what the default writes.
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new EpicDateTimeConverter()));

        // Two ports on the one Kestrel: HTTP, and XMPP one above it.
        if (urls is null) builder.WebHost.UseUrls($"http://{host}:{port}", $"http://{host}:{XmppPort}");
        else if (port > 0) builder.WebHost.UseUrls(urls.Append($"http://{host}:{XmppPort}").ToArray());
        // else: ASPNETCORE_URLS named something unreadable, so it goes to Kestrel as-is and there
        // is no XMPP port to add.

        var app = builder.Build();

        app.UseWebSockets(new() {
            KeepAliveInterval = TimeSpan.FromMinutes(1)
        });
        app.UseXmpp();
        app.UseEpicAuth();

        AccountRoutes.Map(app);
        CloudStorageRoutes.Map(app);
        FortniteRoutes.Map(app);
        FriendsRoutes.Map(app);
        MatchMakingRoutes.Map(app);
        McpRoutes.Map(app);
        MiscRoutes.Map(app);
        PartyRoutes.Map(app);
        ServerApiRoutes.Map(app);

        app.MapFallback(NotFound).NoAuth();

        // new Thread(RunTcpListener).Start();

        Task.Run(async () => await DiscordRest.StartAsync());

        app.Run();
    }

    /// <summary>
    ///     Where to listen: <c>HOST</c> (default <c>0.0.0.0</c>, so the box is reachable from the
    ///     LAN without a launch profile) and <c>PORT</c> (default 8000), with XMPP one above.
    ///     <para>
    ///         A plain <c>ASPNETCORE_URLS</c> still wins, and comes back in <c>Urls</c> so every
    ///         entry it names is kept. Its first entry is also read apart so XMPP can still sit one
    ///         port above; <c>Port</c> comes back 0 when that entry names nothing this can read,
    ///         which only switches XMPP off - Kestrel is unaffected either way.
    ///     </para>
    /// </summary>
    static (string Host, int Port, string[]? Urls) Listen() {
        if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is string raw && raw.Length > 0) {
            var urls = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var first = urls.FirstOrDefault() ?? "";
            var authority = (first.Contains("://") ? first[(first.IndexOf("://") + 3)..] : first).Split('/')[0];
            var colon = authority.LastIndexOf(':');
            if (colon > 0 && int.TryParse(authority[(colon + 1)..], out var urlPort)) return (authority[..colon], urlPort, urls);

            Console.WriteLine($"XMPP disabled: no host and port to read out of ASPNETCORE_URLS ({raw})");
            return ("", 0, urls);
        }

        var host = Environment.GetEnvironmentVariable("HOST") is string h && h.Length > 0 ? h : "0.0.0.0";
        return (host, int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 8000, null);
    }

    static async Task NotFound(HttpContext ctx) {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsJsonAsync(EpicError.Create(
            "errors.com.epicgames.common.not_found", 1004,
            "Sorry the resource you were trying to find could not be found",
            "com.epicgames.account.public"
        ));
    }

    public async static void RunTcpListener() {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 8001);
        listener.Start();
        while (true) {
            using (var client = await listener.AcceptTcpClientAsync()) {
                var buffer = new byte[1024];
                var data = "";
                using (var stream = client.GetStream()) {
                    while (stream.DataAvailable) {
                        var size = await stream.ReadAsync(buffer, 0, buffer.Length);
                        data += System.Text.Encoding.UTF8.GetString(buffer, 0, size);
                        await stream.WriteAsync(buffer, 0, size);
                    }
                    Console.WriteLine($"RECV: {data}");
                }
            }
        }
    }
}
