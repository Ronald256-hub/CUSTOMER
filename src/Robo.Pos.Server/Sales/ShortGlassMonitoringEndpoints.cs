using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public static class ShortGlassMonitoringEndpoints
{
    public static void MapShortGlassMonitoringEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/v3/reports/short-glass",
            async Task<IResult> (
                string? fromDate,
                string? toDate,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                ShortGlassMonitoringService monitoring,
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

                    ShortGlassMonitorReport report =
                        await monitoring.GetReportAsync(
                            context,
                            fromDate,
                            toDate,
                            cancellationToken);

                    return Results.Ok(new
                    {
                        report.OrganizationId,
                        report.ShopId,
                        report.ShopCode,
                        report.FromDate,
                        report.ToDate,
                        report.FromUtc,
                        report.ToUtc,
                        report.TotalGlassesSold,
                        report.TotalVolumeDispensedMl,
                        report.TotalRevenueMinor,
                        report.TotalRemainingGlasses,
                        products = report.Products,
                        count = report.Products.Count
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
                catch (ShortGlassMonitoringException exception)
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
