using Microsoft.AspNetCore.Mvc;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Crm;

public static class CrmEndpoints
{
    public static void MapCrmEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v3/crm/customers",
            async Task<IResult> (
                string? search,
                string? segment,
                bool? includeInactive,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<CrmCustomerRecord> customers =
                            await crm.ListCustomersAsync(
                                user,
                                context,
                                search,
                                segment,
                                includeInactive ?? false,
                                limit ?? 500,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            customers,
                            count = customers.Count,
                            totalLifetimeSpendMinor = customers.Sum(item => item.Metrics.LifetimeSpendMinor),
                            totalOutstandingMinor = customers.Sum(item => item.Metrics.OutstandingMinor)
                        });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/crm/customers/{customerId}",
            async Task<IResult> (
                string customerId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.GetCustomerAsync(user, context, customerId, cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/customers",
            async Task<IResult> (
                [FromBody] CreateCrmCustomerRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        CrmCustomerRecord customer = await crm.CreateCustomerAsync(
                            user,
                            context,
                            request,
                            cancellationToken);
                        return Results.Created($"/api/v3/crm/customers/{customer.Id}", customer);
                    },
                    cancellationToken));

        app.MapPut(
            "/api/v3/crm/customers/{customerId}",
            async Task<IResult> (
                string customerId,
                [FromBody] UpdateCrmCustomerRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.UpdateCustomerAsync(
                            user,
                            context,
                            customerId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/crm/customers/duplicates",
            async Task<IResult> (
                string? phone,
                string? email,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<DuplicateCustomerCandidate> candidates =
                            await crm.FindDuplicatesAsync(
                                user,
                                context,
                                phone,
                                email,
                                cancellationToken);
                        return Results.Ok(new { candidates, count = candidates.Count });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/crm/tags",
            async Task<IResult> (
                bool? includeInactive,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<CrmTagRecord> tags = await crm.ListTagsAsync(
                            user,
                            context,
                            includeInactive ?? false,
                            cancellationToken);
                        return Results.Ok(new { tags, count = tags.Count });
                    },
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/tags",
            async Task<IResult> (
                [FromBody] CreateCrmTagRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        CrmTagRecord tag = await crm.CreateTagAsync(
                            user,
                            context,
                            request,
                            cancellationToken);
                        return Results.Created($"/api/v3/crm/tags/{tag.Id}", tag);
                    },
                    cancellationToken));

        app.MapPut(
            "/api/v3/crm/tags/{tagId}",
            async Task<IResult> (
                string tagId,
                [FromBody] UpdateCrmTagRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.UpdateTagAsync(user, context, tagId, request, cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/crm/communications",
            async Task<IResult> (
                string? customerId,
                string? type,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<CrmCommunicationRecord> communications =
                            await crm.ListCommunicationsAsync(
                                user,
                                context,
                                customerId,
                                type,
                                limit ?? 500,
                                cancellationToken);
                        return Results.Ok(new { communications, count = communications.Count });
                    },
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/communications",
            async Task<IResult> (
                [FromBody] CreateCommunicationRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        CrmCommunicationRecord communication =
                            await crm.CreateCommunicationAsync(
                                user,
                                context,
                                request,
                                cancellationToken);
                        return Results.Created(
                            $"/api/v3/crm/communications/{communication.Id}",
                            communication);
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/crm/tasks",
            async Task<IResult> (
                string? customerId,
                string? status,
                bool? assignedToMe,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<CrmTaskRecord> tasks = await crm.ListTasksAsync(
                            user,
                            context,
                            customerId,
                            status,
                            assignedToMe ?? false,
                            limit ?? 500,
                            cancellationToken);
                        return Results.Ok(new
                        {
                            tasks,
                            count = tasks.Count,
                            overdueCount = tasks.Count(task => task.IsOverdue)
                        });
                    },
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/tasks",
            async Task<IResult> (
                [FromBody] CreateCrmTaskRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        CrmTaskRecord task = await crm.CreateTaskAsync(
                            user,
                            context,
                            request,
                            cancellationToken);
                        return Results.Created($"/api/v3/crm/tasks/{task.Id}", task);
                    },
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/tasks/{taskId}/complete",
            async Task<IResult> (
                string taskId,
                [FromBody] CompleteCrmTaskRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.CompleteTaskAsync(user, context, taskId, request, cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/tasks/{taskId}/cancel",
            async Task<IResult> (
                string taskId,
                [FromBody] CancelCrmTaskRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.CancelTaskAsync(user, context, taskId, request, cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/crm/loyalty/settings",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.GetLoyaltySettingsAsync(user, context, cancellationToken)),
                    cancellationToken));

        app.MapPut(
            "/api/v3/crm/loyalty/settings",
            async Task<IResult> (
                [FromBody] UpdateLoyaltySettingsRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.UpdateLoyaltySettingsAsync(user, context, request, cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/crm/customers/{customerId}/loyalty-ledger",
            async Task<IResult> (
                string customerId,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<LoyaltyLedgerRecord> entries =
                            await crm.ListLoyaltyLedgerAsync(
                                user,
                                context,
                                customerId,
                                limit ?? 500,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            entries,
                            count = entries.Count,
                            netPoints = entries.Sum(entry => entry.PointsDelta)
                        });
                    },
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/customers/{customerId}/loyalty-adjustments",
            async Task<IResult> (
                string customerId,
                [FromBody] LoyaltyAdjustmentRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.AdjustLoyaltyPointsAsync(
                            user,
                            context,
                            customerId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/customers/{customerId}/loyalty-redemptions",
            async Task<IResult> (
                string customerId,
                [FromBody] LoyaltyRedemptionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.RedeemLoyaltyPointsAsync(
                            user,
                            context,
                            customerId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/crm/quotations",
            async Task<IResult> (
                string? customerId,
                string? status,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<QuotationRecord> quotations =
                            await crm.ListQuotationsAsync(
                                user,
                                context,
                                customerId,
                                status,
                                limit ?? 500,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            quotations,
                            count = quotations.Count,
                            pipelineValueMinor = quotations
                                .Where(item => item.Status is "draft" or "sent" or "accepted")
                                .Sum(item => item.TotalMinor)
                        });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/crm/quotations/{quotationId}",
            async Task<IResult> (
                string quotationId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.GetQuotationAsync(user, context, quotationId, cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/quotations",
            async Task<IResult> (
                [FromBody] CreateQuotationRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        QuotationRecord quotation = await crm.CreateQuotationAsync(
                            user,
                            context,
                            request,
                            cancellationToken);
                        return Results.Created(
                            $"/api/v3/crm/quotations/{quotation.Id}",
                            quotation);
                    },
                    cancellationToken));

        app.MapPut(
            "/api/v3/crm/quotations/{quotationId}",
            async Task<IResult> (
                string quotationId,
                [FromBody] UpdateQuotationRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.UpdateQuotationAsync(
                            user,
                            context,
                            quotationId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/quotations/{quotationId}/send",
            async Task<IResult> (
                string quotationId,
                [FromBody] QuotationActionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.SendQuotationAsync(user, context, quotationId, request, cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/quotations/{quotationId}/accept",
            async Task<IResult> (
                string quotationId,
                [FromBody] QuotationActionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.AcceptQuotationAsync(user, context, quotationId, request, cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/quotations/{quotationId}/reject",
            async Task<IResult> (
                string quotationId,
                [FromBody] QuotationActionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.RejectQuotationAsync(user, context, quotationId, request, cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/quotations/{quotationId}/cancel",
            async Task<IResult> (
                string quotationId,
                [FromBody] QuotationActionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.CancelQuotationAsync(user, context, quotationId, request, cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/crm/quotations/{quotationId}/convert",
            async Task<IResult> (
                string quotationId,
                [FromBody] ConvertQuotationRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.ConvertQuotationAsync(user, context, quotationId, request, cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/crm/customers/{customerId}/timeline",
            async Task<IResult> (
                string customerId,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<CrmTimelineEntry> timeline =
                            await crm.GetCustomerTimelineAsync(
                                user,
                                context,
                                customerId,
                                limit ?? 500,
                                cancellationToken);
                        return Results.Ok(new { customerId, timeline, count = timeline.Count });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/crm/dashboard",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await crm.GetDashboardAsync(user, context, cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/crm/segments",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                CrmService crm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<CrmSegmentRecord> segments =
                            await crm.GetSegmentsAsync(user, context, cancellationToken);
                        return Results.Ok(new { segments, count = segments.Sum(item => item.CustomerCount) });
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
        EndpointAccessDecision access = await EndpointAccessControl.RequireUserAsync(
            http,
            sessions,
            cancellationToken);
        if (!access.IsAllowed)
        {
            return access.Failure!;
        }
        try
        {
            ActiveShopContextRecord context = await contexts.GetOrCreateAsync(
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
        catch (CrmException exception)
        {
            return Results.Json(
                new { error = exception.ErrorCode, message = exception.Message },
                statusCode: exception.StatusCode);
        }
    }
}
