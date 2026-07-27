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
                SalesService sales,
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

                ShiftRecord? shift =
                    await sales.GetOpenShiftAsync(
                        access.User!,
                        cancellationToken);

                return Results.Ok(new { shift });
            });

        app.MapPost(
            "/api/v3/shifts/open",
            async Task<IResult> (
                OpenShiftRequest request,
                HttpContext http,
                SessionService sessions,
                SalesService sales,
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
                    ShiftRecord shift =
                        await sales.OpenShiftAsync(
                            access.User!,
                            request,
                            cancellationToken);

                    return Results.Created(
                        $"/api/v3/shifts/{shift.Id}",
                        shift);
                }
                catch (SalesException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPost(
            "/api/v3/shifts/close",
            async Task<IResult> (
                CloseShiftRequest request,
                HttpContext http,
                SessionService sessions,
                SalesService sales,
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
                    ShiftRecord shift =
                        await sales.CloseShiftAsync(
                            access.User!,
                            request,
                            cancellationToken);

                    return Results.Ok(shift);
                }
                catch (SalesException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPost(
            "/api/v3/sales",
            async Task<IResult> (
                CompleteSaleRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopSalesService sales,
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
                        await contexts.GetOrCreateAsync(
                            access.User!,
                            access.SessionId!,
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
                    return Results.Json(
                        new
                        {
                            error = exception.ErrorCode,
                            message = exception.Message
                        },
                        statusCode: exception.StatusCode);
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
                VoidSaleRequest request,
                HttpContext http,
                SessionService sessions,
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
                    VoidSaleResult result =
                        await saleVoids.VoidAsync(
                            access.User!,
                            saleId,
                            request,
                            cancellationToken);

                    return Results.Ok(result);
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
                SalesService sales,
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

                IReadOnlyList<ReceiptListItem> receipts =
                    await sales.ListReceiptsAsync(
                        access.User!,
                        limit ?? 100,
                        cancellationToken);

                return Results.Ok(new
                {
                    receipts,
                    count = receipts.Count
                });
            });

        app.MapGet(
            "/api/v3/receipts/{saleId}",
            async Task<IResult> (
                string saleId,
                HttpContext http,
                SessionService sessions,
                SalesService sales,
                SaleVoidService saleVoids,
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
                    ReceiptDetails receipt =
                        await sales.GetReceiptAsync(
                            access.User!,
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
                        voidReason = voidMetadata?.VoidReason,
                        voidedAtUtc = voidMetadata?.VoidedAtUtc,
                        voidedByDisplayName = voidMetadata?.VoidedByDisplayName
                    });
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
                SalesService sales,
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
                    StoredDocumentFile document =
                        await sales.ResolveDocumentAsync(
                            access.User!,
                            saleId,
                            documentId,
                            cancellationToken);

                    return Results.File(
                        document.FullPath,
                        document.ContentType,
                        document.DownloadName,
                        enableRangeProcessing: true);
                }
                catch (SalesException exception)
                {
                    return Error(exception);
                }
            });
    }

    private static IResult Error(
        SalesException exception)
    {
        return Results.Json(
            new
            {
                error = exception.ErrorCode,
                message = exception.Message
            },
            statusCode: exception.StatusCode);
    }
}
