using Microsoft.AspNetCore.Mvc;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public static class SalesReturnEndpoints
{
    public static void MapSalesReturnEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v3/sales/returns/eligible",
            async Task<IResult> (
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SalesReturnService returns,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access = await EndpointAccessControl.RequireAdminAsync(
                    http,
                    sessions,
                    cancellationToken);
                if (!access.IsAllowed) return access.Failure!;

                try
                {
                    ActiveShopContextRecord context = await GetContextAsync(
                        access,
                        contexts,
                        cancellationToken);
                    IReadOnlyList<ReturnableSaleListItem> records =
                        await returns.ListEligibleSalesAsync(
                            access.User!,
                            context,
                            limit ?? 100,
                            cancellationToken);
                    return Results.Ok(new
                    {
                        context.ShopId,
                        context.ShopCode,
                        context.ShopName,
                        sales = records,
                        count = records.Count
                    });
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (SalesException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/sales/{saleId}/returnable",
            async Task<IResult> (
                string saleId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SalesReturnService returns,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access = await EndpointAccessControl.RequireAdminAsync(
                    http,
                    sessions,
                    cancellationToken);
                if (!access.IsAllowed) return access.Failure!;

                try
                {
                    ActiveShopContextRecord context = await GetContextAsync(
                        access,
                        contexts,
                        cancellationToken);
                    ReturnableSaleDetails record = await returns.GetReturnableSaleAsync(
                        access.User!,
                        context,
                        saleId,
                        cancellationToken);
                    return Results.Ok(record);
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (SalesException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPost(
            "/api/v3/sales/{saleId}/returns",
            async Task<IResult> (
                string saleId,
                [FromBody] CreateSalesReturnRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SalesReturnService returns,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access = await EndpointAccessControl.RequireAdminAsync(
                    http,
                    sessions,
                    cancellationToken);
                if (!access.IsAllowed) return access.Failure!;

                try
                {
                    ActiveShopContextRecord context = await GetContextAsync(
                        access,
                        contexts,
                        cancellationToken);
                    SalesReturnRecord record = await returns.CreateReturnAsync(
                        access.User!,
                        context,
                        saleId,
                        request,
                        cancellationToken);
                    return Results.Created(
                        $"/api/v3/sales/returns/{record.Id}",
                        record);
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (SalesException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/sales/returns",
            async Task<IResult> (
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SalesReturnService returns,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access = await EndpointAccessControl.RequireAdminAsync(
                    http,
                    sessions,
                    cancellationToken);
                if (!access.IsAllowed) return access.Failure!;

                try
                {
                    ActiveShopContextRecord context = await GetContextAsync(
                        access,
                        contexts,
                        cancellationToken);
                    IReadOnlyList<SalesReturnRecord> records = await returns.ListReturnsAsync(
                        access.User!,
                        context,
                        limit ?? 100,
                        cancellationToken);
                    return Results.Ok(new
                    {
                        context.ShopId,
                        context.ShopCode,
                        context.ShopName,
                        returns = records,
                        count = records.Count
                    });
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (SalesException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/sales/returns/{returnId}",
            async Task<IResult> (
                string returnId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SalesReturnService returns,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access = await EndpointAccessControl.RequireAdminAsync(
                    http,
                    sessions,
                    cancellationToken);
                if (!access.IsAllowed) return access.Failure!;

                try
                {
                    ActiveShopContextRecord context = await GetContextAsync(
                        access,
                        contexts,
                        cancellationToken);
                    SalesReturnRecord record = await returns.GetReturnAsync(
                        access.User!,
                        context,
                        returnId,
                        cancellationToken);
                    return Results.Ok(record);
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (SalesException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/sales/returns/{returnId}/documents/{documentId}",
            async Task<IResult> (
                string returnId,
                string documentId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                SalesReturnService returns,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access = await EndpointAccessControl.RequireAdminAsync(
                    http,
                    sessions,
                    cancellationToken);
                if (!access.IsAllowed) return access.Failure!;

                try
                {
                    ActiveShopContextRecord context = await GetContextAsync(
                        access,
                        contexts,
                        cancellationToken);
                    StoredSalesReturnDocument document = await returns.ResolveDocumentAsync(
                        access.User!,
                        context,
                        returnId,
                        documentId,
                        cancellationToken);
                    return Results.File(
                        document.FullPath,
                        document.ContentType,
                        document.DownloadName,
                        enableRangeProcessing: true);
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (SalesException exception)
                {
                    return Error(exception);
                }
            });
    }

    private static Task<ActiveShopContextRecord> GetContextAsync(
        EndpointAccessDecision access,
        ShopContextService contexts,
        CancellationToken cancellationToken) =>
        contexts.GetOrCreateAsync(
            access.User!,
            access.SessionId!,
            cancellationToken);

    private static IResult Error(SalesException exception) =>
        Results.Json(
            new { error = exception.ErrorCode, message = exception.Message },
            statusCode: exception.StatusCode);

    private static IResult Error(ShopContextException exception) =>
        Results.Json(
            new { error = exception.ErrorCode, message = exception.Message },
            statusCode: exception.StatusCode);
}
