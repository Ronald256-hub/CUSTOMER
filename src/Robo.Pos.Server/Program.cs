using Robo.Pos.Server.Accounting;
using Robo.Pos.Server.Business;
using Robo.Pos.Server.Administration;
using Robo.Pos.Server.Sales;
using Robo.Pos.Server.Inventory;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;
using Microsoft.AspNetCore.Identity;
using Robo.Pos.Server.Data;

var builder = WebApplication.CreateBuilder(
    new WebApplicationOptions
    {
        Args = args,
        WebRootPath = Path.Combine(
            AppContext.BaseDirectory,
            "wwwroot")
    });

builder.Services.AddSingleton<DatabaseBootstrap>();

builder.Services.AddSingleton<IPasswordHasher<PosUser>>(
    _ => new PasswordHasher<PosUser>());

builder.Services.AddSingleton<InitialUserSeeder>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<PasswordChangeService>();
builder.Services.AddSingleton<AdminTellerResetService>();
builder.Services.AddSingleton<UserAdministrationService>();
builder.Services.AddSingleton<ShopService>();
builder.Services.AddSingleton<ShopContextService>();
builder.Services.AddSingleton<InventoryService>();
builder.Services.AddSingleton<ShopInventoryService>();
builder.Services.AddSingleton<StockTransferService>();
builder.Services.AddSingleton<StockTransferAuditService>();
builder.Services.AddSingleton<AccountingService>();
builder.Services.AddSingleton<AuditDocumentWriter>();
builder.Services.AddSingleton<SalesService>();
builder.Services.AddSingleton<ShopSalesService>();
builder.Services.AddSingleton<ShopShiftService>();
builder.Services.AddSingleton<ShopSaleCompletionService>();
builder.Services.AddSingleton<ShopReceiptService>();
builder.Services.AddSingleton<ShopSalesReportingService>();
builder.Services.AddSingleton<SaleVoidService>();
builder.Services.AddSingleton<ShopSaleVoidService>();
builder.Services.AddSingleton<BusinessOperationsService>();
builder.Services.AddSingleton<SystemAdministrationService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

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

app.MapGet("/api/v3/service", () => Results.Ok(new
{
    application = "Nexus POS",
    service = "Production Server",
    version = "5.5.0",
    status = "running",
    capabilities = new[]
    {
        "multi-shop-foundation",
        "explicit-session-shop-context",
        "audited-shop-switching",
        "shop-scoped-product-availability",
        "shop-scoped-stock-adjustments",
        "shop-scoped-sale-deduction",
        "shop-scoped-sale-void-restoration",
        "shop-scoped-teller-shifts",
        "shop-scoped-receipt-numbering",
        "audited-receipt-reprints",
        "shop-and-consolidated-sales-reporting",
        "open-shift-shop-switch-protection",
        "stock-transfer-drafts",
        "stock-transfer-approval-and-reservation",
        "stock-transfer-dispatch-and-transit",
        "partial-stock-transfer-receiving",
        "stock-transfer-discrepancy-audit",
        "stock-transfer-reporting",
        "database-enforced-stock-transfer-state-machine",
        "immutable-stock-transfer-line-audit",
        "organization-chart-of-accounts",
        "branch-scoped-double-entry-journals",
        "immutable-posted-ledger",
        "audited-journal-reversals",
        "accounting-period-closing-controls",
        "shop-and-consolidated-trial-balance"
    }
}));

app.MapGet(
    "/api/v3/health",
    async (
        DatabaseBootstrap db,
        CancellationToken cancellationToken) =>
    {
        DatabaseStatus status =
            await db.GetStatusAsync(cancellationToken);

        string instanceId =
            Environment.GetEnvironmentVariable("NEXUS_INSTANCE_ID")
            ?? Environment.GetEnvironmentVariable("ROBO_INSTANCE_ID")
            ?? string.Empty;

        return Results.Ok(new
        {
            ok = true,
            application = "Nexus POS",
            version = "5.5.0",
            instanceId,
            schemaVersion = status.SchemaVersion,
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
app.MapUserAdministrationEndpoints();
app.MapShopEndpoints();
app.MapShopContextEndpoints();
app.MapInventoryEndpoints();
app.MapStockTransferEndpoints();
app.MapStockTransferAuditEndpoints();
app.MapAccountingEndpoints();
app.MapSalesEndpoints();
app.MapAdminReferenceEndpoints();
app.MapBusinessOperationsEndpoints();
app.MapSystemAdministrationEndpoints();

app.MapGet(
    "/",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(
                environment.WebRootPath,
                "index.html"),
            "text/html; charset=utf-8"));

app.MapFallbackToFile("index.html");

app.Run();
