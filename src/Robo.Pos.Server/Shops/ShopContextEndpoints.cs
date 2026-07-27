using Microsoft.AspNetCore.Mvc;
using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Shops;

public static class ShopContextEndpoints
{
    public static void MapShopContextEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v3/session/shop-context",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
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

                    return Results.Ok(context);
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPut(
            "/api/v3/session/shop-context",
            async Task<IResult> (
                [FromBody] SetActiveShopContextRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
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
                        await contexts.SetAsync(
                            access.User!,
                            access.SessionId!,
                            request,
                            cancellationToken);

                    return Results.Ok(context);
                }
                catch (ShopContextException exception)
                {
                    return Error(exception);
                }
            });
    }

    private static IResult Error(ShopContextException exception) =>
        Results.Json(
            new
            {
                error = exception.ErrorCode,
                message = exception.Message
            },
            statusCode: exception.StatusCode);
}
