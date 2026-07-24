using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Business;

public static class BusinessOperationsEndpoints
{
    public static void MapBusinessOperationsEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/v3/admin/suppliers",
            async Task<IResult> (
                bool? includeInactive,
                HttpContext http,
                SessionService sessions,
                BusinessOperationsService service,
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

                IReadOnlyList<SupplierResult> suppliers =
                    await service.ListSuppliersAsync(
                        includeInactive ?? false,
                        cancellationToken);

                return Results.Ok(new
                {
                    suppliers,
                    count = suppliers.Count
                });
            });

        app.MapPost(
            "/api/v3/admin/suppliers",
            async Task<IResult> (
                CreateSupplierRequest request,
                HttpContext http,
                SessionService sessions,
                BusinessOperationsService service,
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
                    SupplierResult supplier =
                        await service.CreateSupplierAsync(
                            access.User!,
                            request,
                            cancellationToken);

                    return Results.Created(
                        $"/api/v3/admin/suppliers/{supplier.Id}",
                        supplier);
                }
                catch (BusinessOperationsException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPut(
            "/api/v3/admin/suppliers/{supplierId}",
            async Task<IResult> (
                string supplierId,
                UpdateSupplierRequest request,
                HttpContext http,
                SessionService sessions,
                BusinessOperationsService service,
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
                    return Results.Ok(
                        await service.UpdateSupplierAsync(
                            access.User!,
                            supplierId,
                            request,
                            cancellationToken));
                }
                catch (BusinessOperationsException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/admin/purchases",
            async Task<IResult> (
                int? limit,
                HttpContext http,
                SessionService sessions,
                BusinessOperationsService service,
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

                IReadOnlyList<PurchaseResult> purchases =
                    await service.ListPurchasesAsync(
                        limit ?? 200,
                        cancellationToken);

                return Results.Ok(new
                {
                    purchases,
                    count = purchases.Count
                });
            });

        app.MapPost(
            "/api/v3/admin/purchases",
            async Task<IResult> (
                ReceivePurchaseRequest request,
                HttpContext http,
                SessionService sessions,
                BusinessOperationsService service,
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
                    PurchaseResult purchase =
                        await service.ReceivePurchaseAsync(
                            access.User!,
                            request,
                            cancellationToken);

                    return Results.Created(
                        $"/api/v3/admin/purchases/{purchase.Id}",
                        purchase);
                }
                catch (BusinessOperationsException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/admin/expenses",
            async Task<IResult> (
                bool? includeVoided,
                int? limit,
                HttpContext http,
                SessionService sessions,
                BusinessOperationsService service,
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

                IReadOnlyList<ExpenseResult> expenses =
                    await service.ListExpensesAsync(
                        includeVoided ?? false,
                        limit ?? 500,
                        cancellationToken);

                return Results.Ok(new
                {
                    expenses,
                    count = expenses.Count
                });
            });

        app.MapPost(
            "/api/v3/admin/expenses",
            async Task<IResult> (
                CreateExpenseRequest request,
                HttpContext http,
                SessionService sessions,
                BusinessOperationsService service,
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
                    ExpenseResult expense =
                        await service.CreateExpenseAsync(
                            access.User!,
                            request,
                            cancellationToken);

                    return Results.Created(
                        $"/api/v3/admin/expenses/{expense.Id}",
                        expense);
                }
                catch (BusinessOperationsException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPost(
            "/api/v3/admin/expenses/{expenseId}/void",
            async Task<IResult> (
                string expenseId,
                VoidExpenseRequest request,
                HttpContext http,
                SessionService sessions,
                BusinessOperationsService service,
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
                    return Results.Ok(
                        await service.VoidExpenseAsync(
                            access.User!,
                            expenseId,
                            request,
                            cancellationToken));
                }
                catch (BusinessOperationsException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/admin/reports/summary",
            async Task<IResult> (
                string? from,
                string? to,
                HttpContext http,
                SessionService sessions,
                BusinessOperationsService service,
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
                    return Results.Ok(
                        await service.GetReportAsync(
                            from,
                            to,
                            cancellationToken));
                }
                catch (BusinessOperationsException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/admin/reports/sales.csv",
            async Task<IResult> (
                string? from,
                string? to,
                HttpContext http,
                SessionService sessions,
                BusinessOperationsService service,
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
                    byte[] content =
                        await service.BuildSalesCsvAsync(
                            from,
                            to,
                            cancellationToken);

                    string filename =
                        $"ROBO-Sales-{from ?? "report"}-" +
                        $"{to ?? DateTime.UtcNow.ToString("yyyy-MM-dd")}.csv";

                    return Results.File(
                        content,
                        "text/csv; charset=utf-8",
                        filename);
                }
                catch (BusinessOperationsException exception)
                {
                    return Error(exception);
                }
            });
    }

    private static IResult Error(
        BusinessOperationsException exception)
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
