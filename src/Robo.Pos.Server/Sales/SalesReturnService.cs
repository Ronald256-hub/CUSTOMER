using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Inventory;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public sealed class SalesReturnService
{
    private static readonly HashSet<string> RefundMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cash",
            "mobile_money",
            "card",
            "bank"
        };

    private static readonly HashSet<string> Dispositions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "restock",
            "damaged"
        };

    private readonly DatabaseBootstrap _database;
    private readonly SalesReturnDocumentWriter _documents;

    public SalesReturnService(
        DatabaseBootstrap database,
        SalesReturnDocumentWriter documents)
    {
        _database = database;
        _documents = documents;
    }

    public async Task<IReadOnlyList<ReturnableSaleListItem>> ListEligibleSalesAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        int limit,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        int safeLimit = Math.Clamp(limit, 1, 200);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            sale.id,
            sale.receipt_number,
            sale.invoice_number,
            sale.customer_name,
            payment.payment_method,
            sale.total_minor,
            COALESCE(returned.refund_minor, 0),
            COALESCE(remaining.remaining_quantity, 0),
            COALESCE(sale.completed_at_utc, sale.created_at_utc),
            sale.status,
            shop.id,
            shop.code,
            shop.name
        FROM sales AS sale
        INNER JOIN shops AS shop
            ON shop.id = sale.shop_id
        INNER JOIN sale_payments AS payment
            ON payment.sale_id = sale.id
        LEFT JOIN
        (
            SELECT sale_id, SUM(refund_amount_minor) AS refund_minor
            FROM sales_returns
            WHERE status = 'completed'
            GROUP BY sale_id
        ) AS returned
            ON returned.sale_id = sale.id
        LEFT JOIN
        (
            SELECT sale_id, SUM(quantity - returned_quantity) AS remaining_quantity
            FROM sale_items
            GROUP BY sale_id
        ) AS remaining
            ON remaining.sale_id = sale.id
        WHERE shop.organization_id = $organizationId
          AND shop.id = $shopId
          AND sale.status IN ('completed', 'partially_returned')
          AND payment.payment_method <> 'credit'
          AND COALESCE(remaining.remaining_quantity, 0) > 0
        ORDER BY COALESCE(sale.completed_at_utc, sale.created_at_utc) DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$limit", safeLimit);

        var records = new List<ReturnableSaleListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long original = reader.GetInt64(5);
            long returned = reader.GetInt64(6);
            records.Add(new ReturnableSaleListItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                original,
                returned,
                Math.Max(0, original - returned),
                reader.GetInt64(7),
                DateTimeOffset.Parse(reader.GetString(8)),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12)));
        }
        return records;
    }

    public async Task<ReturnableSaleDetails> GetReturnableSaleAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string saleId,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string id = NormalizeId(saleId);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        SaleHeader header = await ReadSaleHeaderAsync(
            connection,
            transaction: null,
            context,
            id,
            cancellationToken);
        IReadOnlyList<ReturnableSaleLine> lines = await ReadReturnableLinesAsync(
            connection,
            transaction: null,
            id,
            cancellationToken);

        return new ReturnableSaleDetails(
            header.SaleId,
            header.ReceiptNumber,
            header.InvoiceNumber,
            header.CustomerName,
            header.PaymentMethod,
            header.TotalMinor,
            header.ReturnedAmountMinor,
            Math.Max(0, header.TotalMinor - header.ReturnedAmountMinor),
            header.CompletedAtUtc,
            header.Status,
            header.ShopId,
            header.ShopCode,
            header.ShopName,
            lines);
    }

    public async Task<SalesReturnRecord> CreateReturnAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string saleId,
        CreateSalesReturnRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string normalizedSaleId = NormalizeId(saleId);
        string refundMethod = NormalizeRefundMethod(request.RefundMethod);
        string reason = RequiredText(
            request.Reason,
            500,
            "return_reason_required",
            "Enter a clear return reason of at least five characters.");
        if (reason.Length < 5)
        {
            throw Validation(
                "return_reason_required",
                "Enter a clear return reason of at least five characters.");
        }
        string notes = OptionalText(request.Notes, 500);
        IReadOnlyList<NormalizedRequestLine> requestedLines = NormalizeLines(request.Items);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        SaleHeader sale = await ReadSaleHeaderAsync(
            connection,
            transaction,
            context,
            normalizedSaleId,
            cancellationToken);

        if (sale.Status is not ("completed" or "partially_returned"))
        {
            throw Conflict(
                "sale_not_returnable",
                "Only a completed or partially returned sale can receive another return.");
        }
        if (sale.PaymentMethod == "credit")
        {
            throw Conflict(
                "credit_sale_return_requires_account_adjustment",
                "Credit-account returns require a receivable adjustment and cannot be processed as a cash refund.");
        }
        if (!string.Equals(refundMethod, sale.PaymentMethod, StringComparison.OrdinalIgnoreCase))
        {
            throw Validation(
                "refund_method_mismatch",
                $"Refund this sale through {sale.PaymentMethod.Replace('_', ' ')} to preserve the payment audit trail.");
        }

        string shiftId = await FindOpenShiftIdAsync(
            connection,
            transaction,
            user.Id,
            context.ShopId,
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string journalDate = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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

        long refundAmount = lines.Sum(line => line.RefundMinor);
        long returnedBaseUnits = lines.Sum(line => line.BaseUnitsReturned);
        long restockedBaseUnits = lines.Sum(line => line.BaseUnitsRestocked);
        long returnedCost = lines.Sum(line => line.CostValueMinor);
        long restockedCost = lines.Sum(line => line.RestockedCostMinor);
        if (refundAmount <= 0)
        {
            throw Validation(
                "return_has_no_refund_value",
                "The selected lines do not have a refundable value.");
        }

        string returnId = Guid.NewGuid().ToString("N");
        string returnNumber = await NextReturnNumberAsync(
            connection,
            transaction,
            context,
            now,
            cancellationToken);

        await InsertHeaderAsync(
            connection,
            transaction,
            returnId,
            context,
            sale,
            shiftId,
            returnNumber,
            refundMethod,
            refundAmount,
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
            await InsertLineAsync(
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
                    returnNumber,
                    line,
                    reason,
                    user.Id,
                    now,
                    cancellationToken);
            }
        }

        await PostAccountingAsync(
            connection,
            transaction,
            context,
            sale,
            returnId,
            returnNumber,
            refundMethod,
            refundAmount,
            restockedCost,
            reason,
            user.Id,
            now,
            journalDate,
            cancellationToken);

        await using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText =
            """
            UPDATE sales_returns
            SET status = 'completed',
                version = version + 1
            WHERE id = $returnId
              AND status = 'draft';
            """;
            complete.Parameters.AddWithValue("$returnId", returnId);
            if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "return_completion_conflict",
                    "The return changed while it was being completed.");
            }
        }

        await UpdateSaleReturnStatusAsync(
            connection,
            transaction,
            normalizedSaleId,
            cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            returnId,
            new
            {
                context.OrganizationId,
                context.ShopId,
                saleId = normalizedSaleId,
                sale.ReceiptNumber,
                returnNumber,
                refundMethod,
                refundAmountMinor = refundAmount,
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

        SalesReturnRecord created = await GetReturnAsync(
            user,
            context,
            returnId,
            cancellationToken);

        IReadOnlyList<WrittenSalesReturnDocument> written = await _documents.WriteAsync(
            new SalesReturnDocumentSnapshot(
                business.BusinessName,
                business.Address,
                business.Phone,
                business.Email,
                business.CurrencyCode,
                created.ReturnNumber,
                created.OriginalReceiptNumber,
                sale.CustomerName,
                created.RefundMethod,
                created.Reason,
                created.Notes,
                created.ApprovedByDisplayName,
                created.CompletedAtUtc,
                created.RefundAmountMinor,
                created.Items),
            cancellationToken);

        await RegisterDocumentsAsync(
            returnId,
            user.Id,
            written,
            now,
            cancellationToken);

        return await GetReturnAsync(
            user,
            context,
            returnId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<SalesReturnRecord>> ListReturnsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        int limit,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        int safeLimit = Math.Clamp(limit, 1, 200);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id
        FROM sales_returns
        WHERE organization_id = $organizationId
          AND shop_id = $shopId
          AND status = 'completed'
        ORDER BY completed_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$limit", safeLimit);

        var ids = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        }

        var records = new List<SalesReturnRecord>(ids.Count);
        foreach (string id in ids)
        {
            records.Add(await GetReturnAsync(user, context, id, cancellationToken));
        }
        return records;
    }

    public async Task<SalesReturnRecord> GetReturnAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string returnId,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string id = NormalizeId(returnId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        SalesReturnHeader header;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT
                header.id,
                header.return_number,
                header.sale_id,
                header.original_receipt_number,
                header.status,
                header.refund_method,
                header.refund_amount_minor,
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
            FROM sales_returns AS header
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
            command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            command.Parameters.AddWithValue("$shopId", context.ShopId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw NotFound("sales_return_not_found", "The sales return could not be found.");
            }
            header = new SalesReturnHeader(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7),
                reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetString(11),
                reader.GetString(12), reader.GetString(13), reader.GetString(14),
                DateTimeOffset.Parse(reader.GetString(15)), reader.GetString(16),
                reader.GetString(17), reader.GetString(18));
        }

        IReadOnlyList<SalesReturnLineRecord> items = await ReadReturnItemsAsync(
            connection,
            id,
            cancellationToken);
        IReadOnlyList<SalesReturnDocumentRecord> documents = await ReadDocumentsAsync(
            connection,
            id,
            cancellationToken);

        return new SalesReturnRecord(
            header.Id, header.ReturnNumber, header.SaleId, header.OriginalReceiptNumber,
            header.Status, header.RefundMethod, header.RefundAmountMinor,
            header.ReturnedBaseUnits, header.RestockedBaseUnits, header.ReturnedCostMinor,
            header.RestockedCostMinor, header.Reason, header.Notes,
            header.CreatedByDisplayName, header.ApprovedByDisplayName,
            header.CompletedAtUtc, header.ShopId, header.ShopCode, header.ShopName,
            items, documents);
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
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT document.relative_path, document.file_format, document.document_number
        FROM sales_return_documents AS document
        INNER JOIN sales_returns AS header
            ON header.id = document.return_id
        WHERE document.id = $documentId
          AND document.return_id = $returnId
          AND header.organization_id = $organizationId
          AND header.shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$documentId", normalizedDocumentId);
        command.Parameters.AddWithValue("$returnId", normalizedReturnId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound("credit_note_not_found", "The credit-note document could not be found.");
        }

        string relativePath = reader.GetString(0);
        string format = reader.GetString(1);
        string number = reader.GetString(2);
        string fullPath = _documents.ResolveStoredPath(relativePath);
        if (!File.Exists(fullPath))
        {
            throw NotFound("credit_note_file_missing", "The stored credit-note file is missing.");
        }
        return new StoredSalesReturnDocument(
            fullPath,
            format == "html" ? "text/html; charset=utf-8" : "application/json; charset=utf-8",
            $"{number}.{format}");
    }

    private static IReadOnlyList<NormalizedRequestLine> NormalizeLines(
        IReadOnlyList<SalesReturnLineRequest>? items)
    {
        if (items is null || items.Count == 0)
        {
            throw Validation("return_items_required", "Select at least one sold item to return.");
        }
        if (items.Count > 100)
        {
            throw Validation("too_many_return_items", "A return cannot contain more than 100 lines.");
        }

        var result = new List<NormalizedRequestLine>();
        foreach (IGrouping<long, SalesReturnLineRequest> group in items.GroupBy(item => item.SaleItemId))
        {
            if (group.Key <= 0 || group.Any(item => item.Quantity <= 0))
            {
                throw Validation("invalid_return_item", "Every returned line requires a valid item and quantity.");
            }
            string[] dispositions = group
                .Select(item => NormalizeDisposition(item.Disposition))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (dispositions.Length != 1)
            {
                throw Validation(
                    "mixed_return_disposition",
                    "Split restock and damaged quantities into separate return operations.");
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
        LEFT JOIN
        (
            SELECT
                return_item.sale_item_id,
                SUM(return_item.refund_minor) AS refund_minor,
                SUM(return_item.base_units_returned) AS base_units_returned
            FROM sales_return_items AS return_item
            INNER JOIN sales_returns AS header
                ON header.id = return_item.return_id
            WHERE header.status = 'completed'
            GROUP BY return_item.sale_item_id
        ) AS history
            ON history.sale_item_id = item.id
        WHERE item.id = $saleItemId
          AND item.sale_id = $saleId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$saleItemId", requested.SaleItemId);
        command.Parameters.AddWithValue("$saleId", saleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound("sale_item_not_found", "A selected sold item could not be found.");
        }

        long soldQuantity = reader.GetInt64(4);
        long returnedQuantity = reader.GetInt64(5);
        long remainingQuantity = soldQuantity - returnedQuantity;
        if (requested.Quantity > remainingQuantity)
        {
            throw Conflict(
                "return_quantity_exceeds_remaining",
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
                "return_line_has_no_value",
                $"{reader.GetString(2)} does not have a remaining refundable quantity or value.");
        }

        long unitCost = reader.GetInt64(9);
        long costValue = checked(unitCost * requested.Quantity);
        bool restock = requested.Disposition == "restock";

        return new CalculatedReturnLine(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            soldQuantity, returnedQuantity, requested.Quantity, reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.GetInt64(8), unitCost,
            refundMinor, baseUnitsReturned, costValue, requested.Disposition,
            restock ? baseUnitsReturned : 0, restock ? costValue : 0);
    }

    private static async Task<SaleHeader> ReadSaleHeaderAsync(
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
        SELECT
            sale.id,
            sale.receipt_number,
            sale.invoice_number,
            sale.customer_name,
            sale.total_minor,
            sale.status,
            COALESCE(sale.completed_at_utc, sale.created_at_utc),
            payment.payment_method,
            COALESCE(returned.refund_minor, 0),
            shop.id,
            shop.code,
            shop.name
        FROM sales AS sale
        INNER JOIN shops AS shop
            ON shop.id = sale.shop_id
        INNER JOIN sale_payments AS payment
            ON payment.sale_id = sale.id
        LEFT JOIN
        (
            SELECT sale_id, SUM(refund_amount_minor) AS refund_minor
            FROM sales_returns
            WHERE status = 'completed'
            GROUP BY sale_id
        ) AS returned
            ON returned.sale_id = sale.id
        WHERE sale.id = $saleId
          AND shop.organization_id = $organizationId
          AND shop.id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$saleId", saleId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "sale_not_found_in_active_shop",
                "The sale could not be found in the active shop.");
        }
        return new SaleHeader(
            reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)),
            reader.GetString(7), reader.GetInt64(8), reader.GetString(9),
            reader.GetString(10), reader.GetString(11));
    }

    private static async Task<IReadOnlyList<ReturnableSaleLine>> ReadReturnableLinesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string saleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
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
        LEFT JOIN
        (
            SELECT return_item.sale_item_id, SUM(return_item.refund_minor) AS refund_minor
            FROM sales_return_items AS return_item
            INNER JOIN sales_returns AS header
                ON header.id = return_item.return_id
            WHERE header.status = 'completed'
            GROUP BY return_item.sale_item_id
        ) AS history
            ON history.sale_item_id = item.id
        WHERE item.sale_id = $saleId
        ORDER BY item.id;
        """;
        command.Parameters.AddWithValue("$saleId", saleId);
        var lines = new List<ReturnableSaleLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long sold = reader.GetInt64(4);
            long returned = reader.GetInt64(5);
            long remaining = sold - returned;
            if (remaining <= 0) continue;
            long remainingRefund = Math.Max(0, reader.GetInt64(9) - reader.GetInt64(10));
            lines.Add(new ReturnableSaleLine(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                sold, returned, remaining, reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.GetInt64(8),
                remainingRefund));
        }
        return lines;
    }

    private static async Task InsertHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string returnId,
        ActiveShopContextRecord context,
        SaleHeader sale,
        string shiftId,
        string returnNumber,
        string refundMethod,
        long refundAmount,
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
        INSERT INTO sales_returns
        (
            id, organization_id, shop_id, sale_id, shift_id, return_number,
            original_receipt_number, status, refund_method, refund_amount_minor,
            returned_base_units, restocked_base_units, returned_cost_minor,
            restocked_cost_minor, reason, notes, created_by_user_id,
            approved_by_user_id, completed_at_utc, version
        )
        VALUES
        (
            $id, $organizationId, $shopId, $saleId, $shiftId, $returnNumber,
            $receiptNumber, 'draft', $refundMethod, $refundAmount,
            $returnedBaseUnits, $restockedBaseUnits, $returnedCost,
            $restockedCost, $reason, $notes, $userId, $userId, $now, 1
        );
        """;
        command.Parameters.AddWithValue("$id", returnId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$saleId", sale.SaleId);
        command.Parameters.AddWithValue("$shiftId", shiftId);
        command.Parameters.AddWithValue("$returnNumber", returnNumber);
        command.Parameters.AddWithValue("$receiptNumber", sale.ReceiptNumber);
        command.Parameters.AddWithValue("$refundMethod", refundMethod);
        command.Parameters.AddWithValue("$refundAmount", refundAmount);
        command.Parameters.AddWithValue("$returnedBaseUnits", returnedBaseUnits);
        command.Parameters.AddWithValue("$restockedBaseUnits", restockedBaseUnits);
        command.Parameters.AddWithValue("$returnedCost", returnedCost);
        command.Parameters.AddWithValue("$restockedCost", restockedCost);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLineAsync(
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
        INSERT INTO sales_return_items
        (
            return_id, sale_item_id, product_id, product_name_snapshot,
            sku_snapshot, quantity, sale_unit_snapshot, unit_size_ml_snapshot,
            unit_price_minor, unit_cost_minor, refund_minor, base_units_returned,
            cost_value_minor, disposition, base_units_restocked, restocked_cost_minor
        )
        VALUES
        (
            $returnId, $saleItemId, $productId, $productName, $sku, $quantity,
            $saleUnit, $unitSizeMl, $unitPrice, $unitCost, $refundMinor,
            $baseUnitsReturned, $costValue, $disposition, $baseUnitsRestocked,
            $restockedCost
        );
        """;
        command.Parameters.AddWithValue("$returnId", returnId);
        command.Parameters.AddWithValue("$saleItemId", line.SaleItemId);
        command.Parameters.AddWithValue("$productId", line.ProductId);
        command.Parameters.AddWithValue("$productName", line.ProductName);
        command.Parameters.AddWithValue("$sku", line.Sku);
        command.Parameters.AddWithValue("$quantity", line.Quantity);
        command.Parameters.AddWithValue("$saleUnit", line.SaleUnit);
        command.Parameters.AddWithValue("$unitSizeMl", (object?)line.UnitSizeMl ?? DBNull.Value);
        command.Parameters.AddWithValue("$unitPrice", line.UnitPriceMinor);
        command.Parameters.AddWithValue("$unitCost", line.UnitCostMinor);
        command.Parameters.AddWithValue("$refundMinor", line.RefundMinor);
        command.Parameters.AddWithValue("$baseUnitsReturned", line.BaseUnitsReturned);
        command.Parameters.AddWithValue("$costValue", line.CostValueMinor);
        command.Parameters.AddWithValue("$disposition", line.Disposition);
        command.Parameters.AddWithValue("$baseUnitsRestocked", line.BaseUnitsRestocked);
        command.Parameters.AddWithValue("$restockedCost", line.RestockedCostMinor);
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
                "sale_item_return_conflict",
                $"The returnable quantity for {line.ProductName} changed. Reload and try again.");
        }
    }

    private static async Task RestoreStockAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string shopId,
        string returnId,
        string returnNumber,
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
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
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
                    "stock_changed_during_return",
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
            "sales_return",
            returnId,
            $"{returnNumber}: {reason}",
            userId,
            userId,
            now,
            cancellationToken);
    }

    private static async Task PostAccountingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        SaleHeader sale,
        string returnId,
        string returnNumber,
        string refundMethod,
        long refundAmount,
        long restockedCost,
        string reason,
        string userId,
        DateTimeOffset now,
        string journalDate,
        CancellationToken cancellationToken)
    {
        string revenueAccount = await ResolveSystemAccountAsync(
            connection, transaction, context.OrganizationId, "sales_revenue", cancellationToken);
        string paymentAccount = await ResolveSystemAccountAsync(
            connection, transaction, context.OrganizationId, PaymentAccountKey(refundMethod), cancellationToken);
        string? inventoryAccount = null;
        string? cogsAccount = null;
        if (restockedCost > 0)
        {
            inventoryAccount = await ResolveSystemAccountAsync(
                connection, transaction, context.OrganizationId, "inventory", cancellationToken);
            cogsAccount = await ResolveSystemAccountAsync(
                connection, transaction, context.OrganizationId, "cost_of_goods_sold", cancellationToken);
        }

        string journalId = "sys-sale-return-" + returnId;
        long journalTotal = checked(refundAmount + restockedCost);
        await using (var journal = connection.CreateCommand())
        {
            journal.Transaction = transaction;
            journal.CommandText =
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
                $total, $total, 1, $userId, $now, $now
            );
            """;
            journal.Parameters.AddWithValue("$id", journalId);
            journal.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            journal.Parameters.AddWithValue("$shopId", context.ShopId);
            journal.Parameters.AddWithValue("$journalNumber", "SYS-" + returnNumber);
            journal.Parameters.AddWithValue("$journalDate", journalDate);
            journal.Parameters.AddWithValue("$currencyCode", context.CurrencyCode);
            journal.Parameters.AddWithValue(
                "$description",
                $"Sales return {returnNumber} for receipt {sale.ReceiptNumber}: {reason}");
            journal.Parameters.AddWithValue("$sourceId", "sale_return:" + returnId);
            journal.Parameters.AddWithValue("$total", journalTotal);
            journal.Parameters.AddWithValue("$userId", userId);
            journal.Parameters.AddWithValue("$now", now.ToString("O"));
            await journal.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertJournalLineAsync(
            connection, transaction, journalId, 1, revenueAccount, context.ShopId,
            refundAmount, 0, $"Revenue returned on {returnNumber}", cancellationToken);
        await InsertJournalLineAsync(
            connection, transaction, journalId, 2, paymentAccount, context.ShopId,
            0, refundAmount, $"Customer refund through {refundMethod}", cancellationToken);
        if (restockedCost > 0)
        {
            await InsertJournalLineAsync(
                connection, transaction, journalId, 3, inventoryAccount!, context.ShopId,
                restockedCost, 0, $"Inventory restored by {returnNumber}", cancellationToken);
            await InsertJournalLineAsync(
                connection, transaction, journalId, 4, cogsAccount!, context.ShopId,
                0, restockedCost, $"COGS reversal for {returnNumber}", cancellationToken);
        }

        await using (var post = connection.CreateCommand())
        {
            post.Transaction = transaction;
            post.CommandText =
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
            post.Parameters.AddWithValue("$userId", userId);
            post.Parameters.AddWithValue("$now", now.ToString("O"));
            post.Parameters.AddWithValue("$journalId", journalId);
            if (await post.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("return_journal_post_failed", "The return journal could not be posted.");
            }
        }

        await using var link = connection.CreateCommand();
        link.Transaction = transaction;
        link.CommandText =
        """
        INSERT INTO sales_return_accounting_links
        (return_id, organization_id, shop_id, posting_journal_id, posted_at_utc)
        VALUES ($returnId, $organizationId, $shopId, $journalId, $now);
        """;
        link.Parameters.AddWithValue("$returnId", returnId);
        link.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        link.Parameters.AddWithValue("$shopId", context.ShopId);
        link.Parameters.AddWithValue("$journalId", journalId);
        link.Parameters.AddWithValue("$now", now.ToString("O"));
        await link.ExecuteNonQueryAsync(cancellationToken);
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
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO accounting_journal_lines
        (journal_id, line_number, account_id, shop_id, debit_minor, credit_minor, description)
        VALUES ($journalId, $lineNumber, $accountId, $shopId, $debit, $credit, $description);
        """;
        command.Parameters.AddWithValue("$journalId", journalId);
        command.Parameters.AddWithValue("$lineNumber", lineNumber);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$debit", debit);
        command.Parameters.AddWithValue("$credit", credit);
        command.Parameters.AddWithValue("$description", description);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
            remaining = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken));
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
        update.Parameters.AddWithValue("$status", remaining == 0 ? "returned" : "partially_returned");
        update.Parameters.AddWithValue("$saleId", saleId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("sale_return_status_conflict", "The sale status changed during the return.");
        }
    }

    private static async Task<string> NextReturnNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = transaction;
            ensure.CommandText =
            """
            INSERT OR IGNORE INTO sales_return_sequences(shop_id, next_value, updated_at_utc)
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
            read.CommandText = "SELECT next_value FROM sales_return_sequences WHERE shop_id = $shopId;";
            read.Parameters.AddWithValue("$shopId", context.ShopId);
            value = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken));
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE sales_return_sequences
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
                throw Conflict("return_number_conflict", "The return number changed. Retry the operation.");
            }
        }
        string code = string.IsNullOrWhiteSpace(context.ShopCode) ? "SHOP" : context.ShopCode.ToUpperInvariant();
        return $"RET-{code}-{now:yyyy}-{value:000000}";
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
        string? id = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        if (string.IsNullOrWhiteSpace(id))
        {
            throw Conflict(
                "open_shift_required_for_return",
                "Open a shift at the active shop before processing a customer refund.");
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
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw Conflict(
                "accounting_period_closed",
                "The return date is not inside an open accounting period.");
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
        string? id = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        if (string.IsNullOrWhiteSpace(id))
        {
            throw Conflict(
                "system_account_missing",
                $"The required {systemKey.Replace('_', ' ')} account is missing.");
        }
        return id;
    }

    private static string PaymentAccountKey(string method) => method switch
    {
        "cash" => "cash_on_hand",
        "mobile_money" => "mobile_money_clearing",
        "card" => "card_clearing",
        "bank" => "bank_account",
        _ => throw new InvalidOperationException("Unsupported refund method.")
    };

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
            CASE WHEN TRIM(shop.address) <> '' THEN shop.address ELSE business.address END,
            CASE WHEN TRIM(shop.phone) <> '' THEN shop.phone ELSE business.phone END,
            CASE WHEN TRIM(shop.email) <> '' THEN shop.email ELSE business.email END,
            shop.currency_code
        FROM business_settings AS business
        INNER JOIN shops AS shop ON shop.id = $shopId
        WHERE business.id = 1
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Conflict("business_settings_missing", "The active shop business settings are missing.");
        }
        string businessName = reader.GetString(0);
        string shopName = reader.GetString(1);
        bool headOffice = reader.GetInt32(2) == 1;
        return new BusinessSnapshot(
            headOffice ? businessName : $"{businessName} - {shopName}",
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6));
    }

    private async Task RegisterDocumentsAsync(
        string returnId,
        string userId,
        IReadOnlyList<WrittenSalesReturnDocument> documents,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (WrittenSalesReturnDocument document in documents)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            INSERT INTO sales_return_documents
            (
                id, return_id, document_type, document_number, file_format,
                relative_path, file_sha256, file_size_bytes,
                generated_by_user_id, generated_at_utc
            )
            VALUES
            (
                $id, $returnId, $documentType, $documentNumber, $fileFormat,
                $relativePath, $fileSha256, $fileSizeBytes, $userId, $generatedAt
            );
            """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$returnId", returnId);
            command.Parameters.AddWithValue("$documentType", document.DocumentType);
            command.Parameters.AddWithValue("$documentNumber", document.DocumentNumber);
            command.Parameters.AddWithValue("$fileFormat", document.FileFormat);
            command.Parameters.AddWithValue("$relativePath", document.RelativePath);
            command.Parameters.AddWithValue("$fileSha256", document.FileSha256);
            command.Parameters.AddWithValue("$fileSizeBytes", document.FileSizeBytes);
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$generatedAt", generatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<SalesReturnLineRecord>> ReadReturnItemsAsync(
        SqliteConnection connection,
        string returnId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            id, sale_item_id, product_id, product_name_snapshot, sku_snapshot,
            quantity, sale_unit_snapshot, unit_size_ml_snapshot, unit_price_minor,
            unit_cost_minor, refund_minor, base_units_returned, cost_value_minor,
            disposition, base_units_restocked, restocked_cost_minor
        FROM sales_return_items
        WHERE return_id = $returnId
        ORDER BY id;
        """;
        command.Parameters.AddWithValue("$returnId", returnId);
        var records = new List<SalesReturnLineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new SalesReturnLineRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.GetInt64(8),
                reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11),
                reader.GetInt64(12), reader.GetString(13), reader.GetInt64(14),
                reader.GetInt64(15)));
        }
        return records;
    }

    private static async Task<IReadOnlyList<SalesReturnDocumentRecord>> ReadDocumentsAsync(
        SqliteConnection connection,
        string returnId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id, document_type, document_number, file_format,
               relative_path, file_sha256, file_size_bytes
        FROM sales_return_documents
        WHERE return_id = $returnId
        ORDER BY file_format;
        """;
        command.Parameters.AddWithValue("$returnId", returnId);
        var records = new List<SalesReturnDocumentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new SalesReturnDocumentRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt64(6)));
        }
        return records;
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string returnId,
        object details,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO audit_logs
        (
            occurred_at_utc, user_id, username, event_type, entity_type,
            entity_id, success, details_json, client_ip_hash
        )
        VALUES
        (
            $now, $userId, $username, 'sale.return.completed', 'sales_return',
            $returnId, 1, $detailsJson, NULL
        );
        """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$returnId", returnId);
        command.Parameters.AddWithValue("$detailsJson", JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void RequireAdministrator(AuthenticatedUser user)
    {
        if (!string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            throw new SalesException(
                StatusCodes.Status403Forbidden,
                "administrator_required",
                "Only an administrator can process or review sales returns.");
        }
    }

    private static string NormalizeId(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 100)
        {
            throw Validation("invalid_identifier", "The supplied identifier is invalid.");
        }
        return normalized;
    }

    private static string NormalizeRefundMethod(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RefundMethods.Contains(normalized))
        {
            throw Validation(
                "invalid_refund_method",
                "Use cash, mobile money, card or bank for the refund.");
        }
        return normalized;
    }

    private static string NormalizeDisposition(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!Dispositions.Contains(normalized))
        {
            throw Validation(
                "invalid_return_disposition",
                "Return disposition must be restock or damaged.");
        }
        return normalized;
    }

    private static string RequiredText(
        string? value,
        int maximumLength,
        string errorCode,
        string message)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0) throw Validation(errorCode, message);
        if (normalized.Length > maximumLength)
        {
            throw Validation("text_too_long", $"Text cannot exceed {maximumLength} characters.");
        }
        return normalized;
    }

    private static string OptionalText(string? value, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximumLength)
        {
            throw Validation("text_too_long", $"Text cannot exceed {maximumLength} characters.");
        }
        return normalized;
    }

    private static SalesException Validation(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);
    private static SalesException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);
    private static SalesException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private sealed record NormalizedRequestLine(long SaleItemId, long Quantity, string Disposition);
    private sealed record CalculatedReturnLine(
        long SaleItemId, string ProductId, string ProductName, string Sku,
        long SoldQuantity, long PreviouslyReturnedQuantity, long Quantity,
        string SaleUnit, int? UnitSizeMl, long UnitPriceMinor, long UnitCostMinor,
        long RefundMinor, long BaseUnitsReturned, long CostValueMinor,
        string Disposition, long BaseUnitsRestocked, long RestockedCostMinor);
    private sealed record SaleHeader(
        string SaleId, string ReceiptNumber, string? InvoiceNumber, string CustomerName,
        long TotalMinor, string Status, DateTimeOffset CompletedAtUtc, string PaymentMethod,
        long ReturnedAmountMinor, string ShopId, string ShopCode, string ShopName);
    private sealed record SalesReturnHeader(
        string Id, string ReturnNumber, string SaleId, string OriginalReceiptNumber,
        string Status, string RefundMethod, long RefundAmountMinor,
        long ReturnedBaseUnits, long RestockedBaseUnits, long ReturnedCostMinor,
        long RestockedCostMinor, string Reason, string Notes,
        string CreatedByDisplayName, string ApprovedByDisplayName,
        DateTimeOffset CompletedAtUtc, string ShopId, string ShopCode, string ShopName);
    private sealed record BusinessSnapshot(
        string BusinessName, string Address, string Phone, string Email, string CurrencyCode);
}
