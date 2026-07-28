using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Crm;

public sealed partial class CrmService
{
    public async Task<IReadOnlyList<CrmTimelineEntry>> GetCustomerTimelineAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string customerId,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(customerId);
        int limit = Math.Clamp(requestedLimit, 1, 2000);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using (var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            await RequireCustomerAsync(
                connection,
                transaction,
                context.OrganizationId,
                id,
                includeInactive: true,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT occurred_at_utc, entry_type, title, description, status,
               amount_minor, points_delta, shop_code, source_id
        FROM
        (
            SELECT
                COALESCE(sale.voided_at_utc, sale.completed_at_utc, sale.created_at_utc) AS occurred_at_utc,
                CASE WHEN sale.status = 'voided' THEN 'sale_void' ELSE 'sale' END AS entry_type,
                COALESCE(sale.invoice_number, sale.receipt_number) AS title,
                'Customer sale ' || sale.receipt_number AS description,
                sale.status AS status,
                CASE WHEN sale.status = 'voided' THEN -sale.total_minor ELSE sale.total_minor END AS amount_minor,
                0 AS points_delta,
                shop.code AS shop_code,
                sale.id AS source_id
            FROM sales AS sale
            INNER JOIN shops AS shop ON shop.id = sale.shop_id
            WHERE sale.customer_id = $customerId
              AND shop.organization_id = $organizationId

            UNION ALL

            SELECT
                COALESCE(receipt.reversed_at_utc, receipt.posted_at_utc, receipt.created_at_utc),
                CASE WHEN receipt.status = 'reversed' THEN 'customer_receipt_reversal' ELSE 'customer_receipt' END,
                receipt.receipt_number,
                'Customer account receipt via ' || replace(receipt.payment_method, '_', ' '),
                receipt.status,
                CASE WHEN receipt.status = 'reversed' THEN -receipt.amount_minor ELSE receipt.amount_minor END,
                0,
                shop.code,
                receipt.id
            FROM finance_customer_receipts AS receipt
            INNER JOIN shops AS shop ON shop.id = receipt.shop_id
            WHERE receipt.customer_id = $customerId
              AND receipt.organization_id = $organizationId

            UNION ALL

            SELECT
                communication.occurred_at_utc,
                'communication',
                CASE WHEN trim(communication.subject) = ''
                     THEN replace(communication.communication_type, '_', ' ')
                     ELSE communication.subject END,
                communication.details,
                communication.direction,
                0,
                0,
                shop.code,
                communication.id
            FROM crm_communications AS communication
            INNER JOIN shops AS shop ON shop.id = communication.shop_id
            WHERE communication.customer_id = $customerId
              AND communication.organization_id = $organizationId

            UNION ALL

            SELECT
                CASE
                    WHEN task.status = 'completed' THEN task.completed_at_utc
                    WHEN task.status = 'cancelled' THEN task.cancelled_at_utc
                    ELSE task.created_at_utc END,
                'task',
                task.title,
                task.details,
                task.status,
                0,
                0,
                shop.code,
                task.id
            FROM crm_tasks AS task
            INNER JOIN shops AS shop ON shop.id = task.shop_id
            WHERE task.customer_id = $customerId
              AND task.organization_id = $organizationId

            UNION ALL

            SELECT
                quotation.updated_at_utc,
                'quotation',
                quotation.quotation_number,
                'Quotation valid until ' || quotation.valid_until,
                quotation.status,
                quotation.total_minor,
                0,
                shop.code,
                quotation.id
            FROM crm_quotations AS quotation
            INNER JOIN shops AS shop ON shop.id = quotation.shop_id
            WHERE quotation.customer_id = $customerId
              AND quotation.organization_id = $organizationId

            UNION ALL

            SELECT
                ledger.created_at_utc,
                'loyalty',
                replace(ledger.entry_type, '_', ' '),
                ledger.reason,
                ledger.entry_type,
                0,
                ledger.points_delta,
                COALESCE(shop.code, ''),
                ledger.id
            FROM crm_loyalty_ledger AS ledger
            LEFT JOIN shops AS shop ON shop.id = ledger.shop_id
            WHERE ledger.customer_id = $customerId
              AND ledger.organization_id = $organizationId
        ) AS timeline
        ORDER BY occurred_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$customerId", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<CrmTimelineEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new CrmTimelineEntry(
                DateTimeOffset.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetString(8)));
        }
        return records;
    }

    public async Task<CrmDashboardRecord> GetDashboardAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await ExpireQuotationsAsync(
            connection,
            null,
            context.OrganizationId,
            context.ShopId,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            (SELECT COUNT(1)
             FROM finance_customers
             WHERE organization_id = $organizationId AND is_active = 1),
            (SELECT COUNT(1)
             FROM crm_customer_segments
             WHERE organization_id = $organizationId AND segment = 'prospect'),
            (SELECT COUNT(1)
             FROM crm_customer_sales_metrics
             WHERE organization_id = $organizationId
               AND first_sale_at_utc >= datetime('now', '-30 days')),
            (SELECT COUNT(1)
             FROM crm_customer_sales_metrics
             WHERE organization_id = $organizationId
               AND completed_sale_count >= 2),
            (SELECT COUNT(1)
             FROM crm_customer_segments
             WHERE organization_id = $organizationId AND segment = 'dormant'),
            (SELECT COUNT(1)
             FROM crm_customer_segments
             WHERE organization_id = $organizationId AND segment = 'debtor'),
            (SELECT COALESCE(SUM(outstanding_minor), 0)
             FROM crm_customer_outstanding_balances
             WHERE organization_id = $organizationId),
            (SELECT COUNT(1)
             FROM crm_tasks
             WHERE organization_id = $organizationId AND shop_id = $shopId AND status = 'open'),
            (SELECT COUNT(1)
             FROM crm_tasks
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND status = 'open' AND due_at_utc < strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            (SELECT COUNT(1)
             FROM crm_customer_profiles
             WHERE organization_id = $organizationId
               AND next_follow_up_at_utc IS NOT NULL
               AND next_follow_up_at_utc <= strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            (SELECT COUNT(1)
             FROM crm_quotations
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND status IN ('draft', 'sent', 'accepted')),
            (SELECT COALESCE(SUM(total_minor), 0)
             FROM crm_quotations
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND status IN ('draft', 'sent', 'accepted')),
            (SELECT COUNT(1)
             FROM crm_customer_profiles
             WHERE organization_id = $organizationId AND loyalty_enrolled = 1),
            (SELECT COALESCE(SUM(CASE WHEN current_points > 0 THEN current_points ELSE 0 END), 0)
             FROM crm_customer_profiles
             WHERE organization_id = $organizationId AND loyalty_enrolled = 1);
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Conflict("crm_dashboard_failed", "The CRM dashboard could not be calculated.");
        }
        return new CrmDashboardRecord(
            context.OrganizationId,
            context.ShopId,
            context.ShopCode,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13));
    }

    public async Task<IReadOnlyList<CrmSegmentRecord>> GetSegmentsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            segment.segment,
            COUNT(1),
            COALESCE(SUM(metrics.lifetime_spend_minor), 0),
            COALESCE(SUM(outstanding.outstanding_minor), 0)
        FROM crm_customer_segments AS segment
        INNER JOIN crm_customer_sales_metrics AS metrics
            ON metrics.customer_id = segment.customer_id
        INNER JOIN crm_customer_outstanding_balances AS outstanding
            ON outstanding.customer_id = segment.customer_id
        WHERE segment.organization_id = $organizationId
        GROUP BY segment.segment
        ORDER BY
            CASE segment.segment
                WHEN 'debtor' THEN 1
                WHEN 'dormant' THEN 2
                WHEN 'loyal' THEN 3
                WHEN 'active' THEN 4
                WHEN 'new' THEN 5
                WHEN 'prospect' THEN 6
                ELSE 7 END;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        var records = new List<CrmSegmentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new CrmSegmentRecord(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }
        return records;
    }
}
