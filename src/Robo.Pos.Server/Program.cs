using Robo.Pos.Server.Inventory;
using Robo.Pos.Server.Security;
using Microsoft.AspNetCore.Identity;
using Robo.Pos.Server.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DatabaseBootstrap>();

builder.Services.AddSingleton<IPasswordHasher<PosUser>>(
    _ => new PasswordHasher<PosUser>());

builder.Services.AddSingleton<InitialUserSeeder>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<PasswordChangeService>();
builder.Services.AddSingleton<AdminTellerResetService>();
builder.Services.AddSingleton<InventoryService>();

var app = builder.Build();

var database =
    app.Services.GetRequiredService<DatabaseBootstrap>();

await database.InitializeAsync();

var initialUserSeeder =
    app.Services.GetRequiredService<InitialUserSeeder>();

await initialUserSeeder.SeedAsync();

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


app.MapPost(
    "/api/v3/auth/login",
    async (
        LoginRequest request,
        HttpContext http,
        AuthService authService,
        CancellationToken cancellationToken) =>
    {
        LoginResult result = await authService.LoginAsync(
            request.Username,
            request.Password,
            http.Request.Headers.UserAgent.ToString(),
            cancellationToken);

        if (result.Status == LoginStatus.Success &&
            result.User is not null &&
            result.SessionToken is not null &&
            result.ExpiresAtUtc is not null)
        {
            http.Response.Cookies.Append(
                "robo_session",
                result.SessionToken,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = http.Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                    Expires = result.ExpiresAtUtc,
                    IsEssential = true
                });

            return Results.Ok(new
            {
                user = result.User,
                expiresAtUtc = result.ExpiresAtUtc
            });
        }

        if (result.Status == LoginStatus.Locked)
        {
            return Results.Json(
                new
                {
                    error = "account_locked",
                    message =
                        "The account is temporarily locked after too many failed login attempts.",
                    lockedUntilUtc = result.LockedUntilUtc
                },
                statusCode: StatusCodes.Status423Locked);
        }

        if (result.Status == LoginStatus.Disabled)
        {
            return Results.Json(
                new
                {
                    error = "account_disabled",
                    message = "This account is disabled."
                },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Json(
            new
            {
                error = "invalid_credentials",
                message = "Invalid username or password."
            },
            statusCode: StatusCodes.Status401Unauthorized);
    });

app.MapSessionEndpoints();
app.MapPasswordEndpoints();
app.MapAdminTellerResetEndpoints();
app.MapInventoryEndpoints();

app.Run();
