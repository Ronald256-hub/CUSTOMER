using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public sealed record ShortGlassMonitorRow(
    string ProductId,
    string ProductName,
    string Sku,
    int BottleVolumeMl,
    int GlassSizeMl,
    long AvailableVolumeMl,
    long RemainingGlasses,
    decimal RemainingBottleEquivalents,
    long GlassesSold,
    long VolumeDispensedMl,
    decimal BottleEquivalentsDispensed,
    long RevenueMinor,
    long LowStockThresholdMl,
    bool IsLowStock);

public sealed record ShortGlassMonitorReport(
    string OrganizationId,
    string ShopId,
    string ShopCode,
    DateOnly FromDate,
    DateOnly ToDate,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long TotalGlassesSold,
    long TotalVolumeDispensedMl,
    long TotalRevenueMinor,
    long TotalRemainingGlasses,
    IReadOnlyList<ShortGlassMonitorRow> Products);

/// <summary>
/// Provides read-only, branch-scoped short-glass quantity and revenue monitoring.
/// </summary>
public sealed class ShortGlassMonitoringService
{
    private readonly DatabaseBootstrap _database;

    public ShortGlassMonitoringService(DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task<ShortGlassMonitorReport> GetReportAsync(
        ActiveShopContextRecord context,
        string? requestedFromDate,
        string? requestedToDate,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly fromDate = ParseDate(requestedFromDate, today);
        DateOnly toDate = ParseDate(requestedToDate, fromDate);

        if (toDate < fromDate)
        {
            throw new ShortGlassMonitoringException(
                StatusCodes.Status400BadRequest,
                "invalid_short_glass_period",
                "The short-glass report end date cannot be before the start date.");
        }

        if (toDate.DayNumber - fromDate.DayNumber > 366)
        {
            throw new ShortGlassMonitoringException(
                StatusCodes.Status400BadRequest,
                "short_glass_period_too_large",
                "A short-glass report cannot cover more than 367 days.");
        }

        DateTimeOffset fromUtc = new(
            fromDate.Year,
            fromDate.Month,
            fromDate.Day,
            0,
            0,
            0,
            TimeSpan.Zero);

        DateOnly exclusiveEndDate = toDate.AddDays(1);
        DateTimeOffset toUtc = new(
            exclusiveEndDate.Year,
            exclusiveEndDate.Month,
            exclusiveEndDate.Day,
            0,
            0,
            0,
            TimeSpan.Zero);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        WITH period_sales AS
        (
            SELECT
                item.product_id,
                COALESCE(SUM(item.quantity), 0) AS glasses_sold,
                COALESCE(SUM(item.base_units_deducted), 0)
                    AS volume_dispensed_ml,
                COALESCE(SUM(item.line_total_minor), 0) AS revenue_minor
            FROM sale_items AS item
            INNER JOIN sales AS sale
                ON sale.id = item.sale_id
            WHERE sale.shop_id = $shopId
              AND sale.status = 'completed'
              AND COALESCE(sale.completed_at_utc, sale.created_at_utc) >= $fromUtc
              AND COALESCE(sale.completed_at_utc, sale.created_at_utc) < $toUtc
            GROUP BY item.product_id
        )
        SELECT
            product.id,
            product.name,
            product.sku,
            product.bottle_volume_ml,
            product.glass_size_ml,
            COALESCE(stock.quantity_base_units, 0),
            COALESCE(period.glasses_sold, 0),
            COALESCE(period.volume_dispensed_ml, 0),
            COALESCE(period.revenue_minor, 0),
            product.low_stock_threshold
        FROM products AS product
        LEFT JOIN shop_stock_balances AS stock
            ON stock.shop_id = $shopId
           AND stock.product_id = product.id
        LEFT JOIN period_sales AS period
            ON period.product_id = product.id
        WHERE product.product_type = 'short_glass'
          AND product.is_active = 1
        ORDER BY product.name COLLATE NOCASE, product.sku COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$fromUtc", fromUtc.ToString("O"));
        command.Parameters.AddWithValue("$toUtc", toUtc.ToString("O"));

        var rows = new List<ShortGlassMonitorRow>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            int bottleVolumeMl = reader.GetInt32(3);
            int glassSizeMl = reader.GetInt32(4);
            long availableVolumeMl = reader.GetInt64(5);
            long glassesSold = reader.GetInt64(6);
            long volumeDispensedMl = reader.GetInt64(7);
            long revenueMinor = reader.GetInt64(8);
            long lowStockThresholdMl = reader.GetInt64(9);

            long remainingGlasses = glassSizeMl <= 0
                ? 0
                : availableVolumeMl / glassSizeMl;

            decimal remainingBottleEquivalents = bottleVolumeMl <= 0
                ? 0
                : decimal.Round(
                    availableVolumeMl / (decimal)bottleVolumeMl,
                    2,
                    MidpointRounding.AwayFromZero);

            decimal bottleEquivalentsDispensed = bottleVolumeMl <= 0
                ? 0
                : decimal.Round(
                    volumeDispensedMl / (decimal)bottleVolumeMl,
                    2,
                    MidpointRounding.AwayFromZero);

            rows.Add(new ShortGlassMonitorRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                bottleVolumeMl,
                glassSizeMl,
                availableVolumeMl,
                remainingGlasses,
                remainingBottleEquivalents,
                glassesSold,
                volumeDispensedMl,
                bottleEquivalentsDispensed,
                revenueMinor,
                lowStockThresholdMl,
                availableVolumeMl <= lowStockThresholdMl));
        }

        return new ShortGlassMonitorReport(
            context.OrganizationId,
            context.ShopId,
            context.ShopCode,
            fromDate,
            toDate,
            fromUtc,
            toUtc,
            rows.Sum(item => item.GlassesSold),
            rows.Sum(item => item.VolumeDispensedMl),
            rows.Sum(item => item.RevenueMinor),
            rows.Sum(item => item.RemainingGlasses),
            rows);
    }

    private static DateOnly ParseDate(
        string? value,
        DateOnly fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!DateOnly.TryParseExact(
                value.Trim(),
                "yyyy-MM-dd",
                out DateOnly parsed))
        {
            throw new ShortGlassMonitoringException(
                StatusCodes.Status400BadRequest,
                "invalid_short_glass_date",
                "Use dates in YYYY-MM-DD format for the short-glass report.");
        }

        return parsed;
    }
}

public sealed class ShortGlassMonitoringException : Exception
{
    public ShortGlassMonitoringException(
        int statusCode,
        string errorCode,
        string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }

    public string ErrorCode { get; }
}
