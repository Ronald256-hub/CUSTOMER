using Microsoft.AspNetCore.Mvc;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public static class SalesEndpoints
{
    public static void MapSalesEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/v3/shifts/current",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopShiftService shifts,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access =
                    await EndpointAccessControl.RequireUserAsync(
                        http,
                        sessions,
                        cancellationToken);

                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context =
                        await GetContextAsync(
                            access,
                            contexts,
                            cancellationToken);

                    ShiftRecord? shift =
                        await shifts.GetOpenShiftAsync(
                            access.User!,
                            context,
                            cancellationToken);

                    return Results.Ok(new
                    {
                        context.ShopId,
                        context.ShopCode,
                        context.ShopName,
                        shift
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

        app.MapPost(
            "/api/v3/shifts/open",
            async Task<IResult> (
                [FromBody] OpenShiftRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopShiftService shifts,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access =
                    await EndpointAccessControl.RequireUserAsync(
                        http,
                        sessions,
                        cancellationToken);

                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context =
                        await GetContextAsync(
                            access,
                            contexts,
                            cancellationToken);

                    ShiftRecord shift =
                        await shifts.OpenShiftAsync(
                            access.User!,
                            context,
                            request,
                            cancellationToken);

                    return Results.Created(
                        $"/api/v3/shifts/{shift.Id}",
                        shift);
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
            "/api/v3/shifts/close",
            async Task<IResult> (
                [FromBody] CloseShiftRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopShiftService shifts,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access =
                    await EndpointAccessControl.RequireUserAsync(
                        http,
                        sessions,
                        cancellationToken);

                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context =
                        await GetContextAsync(
                            access,
                            contexts,
                            cancellationToken);

                    ShiftRecord shift =
                        await shifts.CloseShiftAsync(
                            access.User!,
                            context,
                            request,
                            cancellationToken);

                    return Results.Ok(shift);
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
            "/api/v3/sales",
            async Task<IResult> (
                [FromBody] CompleteSaleRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopSaleCompletionService sales,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access =
                    await EndpointAccessControl.RequireUserAsync(
                        http,
                        sessions,
                        cancellationToken);

                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context =
                        await GetContextAsync(
                            access,
                            contexts,
                            cancellationToken);

                    CompleteSaleResult result =
                        await sales.CompleteSaleAsync(
                            access.User!,
                            context,
                            request,
                            cancellationToken);

                    return Results.Created(
                        $"/api/v3/receipts/{result.SaleId}",
                        result);
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
            "/api/v3/admin/sales/{saleId}/void",
            async Task<IResult> (
                string saleId,
                [FromBody] VoidSaleRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopReceiptService receipts,
                ShopSaleVoidService saleVoids,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access =
                    await EndpointAccessControl.RequireAdminAsync(
                        http,
                        sessions,
                        cancellationToken);

                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context =
                        await GetContextAsync(
                            access,
                            contexts,
                            cancellationToken);

                    await receipts.EnsureSaleInOrganizationAsync(
                        context.OrganizationId,
                        saleId,
                        cancellationToken);

                    VoidSaleResult result =
                        await saleVoids.VoidAsync(
                            access.User!,
                            saleId,
                            request,
                            cancellationToken);

                    return Results.Ok(result);
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
            "/api/v3/receipts",
            async Task<IResult> (
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopReceiptService receipts,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access =
                    await EndpointAccessControl.RequireUserAsync(
                        http,
                        sessions,
                        cancellationToken);

                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context =
                        await GetContextAsync(
                            access,
                            contexts,
                            cancellationToken);

                    IReadOnlyList<ReceiptListItem> records =
                        await receipts.ListReceiptsAsync(
                            access.User!,
                            context,
                            limit ?? 100,
                            cancellationToken);

                    return Results.Ok(new
                    {
                        context.ShopId,
                        context.ShopCode,
                        context.ShopName,
                        receipts = records,
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
            "/api/v3/receipts/{saleId}",
            async Task<IResult> (
                string saleId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopReceiptService receipts,
                ShopSaleVoidService saleVoids,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access =
                    await EndpointAccessControl.RequireUserAsync(
                        http,
                        sessions,
                        cancellationToken);

                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context =
                        await GetContextAsync(
                            access,
                            contexts,
                            cancellationToken);

                    ReceiptDetails receipt =
                        await receipts.GetReceiptAsync(
                            access.User!,
                            context,
                            saleId,
                            cancellationToken);

                    SaleVoidMetadata? voidMetadata =
                        await saleVoids.GetMetadataAsync(
                            saleId,
                            cancellationToken);

                    return Results.Ok(new
                    {
                        receipt.SaleId,
                        receipt.ReceiptNumber,
                        receipt.InvoiceNumber,
                        receipt.TellerName,
                        receipt.Status,
                        receipt.CustomerName,
                        receipt.CustomerPhone,
                        receipt.CustomerAddress,
                        receipt.CustomerTaxNumber,
                        receipt.SubtotalMinor,
                        receipt.DiscountMinor,
                        receipt.TotalMinor,
                        receipt.AmountReceivedMinor,
                        receipt.ChangeMinor,
                        receipt.PaymentMethod,
                        receipt.Notes,
                        receipt.CompletedAtUtc,
                        receipt.Items,
                        receipt.Documents,
                        receipt.Payments,
                        receipt.ShopId,
                        receipt.ShopCode,
                        receipt.ShopName,
                        voidReason = voidMetadata?.VoidReason,
                        voidedAtUtc = voidMetadata?.VoidedAtUtc,
                        voidedByDisplayName =
                            voidMetadata?.VoidedByDisplayName
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

        app.MapPost(
            "/api/v3/receipts/{saleId}/reprint",
            async Task<IResult> (
                string saleId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopReceiptService receipts,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access =
                    await EndpointAccessControl.RequireUserAsync(
                        http,
                        sessions,
                        cancellationToken);

                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context =
                        await GetContextAsync(
                            access,
                            contexts,
                            cancellationToken);

                    ReceiptReprintResult result =
                        await receipts.RecordReprintAsync(
                            access.User!,
                            context,
                            saleId,
                            cancellationToken);

                    return Results.Ok(result);
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
            "/api/v3/receipts/{saleId}/documents/{documentId}",
            async Task<IResult> (
                string saleId,
                string documentId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopReceiptService receipts,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access =
                    await EndpointAccessControl.RequireUserAsync(
                        http,
                        sessions,
                        cancellationToken);

                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context =
                        await GetContextAsync(
                            access,
                            contexts,
                            cancellationToken);

                    StoredDocumentFile document =
                        await receipts.ResolveDocumentAsync(
                            access.User!,
                            context,
                            saleId,
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

        app.MapGet(
            "/api/v3/reports/sales/summary",
            async Task<IResult> (
                string? scope,
                DateTimeOffset? fromUtc,
                DateTimeOffset? toUtc,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopSalesReportingService reporting,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access =
                    await EndpointAccessControl.RequireUserAsync(
                        http,
                        sessions,
                        cancellationToken);

                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    ActiveShopContextRecord context =
                        await GetContextAsync(
                            access,
                            contexts,
                            cancellationToken);

                    SalesSummaryReport report =
                        await reporting.GetSummaryAsync(
                            access.User!,
                            context,
                            scope,
                            fromUtc,
                            toUtc,
                            cancellationToken);

                    return Results.Ok(report);
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

    private static IResult Error(
        SalesException exception) =>
        Results.Json(
            new
            {
                error = exception.ErrorCode,
                message = exception.Message
            },
            statusCode: exception.StatusCode);

    private static IResult Error(
        ShopContextException exception) =>
        Results.Json(
            new
            {
                error = exception.ErrorCode,
                message = exception.Message
            },
            statusCode: exception.StatusCode);
}
