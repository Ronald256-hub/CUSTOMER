using Microsoft.AspNetCore.Mvc;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Procurement;

public static class ProcurementEndpoints
{
    public static void MapProcurementEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v3/procurement/purchase-orders",
            async Task<IResult> (
                string? status,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<PurchaseOrderRecord> orders =
                            await procurement.ListPurchaseOrdersAsync(
                                user,
                                context,
                                status,
                                limit ?? 500,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            purchaseOrders = orders,
                            count = orders.Count,
                            openValueMinor = orders
                                .Where(order => order.Status is "submitted" or "approved" or "partially_received")
                                .Sum(order => order.TotalMinor)
                        });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/procurement/purchase-orders/{purchaseOrderId}",
            async Task<IResult> (
                string purchaseOrderId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await procurement.GetPurchaseOrderAsync(
                            user,
                            context,
                            purchaseOrderId,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/procurement/purchase-orders",
            async Task<IResult> (
                [FromBody] CreatePurchaseOrderRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        PurchaseOrderRecord order =
                            await procurement.CreatePurchaseOrderAsync(
                                user,
                                context,
                                request,
                                cancellationToken);
                        return Results.Created(
                            $"/api/v3/procurement/purchase-orders/{order.Id}",
                            order);
                    },
                    cancellationToken));

        app.MapPut(
            "/api/v3/procurement/purchase-orders/{purchaseOrderId}",
            async Task<IResult> (
                string purchaseOrderId,
                [FromBody] UpdatePurchaseOrderRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await procurement.UpdatePurchaseOrderAsync(
                            user,
                            context,
                            purchaseOrderId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/procurement/purchase-orders/{purchaseOrderId}/submit",
            async Task<IResult> (
                string purchaseOrderId,
                [FromBody] VersionedActionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await procurement.SubmitPurchaseOrderAsync(
                            user,
                            context,
                            purchaseOrderId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/procurement/purchase-orders/{purchaseOrderId}/approve",
            async Task<IResult> (
                string purchaseOrderId,
                [FromBody] VersionedActionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await procurement.ApprovePurchaseOrderAsync(
                            user,
                            context,
                            purchaseOrderId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/procurement/purchase-orders/{purchaseOrderId}/cancel",
            async Task<IResult> (
                string purchaseOrderId,
                [FromBody] CancelPurchaseOrderRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await procurement.CancelPurchaseOrderAsync(
                            user,
                            context,
                            purchaseOrderId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/procurement/purchase-orders/{purchaseOrderId}/receive",
            async Task<IResult> (
                string purchaseOrderId,
                [FromBody] ReceiveGoodsRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        GoodsReceiptRecord receipt =
                            await procurement.ReceiveGoodsAsync(
                                user,
                                context,
                                purchaseOrderId,
                                request,
                                cancellationToken);
                        return Results.Created(
                            $"/api/v3/procurement/goods-receipts/{receipt.Id}",
                            receipt);
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/procurement/goods-receipts",
            async Task<IResult> (
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<GoodsReceiptRecord> receipts =
                            await procurement.ListGoodsReceiptsAsync(
                                user,
                                context,
                                limit ?? 500,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            goodsReceipts = receipts,
                            count = receipts.Count,
                            totalMinor = receipts
                                .Where(receipt => receipt.Status == "posted")
                                .Sum(receipt => receipt.TotalMinor)
                        });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/procurement/goods-receipts/{goodsReceiptId}",
            async Task<IResult> (
                string goodsReceiptId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await procurement.GetGoodsReceiptAsync(
                            user,
                            context,
                            goodsReceiptId,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/procurement/goods-receipts/{goodsReceiptId}/supplier-returns",
            async Task<IResult> (
                string goodsReceiptId,
                [FromBody] CreateSupplierReturnRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        SupplierReturnRecord supplierReturn =
                            await procurement.CreateSupplierReturnAsync(
                                user,
                                context,
                                goodsReceiptId,
                                request,
                                cancellationToken);
                        return Results.Created(
                            $"/api/v3/procurement/supplier-returns/{supplierReturn.Id}",
                            supplierReturn);
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/procurement/supplier-returns",
            async Task<IResult> (
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<SupplierReturnRecord> returns =
                            await procurement.ListSupplierReturnsAsync(
                                user,
                                context,
                                limit ?? 500,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            supplierReturns = returns,
                            count = returns.Count,
                            totalMinor = returns
                                .Where(item => item.Status == "posted")
                                .Sum(item => item.TotalMinor)
                        });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/procurement/supplier-returns/{supplierReturnId}",
            async Task<IResult> (
                string supplierReturnId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await procurement.GetSupplierReturnAsync(
                            user,
                            context,
                            supplierReturnId,
                            cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/procurement/reorder-policies",
            async Task<IResult> (
                bool? includeInactive,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<ReorderPolicyRecord> policies =
                            await procurement.ListReorderPoliciesAsync(
                                user,
                                context,
                                includeInactive ?? false,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            reorderPolicies = policies,
                            count = policies.Count
                        });
                    },
                    cancellationToken));

        app.MapPut(
            "/api/v3/procurement/reorder-policies/{productId}",
            async Task<IResult> (
                string productId,
                [FromBody] ReorderPolicyRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        ReorderPolicyRequest scopedRequest = request with
                        {
                            ProductId = productId
                        };
                        return Results.Ok(
                            await procurement.UpsertReorderPolicyAsync(
                                user,
                                context,
                                scopedRequest,
                                cancellationToken));
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/procurement/reorder-recommendations",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<ReorderRecommendationRecord> recommendations =
                            await procurement.ListReorderRecommendationsAsync(
                                user,
                                context,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            recommendations,
                            count = recommendations.Count,
                            suggestedQuantityBaseUnits = recommendations
                                .Sum(item => item.SuggestedOrderBaseUnits)
                        });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/procurement/batches",
            async Task<IResult> (
                string? productId,
                string? status,
                int? expiringWithinDays,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<InventoryBatchRecord> batches =
                            await procurement.ListInventoryBatchesAsync(
                                user,
                                context,
                                productId,
                                status,
                                expiringWithinDays,
                                limit ?? 1000,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            batches,
                            count = batches.Count,
                            availableQuantityBaseUnits = batches
                                .Sum(batch => batch.AvailableQuantityBaseUnits)
                        });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/procurement/stock-counts",
            async Task<IResult> (
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<StockCountRecord> counts =
                            await procurement.ListStockCountsAsync(
                                user,
                                context,
                                limit ?? 500,
                                cancellationToken);
                        return Results.Ok(new
                        {
                            context.OrganizationId,
                            context.ShopId,
                            context.ShopCode,
                            stockCounts = counts,
                            count = counts.Count
                        });
                    },
                    cancellationToken));

        app.MapGet(
            "/api/v3/procurement/stock-counts/{stockCountId}",
            async Task<IResult> (
                string stockCountId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await procurement.GetStockCountAsync(
                            user,
                            context,
                            stockCountId,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/procurement/stock-counts",
            async Task<IResult> (
                [FromBody] CreateStockCountRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) =>
                    {
                        StockCountRecord count =
                            await procurement.CreateStockCountAsync(
                                user,
                                context,
                                request,
                                cancellationToken);
                        return Results.Created(
                            $"/api/v3/procurement/stock-counts/{count.Id}",
                            count);
                    },
                    cancellationToken));

        app.MapPost(
            "/api/v3/procurement/stock-counts/{stockCountId}/submit",
            async Task<IResult> (
                string stockCountId,
                [FromBody] SubmitStockCountRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await procurement.SubmitStockCountAsync(
                            user,
                            context,
                            stockCountId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapPost(
            "/api/v3/procurement/stock-counts/{stockCountId}/approve",
            async Task<IResult> (
                string stockCountId,
                [FromBody] ApproveStockCountRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await procurement.ApproveStockCountAsync(
                            user,
                            context,
                            stockCountId,
                            request,
                            cancellationToken)),
                    cancellationToken));

        app.MapGet(
            "/api/v3/procurement/reports/summary",
            async Task<IResult> (
                string? fromDate,
                string? toDate,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ProcurementService procurement,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http,
                    sessions,
                    contexts,
                    async (user, context) => Results.Ok(
                        await procurement.GetProcurementSummaryAsync(
                            user,
                            context,
                            fromDate,
                            toDate,
                            cancellationToken)),
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
        catch (ProcurementException exception)
        {
            return Results.Json(
                new { error = exception.ErrorCode, message = exception.Message },
                statusCode: exception.StatusCode);
        }
    }
}
