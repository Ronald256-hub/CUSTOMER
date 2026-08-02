using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Inventory;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public sealed class CreditSalesReturnService
{
    private static readonly HashSet<string> Dispositions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "restock",
            "damaged"
        };

    private readonly DatabaseBootstrap _database;
    private readonly SalesReturnDocumentWriter _documents;

    public CreditSalesReturnService(
        DatabaseBootstrap database,
        SalesReturnDocumentWriter documents)
    {
        _database = database;
        _documents = documents;
    }

    public async Task<IReadOnlyList<CreditReturnableSaleListItem>>
        ListEligibleCreditSalesAsync(
            AuthenticatedUser user,
            ActiveShopContextRecord context,
            int requestedLimit,
            CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        int limit = Math.Clamp(requestedLimit, 1, 200);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        WITH settled AS
        (
            SELECT
                item.id,
                COALESCE(SUM
                (
                    CASE WHEN receipt.status = 'posted'
                         THEN allocation.amount_minor ELSE 0 END
                ), 0) AS settled_minor
            FROM finance_receivable_items AS item
            LEFT JOIN finance_customer_receipt_allocations AS allocation
                ON allocation.receivable_item_id = item.id
            LEFT JOIN finance_customer_receipts AS receipt
                ON receipt.id = allocation.receipt_id
            GROUP BY item.id
        ),
        returned AS
        (
            SELECT
                sale_id,
                COALESCE(SUM(return_amount_minor), 0) AS returned_minor
            FROM finance_credit_returns
            WHERE status = 'completed'
            GROUP BY sale_id
        ),
        remaining AS
        (
            SELECT
                sale_id,
                COALESCE(SUM(quantity - returned_quantity), 0)
                    AS remaining_quantity
            FROM sale_items
            GROUP BY sale_id
        )
        SELECT
            sale.id,
            sale.receipt_number,
            sale.invoice_number,
            customer.id,
            customer.customer_number,
            customer.name,
            receivable.id,
            sale.total_minor,
            COALESCE(returned.returned_minor, 0),
            receivable.original_amount_minor,
            COALESCE(settled.settled_minor, 0),
            COALESCE(remaining.remaining_quantity, 0),
            COALESCE(sale.completed_at_utc, sale.created_at_utc),
            sale.status,
            shop.id,
            shop.code,
            shop.name
        FROM sales AS sale
        INNER JOIN sale_payments AS payment
            ON payment.sale_id = sale.id
           AND payment.payment_method = 'credit'
        INNER JOIN finance_customers AS customer
            ON customer.id = sale.customer_id
        INNER JOIN finance_receivable_items AS receivable
            ON receivable.sale_id = sale.id
           AND receivable.customer_id = customer.id
        INNER JOIN accounting_journals AS source_journal
            ON source_journal.id = receivable.posting_journal_id
           AND source_journal.status = 'posted'
        INNER JOIN shops AS shop
            ON shop.id = sale.shop_id
        INNER JOIN settled
            ON settled.id = receivable.id
        LEFT JOIN returned
            ON returned.sale_id = sale.id
        LEFT JOIN remaining
            ON remaining.sale_id = sale.id
        WHERE shop.organization_id = $organizationId
          AND shop.id = $shopId
          AND sale.status IN ('completed', 'partially_returned')
          AND COALESCE(remaining.remaining_quantity, 0) > 0
        ORDER BY COALESCE(sale.completed_at_utc, sale.created_at_utc) DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<CreditReturnableSaleListItem>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long originalTotal = reader.GetInt64(7);
            long returnedAmount = reader.GetInt64(8);
            long receivableOriginal = reader.GetInt64(9);
            long receivableSettled = reader.GetInt64(10);

            records.Add(new CreditReturnableSaleListItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                originalTotal,
                returnedAmount,
                Math.Max(0, originalTotal - returnedAmount),
                receivableOriginal,
                receivableSettled,
                Math.Max(0, receivableOriginal - receivableSettled),
                reader.GetInt64(11),
                DateTimeOffset.Parse(reader.GetString(12)),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetString(15),
                reader.GetString(16)));
        }

        return records;
    }

    public async Task<CreditReturnableSaleDetails>
        GetReturnableCreditSaleAsync(
            AuthenticatedUser user,
            ActiveShopContextRecord context,
            string saleId,
            CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string id = NormalizeId(saleId);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        CreditSaleHeader header = await ReadCreditSaleHeaderAsync(
            connection,
            transaction: null,
            context,
            id,
            cancellationToken);
        IReadOnlyList<ReturnableSaleLine> lines =
            await ReadReturnableLinesAsync(
                connection,
                transaction: null,
                id,
                cancellationToken);

        return new CreditReturnableSaleDetails(
            header.SaleId,
            header.ReceiptNumber,
            header.InvoiceNumber,
            header.CustomerId,
            header.CustomerNumber,
            header.CustomerName,
            header.ReceivableItemId,
            header.TotalMinor,
            header.ReturnedAmountMinor,
            Math.Max(0, header.TotalMinor - header.ReturnedAmountMinor),
            header.ReceivableOriginalMinor,
            header.ReceivableSettledMinor,
            header.ReceivableOutstandingMinor,
            header.CompletedAtUtc,
            header.Status,
            header.ShopId,
            header.ShopCode,
            header.ShopName,
            lines);
    }

    public async Task<CreditSalesReturnRecord> CreateCreditReturnAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string saleId,
        CreateCreditSalesReturnRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string normalizedSaleId = NormalizeId(saleId);
        string reason = RequiredText(
            request.Reason,
            500,
            "credit_return_reason_required",
            "Enter a clear credit-return reason of at least five characters.");
        if (reason.Length < 5)
        {
            throw Validation(
                "credit_return_reason_required",
                "Enter a clear credit-return reason of at least five characters.");
        }
        string notes = OptionalText(request.Notes, 500);
        IReadOnlyList<NormalizedRequestLine> requestedLines =
            NormalizeLines(request.Items);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        CreditSaleHeader sale = await ReadCreditSaleHeaderAsync(
            connection,
            transaction,
            context,
            normalizedSaleId,
            cancellationToken);
        if (sale.Status is not ("completed" or "partially_returned"))
        {
            throw Conflict(
                "credit_sale_not_returnable",
                "Only a completed or partially returned credit sale can be returned.");
        }

        string shiftId = await FindOpenShiftIdAsync(
            connection,
            transaction,
            user.Id,
            context.ShopId,
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string journalDate =
            now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await EnsureOpenPeriodAsync(
            connection,
            transaction,
            context.OrganizationId,
            journalDate,
            cancellationToken);

        var lines = new List<CalculatedReturnLine>();
        foreach (NormalizedRequestLine requested in requestedLines)
        {
            lines.Add(await CalculateLineAsync(
                connection,
                transaction,
                normalizedSaleId,
                requested,
                cancellationToken));
        }

        long returnAmount = checked(lines.Sum(line => line.RefundMinor));
        long returnedBaseUnits = checked(
            lines.Sum(line => line.BaseUnitsReturned));
        long restockedBaseUnits = checked(
            lines.Sum(line => line.BaseUnitsRestocked));
        long returnedCost = checked(lines.Sum(line => line.CostValueMinor));
        long restockedCost = checked(
            lines.Sum(line => line.RestockedCostMinor));
        if (returnAmount <= 0)
        {
            throw Validation(
                "credit_return_has_no_value",
                "The selected lines do not have a remaining credit value.");
        }

        long receivableReduction = Math.Min(
            returnAmount,
            sale.ReceivableOutstandingMinor);
        long customerCredit = checked(returnAmount - receivableReduction);

        string returnId = Guid.NewGuid().ToString("N");
        string creditNoteNumber = await NextNumberAsync(
            connection,
            transaction,
            "finance_credit_return_sequences",
            "CRN",
            context,
            now,
            cancellationToken);

        await InsertReturnHeaderAsync(
            connection,
            transaction,
            returnId,
            context,
            sale,
            shiftId,
            creditNoteNumber,
            returnAmount,
            receivableReduction,
            customerCredit,
            returnedBaseUnits,
            restockedBaseUnits,
            returnedCost,
            restockedCost,
            reason,
            notes,
            user.Id,
            now,
            cancellationToken);

        foreach (CalculatedReturnLine line in lines)
        {
            await InsertReturnLineAsync(
                connection,
                transaction,
                returnId,
                line,
                cancellationToken);
            await ApplyReturnedQuantityAsync(
                connection,
                transaction,
                line,
                cancellationToken);
            if (line.BaseUnitsRestocked > 0)
            {
                await RestoreStockAsync(
                    connection,
                    transaction,
                    context.ShopId,
                    returnId,
                    creditNoteNumber,
                    line,
                    reason,
                    user.Id,
                    now,
                    cancellationToken);
            }
        }

        string? settlementReceiptId = null;
        if (receivableReduction > 0)
        {
            settlementReceiptId = await PostReceivableReductionAsync(
                connection,
                transaction,
                context,
                sale,
                returnId,
                creditNoteNumber,
                receivableReduction,
                reason,
                user.Id,
                now,
                journalDate,
                cancellationToken);
        }

        string? returnJournalId = null;
        if (customerCredit + restockedCost > 0)
        {
            returnJournalId = await PostCreditAndStockJournalAsync(
                connection,
                transaction,
                context,
                sale,
                returnId,
                creditNoteNumber,
                customerCredit,
                restockedCost,
                reason,
                user.Id,
                now,
                journalDate,
                cancellationToken);
        }

        if (customerCredit > 0)
        {
            await InsertCustomerCreditAsync(
                connection,
                transaction,
                context,
                sale,
                returnId,
                creditNoteNumber,
                customerCredit,
                returnJournalId!,
                now,
                cancellationToken);
        }

        await CompleteCreditReturnAsync(
            connection,
            transaction,
            returnId,
            settlementReceiptId,
            returnJournalId,
            cancellationToken);
        await UpdateSaleReturnStatusAsync(
            connection,
            transaction,
            normalizedSaleId,
            cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "credit_sale.return.completed",
            "credit_sales_return",
            returnId,
            new
            {
                context.OrganizationId,
                context.ShopId,
                saleId = normalizedSaleId,
                sale.ReceiptNumber,
                sale.CustomerId,
                creditNoteNumber,
                returnAmountMinor = returnAmount,
                receivableReductionMinor = receivableReduction,
                customerCreditMinor = customerCredit,
                returnedBaseUnits,
                restockedBaseUnits,
                returnedCostMinor = returnedCost,
                restockedCostMinor = restockedCost,
                reason,
                lineCount = lines.Count
            },
            cancellationToken);

        BusinessSnapshot business = await ReadBusinessAsync(
            connection,
            transaction,
            context.ShopId,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        CreditSalesReturnRecord created = await GetCreditReturnAsync(
            user,
            context,
            returnId,
            cancellationToken);

        IReadOnlyList<WrittenSalesReturnDocument> written =
            await _documents.WriteAsync(
                new SalesReturnDocumentSnapshot(
                    business.BusinessName,
                    business.Address,
                    business.Phone,
                    business.Email,
                    business.CurrencyCode,
                    created.CreditNoteNumber,
                    created.OriginalReceiptNumber,
                    created.CustomerName,
                    "credit_note",
                    created.Reason,
                    BuildDocumentNotes(created),
                    created.ApprovedByDisplayName,
                    created.CompletedAtUtc,
                    created.ReturnAmountMinor,
                    created.Items),
                cancellationToken);

        await RegisterDocumentsAsync(
            returnId,
            user.Id,
            written,
            now,
            cancellationToken);

        return await GetCreditReturnAsync(
            user,
            context,
            returnId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<CreditSalesReturnRecord>>
        ListCreditReturnsAsync(
            AuthenticatedUser user,
            ActiveShopContextRecord context,
            int requestedLimit,
            CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        int limit = Math.Clamp(requestedLimit, 1, 200);
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id
        FROM finance_credit_returns
        WHERE organization_id = $organizationId
          AND shop_id = $shopId
          AND status = 'completed'
        ORDER BY completed_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$limit", limit);

        var ids = new List<string>();
        await using (var reader =
                     await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
        }

        var records = new List<CreditSalesReturnRecord>(ids.Count);
        foreach (string id in ids)
        {
            records.Add(await GetCreditReturnAsync(
                user,
                context,
                id,
                cancellationToken));
        }
        return records;
    }

    public async Task<CreditSalesReturnRecord> GetCreditReturnAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string returnId,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string id = NormalizeId(returnId);
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        CreditReturnHeader header;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT
                header.id,
                header.credit_note_number,
                header.sale_id,
                header.original_receipt_number,
                header.customer_id,
                customer.customer_number,
                customer.name,
                header.receivable_item_id,
                header.status,
                header.return_amount_minor,
                header.receivable_reduction_minor,
                header.customer_credit_minor,
                header.returned_base_units,
                header.restocked_base_units,
                header.returned_cost_minor,
                header.restocked_cost_minor,
                header.reason,
                header.notes,
                creator.display_name,
                approver.display_name,
                header.completed_at_utc,
                shop.id,
                shop.code,
                shop.name
            FROM finance_credit_returns AS header
            INNER JOIN finance_customers AS customer
                ON customer.id = header.customer_id
            INNER JOIN shops AS shop
                ON shop.id = header.shop_id
            INNER JOIN users AS creator
                ON creator.id = header.created_by_user_id
            INNER JOIN users AS approver
                ON approver.id = header.approved_by_user_id
            WHERE header.id = $returnId
              AND header.organization_id = $organizationId
              AND header.shop_id = $shopId
            LIMIT 1;
            """;
            command.Parameters.AddWithValue("$returnId", id);
            command.Parameters.AddWithValue(
                "$organizationId",
                context.OrganizationId);
            command.Parameters.AddWithValue("$shopId", context.ShopId);

            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw NotFound(
                    "credit_sales_return_not_found",
                    "The credit-sales return could not be found.");
            }

            header = new CreditReturnHeader(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.GetInt64(14),
                reader.GetInt64(15),
                reader.GetString(16),
                reader.GetString(17),
                reader.GetString(18),
                reader.GetString(19),
                DateTimeOffset.Parse(reader.GetString(20)),
                reader.GetString(21),
                reader.GetString(22),
                reader.GetString(23));
        }

        IReadOnlyList<SalesReturnLineRecord> items =
            await ReadCreditReturnItemsAsync(
                connection,
                id,
                cancellationToken);
        IReadOnlyList<SalesReturnDocumentRecord> documents =
            await ReadCreditReturnDocumentsAsync(
                connection,
                id,
                cancellationToken);

        return new CreditSalesReturnRecord(
            header.Id,
            header.CreditNoteNumber,
            header.SaleId,
            header.OriginalReceiptNumber,
            header.CustomerId,
            header.CustomerNumber,
            header.CustomerName,
            header.ReceivableItemId,
            header.Status,
            header.ReturnAmountMinor,
            header.ReceivableReductionMinor,
            header.CustomerCreditMinor,
            header.ReturnedBaseUnits,
            header.RestockedBaseUnits,
            header.ReturnedCostMinor,
            header.RestockedCostMinor,
            header.Reason,
            header.Notes,
            header.CreatedByDisplayName,
            header.ApprovedByDisplayName,
            header.CompletedAtUtc,
            header.ShopId,
            header.ShopCode,
            header.ShopName,
            items,
            documents);
    }

    public async Task<StoredSalesReturnDocument> ResolveDocumentAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string returnId,
        string documentId,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string normalizedReturnId = NormalizeId(returnId);
        string normalizedDocumentId = NormalizeId(documentId);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            document.relative_path,
            document.file_format,
            document.document_number
        FROM finance_credit_return_documents AS document
        INNER JOIN finance_credit_returns AS header
            ON header.id = document.return_id
        WHERE document.id = $documentId
          AND document.return_id = $returnId
          AND header.organization_id = $organizationId
          AND header.shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$documentId", normalizedDocumentId);
        command.Parameters.AddWithValue("$returnId", normalizedReturnId);
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "credit_return_document_not_found",
                "The credit-return document could not be found.");
        }

        string relativePath = reader.GetString(0);
        string format = reader.GetString(1);
        string number = reader.GetString(2);
        string fullPath = _documents.ResolveStoredPath(relativePath);
        if (!File.Exists(fullPath))
        {
            throw NotFound(
                "credit_return_document_file_missing",
                "The stored credit-return document is missing.");
        }

        return new StoredSalesReturnDocument(
            fullPath,
            format == "html"
                ? "text/html; charset=utf-8"
                : "application/json; charset=utf-8",
            $"{number}.{format}");
    }

    public async Task<IReadOnlyList<CustomerCreditBalanceRecord>>
        ListCustomerCreditsAsync(
            AuthenticatedUser user,
            ActiveShopContextRecord context,
            string? customerId,
            string? requestedStatus,
            int requestedLimit,
            CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string customer = string.IsNullOrWhiteSpace(customerId)
            ? string.Empty
            : NormalizeId(customerId);
        string status = NormalizeCreditStatus(requestedStatus);
        int limit = Math.Clamp(requestedLimit, 1, 500);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            balance.id,
            balance.customer_id,
            customer.customer_number,
            customer.name,
            balance.credit_number,
            balance.source_credit_return_id,
            balance.original_amount_minor,
            balance.applied_amount_minor,
            balance.available_amount_minor,
            balance.status,
            balance.shop_id,
            shop.code,
            balance.created_at_utc
        FROM finance_customer_credit_balances AS balance
        INNER JOIN finance_customers AS customer
            ON customer.id = balance.customer_id
        INNER JOIN shops AS shop
            ON shop.id = balance.shop_id
        WHERE balance.organization_id = $organizationId
          AND balance.shop_id = $shopId
          AND ($customerId = '' OR balance.customer_id = $customerId)
          AND ($status = '' OR balance.status = $status)
        ORDER BY balance.created_at_utc DESC, balance.credit_number
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$customerId", customer);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<CustomerCreditBalanceRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new CustomerCreditBalanceRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                DateTimeOffset.Parse(reader.GetString(12))));
        }
        return records;
    }

    public async Task<CustomerCreditApplicationRecord>
        ApplyCustomerCreditAsync(
            AuthenticatedUser user,
            ActiveShopContextRecord context,
            ApplyCustomerCreditRequest request,
            CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string creditId = NormalizeId(request.CreditId);
        string receivableItemId = NormalizeId(request.ReceivableItemId);
        string applicationDate = NormalizeDate(
            request.ApplicationDate,
            "invalid_credit_application_date");
        if (request.AmountMinor <= 0)
        {
            throw Validation(
                "invalid_credit_application_amount",
                "The customer-credit application amount must be greater than zero.");
        }
        string notes = OptionalText(request.Notes, 500);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        await EnsureOpenPeriodAsync(
            connection,
            transaction,
            context.OrganizationId,
            applicationDate,
            cancellationToken);

        CustomerCreditSnapshot credit = await ReadCustomerCreditAsync(
            connection,
            transaction,
            context,
            creditId,
            cancellationToken);
        ReceivableSnapshot receivable = await ReadReceivableAsync(
            connection,
            transaction,
            context,
            receivableItemId,
            cancellationToken);

        if (!string.Equals(
                credit.CustomerId,
                receivable.CustomerId,
                StringComparison.Ordinal))
        {
            throw Validation(
                "customer_credit_counterparty_mismatch",
                "The customer credit and receivable must belong to the same customer.");
        }
        if (request.AmountMinor > credit.AvailableAmountMinor)
        {
            throw Conflict(
                "customer_credit_insufficient",
                $"Only {credit.AvailableAmountMinor:N0} remains available on {credit.CreditNumber}.");
        }
        if (request.AmountMinor > receivable.OutstandingAmountMinor)
        {
            throw Conflict(
                "credit_application_exceeds_receivable",
                $"Only {receivable.OutstandingAmountMinor:N0} remains outstanding on {receivable.DocumentNumber}.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string applicationId = Guid.NewGuid().ToString("N");
        string receiptId = Guid.NewGuid().ToString("N");
        string applicationNumber = await NextNumberAsync(
            connection,
            transaction,
            "finance_customer_credit_application_sequences",
            "CCA",
            context,
            now,
            cancellationToken);
        string journalId = "sys-customer-credit-application-" + applicationId;

        await InsertSystemSettlementAsync(
            connection,
            transaction,
            receiptId,
            context,
            credit.CustomerId,
            applicationNumber,
            applicationDate,
            "customer_credit",
            request.AmountMinor,
            credit.CreditNumber,
            notes,
            user.Id,
            now,
            cancellationToken);
        await InsertSettlementAllocationAsync(
            connection,
            transaction,
            receiptId,
            receivable.Id,
            request.AmountMinor,
            cancellationToken);

        string customerCreditAccount = await ResolveSystemAccountAsync(
            connection,
            transaction,
            context.OrganizationId,
            "customer_credits",
            cancellationToken);
        string receivableAccount = await ResolveSystemAccountAsync(
            connection,
            transaction,
            context.OrganizationId,
            "accounts_receivable",
            cancellationToken);

        await InsertJournalAsync(
            connection,
            transaction,
            journalId,
            context,
            "SYS-" + applicationNumber,
            applicationDate,
            $"Apply customer credit {credit.CreditNumber} to {receivable.DocumentNumber}",
            "customer_receipt:" + receiptId,
            request.AmountMinor,
            user.Id,
            now,
            cancellationToken);
        await InsertJournalLineAsync(
            connection,
            transaction,
            journalId,
            1,
            customerCreditAccount,
            context.ShopId,
            request.AmountMinor,
            0,
            $"Customer credit applied from {credit.CreditNumber}",
            credit.CustomerId,
            cancellationToken);
        await InsertJournalLineAsync(
            connection,
            transaction,
            journalId,
            2,
            receivableAccount,
            context.ShopId,
            0,
            request.AmountMinor,
            $"Receivable reduced on {receivable.DocumentNumber}",
            credit.CustomerId,
            cancellationToken);
        await PostJournalAsync(
            connection,
            transaction,
            journalId,
            user.Id,
            now,
            cancellationToken);
        await CompleteSystemSettlementAsync(
            connection,
            transaction,
            receiptId,
            journalId,
            now,
            cancellationToken);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
            """
            INSERT INTO finance_customer_credit_applications
            (
                id, organization_id, shop_id, customer_id, credit_id,
                receipt_id, receivable_item_id, application_number,
                application_date, amount_minor, posting_journal_id,
                notes, created_by_user_id, created_at_utc
            )
            VALUES
            (
                $id, $organizationId, $shopId, $customerId, $creditId,
                $receiptId, $receivableItemId, $applicationNumber,
                $applicationDate, $amount, $journalId,
                $notes, $userId, $now
            );
            """;
            insert.Parameters.AddWithValue("$id", applicationId);
            insert.Parameters.AddWithValue(
                "$organizationId",
                context.OrganizationId);
            insert.Parameters.AddWithValue("$shopId", context.ShopId);
            insert.Parameters.AddWithValue("$customerId", credit.CustomerId);
            insert.Parameters.AddWithValue("$creditId", credit.Id);
            insert.Parameters.AddWithValue("$receiptId", receiptId);
            insert.Parameters.AddWithValue(
                "$receivableItemId",
                receivable.Id);
            insert.Parameters.AddWithValue(
                "$applicationNumber",
                applicationNumber);
            insert.Parameters.AddWithValue(
                "$applicationDate",
                applicationDate);
            insert.Parameters.AddWithValue("$amount", request.AmountMinor);
            insert.Parameters.AddWithValue("$journalId", journalId);
            insert.Parameters.AddWithValue("$notes", notes);
            insert.Parameters.AddWithValue("$userId", user.Id);
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "finance.customer_credit.applied",
            "customer_credit_application",
            applicationId,
            new
            {
                context.OrganizationId,
                context.ShopId,
                creditId = credit.Id,
                credit.CreditNumber,
                receivableItemId = receivable.Id,
                receivable.DocumentNumber,
                applicationNumber,
                applicationDate,
                amountMinor = request.AmountMinor
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return await GetCreditApplicationAsync(
            user,
            context,
            applicationId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerCreditApplicationRecord>>
        ListCreditApplicationsAsync(
            AuthenticatedUser user,
            ActiveShopContextRecord context,
            string? customerId,
            int requestedLimit,
            CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string customer = string.IsNullOrWhiteSpace(customerId)
            ? string.Empty
            : NormalizeId(customerId);
        int limit = Math.Clamp(requestedLimit, 1, 500);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id
        FROM finance_customer_credit_applications
        WHERE organization_id = $organizationId
          AND shop_id = $shopId
          AND ($customerId = '' OR customer_id = $customerId)
        ORDER BY created_at_utc DESC, application_number
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$customerId", customer);
        command.Parameters.AddWithValue("$limit", limit);

        var ids = new List<string>();
        await using (var reader =
                     await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
        }

        var records = new List<CustomerCreditApplicationRecord>(ids.Count);
        foreach (string id in ids)
        {
            records.Add(await GetCreditApplicationAsync(
                user,
                context,
                id,
                cancellationToken));
        }
        return records;
    }

    private async Task<CustomerCreditApplicationRecord>
        GetCreditApplicationAsync(
            AuthenticatedUser user,
            ActiveShopContextRecord context,
            string applicationId,
            CancellationToken cancellationToken)
    {
        RequireAdministrator(user);
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            application.id,
            application.application_number,
            application.application_date,
            application.credit_id,
            credit.credit_number,
            application.customer_id,
            customer.customer_number,
            customer.name,
            application.receivable_item_id,
            receivable.document_number,
            application.amount_minor,
            application.notes,
            creator.display_name,
            application.created_at_utc,
            application.shop_id,
            shop.code
        FROM finance_customer_credit_applications AS application
        INNER JOIN finance_customer_credits AS credit
            ON credit.id = application.credit_id
        INNER JOIN finance_customers AS customer
            ON customer.id = application.customer_id
        INNER JOIN finance_receivable_items AS receivable
            ON receivable.id = application.receivable_item_id
        INNER JOIN users AS creator
            ON creator.id = application.created_by_user_id
        INNER JOIN shops AS shop
            ON shop.id = application.shop_id
        WHERE application.id = $id
          AND application.organization_id = $organizationId
          AND application.shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", applicationId);
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "customer_credit_application_not_found",
                "The customer-credit application could not be found.");
        }

        return new CustomerCreditApplicationRecord(
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
            reader.GetString(11),
            reader.GetString(12),
            DateTimeOffset.Parse(reader.GetString(13)),
            reader.GetString(14),
            reader.GetString(15));
    }

    private static IReadOnlyList<NormalizedRequestLine> NormalizeLines(
        IReadOnlyList<CreditSalesReturnLineRequest>? items)
    {
        if (items is null || items.Count == 0)
        {
            throw Validation(
                "credit_return_items_required",
                "Select at least one sold item to return.");
        }
        if (items.Count > 100)
        {
            throw Validation(
                "too_many_credit_return_items",
                "A credit return cannot contain more than 100 lines.");
        }

        var result = new List<NormalizedRequestLine>();
        foreach (IGrouping<long, CreditSalesReturnLineRequest> group
                 in items.GroupBy(item => item.SaleItemId))
        {
            if (group.Key <= 0 || group.Any(item => item.Quantity <= 0))
            {
                throw Validation(
                    "invalid_credit_return_item",
                    "Every credit-return line requires a valid item and quantity.");
            }

            string[] dispositions = group
                .Select(item => NormalizeDisposition(item.Disposition))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (dispositions.Length != 1)
            {
                throw Validation(
                    "mixed_credit_return_disposition",
                    "Split resellable and damaged quantities into separate returns.");
            }

            result.Add(new NormalizedRequestLine(
                group.Key,
                checked(group.Sum(item => item.Quantity)),
                dispositions[0]));
        }
        return result;
    }

    private static async Task<CalculatedReturnLine> CalculateLineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string saleId,
        NormalizedRequestLine requested,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        WITH return_history AS
        (
            SELECT
                sale_item_id,
                refund_minor,
                base_units_returned
            FROM sales_return_items AS item
            INNER JOIN sales_returns AS header
                ON header.id = item.return_id
               AND header.status = 'completed'

            UNION ALL

            SELECT
                sale_item_id,
                refund_minor,
                base_units_returned
            FROM finance_credit_return_items AS item
            INNER JOIN finance_credit_returns AS header
                ON header.id = item.return_id
               AND header.status = 'completed'
        ),
        history AS
        (
            SELECT
                sale_item_id,
                COALESCE(SUM(refund_minor), 0) AS refund_minor,
                COALESCE(SUM(base_units_returned), 0)
                    AS base_units_returned
            FROM return_history
            GROUP BY sale_item_id
        )
        SELECT
            item.id,
            item.product_id,
            item.product_name_snapshot,
            item.sku_snapshot,
            item.quantity,
            item.returned_quantity,
            item.sale_unit_snapshot,
            item.unit_size_ml_snapshot,
            item.unit_price_minor,
            item.unit_cost_minor,
            item.line_total_minor,
            item.base_units_deducted,
            COALESCE(history.refund_minor, 0),
            COALESCE(history.base_units_returned, 0)
        FROM sale_items AS item
        LEFT JOIN history
            ON history.sale_item_id = item.id
        WHERE item.id = $saleItemId
          AND item.sale_id = $saleId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$saleItemId", requested.SaleItemId);
        command.Parameters.AddWithValue("$saleId", saleId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "credit_sale_item_not_found",
                "A selected credit-sale item could not be found.");
        }

        long soldQuantity = reader.GetInt64(4);
        long returnedQuantity = reader.GetInt64(5);
        long remainingQuantity = soldQuantity - returnedQuantity;
        if (requested.Quantity > remainingQuantity)
        {
            throw Conflict(
                "credit_return_quantity_exceeds_remaining",
                $"Only {remainingQuantity:N0} of {reader.GetString(2)} remain returnable.");
        }

        long lineTotal = reader.GetInt64(10);
        long originalBaseUnits = reader.GetInt64(11);
        long previousRefund = reader.GetInt64(12);
        long previousBaseUnits = reader.GetInt64(13);
        long refundMinor = requested.Quantity == remainingQuantity
            ? checked(lineTotal - previousRefund)
            : checked(lineTotal * requested.Quantity / soldQuantity);
        long baseUnitsReturned = requested.Quantity == remainingQuantity
            ? checked(originalBaseUnits - previousBaseUnits)
            : checked(originalBaseUnits * requested.Quantity / soldQuantity);
        if (refundMinor <= 0 || baseUnitsReturned <= 0)
        {
            throw Conflict(
                "credit_return_line_has_no_value",
                $"{reader.GetString(2)} has no remaining return value.");
        }

        long unitCost = reader.GetInt64(9);
        long costValue = checked(unitCost * requested.Quantity);
        bool restock = requested.Disposition == "restock";

        return new CalculatedReturnLine(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            soldQuantity,
            returnedQuantity,
            requested.Quantity,
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.GetInt64(8),
            unitCost,
            refundMinor,
            baseUnitsReturned,
            costValue,
            requested.Disposition,
            restock ? baseUnitsReturned : 0,
            restock ? costValue : 0);
    }

    private static async Task<CreditSaleHeader> ReadCreditSaleHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ActiveShopContextRecord context,
        string saleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        WITH settled AS
        (
            SELECT
                allocation.receivable_item_id,
                COALESCE(SUM(allocation.amount_minor), 0) AS settled_minor
            FROM finance_customer_receipt_allocations AS allocation
            INNER JOIN finance_customer_receipts AS receipt
                ON receipt.id = allocation.receipt_id
               AND receipt.status = 'posted'
            GROUP BY allocation.receivable_item_id
        ),
        returned AS
        (
            SELECT
                sale_id,
                COALESCE(SUM(return_amount_minor), 0) AS returned_minor
            FROM finance_credit_returns
            WHERE status = 'completed'
            GROUP BY sale_id
        )
        SELECT
            sale.id,
            sale.receipt_number,
            sale.invoice_number,
            customer.id,
            customer.customer_number,
            customer.name,
            sale.total_minor,
            sale.status,
            COALESCE(sale.completed_at_utc, sale.created_at_utc),
            receivable.id,
            receivable.original_amount_minor,
            COALESCE(settled.settled_minor, 0),
            COALESCE(returned.returned_minor, 0),
            shop.id,
            shop.code,
            shop.name
        FROM sales AS sale
        INNER JOIN sale_payments AS payment
            ON payment.sale_id = sale.id
           AND payment.payment_method = 'credit'
        INNER JOIN finance_customers AS customer
            ON customer.id = sale.customer_id
        INNER JOIN finance_receivable_items AS receivable
            ON receivable.sale_id = sale.id
           AND receivable.customer_id = customer.id
        INNER JOIN accounting_journals AS source_journal
            ON source_journal.id = receivable.posting_journal_id
           AND source_journal.status = 'posted'
        INNER JOIN shops AS shop
            ON shop.id = sale.shop_id
        LEFT JOIN settled
            ON settled.receivable_item_id = receivable.id
        LEFT JOIN returned
            ON returned.sale_id = sale.id
        WHERE sale.id = $saleId
          AND shop.organization_id = $organizationId
          AND shop.id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$saleId", saleId);
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "credit_sale_not_found_in_active_shop",
                "The posted credit sale could not be found in the active shop.");
        }

        long receivableOriginal = reader.GetInt64(10);
        long receivableSettled = reader.GetInt64(11);
        return new CreditSaleHeader(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8)),
            reader.GetString(9),
            receivableOriginal,
            receivableSettled,
            Math.Max(0, receivableOriginal - receivableSettled),
            reader.GetInt64(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15));
    }

    private static async Task<IReadOnlyList<ReturnableSaleLine>>
        ReadReturnableLinesAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string saleId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        WITH return_history AS
        (
            SELECT item.sale_item_id, item.refund_minor
            FROM sales_return_items AS item
            INNER JOIN sales_returns AS header
                ON header.id = item.return_id
               AND header.status = 'completed'

            UNION ALL

            SELECT item.sale_item_id, item.refund_minor
            FROM finance_credit_return_items AS item
            INNER JOIN finance_credit_returns AS header
                ON header.id = item.return_id
               AND header.status = 'completed'
        ),
        history AS
        (
            SELECT
                sale_item_id,
                COALESCE(SUM(refund_minor), 0) AS refund_minor
            FROM return_history
            GROUP BY sale_item_id
        )
        SELECT
            item.id,
            item.product_id,
            item.product_name_snapshot,
            item.sku_snapshot,
            item.quantity,
            item.returned_quantity,
            item.sale_unit_snapshot,
            item.unit_size_ml_snapshot,
            item.unit_price_minor,
            item.line_total_minor,
            COALESCE(history.refund_minor, 0)
        FROM sale_items AS item
        LEFT JOIN history
            ON history.sale_item_id = item.id
        WHERE item.sale_id = $saleId
        ORDER BY item.id;
        """;
        command.Parameters.AddWithValue("$saleId", saleId);

        var lines = new List<ReturnableSaleLine>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long sold = reader.GetInt64(4);
            long returned = reader.GetInt64(5);
            long remaining = sold - returned;
            if (remaining <= 0)
            {
                continue;
            }

            lines.Add(new ReturnableSaleLine(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                sold,
                returned,
                remaining,
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetInt64(8),
                Math.Max(0, reader.GetInt64(9) - reader.GetInt64(10))));
        }
        return lines;
    }

    private static async Task InsertReturnHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string returnId,
        ActiveShopContextRecord context,
        CreditSaleHeader sale,
        string shiftId,
        string creditNoteNumber,
        long returnAmount,
        long receivableReduction,
        long customerCredit,
        long returnedBaseUnits,
        long restockedBaseUnits,
        long returnedCost,
        long restockedCost,
        string reason,
        string notes,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO finance_credit_returns
        (
            id, organization_id, shop_id, sale_id, shift_id,
            customer_id, receivable_item_id, credit_note_number,
            original_receipt_number, status, return_amount_minor,
            receivable_reduction_minor, customer_credit_minor,
            returned_base_units, restocked_base_units,
            returned_cost_minor, restocked_cost_minor,
            settlement_receipt_id, return_journal_id,
            reason, notes, created_by_user_id, approved_by_user_id,
            completed_at_utc, version
        )
        VALUES
        (
            $id, $organizationId, $shopId, $saleId, $shiftId,
            $customerId, $receivableItemId, $creditNoteNumber,
            $receiptNumber, 'draft', $returnAmount,
            $receivableReduction, $customerCredit,
            $returnedBaseUnits, $restockedBaseUnits,
            $returnedCost, $restockedCost,
            NULL, NULL,
            $reason, $notes, $userId, $userId, $now, 1
        );
        """;
        command.Parameters.AddWithValue("$id", returnId);
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$saleId", sale.SaleId);
        command.Parameters.AddWithValue("$shiftId", shiftId);
        command.Parameters.AddWithValue("$customerId", sale.CustomerId);
        command.Parameters.AddWithValue(
            "$receivableItemId",
            sale.ReceivableItemId);
        command.Parameters.AddWithValue(
            "$creditNoteNumber",
            creditNoteNumber);
        command.Parameters.AddWithValue(
            "$receiptNumber",
            sale.ReceiptNumber);
        command.Parameters.AddWithValue("$returnAmount", returnAmount);
        command.Parameters.AddWithValue(
            "$receivableReduction",
            receivableReduction);
        command.Parameters.AddWithValue("$customerCredit", customerCredit);
        command.Parameters.AddWithValue(
            "$returnedBaseUnits",
            returnedBaseUnits);
        command.Parameters.AddWithValue(
            "$restockedBaseUnits",
            restockedBaseUnits);
        command.Parameters.AddWithValue("$returnedCost", returnedCost);
        command.Parameters.AddWithValue("$restockedCost", restockedCost);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertReturnLineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string returnId,
        CalculatedReturnLine line,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO finance_credit_return_items
        (
            return_id, sale_item_id, product_id, product_name_snapshot,
            sku_snapshot, quantity, sale_unit_snapshot,
            unit_size_ml_snapshot, unit_price_minor, unit_cost_minor,
            refund_minor, base_units_returned, cost_value_minor,
            disposition, base_units_restocked, restocked_cost_minor
        )
        VALUES
        (
            $returnId, $saleItemId, $productId, $productName,
            $sku, $quantity, $saleUnit,
            $unitSizeMl, $unitPrice, $unitCost,
            $refundMinor, $baseUnitsReturned, $costValue,
            $disposition, $baseUnitsRestocked, $restockedCost
        );
        """;
        command.Parameters.AddWithValue("$returnId", returnId);
        command.Parameters.AddWithValue("$saleItemId", line.SaleItemId);
        command.Parameters.AddWithValue("$productId", line.ProductId);
        command.Parameters.AddWithValue("$productName", line.ProductName);
        command.Parameters.AddWithValue("$sku", line.Sku);
        command.Parameters.AddWithValue("$quantity", line.Quantity);
        command.Parameters.AddWithValue("$saleUnit", line.SaleUnit);
        command.Parameters.AddWithValue(
            "$unitSizeMl",
            (object?)line.UnitSizeMl ?? DBNull.Value);
        command.Parameters.AddWithValue("$unitPrice", line.UnitPriceMinor);
        command.Parameters.AddWithValue("$unitCost", line.UnitCostMinor);
        command.Parameters.AddWithValue("$refundMinor", line.RefundMinor);
        command.Parameters.AddWithValue(
            "$baseUnitsReturned",
            line.BaseUnitsReturned);
        command.Parameters.AddWithValue("$costValue", line.CostValueMinor);
        command.Parameters.AddWithValue("$disposition", line.Disposition);
        command.Parameters.AddWithValue(
            "$baseUnitsRestocked",
            line.BaseUnitsRestocked);
        command.Parameters.AddWithValue(
            "$restockedCost",
            line.RestockedCostMinor);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyReturnedQuantityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CalculatedReturnLine line,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE sale_items
        SET returned_quantity = returned_quantity + $quantity
        WHERE id = $saleItemId
          AND returned_quantity + $quantity <= quantity;
        """;
        command.Parameters.AddWithValue("$quantity", line.Quantity);
        command.Parameters.AddWithValue("$saleItemId", line.SaleItemId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "credit_sale_item_return_conflict",
                $"The returnable quantity for {line.ProductName} changed. Reload and try again.");
        }
    }

    private static async Task RestoreStockAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string shopId,
        string returnId,
        string creditNoteNumber,
        CalculatedReturnLine line,
        string reason,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long current;
        int version;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
            """
            SELECT quantity_base_units, version
            FROM shop_stock_balances
            WHERE shop_id = $shopId
              AND product_id = $productId
            LIMIT 1;
            """;
            read.Parameters.AddWithValue("$shopId", shopId);
            read.Parameters.AddWithValue("$productId", line.ProductId);
            await using var reader =
                await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw Conflict(
                    "shop_stock_balance_missing",
                    $"The stock balance for {line.ProductName} is missing.");
            }
            current = reader.GetInt64(0);
            version = reader.GetInt32(1);
        }

        long updated = checked(current + line.BaseUnitsRestocked);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE shop_stock_balances
            SET quantity_base_units = $quantity,
                version = version + 1,
                updated_at_utc = $now
            WHERE shop_id = $shopId
              AND product_id = $productId
              AND version = $version;
            """;
            update.Parameters.AddWithValue("$quantity", updated);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$shopId", shopId);
            update.Parameters.AddWithValue("$productId", line.ProductId);
            update.Parameters.AddWithValue("$version", version);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "stock_changed_during_credit_return",
                    $"Stock changed while restoring {line.ProductName}. Reload and try again.");
            }
        }

        await ShopInventoryService.InsertMovementAsync(
            connection,
            transaction,
            shopId,
            line.ProductId,
            "sale_return",
            line.BaseUnitsRestocked,
            updated,
            line.RestockedCostMinor,
            "credit_sales_return",
            returnId,
            $"{creditNoteNumber}: {reason}",
            userId,
            userId,
            now,
            cancellationToken);
    }

    private static async Task<string> PostReceivableReductionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        CreditSaleHeader sale,
        string returnId,
        string creditNoteNumber,
        long amount,
        string reason,
        string userId,
        DateTimeOffset now,
        string journalDate,
        CancellationToken cancellationToken)
    {
        string receiptId = Guid.NewGuid().ToString("N");
        string settlementNumber = "CNSET-" + creditNoteNumber;
        string journalId = "sys-credit-return-ar-" + returnId;

        await InsertSystemSettlementAsync(
            connection,
            transaction,
            receiptId,
            context,
            sale.CustomerId,
            settlementNumber,
            journalDate,
            "credit_note",
            amount,
            creditNoteNumber,
            reason,
            userId,
            now,
            cancellationToken);
        await InsertSettlementAllocationAsync(
            connection,
            transaction,
            receiptId,
            sale.ReceivableItemId,
            amount,
            cancellationToken);

        string revenueAccount = await ResolveSystemAccountAsync(
            connection,
            transaction,
            context.OrganizationId,
            "sales_revenue",
            cancellationToken);
        string receivableAccount = await ResolveSystemAccountAsync(
            connection,
            transaction,
            context.OrganizationId,
            "accounts_receivable",
            cancellationToken);

        await InsertJournalAsync(
            connection,
            transaction,
            journalId,
            context,
            "SYS-AR-" + creditNoteNumber,
            journalDate,
            $"Receivable adjustment for {creditNoteNumber} and receipt {sale.ReceiptNumber}",
            "customer_receipt:" + receiptId,
            amount,
            userId,
            now,
            cancellationToken);
        await InsertJournalLineAsync(
            connection,
            transaction,
            journalId,
            1,
            revenueAccount,
            context.ShopId,
            amount,
            0,
            $"Revenue returned on {creditNoteNumber}",
            sale.CustomerId,
            cancellationToken);
        await InsertJournalLineAsync(
            connection,
            transaction,
            journalId,
            2,
            receivableAccount,
            context.ShopId,
            0,
            amount,
            $"Accounts receivable reduced by {creditNoteNumber}",
            sale.CustomerId,
            cancellationToken);
        await PostJournalAsync(
            connection,
            transaction,
            journalId,
            userId,
            now,
            cancellationToken);
        await CompleteSystemSettlementAsync(
            connection,
            transaction,
            receiptId,
            journalId,
            now,
            cancellationToken);

        return receiptId;
    }

    private static async Task<string> PostCreditAndStockJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        CreditSaleHeader sale,
        string returnId,
        string creditNoteNumber,
        long customerCredit,
        long restockedCost,
        string reason,
        string userId,
        DateTimeOffset now,
        string journalDate,
        CancellationToken cancellationToken)
    {
        string journalId = "sys-credit-sale-return-" + returnId;
        long total = checked(customerCredit + restockedCost);
        await InsertJournalAsync(
            connection,
            transaction,
            journalId,
            context,
            "SYS-" + creditNoteNumber,
            journalDate,
            $"Credit-sale return {creditNoteNumber} for receipt {sale.ReceiptNumber}: {reason}",
            "credit_sale_return:" + returnId,
            total,
            userId,
            now,
            cancellationToken);

        int lineNumber = 1;
        if (customerCredit > 0)
        {
            string revenueAccount = await ResolveSystemAccountAsync(
                connection,
                transaction,
                context.OrganizationId,
                "sales_revenue",
                cancellationToken);
            string customerCreditAccount = await ResolveSystemAccountAsync(
                connection,
                transaction,
                context.OrganizationId,
                "customer_credits",
                cancellationToken);
            await InsertJournalLineAsync(
                connection,
                transaction,
                journalId,
                lineNumber++,
                revenueAccount,
                context.ShopId,
                customerCredit,
                0,
                $"Revenue returned into customer credit {creditNoteNumber}",
                sale.CustomerId,
                cancellationToken);
            await InsertJournalLineAsync(
                connection,
                transaction,
                journalId,
                lineNumber++,
                customerCreditAccount,
                context.ShopId,
                0,
                customerCredit,
                $"Customer credit liability from {creditNoteNumber}",
                sale.CustomerId,
                cancellationToken);
        }

        if (restockedCost > 0)
        {
            string inventoryAccount = await ResolveSystemAccountAsync(
                connection,
                transaction,
                context.OrganizationId,
                "inventory",
                cancellationToken);
            string cogsAccount = await ResolveSystemAccountAsync(
                connection,
                transaction,
                context.OrganizationId,
                "cost_of_goods_sold",
                cancellationToken);
            await InsertJournalLineAsync(
                connection,
                transaction,
                journalId,
                lineNumber++,
                inventoryAccount,
                context.ShopId,
                restockedCost,
                0,
                $"Inventory restored by {creditNoteNumber}",
                sale.CustomerId,
                cancellationToken);
            await InsertJournalLineAsync(
                connection,
                transaction,
                journalId,
                lineNumber,
                cogsAccount,
                context.ShopId,
                0,
                restockedCost,
                $"COGS reversal for {creditNoteNumber}",
                sale.CustomerId,
                cancellationToken);
        }

        await PostJournalAsync(
            connection,
            transaction,
            journalId,
            userId,
            now,
            cancellationToken);
        return journalId;
    }

    private static async Task InsertCustomerCreditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        CreditSaleHeader sale,
        string returnId,
        string creditNoteNumber,
        long amount,
        string journalId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO finance_customer_credits
        (
            id, organization_id, shop_id, customer_id,
            source_credit_return_id, credit_number,
            original_amount_minor, posting_journal_id, created_at_utc
        )
        VALUES
        (
            $id, $organizationId, $shopId, $customerId,
            $returnId, $creditNumber,
            $amount, $journalId, $now
        );
        """;
        command.Parameters.AddWithValue(
            "$id",
            "customer-credit-" + returnId);
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$customerId", sale.CustomerId);
        command.Parameters.AddWithValue("$returnId", returnId);
        command.Parameters.AddWithValue("$creditNumber", creditNoteNumber);
        command.Parameters.AddWithValue("$amount", amount);
        command.Parameters.AddWithValue("$journalId", journalId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteCreditReturnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string returnId,
        string? settlementReceiptId,
        string? returnJournalId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE finance_credit_returns
        SET settlement_receipt_id = $settlementReceiptId,
            return_journal_id = $returnJournalId,
            status = 'completed',
            version = version + 1
        WHERE id = $returnId
          AND status = 'draft';
        """;
        command.Parameters.AddWithValue(
            "$settlementReceiptId",
            (object?)settlementReceiptId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$returnJournalId",
            (object?)returnJournalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$returnId", returnId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "credit_return_completion_conflict",
                "The credit return changed while it was being completed.");
        }
    }

    private static async Task InsertSystemSettlementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string receiptId,
        ActiveShopContextRecord context,
        string customerId,
        string number,
        string date,
        string paymentMethod,
        long amount,
        string reference,
        string notes,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO finance_customer_receipts
        (
            id, organization_id, shop_id, customer_id,
            receipt_number, receipt_date, payment_method,
            amount_minor, reference, notes, status,
            posting_journal_id, reversal_journal_id,
            created_by_user_id, reversed_by_user_id,
            created_at_utc, posted_at_utc,
            reversed_at_utc, reversal_reason
        )
        VALUES
        (
            $id, $organizationId, $shopId, $customerId,
            $number, $date, $paymentMethod,
            $amount, $reference, $notes, 'draft',
            NULL, NULL,
            $userId, NULL,
            $now, NULL,
            NULL, NULL
        );
        """;
        command.Parameters.AddWithValue("$id", receiptId);
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$customerId", customerId);
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue("$date", date);
        command.Parameters.AddWithValue("$paymentMethod", paymentMethod);
        command.Parameters.AddWithValue("$amount", amount);
        command.Parameters.AddWithValue("$reference", reference);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSettlementAllocationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string receiptId,
        string receivableItemId,
        long amount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO finance_customer_receipt_allocations
        (receipt_id, receivable_item_id, amount_minor)
        VALUES ($receiptId, $receivableItemId, $amount);
        """;
        command.Parameters.AddWithValue("$receiptId", receiptId);
        command.Parameters.AddWithValue(
            "$receivableItemId",
            receivableItemId);
        command.Parameters.AddWithValue("$amount", amount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteSystemSettlementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string receiptId,
        string journalId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE finance_customer_receipts
        SET posting_journal_id = $journalId,
            posted_at_utc = $now,
            status = 'posted'
        WHERE id = $receiptId
          AND status = 'draft';
        """;
        command.Parameters.AddWithValue("$journalId", journalId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$receiptId", receiptId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "system_credit_settlement_conflict",
                "The non-cash customer settlement could not be posted.");
        }
    }

    private static async Task InsertJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string journalId,
        ActiveShopContextRecord context,
        string journalNumber,
        string journalDate,
        string description,
        string sourceId,
        long total,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO accounting_journals
        (
            id, organization_id, shop_id, journal_number, journal_date,
            currency_code, description, source_type, source_id, status,
            total_debit_minor, total_credit_minor, version,
            created_by_user_id, created_at_utc, updated_at_utc
        )
        VALUES
        (
            $id, $organizationId, $shopId, $journalNumber, $journalDate,
            $currencyCode, $description, 'system', $sourceId, 'draft',
            $total, $total, 1,
            $userId, $now, $now
        );
        """;
        command.Parameters.AddWithValue("$id", journalId);
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$journalNumber", journalNumber);
        command.Parameters.AddWithValue("$journalDate", journalDate);
        command.Parameters.AddWithValue(
            "$currencyCode",
            context.CurrencyCode);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$total", total);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertJournalLineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string journalId,
        int lineNumber,
        string accountId,
        string shopId,
        long debit,
        long credit,
        string description,
        string customerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO accounting_journal_lines
        (
            journal_id, line_number, account_id, shop_id,
            debit_minor, credit_minor, description,
            counterparty_type, counterparty_id
        )
        VALUES
        (
            $journalId, $lineNumber, $accountId, $shopId,
            $debit, $credit, $description,
            'customer', $customerId
        );
        """;
        command.Parameters.AddWithValue("$journalId", journalId);
        command.Parameters.AddWithValue("$lineNumber", lineNumber);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$debit", debit);
        command.Parameters.AddWithValue("$credit", credit);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$customerId", customerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PostJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string journalId,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE accounting_journals
        SET status = 'posted',
            posted_by_user_id = $userId,
            posted_at_utc = $now,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $journalId
          AND status = 'draft';
        """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$journalId", journalId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "credit_return_journal_post_failed",
                "The credit-return journal could not be posted.");
        }
    }

    private static async Task UpdateSaleReturnStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string saleId,
        CancellationToken cancellationToken)
    {
        long remaining;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
            """
            SELECT COALESCE(SUM(quantity - returned_quantity), 0)
            FROM sale_items
            WHERE sale_id = $saleId;
            """;
            read.Parameters.AddWithValue("$saleId", saleId);
            remaining = Convert.ToInt64(
                await read.ExecuteScalarAsync(cancellationToken));
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE sales
        SET status = $status
        WHERE id = $saleId
          AND status IN ('completed', 'partially_returned');
        """;
        update.Parameters.AddWithValue(
            "$status",
            remaining == 0 ? "returned" : "partially_returned");
        update.Parameters.AddWithValue("$saleId", saleId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "credit_sale_return_status_conflict",
                "The sale status changed during the credit return.");
        }
    }

    private static async Task<CustomerCreditSnapshot> ReadCustomerCreditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string creditId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            id,
            customer_id,
            credit_number,
            available_amount_minor
        FROM finance_customer_credit_balances
        WHERE id = $id
          AND organization_id = $organizationId
          AND shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", creditId);
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "customer_credit_not_found",
                "The customer credit could not be found in the active shop.");
        }
        return new CustomerCreditSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3));
    }

    private static async Task<ReceivableSnapshot> ReadReceivableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string receivableItemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            item.id,
            item.customer_id,
            item.document_number,
            item.original_amount_minor,
            COALESCE(SUM
            (
                CASE WHEN receipt.status = 'posted'
                     THEN allocation.amount_minor ELSE 0 END
            ), 0) AS settled_minor
        FROM finance_receivable_items AS item
        INNER JOIN accounting_journals AS journal
            ON journal.id = item.posting_journal_id
           AND journal.status = 'posted'
        LEFT JOIN finance_customer_receipt_allocations AS allocation
            ON allocation.receivable_item_id = item.id
        LEFT JOIN finance_customer_receipts AS receipt
            ON receipt.id = allocation.receipt_id
        WHERE item.id = $id
          AND item.organization_id = $organizationId
          AND item.shop_id = $shopId
        GROUP BY item.id
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", receivableItemId);
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "receivable_not_found",
                "The receivable could not be found in the active shop.");
        }

        long original = reader.GetInt64(3);
        long settled = reader.GetInt64(4);
        return new ReceivableSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            original,
            settled,
            Math.Max(0, original - settled));
    }

    private static async Task<string> NextNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sequenceTable,
        string prefix,
        ActiveShopContextRecord context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (sequenceTable is not
            ("finance_credit_return_sequences" or
             "finance_customer_credit_application_sequences"))
        {
            throw new InvalidOperationException(
                "Unsupported credit sequence table.");
        }

        await using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = transaction;
            ensure.CommandText =
                $"""
                INSERT OR IGNORE INTO {sequenceTable}
                (shop_id, next_value, updated_at_utc)
                VALUES ($shopId, 1, $now);
                """;
            ensure.Parameters.AddWithValue("$shopId", context.ShopId);
            ensure.Parameters.AddWithValue("$now", now.ToString("O"));
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        long value;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                $"SELECT next_value FROM {sequenceTable} WHERE shop_id = $shopId;";
            read.Parameters.AddWithValue("$shopId", context.ShopId);
            value = Convert.ToInt64(
                await read.ExecuteScalarAsync(cancellationToken));
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                $"""
                UPDATE {sequenceTable}
                SET next_value = next_value + 1,
                    updated_at_utc = $now
                WHERE shop_id = $shopId
                  AND next_value = $expected;
                """;
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$shopId", context.ShopId);
            update.Parameters.AddWithValue("$expected", value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "credit_document_number_conflict",
                    "The credit document number changed. Retry the operation.");
            }
        }

        string code = string.IsNullOrWhiteSpace(context.ShopCode)
            ? "SHOP"
            : context.ShopCode.ToUpperInvariant();
        return $"{prefix}-{code}-{now:yyyy}-{value:000000}";
    }

    private static async Task<string> FindOpenShiftIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        string shopId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT id
        FROM teller_shifts
        WHERE teller_user_id = $userId
          AND shop_id = $shopId
          AND status = 'open'
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$shopId", shopId);
        string? id = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken));
        if (string.IsNullOrWhiteSpace(id))
        {
            throw Conflict(
                "open_shift_required_for_credit_return",
                "Open a shift at the active shop before processing a credit return.");
        }
        return id;
    }

    private static async Task EnsureOpenPeriodAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string date,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COUNT(1)
        FROM accounting_periods
        WHERE organization_id = $organizationId
          AND status = 'open'
          AND $date BETWEEN start_date AND end_date;
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$date", date);
        if (Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw Conflict(
                "accounting_period_closed",
                "The transaction date is not inside an open accounting period.");
        }
    }

    private static async Task<string> ResolveSystemAccountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string systemKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT id
        FROM accounting_accounts
        WHERE organization_id = $organizationId
          AND system_key = $systemKey
          AND is_active = 1
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$systemKey", systemKey);
        string? id = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken));
        if (string.IsNullOrWhiteSpace(id))
        {
            throw Conflict(
                "system_account_missing",
                $"The required {systemKey.Replace('_', ' ')} account is missing.");
        }
        return id;
    }

    private static async Task<BusinessSnapshot> ReadBusinessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string shopId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            business.business_name,
            shop.name,
            shop.is_head_office,
            CASE WHEN TRIM(shop.address) <> ''
                 THEN shop.address ELSE business.address END,
            CASE WHEN TRIM(shop.phone) <> ''
                 THEN shop.phone ELSE business.phone END,
            CASE WHEN TRIM(shop.email) <> ''
                 THEN shop.email ELSE business.email END,
            shop.currency_code
        FROM business_settings AS business
        INNER JOIN shops AS shop
            ON shop.id = $shopId
        WHERE business.id = 1
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Conflict(
                "business_settings_missing",
                "The active-shop business settings are missing.");
        }

        string businessName = reader.GetString(0);
        string shopName = reader.GetString(1);
        bool headOffice = reader.GetInt32(2) == 1;
        return new BusinessSnapshot(
            headOffice ? businessName : $"{businessName} - {shopName}",
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6));
    }

    private async Task RegisterDocumentsAsync(
        string returnId,
        string userId,
        IReadOnlyList<WrittenSalesReturnDocument> documents,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        foreach (WrittenSalesReturnDocument document in documents)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            INSERT INTO finance_credit_return_documents
            (
                id, return_id, document_type, document_number,
                file_format, relative_path, file_sha256,
                file_size_bytes, generated_by_user_id, generated_at_utc
            )
            VALUES
            (
                $id, $returnId, $documentType, $documentNumber,
                $fileFormat, $relativePath, $fileSha256,
                $fileSizeBytes, $userId, $generatedAt
            );
            """;
            command.Parameters.AddWithValue(
                "$id",
                Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$returnId", returnId);
            command.Parameters.AddWithValue(
                "$documentType",
                document.DocumentType);
            command.Parameters.AddWithValue(
                "$documentNumber",
                document.DocumentNumber);
            command.Parameters.AddWithValue(
                "$fileFormat",
                document.FileFormat);
            command.Parameters.AddWithValue(
                "$relativePath",
                document.RelativePath);
            command.Parameters.AddWithValue(
                "$fileSha256",
                document.FileSha256);
            command.Parameters.AddWithValue(
                "$fileSizeBytes",
                document.FileSizeBytes);
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue(
                "$generatedAt",
                generatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<SalesReturnLineRecord>>
        ReadCreditReturnItemsAsync(
            SqliteConnection connection,
            string returnId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            id, sale_item_id, product_id, product_name_snapshot,
            sku_snapshot, quantity, sale_unit_snapshot,
            unit_size_ml_snapshot, unit_price_minor, unit_cost_minor,
            refund_minor, base_units_returned, cost_value_minor,
            disposition, base_units_restocked, restocked_cost_minor
        FROM finance_credit_return_items
        WHERE return_id = $returnId
        ORDER BY id;
        """;
        command.Parameters.AddWithValue("$returnId", returnId);

        var records = new List<SalesReturnLineRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new SalesReturnLineRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetString(13),
                reader.GetInt64(14),
                reader.GetInt64(15)));
        }
        return records;
    }

    private static async Task<IReadOnlyList<SalesReturnDocumentRecord>>
        ReadCreditReturnDocumentsAsync(
            SqliteConnection connection,
            string returnId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            id, document_type, document_number, file_format,
            relative_path, file_sha256, file_size_bytes
        FROM finance_credit_return_documents
        WHERE return_id = $returnId
        ORDER BY file_format;
        """;
        command.Parameters.AddWithValue("$returnId", returnId);

        var records = new List<SalesReturnDocumentRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new SalesReturnDocumentRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6)));
        }
        return records;
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string eventType,
        string entityType,
        string entityId,
        object details,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO audit_logs
        (
            occurred_at_utc, user_id, username, event_type,
            entity_type, entity_id, success, details_json,
            client_ip_hash
        )
        VALUES
        (
            $now, $userId, $username, $eventType,
            $entityType, $entityId, 1, $detailsJson,
            NULL
        );
        """;
        command.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildDocumentNotes(CreditSalesReturnRecord record)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(record.Notes))
        {
            parts.Add(record.Notes);
        }
        if (record.ReceivableReductionMinor > 0)
        {
            parts.Add(
                $"Receivable reduced by {record.ReceivableReductionMinor:N0}.");
        }
        if (record.CustomerCreditMinor > 0)
        {
            parts.Add(
                $"Customer credit created: {record.CustomerCreditMinor:N0}.");
        }
        return string.Join(" ", parts);
    }

    private static void RequireAdministrator(AuthenticatedUser user)
    {
        if (!string.Equals(
                user.Role,
                "admin",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SalesException(
                StatusCodes.Status403Forbidden,
                "administrator_required",
                "Only an administrator can process credit returns or customer credits.");
        }
    }

    private static string NormalizeId(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 100)
        {
            throw Validation(
                "invalid_identifier",
                "The supplied identifier is invalid.");
        }
        return normalized;
    }

    private static string NormalizeDisposition(string? value)
    {
        string normalized =
            value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!Dispositions.Contains(normalized))
        {
            throw Validation(
                "invalid_credit_return_disposition",
                "Return disposition must be restock or damaged.");
        }
        return normalized;
    }

    private static string NormalizeDate(string? value, string errorCode)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (!DateOnly.TryParseExact(
                normalized,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw Validation(
                errorCode,
                "Use a valid date in YYYY-MM-DD format.");
        }
        return normalized;
    }

    private static string NormalizeCreditStatus(string? value)
    {
        string status = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (status.Length > 0 &&
            status is not ("open" or "partial" or "applied"))
        {
            throw Validation(
                "invalid_customer_credit_status",
                "Use open, partial or applied for customer-credit status.");
        }
        return status;
    }

    private static string RequiredText(
        string? value,
        int maximumLength,
        string errorCode,
        string message)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw Validation(errorCode, message);
        }
        if (normalized.Length > maximumLength)
        {
            throw Validation(
                "text_too_long",
                $"Text cannot exceed {maximumLength} characters.");
        }
        return normalized;
    }

    private static string OptionalText(string? value, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximumLength)
        {
            throw Validation(
                "text_too_long",
                $"Text cannot exceed {maximumLength} characters.");
        }
        return normalized;
    }

    private static SalesException Validation(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    private static SalesException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);

    private static SalesException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private sealed record NormalizedRequestLine(
        long SaleItemId,
        long Quantity,
        string Disposition);

    private sealed record CalculatedReturnLine(
        long SaleItemId,
        string ProductId,
        string ProductName,
        string Sku,
        long SoldQuantity,
        long PreviouslyReturnedQuantity,
        long Quantity,
        string SaleUnit,
        int? UnitSizeMl,
        long UnitPriceMinor,
        long UnitCostMinor,
        long RefundMinor,
        long BaseUnitsReturned,
        long CostValueMinor,
        string Disposition,
        long BaseUnitsRestocked,
        long RestockedCostMinor);

    private sealed record CreditSaleHeader(
        string SaleId,
        string ReceiptNumber,
        string? InvoiceNumber,
        string CustomerId,
        string CustomerNumber,
        string CustomerName,
        long TotalMinor,
        string Status,
        DateTimeOffset CompletedAtUtc,
        string ReceivableItemId,
        long ReceivableOriginalMinor,
        long ReceivableSettledMinor,
        long ReceivableOutstandingMinor,
        long ReturnedAmountMinor,
        string ShopId,
        string ShopCode,
        string ShopName);

    private sealed record CreditReturnHeader(
        string Id,
        string CreditNoteNumber,
        string SaleId,
        string OriginalReceiptNumber,
        string CustomerId,
        string CustomerNumber,
        string CustomerName,
        string ReceivableItemId,
        string Status,
        long ReturnAmountMinor,
        long ReceivableReductionMinor,
        long CustomerCreditMinor,
        long ReturnedBaseUnits,
        long RestockedBaseUnits,
        long ReturnedCostMinor,
        long RestockedCostMinor,
        string Reason,
        string Notes,
        string CreatedByDisplayName,
        string ApprovedByDisplayName,
        DateTimeOffset CompletedAtUtc,
        string ShopId,
        string ShopCode,
        string ShopName);

    private sealed record CustomerCreditSnapshot(
        string Id,
        string CustomerId,
        string CreditNumber,
        long AvailableAmountMinor);

    private sealed record ReceivableSnapshot(
        string Id,
        string CustomerId,
        string DocumentNumber,
        long OriginalAmountMinor,
        long SettledAmountMinor,
        long OutstandingAmountMinor);

    private sealed record BusinessSnapshot(
        string BusinessName,
        string Address,
        string Phone,
        string Email,
        string CurrencyCode);
}
