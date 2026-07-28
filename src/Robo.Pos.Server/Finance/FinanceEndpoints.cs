using Microsoft.AspNetCore.Mvc;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Finance;

public static class FinanceEndpoints
{
    public static void MapFinanceEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v3/finance/customers",
            async Task<IResult> (
                bool? includeInactive,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<CustomerRecord> customers =
                            await finance.ListCustomersAsync(
                                user,
                                context,
                                includeInactive ?? false,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            customers,
                            count = customers.Count
                        });
                    },
                    cancellationToken));

        app.MapPost(
            "/api/v3/finance/customers",
            async Task<IResult> (
                [FromBody] CreateCustomerRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        CustomerRecord customer =
                            await finance.CreateCustomerAsync(
                                user,
                                context,
                                request,
                                cancellationToken);
                        return Results.Created(
                            $"/api/v3/finance/customers/{customer.Id}",
                            customer);
                    },
                    cancellationToken));

        app.MapPut(
            "/api/v3/finance/customers/{customerId}",
            async Task<IResult> (
                string customerId,
                [FromBody] UpdateCustomerRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await finance.UpdateCustomerAsync(
                            user,
                            context,
                            customerId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/finance/receivables",
            async Task<IResult> (
                string? customerId,
                string? status,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<ReceivableItemRecord> items =
                            await finance.ListReceivablesAsync(
                                user,
                                context,
                                customerId,
                                status,
                                limit ?? 500,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            receivables = items,
                            count = items.Count,
                            outstandingMinor = items.Sum(item => item.OutstandingAmountMinor)
                        });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/finance/payables",
            async Task<IResult> (
                string? supplierId,
                string? status,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<PayableItemRecord> items =
                            await finance.ListPayablesAsync(
                                user,
                                context,
                                supplierId,
                                status,
                                limit ?? 500,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            payables = items,
                            count = items.Count,
                            outstandingMinor = items.Sum(item => item.OutstandingAmountMinor)
                        });
                    },
                    cancellationToken));

        app.MapPost(
            "/api/v3/finance/customer-receipts",
            async Task<IResult> (
                [FromBody] CreateCustomerReceiptRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        FinanceSettlementRecord receipt =
                            await finance.CreateCustomerReceiptAsync(
                                user,
                                context,
                                request,
                                cancellationToken);
                        return Results.Created(
                            $"/api/v3/finance/customer-receipts/{receipt.Id}",
                            receipt);
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/finance/customer-receipts/{receiptId}",
            async Task<IResult> (
                string receiptId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await finance.GetCustomerReceiptAsync(
                            user,
                            context,
                            receiptId,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/finance/customer-receipts/{receiptId}/reverse",
            async Task<IResult> (
                string receiptId,
                [FromBody] ReverseSettlementRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await finance.ReverseCustomerReceiptAsync(
                            user,
                            context,
                            receiptId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/finance/supplier-payments",
            async Task<IResult> (
                [FromBody] CreateSupplierPaymentRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        FinanceSettlementRecord payment =
                            await finance.CreateSupplierPaymentAsync(
                                user,
                                context,
                                request,
                                cancellationToken);
                        return Results.Created(
                            $"/api/v3/finance/supplier-payments/{payment.Id}",
                            payment);
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/finance/supplier-payments/{paymentId}",
            async Task<IResult> (
                string paymentId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await finance.GetSupplierPaymentAsync(
                            user,
                            context,
                            paymentId,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/finance/supplier-payments/{paymentId}/reverse",
            async Task<IResult> (
                string paymentId,
                [FromBody] ReverseSettlementRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await finance.ReverseSupplierPaymentAsync(
                            user,
                            context,
                            paymentId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/finance/customers/{customerId}/statement",
            async Task<IResult> (
                string customerId,
                string? fromDate,
                string? toDate,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await finance.GetCustomerStatementAsync(
                            user,
                            context,
                            customerId,
                            fromDate,
                            toDate,
                            cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/finance/suppliers/{supplierId}/statement",
            async Task<IResult> (
                string supplierId,
                string? fromDate,
                string? toDate,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await finance.GetSupplierStatementAsync(
                            user,
                            context,
                            supplierId,
                            fromDate,
                            toDate,
                            cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/reports/receivables-ageing",
            async Task<IResult> (
                string? scope,
                string? asOfDate,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await finance.GetReceivablesAgeingAsync(
                            user,
                            context,
                            scope,
                            asOfDate,
                            cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/reports/payables-ageing",
            async Task<IResult> (
                string? scope,
                string? asOfDate,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await finance.GetPayablesAgeingAsync(
                            user,
                            context,
                            scope,
                            asOfDate,
                            cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/finance/cashbook",
            async Task<IResult> (
                string? scope,
                string? fromDate,
                string? toDate,
                string? accountSystemKey,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                FinanceService finance,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<CashbookEntryRecord> entries =
                            await finance.GetCashbookAsync(
                                user,
                                context,
                                scope,
                                fromDate,
                                toDate,
                                accountSystemKey,
                                limit ?? 1000,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            scope = scope?.Trim().ToLowerInvariant() ?? "shop",
                            entries,
                            count = entries.Count,
                            netMovementMinor = entries.Sum(item => item.SignedAmountMinor)
                        });
                    },
                    cancellationToken));
    }

    private static async Task<IResult> ExecuteAsync(
        HttpContext http,
        SessionService sessions,
        ShopContextService contexts,
        Func<AuthenticatedUser, ActiveShopContextRecord, Task<IResult>> action,
        CancellationToken cancellationToken)
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
            return await action(access.User!, context);
        }
        catch (ShopContextException exception)
        {
            return Results.Json(
                new { error = exception.ErrorCode, message = exception.Message },
                statusCode: exception.StatusCode);
        }
        catch (FinanceException exception)
        {
            return Results.Json(
                new { error = exception.ErrorCode, message = exception.Message },
                statusCode: exception.StatusCode);
        }
    }
}