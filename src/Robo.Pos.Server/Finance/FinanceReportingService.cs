using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Finance;

public sealed partial class FinanceService
{
    public async Task<IReadOnlyList<ReceivableItemRecord>> ListReceivablesAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? customerId,
        string? requestedStatus,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        string customer = string.IsNullOrWhiteSpace(customerId)
            ? string.Empty
            : NormalizeId(customerId);
        string status = NormalizeOpenItemStatus(requestedStatus);
        int limit = Math.Clamp(requestedLimit, 1, 1000);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireFinanceAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        WITH balances AS
        (
            SELECT
                item.id,
                COALESCE(SUM(
                    CASE WHEN receipt.status = 'posted'
                         THEN allocation.amount_minor ELSE 0 END), 0) AS settled_minor
            FROM finance_receivable_items AS item
            LEFT JOIN finance_customer_receipt_allocations AS allocation
                ON allocation.receivable_item_id = item.id
            LEFT JOIN finance_customer_receipts AS receipt
                ON receipt.id = allocation.receipt_id
            GROUP BY item.id
        )
        SELECT
            item.id,
            item.shop_id,
            shop.code,
            item.customer_id,
            customer.customer_number,
            customer.name,
            item.sale_id,
            item.document_number,
            item.document_date,
            item.due_date,
            item.original_amount_minor,
            balances.settled_minor,
            CASE WHEN journal.status = 'reversed'
                 THEN 0
                 ELSE item.original_amount_minor - balances.settled_minor END,
            CASE
                WHEN journal.status = 'reversed' THEN 'reversed'
                WHEN item.original_amount_minor - balances.settled_minor = 0 THEN 'settled'
                WHEN balances.settled_minor > 0 THEN 'partial'
                ELSE 'open'
            END
        FROM finance_receivable_items AS item
        INNER JOIN balances ON balances.id = item.id
        INNER JOIN finance_customers AS customer
            ON customer.id = item.customer_id
        INNER JOIN shops AS shop
            ON shop.id = item.shop_id
        INNER JOIN accounting_journals AS journal
            ON journal.id = item.posting_journal_id
        WHERE item.organization_id = $organizationId
          AND item.shop_id = $shopId
          AND ($customerId = '' OR item.customer_id = $customerId)
          AND
          (
              $status = ''
              OR $status = CASE
                  WHEN journal.status = 'reversed' THEN 'reversed'
                  WHEN item.original_amount_minor - balances.settled_minor = 0 THEN 'settled'
                  WHEN balances.settled_minor > 0 THEN 'partial'
                  ELSE 'open'
              END
          )
        ORDER BY item.due_date, item.document_date, item.document_number
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$customerId", customer);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<ReceivableItemRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new ReceivableItemRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetString(13)));
        }
        return records;
    }

    public async Task<IReadOnlyList<PayableItemRecord>> ListPayablesAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? supplierId,
        string? requestedStatus,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        string supplier = string.IsNullOrWhiteSpace(supplierId)
            ? string.Empty
            : NormalizeId(supplierId);
        string status = NormalizeOpenItemStatus(requestedStatus);
        int limit = Math.Clamp(requestedLimit, 1, 1000);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireFinanceAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        WITH balances AS
        (
            SELECT
                item.id,
                COALESCE(SUM(
                    CASE WHEN payment.status = 'posted'
                         THEN allocation.amount_minor ELSE 0 END), 0) AS settled_minor
            FROM finance_payable_items AS item
            LEFT JOIN finance_supplier_payment_allocations AS allocation
                ON allocation.payable_item_id = item.id
            LEFT JOIN finance_supplier_payments AS payment
                ON payment.id = allocation.payment_id
            GROUP BY item.id
        )
        SELECT
            item.id,
            item.shop_id,
            shop.code,
            item.supplier_id,
            COALESCE(supplier.name, 'Unassigned supplier'),
            item.purchase_id,
            item.document_number,
            item.supplier_invoice_number,
            item.document_date,
            item.due_date,
            item.original_amount_minor,
            balances.settled_minor,
            CASE WHEN journal.status = 'reversed'
                 THEN 0
                 ELSE item.original_amount_minor - balances.settled_minor END,
            CASE
                WHEN journal.status = 'reversed' THEN 'reversed'
                WHEN item.original_amount_minor - balances.settled_minor = 0 THEN 'settled'
                WHEN balances.settled_minor > 0 THEN 'partial'
                ELSE 'open'
            END
        FROM finance_payable_items AS item
        INNER JOIN balances ON balances.id = item.id
        LEFT JOIN suppliers AS supplier
            ON supplier.id = item.supplier_id
        INNER JOIN shops AS shop
            ON shop.id = item.shop_id
        INNER JOIN accounting_journals AS journal
            ON journal.id = item.posting_journal_id
        WHERE item.organization_id = $organizationId
          AND item.shop_id = $shopId
          AND ($supplierId = '' OR item.supplier_id = $supplierId)
          AND
          (
              $status = ''
              OR $status = CASE
                  WHEN journal.status = 'reversed' THEN 'reversed'
                  WHEN item.original_amount_minor - balances.settled_minor = 0 THEN 'settled'
                  WHEN balances.settled_minor > 0 THEN 'partial'
                  ELSE 'open'
              END
          )
        ORDER BY item.due_date, item.document_date, item.document_number
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$supplierId", supplier);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<PayableItemRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new PayableItemRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetString(13)));
        }
        return records;
    }

    public async Task<FinanceSettlementRecord> GetCustomerReceiptAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string receiptId,
        CancellationToken cancellationToken = default) =>
        await GetSettlementAsync(
            user,
            context,
            "customer_receipt",
            NormalizeId(receiptId),
            cancellationToken);

    public async Task<FinanceSettlementRecord> GetSupplierPaymentAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string paymentId,
        CancellationToken cancellationToken = default) =>
        await GetSettlementAsync(
            user,
            context,
            "supplier_payment",
            NormalizeId(paymentId),
            cancellationToken);

    public async Task<CounterpartyStatementReport> GetCustomerStatementAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string customerId,
        string? requestedFromDate,
        string? requestedToDate,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(customerId);
        (string fromDate, string toDate) = NormalizeReportPeriod(
            requestedFromDate,
            requestedToDate);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireFinanceAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);

        (string number, string name) = await ReadCustomerIdentityAsync(
            connection,
            context.OrganizationId,
            id,
            cancellationToken);
        List<RawStatementEvent> events = await ReadCustomerStatementEventsAsync(
            connection,
            context,
            id,
            toDate,
            cancellationToken);
        return BuildStatement(
            "customer",
            id,
            number,
            name,
            context.CurrencyCode,
            fromDate,
            toDate,
            events,
            customerStyle: true);
    }

    public async Task<CounterpartyStatementReport> GetSupplierStatementAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string supplierId,
        string? requestedFromDate,
        string? requestedToDate,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(supplierId);
        (string fromDate, string toDate) = NormalizeReportPeriod(
            requestedFromDate,
            requestedToDate);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireFinanceAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);

        string name = await ReadSupplierIdentityAsync(
            connection,
            context.OrganizationId,
            id,
            cancellationToken);
        List<RawStatementEvent> events = await ReadSupplierStatementEventsAsync(
            connection,
            context,
            id,
            toDate,
            cancellationToken);
        return BuildStatement(
            "supplier",
            id,
            id,
            name,
            context.CurrencyCode,
            fromDate,
            toDate,
            events,
            customerStyle: false);
    }

    public Task<AgeingReport> GetReceivablesAgeingAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? scope,
        string? asOfDate,
        CancellationToken cancellationToken = default) =>
        GetAgeingAsync(
            user,
            context,
            "receivables",
            scope,
            asOfDate,
            cancellationToken);

    public Task<AgeingReport> GetPayablesAgeingAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? scope,
        string? asOfDate,
        CancellationToken cancellationToken = default) =>
        GetAgeingAsync(
            user,
            context,
            "payables",
            scope,
            asOfDate,
            cancellationToken);

    public async Task<IReadOnlyList<CashbookEntryRecord>> GetCashbookAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? requestedScope,
        string? requestedFromDate,
        string? requestedToDate,
        string? accountSystemKey,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        (string scope, bool consolidated) = NormalizeScope(requestedScope, user);
        (string fromDate, string toDate) = NormalizeReportPeriod(
            requestedFromDate,
            requestedToDate);
        string systemKey = OptionalText(accountSystemKey, 100).ToLowerInvariant();
        int limit = Math.Clamp(requestedLimit, 1, 5000);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireFinanceAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            journal_id,
            journal_number,
            journal_date,
            shop_id,
            shop_code,
            account_id,
            account_code,
            account_name,
            system_key,
            direction,
            debit_minor,
            credit_minor,
            signed_amount_minor,
            journal_description,
            line_description,
            source_type,
            source_id,
            counterparty_type,
            counterparty_id,
            posted_at_utc
        FROM finance_cashbook_entries
        WHERE organization_id = $organizationId
          AND ($consolidated = 1 OR shop_id = $shopId)
          AND journal_date BETWEEN $fromDate AND $toDate
          AND ($systemKey = '' OR system_key = $systemKey)
        ORDER BY journal_date DESC, posted_at_utc DESC, journal_number DESC, journal_line_id DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$consolidated", consolidated ? 1 : 0);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$fromDate", fromDate);
        command.Parameters.AddWithValue("$toDate", toDate);
        command.Parameters.AddWithValue("$systemKey", systemKey);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<CashbookEntryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new CashbookEntryRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(19))));
        }
        return records;
    }

    private async Task<FinanceSettlementRecord> GetSettlementAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string settlementType,
        string settlementId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireFinanceAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);

        bool customerReceipt = settlementType == "customer_receipt";
        string table = customerReceipt
            ? "finance_customer_receipts"
            : "finance_supplier_payments";
        string counterpartyTable = customerReceipt
            ? "finance_customers"
            : "suppliers";
        string counterpartyColumn = customerReceipt
            ? "customer_id"
            : "supplier_id";
        string numberColumn = customerReceipt
            ? "receipt_number"
            : "payment_number";
        string dateColumn = customerReceipt
            ? "receipt_date"
            : "payment_date";

        await using var header = connection.CreateCommand();
        header.CommandText =
            $"""
            SELECT
                settlement.id,
                settlement.{numberColumn},
                settlement.{dateColumn},
                settlement.shop_id,
                shop.code,
                settlement.{counterpartyColumn},
                counterparty.name,
                settlement.payment_method,
                settlement.amount_minor,
                settlement.reference,
                settlement.notes,
                settlement.status,
                settlement.posting_journal_id,
                settlement.reversal_journal_id,
                creator.display_name,
                settlement.created_at_utc,
                settlement.posted_at_utc,
                settlement.reversed_at_utc,
                settlement.reversal_reason
            FROM {table} AS settlement
            INNER JOIN shops AS shop
                ON shop.id = settlement.shop_id
            INNER JOIN {counterpartyTable} AS counterparty
                ON counterparty.id = settlement.{counterpartyColumn}
            INNER JOIN users AS creator
                ON creator.id = settlement.created_by_user_id
            WHERE settlement.id = $id
              AND settlement.organization_id = $organizationId
              AND settlement.shop_id = $shopId
            LIMIT 1;
            """;
        header.Parameters.AddWithValue("$id", settlementId);
        header.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        header.Parameters.AddWithValue("$shopId", context.ShopId);

        await using var reader = await header.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "settlement_not_found",
                "The settlement could not be found in the active branch.");
        }

        string allocationTable = customerReceipt
            ? "finance_customer_receipt_allocations"
            : "finance_supplier_payment_allocations";
        string allocationParentColumn = customerReceipt
            ? "receipt_id"
            : "payment_id";
        string allocationItemColumn = customerReceipt
            ? "receivable_item_id"
            : "payable_item_id";
        string itemTable = customerReceipt
            ? "finance_receivable_items"
            : "finance_payable_items";

        var allocations = new List<SettlementAllocationRecord>();
        await using var allocationCommand = connection.CreateCommand();
        allocationCommand.CommandText =
            $"""
            SELECT allocation.{allocationItemColumn}, item.document_number, allocation.amount_minor
            FROM {allocationTable} AS allocation
            INNER JOIN {itemTable} AS item
                ON item.id = allocation.{allocationItemColumn}
            WHERE allocation.{allocationParentColumn} = $settlementId
            ORDER BY item.document_date, item.document_number;
            """;
        allocationCommand.Parameters.AddWithValue("$settlementId", settlementId);
        await using var allocationReader =
            await allocationCommand.ExecuteReaderAsync(cancellationToken);
        while (await allocationReader.ReadAsync(cancellationToken))
        {
            allocations.Add(new SettlementAllocationRecord(
                allocationReader.GetString(0),
                allocationReader.GetString(1),
                allocationReader.GetInt64(2)));
        }

        return new FinanceSettlementRecord(
            reader.GetString(0),
            settlementType,
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.GetString(14),
            DateTimeOffset.Parse(reader.GetString(15)),
            reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16)),
            reader.IsDBNull(17) ? null : DateTimeOffset.Parse(reader.GetString(17)),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            allocations);
    }

    private async Task<AgeingReport> GetAgeingAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string ledgerType,
        string? requestedScope,
        string? requestedAsOfDate,
        CancellationToken cancellationToken)
    {
        (string scope, bool consolidated) = NormalizeScope(requestedScope, user);
        string asOfDate = string.IsNullOrWhiteSpace(requestedAsOfDate)
            ? DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")
            : NormalizeDate(requestedAsOfDate, "invalid_ageing_date");

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireFinanceAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);

        bool receivables = ledgerType == "receivables";
        string itemTable = receivables
            ? "finance_receivable_items"
            : "finance_payable_items";
        string allocationTable = receivables
            ? "finance_customer_receipt_allocations"
            : "finance_supplier_payment_allocations";
        string settlementTable = receivables
            ? "finance_customer_receipts"
            : "finance_supplier_payments";
        string allocationItemColumn = receivables
            ? "receivable_item_id"
            : "payable_item_id";
        string allocationSettlementColumn = receivables
            ? "receipt_id"
            : "payment_id";
        string counterpartyColumn = receivables
            ? "customer_id"
            : "supplier_id";
        string counterpartyTable = receivables
            ? "finance_customers"
            : "suppliers";

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            WITH settled AS
            (
                SELECT
                    allocation.{allocationItemColumn} AS item_id,
                    COALESCE(SUM(allocation.amount_minor), 0) AS settled_minor
                FROM {allocationTable} AS allocation
                INNER JOIN {settlementTable} AS settlement
                    ON settlement.id = allocation.{allocationSettlementColumn}
                   AND settlement.status = 'posted'
                GROUP BY allocation.{allocationItemColumn}
            ),
            outstanding AS
            (
                SELECT
                    item.{counterpartyColumn} AS counterparty_id,
                    COALESCE(counterparty.name, 'Unassigned supplier') AS counterparty_name,
                    item.due_date,
                    item.original_amount_minor - COALESCE(settled.settled_minor, 0) AS amount_minor
                FROM {itemTable} AS item
                LEFT JOIN settled ON settled.item_id = item.id
                LEFT JOIN {counterpartyTable} AS counterparty
                    ON counterparty.id = item.{counterpartyColumn}
                INNER JOIN accounting_journals AS journal
                    ON journal.id = item.posting_journal_id
                WHERE item.organization_id = $organizationId
                  AND ($consolidated = 1 OR item.shop_id = $shopId)
                  AND item.document_date <= $asOfDate
                  AND journal.status = 'posted'
                  AND item.original_amount_minor - COALESCE(settled.settled_minor, 0) > 0
            )
            SELECT
                counterparty_id,
                counterparty_name,
                SUM(CASE WHEN due_date >= $asOfDate THEN amount_minor ELSE 0 END),
                SUM(CASE WHEN julianday($asOfDate) - julianday(due_date) BETWEEN 1 AND 30 THEN amount_minor ELSE 0 END),
                SUM(CASE WHEN julianday($asOfDate) - julianday(due_date) BETWEEN 31 AND 60 THEN amount_minor ELSE 0 END),
                SUM(CASE WHEN julianday($asOfDate) - julianday(due_date) BETWEEN 61 AND 90 THEN amount_minor ELSE 0 END),
                SUM(CASE WHEN julianday($asOfDate) - julianday(due_date) > 90 THEN amount_minor ELSE 0 END),
                SUM(amount_minor),
                COUNT(*)
            FROM outstanding
            GROUP BY counterparty_id, counterparty_name
            ORDER BY SUM(amount_minor) DESC, counterparty_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$consolidated", consolidated ? 1 : 0);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$asOfDate", asOfDate);

        var counterparties = new List<AgeingCounterpartyRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counterparties.Add(new AgeingCounterpartyRecord(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt32(8)));
        }

        long current = counterparties.Sum(item => item.CurrentMinor);
        long days1To30 = counterparties.Sum(item => item.Days1To30Minor);
        long days31To60 = counterparties.Sum(item => item.Days31To60Minor);
        long days61To90 = counterparties.Sum(item => item.Days61To90Minor);
        long over90 = counterparties.Sum(item => item.Over90DaysMinor);
        var buckets = new List<AgeingBucketRecord>
        {
            new("current", current, counterparties.Count(item => item.CurrentMinor > 0)),
            new("1-30", days1To30, counterparties.Count(item => item.Days1To30Minor > 0)),
            new("31-60", days31To60, counterparties.Count(item => item.Days31To60Minor > 0)),
            new("61-90", days61To90, counterparties.Count(item => item.Days61To90Minor > 0)),
            new("90+", over90, counterparties.Count(item => item.Over90DaysMinor > 0))
        };

        return new AgeingReport(
            ledgerType,
            scope,
            context.OrganizationId,
            consolidated ? null : context.ShopId,
            consolidated ? null : context.ShopCode,
            context.CurrencyCode,
            asOfDate,
            counterparties.Sum(item => item.TotalMinor),
            buckets,
            counterparties);
    }

    private static async Task<List<RawStatementEvent>> ReadCustomerStatementEventsAsync(
        SqliteConnection connection,
        ActiveShopContextRecord context,
        string customerId,
        string toDate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT event_date, event_type, reference, description, debit_minor, credit_minor, shop_code, source_id
        FROM
        (
            SELECT
                item.document_date AS event_date,
                'invoice' AS event_type,
                item.document_number AS reference,
                'Credit sale' AS description,
                item.original_amount_minor AS debit_minor,
                0 AS credit_minor,
                shop.code AS shop_code,
                item.sale_id AS source_id,
                item.created_at_utc AS sort_time
            FROM finance_receivable_items AS item
            INNER JOIN shops AS shop ON shop.id = item.shop_id
            WHERE item.organization_id = $organizationId
              AND item.shop_id = $shopId
              AND item.customer_id = $counterpartyId

            UNION ALL

            SELECT
                reversal.journal_date,
                'invoice_reversal',
                item.document_number,
                'Credit sale reversal',
                0,
                item.original_amount_minor,
                shop.code,
                item.sale_id,
                reversal.posted_at_utc
            FROM finance_receivable_items AS item
            INNER JOIN accounting_journals AS original ON original.id = item.posting_journal_id
            INNER JOIN accounting_journals AS reversal ON reversal.id = original.reversed_by_journal_id
            INNER JOIN shops AS shop ON shop.id = item.shop_id
            WHERE item.organization_id = $organizationId
              AND item.shop_id = $shopId
              AND item.customer_id = $counterpartyId

            UNION ALL

            SELECT
                receipt.receipt_date,
                'receipt',
                receipt.receipt_number,
                'Customer receipt via ' || receipt.payment_method,
                0,
                receipt.amount_minor,
                shop.code,
                receipt.id,
                receipt.posted_at_utc
            FROM finance_customer_receipts AS receipt
            INNER JOIN shops AS shop ON shop.id = receipt.shop_id
            WHERE receipt.organization_id = $organizationId
              AND receipt.shop_id = $shopId
              AND receipt.customer_id = $counterpartyId
              AND receipt.status IN ('posted', 'reversed')

            UNION ALL

            SELECT
                reversal.journal_date,
                'receipt_reversal',
                receipt.receipt_number,
                'Customer receipt reversal',
                receipt.amount_minor,
                0,
                shop.code,
                receipt.id,
                reversal.posted_at_utc
            FROM finance_customer_receipts AS receipt
            INNER JOIN accounting_journals AS reversal ON reversal.id = receipt.reversal_journal_id
            INNER JOIN shops AS shop ON shop.id = receipt.shop_id
            WHERE receipt.organization_id = $organizationId
              AND receipt.shop_id = $shopId
              AND receipt.customer_id = $counterpartyId
              AND receipt.status = 'reversed'
        )
        WHERE event_date <= $toDate
        ORDER BY event_date, sort_time, event_type, reference;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$counterpartyId", customerId);
        command.Parameters.AddWithValue("$toDate", toDate);
        return await ReadRawStatementEventsAsync(command, cancellationToken);
    }

    private static async Task<List<RawStatementEvent>> ReadSupplierStatementEventsAsync(
        SqliteConnection connection,
        ActiveShopContextRecord context,
        string supplierId,
        string toDate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT event_date, event_type, reference, description, debit_minor, credit_minor, shop_code, source_id
        FROM
        (
            SELECT
                item.document_date AS event_date,
                'purchase' AS event_type,
                item.document_number AS reference,
                CASE WHEN item.supplier_invoice_number = ''
                     THEN 'Supplier purchase'
                     ELSE 'Supplier invoice ' || item.supplier_invoice_number END,
                0 AS debit_minor,
                item.original_amount_minor AS credit_minor,
                shop.code AS shop_code,
                item.purchase_id AS source_id,
                item.created_at_utc AS sort_time
            FROM finance_payable_items AS item
            INNER JOIN shops AS shop ON shop.id = item.shop_id
            WHERE item.organization_id = $organizationId
              AND item.shop_id = $shopId
              AND item.supplier_id = $counterpartyId

            UNION ALL

            SELECT
                reversal.journal_date,
                'purchase_reversal',
                item.document_number,
                'Supplier purchase reversal',
                item.original_amount_minor,
                0,
                shop.code,
                item.purchase_id,
                reversal.posted_at_utc
            FROM finance_payable_items AS item
            INNER JOIN accounting_journals AS original ON original.id = item.posting_journal_id
            INNER JOIN accounting_journals AS reversal ON reversal.id = original.reversed_by_journal_id
            INNER JOIN shops AS shop ON shop.id = item.shop_id
            WHERE item.organization_id = $organizationId
              AND item.shop_id = $shopId
              AND item.supplier_id = $counterpartyId

            UNION ALL

            SELECT
                payment.payment_date,
                'payment',
                payment.payment_number,
                'Supplier payment via ' || payment.payment_method,
                payment.amount_minor,
                0,
                shop.code,
                payment.id,
                payment.posted_at_utc
            FROM finance_supplier_payments AS payment
            INNER JOIN shops AS shop ON shop.id = payment.shop_id
            WHERE payment.organization_id = $organizationId
              AND payment.shop_id = $shopId
              AND payment.supplier_id = $counterpartyId
              AND payment.status IN ('posted', 'reversed')

            UNION ALL

            SELECT
                reversal.journal_date,
                'payment_reversal',
                payment.payment_number,
                'Supplier payment reversal',
                0,
                payment.amount_minor,
                shop.code,
                payment.id,
                reversal.posted_at_utc
            FROM finance_supplier_payments AS payment
            INNER JOIN accounting_journals AS reversal ON reversal.id = payment.reversal_journal_id
            INNER JOIN shops AS shop ON shop.id = payment.shop_id
            WHERE payment.organization_id = $organizationId
              AND payment.shop_id = $shopId
              AND payment.supplier_id = $counterpartyId
              AND payment.status = 'reversed'
        )
        WHERE event_date <= $toDate
        ORDER BY event_date, sort_time, event_type, reference;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$counterpartyId", supplierId);
        command.Parameters.AddWithValue("$toDate", toDate);
        return await ReadRawStatementEventsAsync(command, cancellationToken);
    }

    private static async Task<List<RawStatementEvent>> ReadRawStatementEventsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var events = new List<RawStatementEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new RawStatementEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetString(7)));
        }
        return events;
    }

    private static CounterpartyStatementReport BuildStatement(
        string counterpartyType,
        string counterpartyId,
        string counterpartyNumber,
        string counterpartyName,
        string currencyCode,
        string fromDate,
        string toDate,
        IReadOnlyList<RawStatementEvent> events,
        bool customerStyle)
    {
        long opening = 0;
        foreach (RawStatementEvent item in events.Where(item =>
                     string.CompareOrdinal(item.Date, fromDate) < 0))
        {
            opening = customerStyle
                ? checked(opening + item.DebitMinor - item.CreditMinor)
                : checked(opening + item.CreditMinor - item.DebitMinor);
        }

        long balance = opening;
        var lines = new List<StatementLineRecord>();
        foreach (RawStatementEvent item in events.Where(item =>
                     string.CompareOrdinal(item.Date, fromDate) >= 0 &&
                     string.CompareOrdinal(item.Date, toDate) <= 0))
        {
            balance = customerStyle
                ? checked(balance + item.DebitMinor - item.CreditMinor)
                : checked(balance + item.CreditMinor - item.DebitMinor);
            lines.Add(new StatementLineRecord(
                item.Date,
                item.EntryType,
                item.Reference,
                item.Description,
                item.DebitMinor,
                item.CreditMinor,
                balance,
                item.ShopCode,
                item.SourceId));
        }

        return new CounterpartyStatementReport(
            counterpartyType,
            counterpartyId,
            counterpartyNumber,
            counterpartyName,
            currencyCode,
            fromDate,
            toDate,
            opening,
            balance,
            lines);
    }

    private static async Task<(string Number, string Name)> ReadCustomerIdentityAsync(
        SqliteConnection connection,
        string organizationId,
        string customerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT customer_number, name
        FROM finance_customers
        WHERE id = $id
          AND organization_id = $organizationId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", customerId);
        command.Parameters.AddWithValue("$organizationId", organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound("customer_not_found", "The customer could not be found.");
        }
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<string> ReadSupplierIdentityAsync(
        SqliteConnection connection,
        string organizationId,
        string supplierId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT name
        FROM suppliers
        WHERE id = $id
          AND organization_id = $organizationId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", supplierId);
        command.Parameters.AddWithValue("$organizationId", organizationId);
        string? name = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw NotFound("supplier_not_found", "The supplier could not be found.");
        }
        return name;
    }

    private static (string FromDate, string ToDate) NormalizeReportPeriod(
        string? requestedFromDate,
        string? requestedToDate)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        string fromDate = string.IsNullOrWhiteSpace(requestedFromDate)
            ? new DateOnly(today.Year, 1, 1).ToString("yyyy-MM-dd")
            : NormalizeDate(requestedFromDate, "invalid_report_from_date");
        string toDate = string.IsNullOrWhiteSpace(requestedToDate)
            ? today.ToString("yyyy-MM-dd")
            : NormalizeDate(requestedToDate, "invalid_report_to_date");
        if (string.CompareOrdinal(fromDate, toDate) > 0)
        {
            throw Validation(
                "invalid_report_period",
                "The report start date must not be after the end date.");
        }
        DateOnly from = DateOnly.ParseExact(fromDate, "yyyy-MM-dd");
        DateOnly to = DateOnly.ParseExact(toDate, "yyyy-MM-dd");
        if (to.DayNumber - from.DayNumber > 3660)
        {
            throw Validation(
                "report_period_too_large",
                "A finance report cannot cover more than ten years at once.");
        }
        return (fromDate, toDate);
    }

    private static (string Scope, bool Consolidated) NormalizeScope(
        string? requestedScope,
        AuthenticatedUser user)
    {
        string scope = requestedScope?.Trim().ToLowerInvariant() ?? "shop";
        if (scope is not ("shop" or "consolidated"))
        {
            throw Validation(
                "invalid_finance_scope",
                "Finance scope must be shop or consolidated.");
        }
        bool consolidated = scope == "consolidated";
        if (consolidated && !IsAdministrator(user))
        {
            throw Forbidden(
                "administrator_required",
                "Only an administrator can view consolidated finance reports.");
        }
        return (scope, consolidated);
    }

    private static string NormalizeOpenItemStatus(string? value)
    {
        string status = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (status.Length > 0 && status is not ("open" or "partial" or "settled" or "reversed"))
        {
            throw Validation(
                "invalid_open_item_status",
                "Use open, partial, settled or reversed.");
        }
        return status;
    }

    private sealed record RawStatementEvent(
        string Date,
        string EntryType,
        string Reference,
        string Description,
        long DebitMinor,
        long CreditMinor,
        string ShopCode,
        string SourceId);
}