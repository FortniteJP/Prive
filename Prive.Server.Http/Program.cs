using WebSocketSharp.Server;

namespace Prive.Server.Http;

public class Program {
    public static WebSocketServer? XMPPServer { get; } = new WebSocketServer(System.Net.IPAddress.Loopback, 8001);
    
    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.

        builder.Services.AddControllers();
        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment()) {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();

        app.UseAuthorization();

        app.MapControllers();

        app.Use(async (context, next) => {
            if (context.GetEndpoint()?.Metadata is var metadata && metadata is null) await next.Invoke();
            if (metadata?.Count <= 3) {
                await next.Invoke(); // Not Found
                Console.WriteLine($"{context.Response.StatusCode} {context.Request.Method} {context.Request.Path.Value}");
                return;
            }
            var requiresAuth = metadata?.GetMetadata<NoAuthAttribute>() is null;
            if (requiresAuth) {
                if (context.Request.Headers.TryGetValue("Authorization", out var value)) {
                    var token = value.ToString().Split(" ")[1];
                    var authToken = AuthTokens.FirstOrDefault(x => x.TokenString == token);
                    var clientToken = ClientTokens.FirstOrDefault(x => x.TokenString == token);
                    if (authToken is not null || clientToken is not null) {
                        context.Items.Add("AuthToken", authToken);
                        context.Items.Add("ClientToken", clientToken);
                        await next.Invoke();
                        Console.WriteLine($"{context.Response.StatusCode} {context.Request.Method} {context.Request.Path.Value}");
                        return;
                    }
                }
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(EpicError.Create(
                    "errors.com.epicgames.common.authentication_failed", 1032,
                    $"Authentication failed for {context.Request.Path.Value}",
                    "com.epicgames.fortnite", "prod", new[] { context.Request.Path.Value ?? "" }
                ));
            } else await next.Invoke();
            Console.WriteLine($"{context.Response.StatusCode} {context.Request.Method} {context.Request.Path.Value}");
        });

        app.MapFallback(async context => {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(EpicError.Create(
                "errors.com.epicgames.common.not_found", 1004,
                "Sorry the resource you were trying to find could not be found",
                "com.epicgames.account.public"
            ));
        });

        XMPPServer!.Log.Level = WebSocketSharp.LogLevel.Trace;
        XMPPServer.AddWebSocketService<XMPPClient>("/");
        XMPPServer.Start();
        XMPPClient.PresenceLoop = new(async () => {
            while (true) {
                XMPPClient.SendPresence();
                await Task.Delay(10000);
            }
        });
        XMPPClient.PresenceLoop.Start();

        // new Thread(RunTcpListener).Start();

        app.Run();

        // XMPPServer.Stop();
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
