using Robo.Pos.Server.Accounting;
using Robo.Pos.Server.Finance;
using Robo.Pos.Server.Procurement;
using Robo.Pos.Server.Crm;
using Robo.Pos.Server.Hrm;
using Robo.Pos.Server.Saas;
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
        WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
    });

builder.Services.AddSingleton<DatabaseBootstrap>();
builder.Services.AddSingleton<IPasswordHasher<PosUser>>(_ => new PasswordHasher<PosUser>());
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
builder.Services.AddSingleton<FinanceService>();
builder.Services.AddSingleton<ProcurementService>();
builder.Services.AddSingleton<CrmService>();
builder.Services.AddSingleton<HrmService>();
builder.Services.AddSingleton<SaasService>();
builder.Services.AddSingleton<AuditDocumentWriter>();
builder.Services.AddSingleton<SalesReturnDocumentWriter>();
builder.Services.AddSingleton<SalesService>();
builder.Services.AddSingleton<ShopSalesService>();
builder.Services.AddSingleton<ShopShiftService>();
builder.Services.AddSingleton<ShopSaleCompletionService>();
builder.Services.AddSingleton<ShopReceiptService>();
builder.Services.AddSingleton<ShopSalesReportingService>();
builder.Services.AddSingleton<ShortGlassMonitoringService>();
builder.Services.AddSingleton<SaleVoidService>();
builder.Services.AddSingleton<ShopSaleVoidService>();
builder.Services.AddSingleton<SalesReturnService>();
builder.Services.AddSingleton<CreditSalesReturnService>();
builder.Services.AddSingleton<CashDrawerService>();
builder.Services.AddSingleton<BusinessOperationsService>();
builder.Services.AddSingleton<SystemAdministrationService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var database = app.Services.GetRequiredService<DatabaseBootstrap>();
await database.InitializeAsync();
var initialUserSeeder = app.Services.GetRequiredService<InitialUserSeeder>();
await initialUserSeeder.SeedAsync();
var saasBootstrap = app.Services.GetRequiredService<SaasService>();
await saasBootstrap.EnsureBootstrapAsync();

app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Referrer-Policy", "no-referrer");
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
    await next();
});

app.MapGet("/api/v3/service", () => Results.Ok(new
{
    application = "Nexus POS",
    service = "Production Server",
    version = "7.0.0",
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
        "controlled-partial-sales-returns",
        "same-channel-customer-refunds",
        "return-stock-disposition",
        "immutable-sales-return-register",
        "automatic-sales-return-accounting",
        "printable-credit-notes",
        "return-aware-shift-reconciliation",
        "return-aware-sales-reporting",
        "credit-sale-return-receivable-adjustments",
        "overpaid-invoice-customer-credits",
        "customer-credit-liability-ledger",
        "customer-credit-applications",
        "non-cash-credit-note-settlements",
        "immutable-credit-return-register",
        "cash-drawer-custody-controls",
        "audited-float-and-safe-drops",
        "denomination-cash-counts",
        "manager-shift-reconciliation",
        "immutable-cash-drawer-register",
        "split-and-partial-payments",
        "cash-change-netting",
        "payment-reference-audit",
        "multi-tender-receipt-breakdown",
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
        "shop-and-consolidated-trial-balance",
        "atomic-sale-ledger-posting",
        "atomic-purchase-ledger-posting",
        "atomic-expense-ledger-posting",
        "automatic-operational-reversals",
        "immutable-operational-accounting-links",
        "customer-credit-accounts",
        "receivables-and-payables-open-items",
        "atomic-customer-receipt-posting",
        "atomic-supplier-payment-posting",
        "audited-settlement-reversals",
        "customer-and-supplier-statements",
        "receivables-and-payables-ageing",
        "ledger-derived-cashbook",
        "purchase-order-draft-submit-approval",
        "partial-goods-receipt-notes",
        "landed-cost-capitalisation",
        "batch-and-expiry-inventory",
        "audited-supplier-return-credits",
        "approved-branch-stock-counts",
        "reorder-policy-and-recommendations",
        "procurement-performance-reporting",
        "unified-finance-and-crm-customer-master",
        "customer-lifecycle-and-tagging",
        "audited-customer-communications",
        "assigned-follow-up-tasks",
        "configurable-loyalty-programme",
        "automatic-sale-loyalty-accrual",
        "automatic-sale-void-loyalty-reversal",
        "controlled-loyalty-redemption",
        "branch-numbered-customer-quotations",
        "quotation-to-sale-reconciliation",
        "customer-commercial-timeline",
        "customer-segmentation-and-dashboard",
        "organization-and-branch-workforce-master",
        "employee-login-account-linking",
        "department-and-position-management",
        "published-work-schedules",
        "audited-clock-in-and-clock-out",
        "attendance-approval-and-overtime",
        "leave-types-and-approval-workflow",
        "approved-leave-overlap-prevention",
        "payroll-period-calculation-and-approval",
        "employee-performance-reviews",
        "training-and-certification-records",
        "disciplinary-case-management",
        "workforce-dashboard-and-analytics",
        "saas-plan-and-entitlement-management",
        "organization-subscription-lifecycle",
        "safe-tenant-onboarding",
        "usage-and-limit-snapshots",
        "optional-hard-shop-and-user-limits",
        "immutable-subscription-event-ledger",
        "external-billing-event-register",
        "platform-operator-access-control",
        "time-bound-support-access-grants",
        "audited-support-case-workflow",
        "tenant-health-snapshots",
        "platform-saas-operations-dashboard",
        "enterprise-operator-command-centre",
        "role-aware-module-navigation",
        "responsive-accessible-web-shell",
        "global-module-command-palette",
        "branch-short-glass-operational-report"
    }
}));

app.MapGet("/api/v3/health", async (DatabaseBootstrap db, CancellationToken cancellationToken) =>
{
    DatabaseStatus status = await db.GetStatusAsync(cancellationToken);
    string instanceId = Environment.GetEnvironmentVariable("NEXUS_INSTANCE_ID")
        ?? Environment.GetEnvironmentVariable("ROBO_INSTANCE_ID") ?? string.Empty;
    return Results.Ok(new
    {
        ok = true,
        application = "Nexus POS",
        version = "7.0.0",
        instanceId,
        schemaVersion = status.SchemaVersion,
        database = status
    });
});

app.MapPost("/api/v3/auth/login", async (
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
    if (result.Status == LoginStatus.Success && result.User is not null &&
        result.SessionToken is not null && result.ExpiresAtUtc is not null)
    {
        http.Response.Cookies.Append("robo_session", result.SessionToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Expires = result.ExpiresAtUtc,
                IsEssential = true
            });
        return Results.Ok(new { user = result.User, expiresAtUtc = result.ExpiresAtUtc });
    }
    if (result.Status == LoginStatus.Locked)
        return Results.Json(new
        {
            error = "account_locked",
            message = "The account is temporarily locked after too many failed login attempts.",
            lockedUntilUtc = result.LockedUntilUtc
        }, statusCode: StatusCodes.Status423Locked);
    if (result.Status == LoginStatus.Disabled)
        return Results.Json(new { error = "account_disabled", message = "This account is disabled." },
            statusCode: StatusCodes.Status403Forbidden);
    return Results.Json(new { error = "invalid_credentials", message = "Invalid username or password." },
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
app.MapFinanceEndpoints();
app.MapProcurementEndpoints();
app.MapCrmEndpoints();
app.MapHrmEndpoints();
app.MapSaasEndpoints();
app.MapSalesEndpoints();
app.MapSalesReturnEndpoints();
app.MapCreditSalesReturnEndpoints();
app.MapCashDrawerEndpoints();
app.MapShortGlassMonitoringEndpoints();
app.MapAdminReferenceEndpoints();
app.MapBusinessOperationsEndpoints();
app.MapSystemAdministrationEndpoints();

app.MapGet("/", (IWebHostEnvironment environment) =>
    Results.File(Path.Combine(environment.WebRootPath, "index.html"), "text/html; charset=utf-8"));
app.MapFallbackToFile("index.html");
app.Run();
