using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Saas;

public static class SaasEndpoints
{
    public static void MapSaasEndpoints(this WebApplication app)
    {
        RouteGroupBuilder tenant = app.MapGroup("/api/v3/saas/tenant");

        tenant.MapGet(
            "/subscription",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteTenantAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await saas.GetCurrentSubscriptionAsync(user, context, cancellationToken)),
                    cancellationToken));

        tenant.MapGet(
            "/entitlements",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteTenantAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<SaasEntitlementRecord> entitlements =
                            await saas.GetCurrentEntitlementsAsync(user, context, cancellationToken);
                        return Results.Ok(new { entitlements, count = entitlements.Count });
                    },
                    cancellationToken));

        tenant.MapPost(
            "/usage-snapshots",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteTenantAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/saas/tenant/usage-snapshots",
                        await saas.CaptureUsageAsync(user, context, cancellationToken)),
                    cancellationToken));

        tenant.MapGet(
            "/usage-snapshots",
            async Task<IResult> (
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteTenantAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<SaasUsageSnapshotRecord> snapshots =
                            await saas.ListUsageAsync(user, context, limit ?? 100, cancellationToken);
                        return Results.Ok(new { snapshots, count = snapshots.Count });
                    },
                    cancellationToken));

        tenant.MapPost(
            "/health-snapshots",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteTenantAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/saas/tenant/health-snapshots",
                        await saas.CaptureHealthAsync(user, context, cancellationToken)),
                    cancellationToken));

        tenant.MapGet(
            "/billing-events",
            async Task<IResult> (
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteTenantAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<SaasBillingEventRecord> events =
                            await saas.ListCurrentBillingEventsAsync(user, context, limit ?? 100, cancellationToken);
                        return Results.Ok(new { billingEvents = events, count = events.Count });
                    },
                    cancellationToken));

        tenant.MapPost(
            "/support-cases",
            async Task<IResult> (
                [FromBody] CreateSaasSupportCaseRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteTenantAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        SaasSupportCaseRecord supportCase =
                            await saas.CreateSupportCaseAsync(user, context, request, cancellationToken);
                        return Results.Created(
                            $"/api/v3/saas/tenant/support-cases/{supportCase.Id}",
                            supportCase);
                    },
                    cancellationToken));

        tenant.MapGet(
            "/support-cases",
            async Task<IResult> (
                string? status,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteTenantAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<SaasSupportCaseRecord> cases =
                            await saas.ListCurrentSupportCasesAsync(
                                user, context, status, limit ?? 200, cancellationToken);
                        return Results.Ok(new { supportCases = cases, count = cases.Count });
                    },
                    cancellationToken));

        tenant.MapGet(
            "/support-cases/{caseId}/events",
            async Task<IResult> (
                string caseId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteTenantAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<SaasSupportCaseEventRecord> events =
                            await saas.ListSupportCaseEventsAsync(
                                user, context, caseId, cancellationToken);
                        return Results.Ok(new { events, count = events.Count });
                    },
                    cancellationToken));

        tenant.MapPost(
            "/support-grants",
            async Task<IResult> (
                [FromBody] CreateSaasSupportGrantRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteTenantAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/saas/tenant/support-grants",
                        await saas.CreateSupportGrantAsync(user, context, request, cancellationToken)),
                    cancellationToken));

        tenant.MapGet(
            "/support-grants",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteTenantAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<SaasSupportAccessGrantRecord> grants =
                            await saas.ListSupportGrantsAsync(user, context, cancellationToken);
                        return Results.Ok(new { grants, count = grants.Count });
                    },
                    cancellationToken));

        tenant.MapPost(
            "/support-grants/{grantId}/revoke",
            async Task<IResult> (
                string grantId,
                [FromBody] RevokeSaasSupportGrantRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteTenantAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await saas.RevokeSupportGrantAsync(
                            user, context, grantId, request, cancellationToken)),
                    cancellationToken));

        RouteGroupBuilder platform = app.MapGroup("/api/v3/saas/platform");

        platform.MapGet(
            "/plans",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteUserAsync(
                    http, sessions,
                    async user =>
                    {
                        IReadOnlyList<SaasPlanRecord> plans =
                            await saas.ListPlansAsync(user, cancellationToken);
                        return Results.Ok(new { plans, count = plans.Count });
                    },
                    cancellationToken));

        platform.MapPost(
            "/plans",
            async Task<IResult> (
                [FromBody] CreateSaasPlanRequest request,
                HttpContext http,
                SessionService sessions,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteUserAsync(
                    http, sessions,
                    async user =>
                    {
                        SaasPlanRecord plan = await saas.CreatePlanAsync(user, request, cancellationToken);
                        return Results.Created($"/api/v3/saas/platform/plans/{plan.Id}", plan);
                    },
                    cancellationToken));

        platform.MapPut(
            "/plans/{planId}",
            async Task<IResult> (
                string planId,
                [FromBody] UpdateSaasPlanRequest request,
                HttpContext http,
                SessionService sessions,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteUserAsync(
                    http, sessions,
                    async user => Results.Ok(
                        await saas.UpdatePlanAsync(user, planId, request, cancellationToken)),
                    cancellationToken));

        platform.MapPut(
            "/plans/{planId}/entitlements/{entitlementKey}",
            async Task<IResult> (
                string planId,
                string entitlementKey,
                [FromBody] SetSaasEntitlementRequest request,
                HttpContext http,
                SessionService sessions,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteUserAsync(
                    http, sessions,
                    async user => Results.Ok(
                        await saas.SetPlanEntitlementAsync(
                            user, planId, entitlementKey, request, cancellationToken)),
                    cancellationToken));

        platform.MapGet(
            "/tenants",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteUserAsync(
                    http, sessions,
                    async user =>
                    {
                        IReadOnlyList<SaasTenantSummaryRecord> tenants =
                            await saas.ListTenantsAsync(user, cancellationToken);
                        return Results.Ok(new { tenants, count = tenants.Count });
                    },
                    cancellationToken));

        platform.MapPost(
            "/tenants",
            async Task<IResult> (
                [FromBody] OnboardSaasTenantRequest request,
                HttpContext http,
                SessionService sessions,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteUserAsync(
                    http, sessions,
                    async user =>
                    {
                        SaasTenantSummaryRecord tenantRecord =
                            await saas.OnboardTenantAsync(user, request, cancellationToken);
                        return Results.Created(
                            $"/api/v3/saas/platform/tenants/{tenantRecord.OrganizationId}",
                            tenantRecord);
                    },
                    cancellationToken));

        platform.MapPut(
            "/tenants/{organizationId}/subscription",
            async Task<IResult> (
                string organizationId,
                [FromBody] UpdateSaasSubscriptionRequest request,
                HttpContext http,
                SessionService sessions,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteUserAsync(
                    http, sessions,
                    async user => Results.Ok(
                        await saas.UpdateSubscriptionAsync(
                            user, organizationId, request, cancellationToken)),
                    cancellationToken));

        platform.MapPost(
            "/tenants/{organizationId}/billing-events",
            async Task<IResult> (
                string organizationId,
                [FromBody] CreateSaasBillingEventRequest request,
                HttpContext http,
                SessionService sessions,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteUserAsync(
                    http, sessions,
                    async user => Results.Created(
                        "/api/v3/saas/platform/billing-events",
                        await saas.CreateBillingEventAsync(
                            user, organizationId, request, cancellationToken)),
                    cancellationToken));

        platform.MapGet(
            "/support-cases",
            async Task<IResult> (
                string? organizationId,
                string? status,
                int? limit,
                HttpContext http,
                SessionService sessions,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteUserAsync(
                    http, sessions,
                    async user =>
                    {
                        IReadOnlyList<SaasSupportCaseRecord> cases =
                            await saas.ListPlatformSupportCasesAsync(
                                user, organizationId, status, limit ?? 500, cancellationToken);
                        return Results.Ok(new { supportCases = cases, count = cases.Count });
                    },
                    cancellationToken));

        platform.MapPut(
            "/support-cases/{caseId}",
            async Task<IResult> (
                string caseId,
                [FromBody] UpdateSaasSupportCaseRequest request,
                HttpContext http,
                SessionService sessions,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteUserAsync(
                    http, sessions,
                    async user => Results.Ok(
                        await saas.UpdateSupportCaseAsync(user, caseId, request, cancellationToken)),
                    cancellationToken));

        platform.MapPost(
            "/support-cases/{caseId}/notes",
            async Task<IResult> (
                string caseId,
                [FromBody] AddSaasSupportCaseNoteRequest request,
                HttpContext http,
                SessionService sessions,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteUserAsync(
                    http, sessions,
                    async user => Results.Created(
                        $"/api/v3/saas/platform/support-cases/{caseId}/notes",
                        await saas.AddSupportCaseNoteAsync(
                            user, caseId, request, cancellationToken)),
                    cancellationToken));

        platform.MapGet(
            "/dashboard",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                SaasService saas,
                CancellationToken cancellationToken) =>
                await ExecuteUserAsync(
                    http, sessions,
                    async user => Results.Ok(
                        await saas.GetPlatformDashboardAsync(user, cancellationToken)),
                    cancellationToken));
    }

    private static async Task<IResult> ExecuteTenantAsync(
        HttpContext http,
        SessionService sessions,
        ShopContextService contexts,
        Func<AuthenticatedUser, ActiveShopContextRecord, Task<IResult>> action,
        CancellationToken cancellationToken)
    {
        EndpointAccessDecision access = await EndpointAccessControl.RequireUserAsync(
            http, sessions, cancellationToken);
        if (!access.IsAllowed) return access.Failure!;
        try
        {
            ActiveShopContextRecord context = await contexts.GetOrCreateAsync(
                access.User!, access.SessionId!, cancellationToken);
            return await action(access.User!, context);
        }
        catch (ShopContextException exception)
        {
            return Results.Json(
                new { error = exception.ErrorCode, message = exception.Message },
                statusCode: exception.StatusCode);
        }
        catch (SaasException exception)
        {
            return Error(exception);
        }
        catch (SqliteException exception) when (
            exception.Message.Contains("saas_active_shop_limit_exceeded", StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "saas_active_shop_limit_exceeded", message = "The active-shop limit for this subscription has been reached." },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (SqliteException exception) when (
            exception.Message.Contains("saas_active_user_limit_exceeded", StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "saas_active_user_limit_exceeded", message = "The active-user limit for this subscription has been reached." },
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ExecuteUserAsync(
        HttpContext http,
        SessionService sessions,
        Func<AuthenticatedUser, Task<IResult>> action,
        CancellationToken cancellationToken)
    {
        EndpointAccessDecision access = await EndpointAccessControl.RequireUserAsync(
            http, sessions, cancellationToken);
        if (!access.IsAllowed) return access.Failure!;
        try
        {
            return await action(access.User!);
        }
        catch (SaasException exception)
        {
            return Error(exception);
        }
        catch (SqliteException exception) when (
            exception.Message.Contains("saas_active_shop_limit_exceeded", StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "saas_active_shop_limit_exceeded", message = "The active-shop limit for this subscription has been reached." },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (SqliteException exception) when (
            exception.Message.Contains("saas_active_user_limit_exceeded", StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "saas_active_user_limit_exceeded", message = "The active-user limit for this subscription has been reached." },
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult Error(SaasException exception) =>
        Results.Json(
            new { error = exception.ErrorCode, message = exception.Message },
            statusCode: exception.StatusCode);
}
