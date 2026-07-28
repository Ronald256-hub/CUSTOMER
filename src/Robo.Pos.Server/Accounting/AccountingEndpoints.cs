using Microsoft.AspNetCore.Mvc;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Accounting;

public static class AccountingEndpoints
{
    public static void MapAccountingEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v3/accounting/accounts",
            async Task<IResult> (
                bool? includeInactive,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
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
                    IReadOnlyList<AccountingAccountRecord> accounts =
                        await accounting.ListAccountsAsync(
                            access.User!,
                            context,
                            includeInactive ?? false,
                            cancellationToken);
                    return Results.Ok(new
                    {
                        context.OrganizationId,
                        accounts,
                        count = accounts.Count
                    });
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (AccountingException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPost(
            "/api/v3/accounting/accounts",
            async Task<IResult> (
                [FromBody] CreateAccountingAccountRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    (user, context) => accounting.CreateAccountAsync(
                        user,
                        context,
                        request,
                        cancellationToken),
                    cancellationToken));

        app.MapPut(
            "/api/v3/accounting/accounts/{accountId}",
            async Task<IResult> (
                string accountId,
                [FromBody] UpdateAccountingAccountRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    (user, context) => accounting.UpdateAccountAsync(
                        user,
                        context,
                        accountId,
                        request,
                        cancellationToken),
                    cancellationToken));

        app.MapGet(
            "/api/v3/accounting/periods",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
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
                    IReadOnlyList<AccountingPeriodRecord> periods =
                        await accounting.ListPeriodsAsync(
                            access.User!,
                            context,
                            cancellationToken);
                    return Results.Ok(new
                    {
                        context.OrganizationId,
                        periods,
                        count = periods.Count
                    });
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (AccountingException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPost(
            "/api/v3/accounting/periods",
            async Task<IResult> (
                [FromBody] CreateAccountingPeriodRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    (user, context) => accounting.CreatePeriodAsync(
                        user,
                        context,
                        request,
                        cancellationToken),
                    cancellationToken));

        app.MapPost(
            "/api/v3/accounting/periods/{periodId}/close",
            async Task<IResult> (
                string periodId,
                [FromBody] CloseAccountingPeriodRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    (user, context) => accounting.ClosePeriodAsync(
                        user,
                        context,
                        periodId,
                        request,
                        cancellationToken),
                    cancellationToken));

        app.MapGet(
            "/api/v3/accounting/journals",
            async Task<IResult> (
                string? scope,
                string? status,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
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
                    IReadOnlyList<AccountingJournalListItem> journals =
                        await accounting.ListJournalsAsync(
                            access.User!,
                            context,
                            scope,
                            status,
                            limit ?? 100,
                            cancellationToken);
                    return Results.Ok(new
                    {
                        context.OrganizationId,
                        context.ShopId,
                        context.ShopCode,
                        scope = scope?.Trim().ToLowerInvariant() ?? "shop",
                        journals,
                        count = journals.Count
                    });
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
                catch (AccountingException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/accounting/journals/{journalId}",
            async Task<IResult> (
                string journalId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    (user, context) => accounting.GetJournalAsync(
                        user,
                        context,
                        journalId,
                        cancellationToken),
                    cancellationToken));

        app.MapPost(
            "/api/v3/accounting/journals",
            async Task<IResult> (
                [FromBody] CreateAccountingJournalRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    (user, context) => accounting.CreateJournalAsync(
                        user,
                        context,
                        request,
                        cancellationToken),
                    cancellationToken));

        app.MapPut(
            "/api/v3/accounting/journals/{journalId}",
            async Task<IResult> (
                string journalId,
                [FromBody] UpdateAccountingJournalRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    (user, context) => accounting.UpdateJournalAsync(
                        user,
                        context,
                        journalId,
                        request,
                        cancellationToken),
                    cancellationToken));

        app.MapPost(
            "/api/v3/accounting/journals/{journalId}/post",
            async Task<IResult> (
                string journalId,
                [FromBody] PostAccountingJournalRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    (user, context) => accounting.PostJournalAsync(
                        user,
                        context,
                        journalId,
                        request,
                        cancellationToken),
                    cancellationToken));

        app.MapPost(
            "/api/v3/accounting/journals/{journalId}/reverse",
            async Task<IResult> (
                string journalId,
                [FromBody] ReverseAccountingJournalRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    (user, context) => accounting.ReverseJournalAsync(
                        user,
                        context,
                        journalId,
                        request,
                        cancellationToken),
                    cancellationToken));

        app.MapGet(
            "/api/v3/reports/trial-balance",
            async Task<IResult> (
                string? scope,
                string? fromDate,
                string? toDate,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                AccountingService accounting,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    (user, context) => accounting.GetTrialBalanceAsync(
                        user,
                        context,
                        scope,
                        fromDate,
                        toDate,
                        cancellationToken),
                    cancellationToken));
    }

    private static async Task<IResult> ExecuteAsync<T>(
        HttpContext http,
        SessionService sessions,
        ShopContextService contexts,
        Func<AuthenticatedUser, ActiveShopContextRecord, Task<T>> action,
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
        catch (AccountingException exception)
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

    private static IResult Error(AccountingException exception) =>
        Results.Json(
            new
            {
                error = exception.ErrorCode,
                message = exception.Message
            },
            statusCode: exception.StatusCode);
}
