using Microsoft.AspNetCore.Mvc;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public static class CreditSalesReturnEndpoints
{
    public static void MapCreditSalesReturnEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v3/finance/credit-returns/eligible",
            async Task<IResult> (
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CreditSalesReturnService returns,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<CreditReturnableSaleListItem> sales =
                            await returns.ListEligibleCreditSalesAsync(
                                user,
                                context,
                                limit ?? 100,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            sales,
                            count = sales.Count
                        });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/finance/credit-returns/sales/{saleId}",
            async Task<IResult> (
                string saleId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CreditSalesReturnService returns,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await returns.GetReturnableCreditSaleAsync(
                            user,
                            context,
                            saleId,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/finance/credit-returns/sales/{saleId}",
            async Task<IResult> (
                string saleId,
                [FromBody] CreateCreditSalesReturnRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CreditSalesReturnService returns,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        CreditSalesReturnRecord record =
                            await returns.CreateCreditReturnAsync(
                                user,
                                context,
                                saleId,
                                request,
                                cancellationToken);
                        return Results.Created(
                            $"/api/v3/finance/credit-returns/{record.Id}",
                            record);
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/finance/credit-returns",
            async Task<IResult> (
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CreditSalesReturnService returns,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<CreditSalesReturnRecord> records =
                            await returns.ListCreditReturnsAsync(
                                user,
                                context,
                                limit ?? 100,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            returns = records,
                            count = records.Count,
                            returnedMinor = records.Sum(
                                item => item.ReturnAmountMinor),
                            receivableReductionMinor = records.Sum(
                                item => item.ReceivableReductionMinor),
                            customerCreditMinor = records.Sum(
                                item => item.CustomerCreditMinor)
                        });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/finance/credit-returns/{returnId}",
            async Task<IResult> (
                string returnId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CreditSalesReturnService returns,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await returns.GetCreditReturnAsync(
                            user,
                            context,
                            returnId,
                            cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/finance/credit-returns/{returnId}/documents/{documentId}",
            async Task<IResult> (
                string returnId,
                string documentId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CreditSalesReturnService returns,
                CancellationToken cancellationToken) =>
            {
                try
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

                    ActiveShopContextRecord context =
                        await contexts.GetOrCreateAsync(
                            access.User!,
                            access.SessionId!,
                            cancellationToken);
                    StoredSalesReturnDocument document =
                        await returns.ResolveDocumentAsync(
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

        app.MapGet(
            "/api/v3/finance/customer-credits",
            async Task<IResult> (
                string? customerId,
                string? status,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CreditSalesReturnService returns,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<CustomerCreditBalanceRecord> credits =
                            await returns.ListCustomerCreditsAsync(
                                user,
                                context,
                                customerId,
                                status,
                                limit ?? 200,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            credits,
                            count = credits.Count,
                            availableMinor = credits.Sum(
                                item => item.AvailableAmountMinor)
                        });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/finance/customer-credit-applications",
            async Task<IResult> (
                string? customerId,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CreditSalesReturnService returns,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<CustomerCreditApplicationRecord> applications =
                            await returns.ListCreditApplicationsAsync(
                                user,
                                context,
                                customerId,
                                limit ?? 200,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            applications,
                            count = applications.Count,
                            appliedMinor = applications.Sum(
                                item => item.AmountMinor)
                        });
                    },
                    cancellationToken));

        app.MapPost(
            "/api/v3/finance/customer-credit-applications",
            async Task<IResult> (
                [FromBody] ApplyCustomerCreditRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CreditSalesReturnService returns,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        CustomerCreditApplicationRecord application =
                            await returns.ApplyCustomerCreditAsync(
                                user,
                                context,
                                request,
                                cancellationToken);
                        return Results.Created(
                            $"/api/v3/finance/customer-credit-applications/{application.Id}",
                            application);
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
                await contexts.GetOrCreateAsync(
                    access.User!,
                    access.SessionId!,
                    cancellationToken);
            return await action(access.User!, context);
        }
        catch (ShopContextException exception)
        {
            return Error(exception);
        }
        catch (SalesException exception)
        {
            return Error(exception);
        }
    }

    private static IResult Error(SalesException exception) =>
        Results.Json(
            new
            {
                error = exception.ErrorCode,
                message = exception.Message
            },
            statusCode: exception.StatusCode);

    private static IResult Error(ShopContextException exception) =>
        Results.Json(
            new
            {
                error = exception.ErrorCode,
                message = exception.Message
            },
            statusCode: exception.StatusCode);
}
