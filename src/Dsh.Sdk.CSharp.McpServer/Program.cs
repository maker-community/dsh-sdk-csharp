using Dsh.Sdk.CSharp.McpServer;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
var settings = McpBridgeSettings.Load(builder.Configuration);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<IDshExecutionService, DshExecutionService>();
builder.Services.AddSingleton<IEmailNotificationService, EmailNotificationService>();
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithToolsFromAssembly();

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/mcp"))
    {
        await next(context);
        return;
    }

    var origin = context.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin) && !settings.AllowedOrigins.Contains(origin))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (!ApiKeyAuthentication.IsAuthorized(
            context.Request.Headers.Authorization.ToString(),
            settings.ApiKey))
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next(context);
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");

await app.RunAsync();
