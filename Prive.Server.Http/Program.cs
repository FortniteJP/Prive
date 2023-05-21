
using System.Reflection;
using Microsoft.AspNetCore.Http.Features;

namespace Prive.Server.Http;

public class Program {
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
                return;
            }
            var requiresAuth = metadata?.GetMetadata<NoAuthAttribute>() is null;
            if (requiresAuth) {
                if (context.Request.Headers.TryGetValue("Authorization", out var value)) {
                    var token = value.ToString().Split(" ")[1];
                    var authToken = AuthTokens.FirstOrDefault(x => x.Token == token);
                    if (authToken is not null) {
                        await next.Invoke();
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
        });

        app.MapFallback(async context => {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(EpicError.Create(
                "errors.com.epicgames.common.not_found", 1004,
                "Sorry the resource you were trying to find could not be found",
                "com.epicgames.account.public"
            ));
        });

        app.Run();
    }
}
