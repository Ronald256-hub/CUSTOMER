using Microsoft.AspNetCore.Mvc;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public static class CashDrawerEndpoints
{
    public static void MapCashDrawerEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v3/cash-drawer/current",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CashDrawerService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(http, sessions, contexts,
                    (user, context) => service.GetCurrentAsync(user, context, cancellationToken),
                    cancellationToken));

        app.MapPost(
            "/api/v3/cash-drawer/movements",
            async Task<IResult> (
                [FromBody] CreateCashDrawerMovementRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CashDrawerService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(http, sessions, contexts,
                    (user, context) => service.CreateMovementAsync(user, context, request, cancellationToken),
                    cancellationToken,
                    created: true));

        app.MapPost(
            "/api/v3/cash-drawer/counts",
            async Task<IResult> (
                [FromBody] RecordCashCountRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CashDrawerService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(http, sessions, contexts,
                    (user, context) => service.RecordCountAsync(user, context, request, cancellationToken),
                    cancellationToken,
                    created: true));

        app.MapGet(
            "/api/v3/admin/cash-drawer/reconciliations",
            async Task<IResult> (
                string? status,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CashDrawerService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(http, sessions, contexts,
                    (user, context) => service.ListReviewsAsync(user, context, status, cancellationToken),
                    cancellationToken,
                    requireAdmin: true));

        app.MapPost(
            "/api/v3/admin/cash-drawer/reconciliations/{shiftId}/review",
            async Task<IResult> (
                string shiftId,
                [FromBody] ReviewShiftReconciliationRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CashDrawerService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(http, sessions, contexts,
                    (user, context) => service.ReviewAsync(user, context, shiftId, request, cancellationToken),
                    cancellationToken,
                    requireAdmin: true));
    }

    private static async Task<IResult> ExecuteAsync<T>(
        HttpContext http,
        SessionService sessions,
        ShopContextService contexts,
        Func<AuthenticatedUser, ActiveShopContextRecord, Task<T>> action,
        CancellationToken cancellationToken,
        bool requireAdmin = false,
        bool created = false)
    {
        EndpointAccessDecision access = requireAdmin
            ? await EndpointAccessControl.RequireAdminAsync(http, sessions, cancellationToken)
            : await EndpointAccessControl.RequireUserAsync(http, sessions, cancellationToken);
        if (!access.IsAllowed) return access.Failure!;

        try
        {
            ActiveShopContextRecord context = await contexts.GetOrCreateAsync(
                access.User!, access.SessionId!, cancellationToken);
            T result = await action(access.User!, context);
            return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
        }
        catch (ShopContextException exception)
        {
            return Results.Json(new { error = exception.ErrorCode, message = exception.Message },
                statusCode: exception.StatusCode);
        }
        catch (CashDrawerException exception)
        {
            return Results.Json(new { error = exception.ErrorCode, message = exception.Message },
                statusCode: exception.StatusCode);
        }
    }
}