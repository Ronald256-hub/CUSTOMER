using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public sealed record ShopSalesSummaryRow(
    string ShopId,
    string ShopCode,
    string ShopName,
    long CompletedSalesCount,
    long VoidedSalesCount,
    long ReturnCount,
    long GrossSalesMinor,
    long VoidedSalesMinor,
    long ReturnedSalesMinor,
    long NetSalesMinor,
    long GrossCostOfGoodsSoldMinor,
    long RestockedCostMinor,
    long CostOfGoodsSoldMinor,
    long GrossProfitMinor);

public sealed record PaymentSalesSummaryRow(
    string PaymentMethod,
    long SaleCount,
    long ReturnCount,
    long GrossAmountMinor,
    long RefundedAmountMinor,
    long AmountMinor);

public sealed record SalesSummaryReport(
    string Scope,
    string OrganizationId,
    string OrganizationName,
    string? ShopId,
    string? ShopCode,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long CompletedSalesCount,
    long VoidedSalesCount,
    long ReturnCount,
    long GrossSalesMinor,
    long VoidedSalesMinor,
    long ReturnedSalesMinor,
    long NetSalesMinor,
    long GrossCostOfGoodsSoldMinor,
    long RestockedCostMinor,
    long CostOfGoodsSoldMinor,
    long GrossProfitMinor,
    IReadOnlyList<ShopSalesSummaryRow> Shops,
    IReadOnlyList<PaymentSalesSummaryRow> Payments);

public sealed class ShopSalesReportingService
{
    private static readonly TimeSpan MaximumReportPeriod =
        TimeSpan.FromDays(36525);

    private readonly DatabaseBootstrap _database;

    public ShopSalesReportingService(DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task<SalesSummaryReport> GetSummaryAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? requestedScope,
        DateTimeOffset? requestedFromUtc,
        DateTimeOffset? requestedToUtc,
        CancellationToken cancellationToken = default)
    {
        string scope = requestedScope?.Trim().ToLowerInvariant() ?? "shop";
        if (scope is not ("shop" or "consolidated"))
        {
            throw Validation(
                "invalid_report_scope",
                "Report scope must be shop or consolidated.");
        }

        bool consolidated = scope == "consolidated";
        if (consolidated &&
            !string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            throw new SalesException(
                StatusCodes.Status403Forbidden,
                "administrator_required",
                "Only an administrator can view consolidated sales reports.");
        }

        DateTimeOffset toUtc = requestedToUtc?.ToUniversalTime()
            ?? DateTimeOffset.UtcNow;
        DateTimeOffset fromUtc = requestedFromUtc?.ToUniversalTime()
            ?? toUtc.AddDays(-30);

        if (fromUtc >= toUtc)
        {
            throw Validation(
                "invalid_report_period",
                "The report start time must be earlier than the end time.");
        }

        if (toUtc - fromUtc > MaximumReportPeriod)
        {
            throw Validation(
                "report_period_too_large",
                "A sales summary cannot cover more than 100 years at once.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        IReadOnlyList<ShopSalesSummaryRow> shops = await ReadShopRowsAsync(
            connection,
            context,
            consolidated,
            fromUtc,
            toUtc,
            cancellationToken);

        IReadOnlyList<PaymentSalesSummaryRow> payments =
            await ReadPaymentRowsAsync(
                connection,
                context,
                consolidated,
                fromUtc,
                toUtc,
                cancellationToken);

        long completedCount = shops.Sum(row => row.CompletedSalesCount);
        long voidedCount = shops.Sum(row => row.VoidedSalesCount);
        long returnCount = shops.Sum(row => row.ReturnCount);
        long grossSales = shops.Sum(row => row.GrossSalesMinor);
        long voidedSales = shops.Sum(row => row.VoidedSalesMinor);
        long returnedSales = shops.Sum(row => row.ReturnedSalesMinor);
        long netSales = shops.Sum(row => row.NetSalesMinor);
        long grossCost = shops.Sum(row => row.GrossCostOfGoodsSoldMinor);
        long restockedCost = shops.Sum(row => row.RestockedCostMinor);
        long netCost = shops.Sum(row => row.CostOfGoodsSoldMinor);
        long grossProfit = shops.Sum(row => row.GrossProfitMinor);

        return new SalesSummaryReport(
            scope,
            context.OrganizationId,
            context.OrganizationName,
            consolidated ? null : context.ShopId,
            consolidated ? null : context.ShopCode,
            fromUtc,
            toUtc,
            completedCount,
            voidedCount,
            returnCount,
            grossSales,
            voidedSales,
            returnedSales,
            netSales,
            grossCost,
            restockedCost,
            netCost,
            grossProfit,
            shops,
            payments);
    }

    private static async Task<IReadOnlyList<ShopSalesSummaryRow>>
        ReadShopRowsAsync(
            SqliteConnection connection,
            ActiveShopContextRecord context,
            bool consolidated,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        WITH sale_costs AS
        (
            SELECT
                item.sale_id,
                COALESCE(SUM(item.unit_cost_minor * item.quantity), 0) AS cost_minor
            FROM sale_items AS item
            GROUP BY item.sale_id
        ),
        sales_in_period AS
        (
            SELECT
                sale.shop_id,
                COUNT(CASE WHEN sale.status IN ('completed', 'partially_returned', 'returned') THEN 1 END) AS sale_count,
                COUNT(CASE WHEN sale.status = 'voided' THEN 1 END) AS void_count,
                COALESCE(SUM(CASE
                    WHEN sale.status IN ('completed', 'partially_returned', 'returned')
                    THEN sale.total_minor ELSE 0 END), 0) AS gross_sales_minor,
                COALESCE(SUM(CASE
                    WHEN sale.status = 'voided'
                    THEN sale.total_minor ELSE 0 END), 0) AS voided_sales_minor,
                COALESCE(SUM(CASE
                    WHEN sale.status IN ('completed', 'partially_returned', 'returned')
                    THEN COALESCE(cost.cost_minor, 0) ELSE 0 END), 0) AS gross_cost_minor
            FROM sales AS sale
            LEFT JOIN sale_costs AS cost
                ON cost.sale_id = sale.id
            WHERE COALESCE(sale.completed_at_utc, sale.created_at_utc) >= $fromUtc
              AND COALESCE(sale.completed_at_utc, sale.created_at_utc) < $toUtc
            GROUP BY sale.shop_id
        ),
        returns_in_period AS
        (
            SELECT
                header.shop_id,
                COUNT(*) AS return_count,
                COALESCE(SUM(header.refund_amount_minor), 0) AS refund_minor,
                COALESCE(SUM(header.restocked_cost_minor), 0) AS restocked_cost_minor
            FROM sales_returns AS header
            WHERE header.status = 'completed'
              AND header.completed_at_utc >= $fromUtc
              AND header.completed_at_utc < $toUtc
            GROUP BY header.shop_id
        )
        SELECT
            shop.id,
            shop.code,
            shop.name,
            COALESCE(sales.sale_count, 0),
            COALESCE(sales.void_count, 0),
            COALESCE(refunds.return_count, 0),
            COALESCE(sales.gross_sales_minor, 0),
            COALESCE(sales.voided_sales_minor, 0),
            COALESCE(refunds.refund_minor, 0),
            COALESCE(sales.gross_cost_minor, 0),
            COALESCE(refunds.restocked_cost_minor, 0)
        FROM shops AS shop
        LEFT JOIN sales_in_period AS sales
            ON sales.shop_id = shop.id
        LEFT JOIN returns_in_period AS refunds
            ON refunds.shop_id = shop.id
        WHERE shop.organization_id = $organizationId
          AND ($consolidated = 1 OR shop.id = $shopId)
        ORDER BY shop.is_head_office DESC, shop.name COLLATE NOCASE;
        """;
        AddScopeParameters(command, context, consolidated, fromUtc, toUtc);

        var rows = new List<ShopSalesSummaryRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long grossSales = reader.GetInt64(6);
            long returnedSales = reader.GetInt64(8);
            long grossCost = reader.GetInt64(9);
            long restockedCost = reader.GetInt64(10);
            long netSales = checked(grossSales - returnedSales);
            long netCost = checked(grossCost - restockedCost);

            rows.Add(new ShopSalesSummaryRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                grossSales,
                reader.GetInt64(7),
                returnedSales,
                netSales,
                grossCost,
                restockedCost,
                netCost,
                checked(netSales - netCost)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PaymentSalesSummaryRow>>
        ReadPaymentRowsAsync(
            SqliteConnection connection,
            ActiveShopContextRecord context,
            bool consolidated,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        WITH gross AS
        (
            SELECT
                payment.payment_method,
                COUNT(DISTINCT sale.id) AS sale_count,
                COALESCE(SUM(payment.amount_minor), 0) AS gross_minor
            FROM sale_payments AS payment
            INNER JOIN sales AS sale
                ON sale.id = payment.sale_id
            INNER JOIN shops AS shop
                ON shop.id = sale.shop_id
            WHERE shop.organization_id = $organizationId
              AND ($consolidated = 1 OR sale.shop_id = $shopId)
              AND sale.status IN ('completed', 'partially_returned', 'returned')
              AND COALESCE(sale.completed_at_utc, sale.created_at_utc) >= $fromUtc
              AND COALESCE(sale.completed_at_utc, sale.created_at_utc) < $toUtc
            GROUP BY payment.payment_method
        ),
        refunds AS
        (
            SELECT
                header.refund_method AS payment_method,
                COUNT(*) AS return_count,
                COALESCE(SUM(header.refund_amount_minor), 0) AS refund_minor
            FROM sales_returns AS header
            INNER JOIN shops AS shop
                ON shop.id = header.shop_id
            WHERE shop.organization_id = $organizationId
              AND ($consolidated = 1 OR header.shop_id = $shopId)
              AND header.status = 'completed'
              AND header.completed_at_utc >= $fromUtc
              AND header.completed_at_utc < $toUtc
            GROUP BY header.refund_method
        ),
        methods AS
        (
            SELECT payment_method FROM gross
            UNION
            SELECT payment_method FROM refunds
        )
        SELECT
            methods.payment_method,
            COALESCE(gross.sale_count, 0),
            COALESCE(refunds.return_count, 0),
            COALESCE(gross.gross_minor, 0),
            COALESCE(refunds.refund_minor, 0)
        FROM methods
        LEFT JOIN gross
            ON gross.payment_method = methods.payment_method
        LEFT JOIN refunds
            ON refunds.payment_method = methods.payment_method
        ORDER BY methods.payment_method;
        """;
        AddScopeParameters(command, context, consolidated, fromUtc, toUtc);

        var rows = new List<PaymentSalesSummaryRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long gross = reader.GetInt64(3);
            long refunded = reader.GetInt64(4);
            rows.Add(new PaymentSalesSummaryRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                gross,
                refunded,
                checked(gross - refunded)));
        }

        return rows;
    }

    private static void AddScopeParameters(
        SqliteCommand command,
        ActiveShopContextRecord context,
        bool consolidated,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$consolidated", consolidated ? 1 : 0);
        command.Parameters.AddWithValue("$fromUtc", fromUtc.ToString("O"));
        command.Parameters.AddWithValue("$toUtc", toUtc.ToString("O"));
    }

    private static SalesException Validation(
        string code,
        string message) =>
        new(StatusCodes.Status400BadRequest, code, message);
}
