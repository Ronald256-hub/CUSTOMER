using Microsoft.AspNetCore.Mvc;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Inventory;

public static class StockTransferEndpoints
{
    public static void MapStockTransferEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v3/stock-transfers",
            async Task<IResult> (
                string? scope,
                string? status,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferService transfers,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access = await RequireAsync(
                    http,
                    sessions,
                    cancellationToken);
                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context = await GetContextAsync(
                        access,
                        contexts,
                        cancellationToken);
                    IReadOnlyList<StockTransferListItem> results =
                        await transfers.ListAsync(
                            access.User!,
                            context,
                            scope,
                            status,
                            limit ?? 100,
                            cancellationToken);
                    return Results.Ok(new
                    {
                        context.ShopId,
                        context.ShopCode,
                        scope = scope?.Trim().ToLowerInvariant() ?? "shop",
                        transfers = results,
                        count = results.Count
                    });
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (StockTransferException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/stock-transfers/{transferId}",
            async Task<IResult> (
                string transferId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferService transfers,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access = await RequireAsync(
                    http,
                    sessions,
                    cancellationToken);
                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context = await GetContextAsync(
                        access,
                        contexts,
                        cancellationToken);
                    return Results.Ok(await transfers.GetAsync(
                        access.User!,
                        context,
                        transferId,
                        cancellationToken));
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (StockTransferException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPost(
            "/api/v3/stock-transfers",
            async Task<IResult> (
                [FromBody] CreateStockTransferRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferService transfers,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access = await RequireAsync(
                    http,
                    sessions,
                    cancellationToken);
                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context = await GetContextAsync(
                        access,
                        contexts,
                        cancellationToken);
                    StockTransferRecord created = await transfers.CreateDraftAsync(
                        access.User!,
                        context,
                        request,
                        cancellationToken);
                    return Results.Created(
                        $"/api/v3/stock-transfers/{created.Id}",
                        created);
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (StockTransferException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPut(
            "/api/v3/stock-transfers/{transferId}",
            async Task<IResult> (
                string transferId,
                [FromBody] UpdateStockTransferDraftRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferService transfers,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    transfers,
                    (user, context) => transfers.UpdateDraftAsync(
                        user,
                        context,
                        transferId,
                        request,
                        cancellationToken),
                    cancellationToken);
            });

        app.MapPost(
            "/api/v3/stock-transfers/{transferId}/submit",
            async Task<IResult> (
                string transferId,
                [FromBody] StockTransferTransitionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferService transfers,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    transfers,
                    (user, context) => transfers.SubmitAsync(
                        user,
                        context,
                        transferId,
                        request,
                        cancellationToken),
                    cancellationToken);
            });

        app.MapPost(
            "/api/v3/stock-transfers/{transferId}/approve",
            async Task<IResult> (
                string transferId,
                [FromBody] StockTransferTransitionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferService transfers,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    transfers,
                    (user, context) => transfers.ApproveAsync(
                        user,
                        context,
                        transferId,
                        request,
                        cancellationToken),
                    cancellationToken);
            });

        app.MapPost(
            "/api/v3/stock-transfers/{transferId}/reject",
            async Task<IResult> (
                string transferId,
                [FromBody] CancelStockTransferRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferService transfers,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    transfers,
                    (user, context) => transfers.RejectAsync(
                        user,
                        context,
                        transferId,
                        request,
                        cancellationToken),
                    cancellationToken);
            });

        app.MapPost(
            "/api/v3/stock-transfers/{transferId}/dispatch",
            async Task<IResult> (
                string transferId,
                [FromBody] StockTransferTransitionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferService transfers,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    transfers,
                    (user, context) => transfers.DispatchAsync(
                        user,
                        context,
                        transferId,
                        request,
                        cancellationToken),
                    cancellationToken);
            });

        app.MapPost(
            "/api/v3/stock-transfers/{transferId}/receive",
            async Task<IResult> (
                string transferId,
                [FromBody] ReceiveStockTransferRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferService transfers,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    transfers,
                    (user, context) => transfers.ReceiveAsync(
                        user,
                        context,
                        transferId,
                        request,
                        cancellationToken),
                    cancellationToken);
            });

        app.MapPost(
            "/api/v3/stock-transfers/{transferId}/cancel",
            async Task<IResult> (
                string transferId,
                [FromBody] CancelStockTransferRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferService transfers,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    transfers,
                    (user, context) => transfers.CancelAsync(
                        user,
                        context,
                        transferId,
                        request,
                        cancellationToken),
                    cancellationToken);
            });

        app.MapGet(
            "/api/v3/stock-transfers/{transferId}/document",
            async Task<IResult> (
                string transferId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferService transfers,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access = await RequireAsync(
                    http,
                    sessions,
                    cancellationToken);
                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context = await GetContextAsync(
                        access,
                        contexts,
                        cancellationToken);
                    string html = await transfers.BuildDocumentHtmlAsync(
                        access.User!,
                        context,
                        transferId,
                        cancellationToken);
                    return Results.Content(
                        html,
                        "text/html; charset=utf-8");
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (StockTransferException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/reports/stock-transfers",
            async Task<IResult> (
                string? scope,
                DateTimeOffset? fromUtc,
                DateTimeOffset? toUtc,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferService transfers,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access = await RequireAsync(
                    http,
                    sessions,
                    cancellationToken);
                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context = await GetContextAsync(
                        access,
                        contexts,
                        cancellationToken);
                    return Results.Ok(await transfers.GetReportAsync(
                        access.User!,
                        context,
                        scope,
                        fromUtc,
                        toUtc,
                        cancellationToken));
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (StockTransferException exception)
                {
                    return Error(exception);
                }
            });
    }

    private static async Task<IResult> ExecuteAsync(
        HttpContext http,
        SessionService sessions,
        ShopContextService contexts,
        StockTransferService transfers,
        Func<AuthenticatedUser, ActiveShopContextRecord, Task<StockTransferRecord>> action,
        CancellationToken cancellationToken)
    {
        EndpointAccessDecision access = await RequireAsync(
            http,
            sessions,
            cancellationToken);
        if (!access.IsAllowed)
        {
            return access.Failure!;
        }

        try
        {
            ActiveShopContextRecord context = await GetContextAsync(
                access,
                contexts,
                cancellationToken);
            return Results.Ok(await action(access.User!, context));
        }
        catch (ShopContextException exception)
        {
            return Error(exception);
        }
        catch (StockTransferException exception)
        {
            return Error(exception);
        }
    }

    private static Task<EndpointAccessDecision> RequireAsync(
        HttpContext http,
        SessionService sessions,
        CancellationToken cancellationToken) =>
        EndpointAccessControl.RequireUserAsync(
            http,
            sessions,
            cancellationToken);

    private static Task<ActiveShopContextRecord> GetContextAsync(
        EndpointAccessDecision access,
        ShopContextService contexts,
        CancellationToken cancellationToken) =>
        contexts.GetOrCreateAsync(
            access.User!,
            access.SessionId!,
            cancellationToken);

    private static IResult Error(ShopContextException exception) =>
        Results.Json(
            new
            {
                error = exception.ErrorCode,
                message = exception.Message
            },
            statusCode: exception.StatusCode);

    private static IResult Error(StockTransferException exception) =>
        Results.Json(
            new
            {
                error = exception.ErrorCode,
                message = exception.Message
            },
            statusCode: exception.StatusCode);
}