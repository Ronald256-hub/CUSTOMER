using Microsoft.AspNetCore.Mvc;
using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Shops;

public static class ShopEndpoints
{
    public static void MapShopEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v3/shops",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopService shops,
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

                IReadOnlyList<AvailableShopRecord> available =
                    await shops.ListAvailableAsync(
                        access.User!,
                        cancellationToken);

                return Results.Ok(new
                {
                    shops = available,
                    count = available.Count
                });
            });

        app.MapGet(
            "/api/v3/admin/shops",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopService shops,
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

                IReadOnlyList<ShopRecord> records =
                    await shops.ListAllAsync(cancellationToken);

                return Results.Ok(new
                {
                    shops = records,
                    count = records.Count
                });
            });

        app.MapPost(
            "/api/v3/admin/shops",
            async Task<IResult> (
                [FromBody] CreateShopRequest request,
                HttpContext http,
                SessionService sessions,
                ShopService shops,
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
                    ShopRecord created = await shops.CreateAsync(
                        access.User!,
                        request,
                        cancellationToken);

                    return Results.Created(
                        $"/api/v3/admin/shops/{created.Id}",
                        created);
                }
                catch (ShopException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPut(
            "/api/v3/admin/shops/{shopId}",
            async Task<IResult> (
                string shopId,
                [FromBody] UpdateShopRequest request,
                HttpContext http,
                SessionService sessions,
                ShopService shops,
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
                    ShopRecord updated = await shops.UpdateAsync(
                        access.User!,
                        shopId,
                        request,
                        cancellationToken);

                    return Results.Ok(updated);
                }
                catch (ShopException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/admin/shops/{shopId}/users",
            async Task<IResult> (
                string shopId,
                HttpContext http,
                SessionService sessions,
                ShopService shops,
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
                    IReadOnlyList<ShopUserAccessRecord> users =
                        await shops.ListUsersAsync(
                            shopId,
                            cancellationToken);

                    return Results.Ok(new
                    {
                        users,
                        count = users.Count
                    });
                }
                catch (ShopException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPut(
            "/api/v3/admin/shops/{shopId}/users/{userId}",
            async Task<IResult> (
                string shopId,
                string userId,
                [FromBody] AssignShopUserRequest request,
                HttpContext http,
                SessionService sessions,
                ShopService shops,
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
                    ShopUserAccessRecord assignment =
                        await shops.AssignUserAsync(
                            access.User!,
                            shopId,
                            userId,
                            request,
                            cancellationToken);

                    return Results.Ok(assignment);
                }
                catch (ShopException exception)
                {
                    return Error(exception);
                }
            });
    }

    private static IResult Error(ShopException exception)
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