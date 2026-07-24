using Robo.Pos.Server.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DatabaseBootstrap>();

var app = builder.Build();

var database =
    app.Services.GetRequiredService<DatabaseBootstrap>();

await database.InitializeAsync();

app.Use(async (context, next) =>
{
    context.Response.Headers.Append(
        "X-Content-Type-Options",
        "nosniff");

    context.Response.Headers.Append(
        "X-Frame-Options",
        "DENY");

    context.Response.Headers.Append(
        "Referrer-Policy",
        "no-referrer");

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.Append(
            "Cache-Control",
            "no-store, no-cache, must-revalidate");
    }

    await next();
});

app.MapGet("/", () => Results.Ok(new
{
    application = "ROBO CASK & TAP POS",
    service = "Production Server",
    version = "3.0.0-dev",
    status = "running"
}));

app.MapGet(
    "/api/v3/health",
    async (
        DatabaseBootstrap db,
        CancellationToken cancellationToken) =>
    {
        DatabaseStatus status =
            await db.GetStatusAsync(cancellationToken);

        return Results.Ok(new
        {
            ok = true,
            application = "ROBO CASK & TAP POS",
            version = "3.0.0-dev",
            database = status
        });
    });

app.Run();
