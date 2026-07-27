using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Inventory;

public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/v3/catalog/products",
            async Task<IResult> (
                string? search,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopInventoryService inventory,
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

                ActiveShopContextRecord context =
                    await contexts.GetOrCreateAsync(
                        access.User!,
                        access.SessionId!,
                        cancellationToken);

                IReadOnlyList<ProductCatalogItem> products =
                    await inventory.ListProductsAsync(
                        context.ShopId,
                        search,
                        includeInactive: false,
                        includeCostPrice: false,
                        cancellationToken);

                return Results.Ok(new
                {
                    shopId = context.ShopId,
                    shopCode = context.ShopCode,
                    products,
                    count = products.Count
                });
            });

        app.MapGet(
            "/api/v3/admin/inventory/products",
            async Task<IResult> (
                string? search,
                bool? includeInactive,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopInventoryService inventory,
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

                ActiveShopContextRecord context =
                    await contexts.GetOrCreateAsync(
                        access.User!,
                        access.SessionId!,
                        cancellationToken);

                IReadOnlyList<ProductCatalogItem> products =
                    await inventory.ListProductsAsync(
                        context.ShopId,
                        search,
                        includeInactive ?? false,
                        includeCostPrice: true,
                        cancellationToken);

                return Results.Ok(new
                {
                    shopId = context.ShopId,
                    shopCode = context.ShopCode,
                    products,
                    count = products.Count
                });
            });

        app.MapGet(
            "/api/v3/admin/inventory/stock-movements",
            async Task<IResult> (
                string? productId,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopInventoryService inventory,
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

                ActiveShopContextRecord context =
                    await contexts.GetOrCreateAsync(
                        access.User!,
                        access.SessionId!,
                        cancellationToken);

                IReadOnlyList<ShopStockMovementRecord> movements =
                    await inventory.ListMovementsAsync(
                        context.ShopId,
                        productId,
                        limit ?? 100,
                        cancellationToken);

                return Results.Ok(new
                {
                    shopId = context.ShopId,
                    shopCode = context.ShopCode,
                    movements,
                    count = movements.Count
                });
            });

        app.MapPost(
            "/api/v3/admin/inventory/categories",
            async Task<IResult> (
                CreateCategoryRequest request,
                HttpContext http,
                SessionService sessions,
                InventoryService inventory,
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
                    CategoryRecord category =
                        await inventory.CreateCategoryAsync(
                            access.User!,
                            request,
                            cancellationToken);

                    return Results.Created(
                        $"/api/v3/admin/inventory/categories/{category.Id}",
                        category);
                }
                catch (InventoryException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPost(
            "/api/v3/admin/inventory/products",
            async Task<IResult> (
                CreateProductRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopInventoryService inventory,
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
                        await contexts.GetOrCreateAsync(
                            access.User!,
                            access.SessionId!,
                            cancellationToken);

                    ProductCatalogItem product =
                        await inventory.CreateProductAsync(
                            access.User!,
                            context.ShopId,
                            request,
                            cancellationToken);

                    return Results.Created(
                        $"/api/v3/admin/inventory/products/{product.Id}",
                        product);
                }
                catch (InventoryException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPut(
            "/api/v3/admin/inventory/products/{productId}/prices",
            async Task<IResult> (
                string productId,
                UpdateProductPriceRequest request,
                HttpContext http,
                SessionService sessions,
                InventoryService inventory,
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
                    PriceChangeRecord result =
                        await inventory.UpdatePricesAsync(
                            access.User!,
                            productId,
                            request,
                            cancellationToken);

                    return Results.Ok(result);
                }
                catch (InventoryException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPost(
            "/api/v3/admin/inventory/products/{productId}/stock-adjustments",
            async Task<IResult> (
                string productId,
                StockAdjustmentRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShopInventoryService inventory,
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
                        await contexts.GetOrCreateAsync(
                            access.User!,
                            access.SessionId!,
                            cancellationToken);

                    StockAdjustmentRecord result =
                        await inventory.AdjustStockAsync(
                            access.User!,
                            context.ShopId,
                            productId,
                            request,
                            cancellationToken);

                    return Results.Ok(result);
                }
                catch (InventoryException exception)
                {
                    return Error(exception);
                }
            });
    }

    private static IResult Error(
        InventoryException exception)
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
