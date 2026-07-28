using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Inventory;

public static class StockTransferAuditEndpoints
{
    public static void MapStockTransferAuditEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v3/stock-transfers/{transferId}/audit-lines",
            async Task<IResult> (
                string transferId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                StockTransferAuditService audits,
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
                    StockTransferAuditTrailRecord trail =
                        await audits.GetAsync(
                            access.User!,
                            context,
                            transferId,
                            cancellationToken);

                    return Results.Ok(new
                    {
                        trail.TransferId,
                        trail.TransferNumber,
                        trail.SourceShopId,
                        trail.DestinationShopId,
                        lines = trail.Lines,
                        count = trail.Lines.Count
                    });
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
                catch (StockTransferException exception)
                {
                    return Results.Json(
                        new
                        {
                            error = exception.ErrorCode,
                            message = exception.Message
                        },
                        statusCode: exception.StatusCode);
                }
            });
    }
}
