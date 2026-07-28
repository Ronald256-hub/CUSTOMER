using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Inventory;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Procurement;

public sealed partial class ProcurementService
{
    public async Task<IReadOnlyList<SupplierReturnRecord>> ListSupplierReturnsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(requestedLimit, 1, 1000);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireProcurementAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            return_header.id,
            return_header.supplier_return_number,
            return_header.purchase_order_id,
            order_header.purchase_order_number,
            return_header.goods_receipt_id,
            receipt.goods_receipt_number,
            return_header.supplier_id,
            supplier.name,
            return_header.status,
            return_header.total_minor,
            return_header.reason,
            user.display_name,
            return_header.returned_at_utc,
            return_header.credit_journal_id
        FROM procurement_supplier_returns AS return_header
        INNER JOIN procurement_purchase_orders AS order_header
            ON order_header.id = return_header.purchase_order_id
        INNER JOIN procurement_goods_receipts AS receipt
            ON receipt.id = return_header.goods_receipt_id
        INNER JOIN suppliers AS supplier ON supplier.id = return_header.supplier_id
        INNER JOIN users AS user ON user.id = return_header.returned_by_user_id
        WHERE return_header.organization_id = $organizationId
          AND return_header.shop_id = $shopId
        ORDER BY return_header.returned_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<SupplierReturnRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadSupplierReturn(
                reader,
                Array.Empty<SupplierReturnLineRecord>()));
        }
        return records;
    }

    public async Task<SupplierReturnRecord> GetSupplierReturnAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string supplierReturnId,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(supplierReturnId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireProcurementAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);
        return await ReadSupplierReturnAsync(
            connection,
            transaction: null,
            context,
            id,
            cancellationToken);
    }

    public async Task<SupplierReturnRecord> CreateSupplierReturnAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string goodsReceiptId,
        CreateSupplierReturnRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user, "post a supplier return");
        string receiptId = NormalizeId(goodsReceiptId);
        string reason = RequiredText(
            request.Reason,
            500,
            "supplier_return_reason_required",
            "Enter the reason for returning goods to the supplier.");
        if (reason.Length < 5)
        {
            throw Validation(
                "supplier_return_reason_too_short",
                "The supplier return reason must contain at least five characters.");
        }
        IReadOnlyList<NormalizedReturnLine> requestedLines =
            NormalizeReturnLines(request.Items);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireProcurementAccessAsync(
            connection,
            transaction,
            user,
            context.ShopId,
            cancellationToken);

        ReturnReceiptHeader receipt = await RequireReturnReceiptHeaderAsync(
            connection,
            transaction,
            context,
            receiptId,
            cancellationToken);
        if (receipt.Status != "posted")
        {
            throw Conflict(
                "goods_receipt_not_posted",
                "Only a posted goods receipt can be returned to the supplier.");
        }

        IReadOnlyDictionary<string, ReturnReceiptLine> receiptLines =
            await ReadReturnReceiptLinesAsync(
                connection,
                transaction,
                receiptId,
                cancellationToken);
        var prepared = new List<PreparedReturnLine>();
        long total = 0;
        foreach (NormalizedReturnLine requested in requestedLines)
        {
            if (!receiptLines.TryGetValue(requested.GoodsReceiptLineId, out ReturnReceiptLine? line))
            {
                throw NotFound(
                    "goods_receipt_line_not_found",
                    "A selected goods receipt line could not be found.");
            }
            long remaining = line.QuantityBaseUnits - line.ReturnedQuantityBaseUnits;
            if (requested.QuantityBaseUnits > remaining)
            {
                throw Conflict(
                    "supplier_return_quantity_exceeds_receipt",
                    $"The return quantity for {line.ProductName} exceeds the unreturned receipt quantity.");
            }
            if (requested.QuantityBaseUnits > line.BatchAvailableQuantityBaseUnits)
            {
                throw Conflict(
                    "supplier_return_quantity_exceeds_batch",
                    $"The batch for {line.ProductName} does not contain enough available stock for this return.");
            }

            await EnsureShopBalanceAsync(
                connection,
                transaction,
                context.ShopId,
                line.ProductId,
                DateTimeOffset.UtcNow,
                cancellationToken);
            BalanceSnapshot balance = await ReadBalanceAsync(
                connection,
                transaction,
                context.ShopId,
                line.ProductId,
                cancellationToken);
            if (balance.QuantityBaseUnits - balance.ReservedBaseUnits < requested.QuantityBaseUnits)
            {
                throw Conflict(
                    "supplier_return_stock_reserved",
                    $"Available stock for {line.ProductName} is lower than the requested supplier return.");
            }

            long lineTotal = checked(requested.QuantityBaseUnits * line.EffectiveUnitCostMinor);
            if (lineTotal <= 0)
            {
                throw Conflict(
                    "supplier_return_value_invalid",
                    $"The return value for {line.ProductName} is invalid.");
            }
            total = checked(total + lineTotal);
            prepared.Add(new PreparedReturnLine(requested, line, balance, lineTotal));
        }

        string journalDate = DateOnly.FromDateTime(DateTime.UtcNow)
            .ToString("yyyy-MM-dd");
        await EnsureOpenPeriodAsync(
            connection,
            transaction,
            context.OrganizationId,
            journalDate,
            cancellationToken);
        string payableAccountId = await ResolveSystemAccountAsync(
            connection,
            transaction,
            context.OrganizationId,
            "accounts_payable",
            cancellationToken);
        string inventoryAccountId = await ResolveSystemAccountAsync(
            connection,
            transaction,
            context.OrganizationId,
            "inventory",
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string returnId = Guid.NewGuid().ToString("N");
        string returnNumber = await NextDocumentNumberAsync(
            connection,
            transaction,
            context,
            "supplier_return",
            now,
            cancellationToken);
        string journalId = Guid.NewGuid().ToString("N");
        string journalNumber = await NextAccountingJournalNumberAsync(
            connection,
            transaction,
            context,
            now,
            cancellationToken);

        await InsertSupplierReturnJournalAsync(
            connection,
            transaction,
            journalId,
            journalNumber,
            journalDate,
            returnId,
            returnNumber,
            receipt.SupplierId,
            total,
            payableAccountId,
            inventoryAccountId,
            context,
            user.Id,
            now,
            cancellationToken);
        await InsertSupplierReturnHeaderAsync(
            connection,
            transaction,
            returnId,
            returnNumber,
            receipt,
            total,
            reason,
            journalId,
            user.Id,
            now,
            cancellationToken);

        foreach (PreparedReturnLine line in prepared)
        {
            string lineId = Guid.NewGuid().ToString("N");
            await InsertSupplierReturnLineAsync(
                connection,
                transaction,
                lineId,
                returnId,
                line,
                cancellationToken);

            long newBalance = checked(
                line.Balance.QuantityBaseUnits - line.Request.QuantityBaseUnits);
            await UpdateShopBalanceAsync(
                connection,
                transaction,
                context.ShopId,
                line.ReceiptLine.ProductId,
                newBalance,
                line.Balance.Version,
                now,
                cancellationToken);
            await UpdateLegacyBalanceAsync(
                connection,
                transaction,
                line.ReceiptLine.ProductId,
                -line.Request.QuantityBaseUnits,
                now,
                cancellationToken);
            await ShopInventoryService.InsertMovementAsync(
                connection,
                transaction,
                context.ShopId,
                line.ReceiptLine.ProductId,
                "adjustment",
                -line.Request.QuantityBaseUnits,
                newBalance,
                line.LineTotalMinor,
                "supplier_return",
                returnId,
                $"Supplier return {returnNumber}: {reason}",
                user.Id,
                user.Id,
                now,
                cancellationToken);

            await UpdateBatchAfterReturnAsync(
                connection,
                transaction,
                line.ReceiptLine.BatchId,
                line.Request.QuantityBaseUnits,
                now,
                cancellationToken);
            await UpdateReceiptLineAfterReturnAsync(
                connection,
                transaction,
                line.ReceiptLine.Id,
                line.Request.QuantityBaseUnits,
                cancellationToken);
            await UpdateOrderLineAfterReturnAsync(
                connection,
                transaction,
                line.ReceiptLine.PurchaseOrderLineId,
                line.Request.QuantityBaseUnits,
                cancellationToken);
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "procurement.supplier_return.posted",
            "supplier_return",
            returnId,
            new
            {
                returnNumber,
                receipt.GoodsReceiptNumber,
                receipt.PurchaseOrderNumber,
                receipt.SupplierId,
                totalMinor = total,
                itemCount = prepared.Count,
                reason,
                journalId,
                journalNumber
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetSupplierReturnAsync(
            user,
            context,
            returnId,
            cancellationToken);
    }

    private static IReadOnlyList<NormalizedReturnLine> NormalizeReturnLines(
        IReadOnlyList<SupplierReturnLineRequest>? items)
    {
        if (items is null || items.Count == 0)
        {
            throw Validation(
                "supplier_return_items_required",
                "Return at least one goods receipt line.");
        }
        if (items.Count > 250)
        {
            throw Validation(
                "too_many_supplier_return_items",
                "A supplier return cannot contain more than 250 lines.");
        }
        var normalized = items
            .GroupBy(item => NormalizeId(item.GoodsReceiptLineId), StringComparer.Ordinal)
            .Select(group => new NormalizedReturnLine(
                group.Key,
                checked(group.Sum(item => item.QuantityBaseUnits))))
            .ToList();
        if (normalized.Any(item => item.QuantityBaseUnits <= 0))
        {
            throw Validation(
                "invalid_supplier_return_item",
                "Every supplier return line requires a positive quantity.");
        }
        return normalized;
    }

    private static async Task<ReturnReceiptHeader> RequireReturnReceiptHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string receiptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            receipt.id,
            receipt.goods_receipt_number,
            receipt.purchase_order_id,
            order_header.purchase_order_number,
            order_header.supplier_id,
            supplier.name,
            receipt.status,
            receipt.organization_id,
            receipt.shop_id
        FROM procurement_goods_receipts AS receipt
        INNER JOIN procurement_purchase_orders AS order_header
            ON order_header.id = receipt.purchase_order_id
        INNER JOIN suppliers AS supplier ON supplier.id = order_header.supplier_id
        WHERE receipt.id = $id
          AND receipt.organization_id = $organizationId
          AND receipt.shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", receiptId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "goods_receipt_not_found",
                "The goods receipt could not be found in the active branch.");
        }
        return new ReturnReceiptHeader(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8));
    }

    private static async Task<IReadOnlyDictionary<string, ReturnReceiptLine>>
        ReadReturnReceiptLinesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string receiptId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            line.id,
            line.purchase_order_line_id,
            line.product_id,
            line.product_name_snapshot,
            line.sku_snapshot,
            line.quantity_base_units,
            line.returned_quantity_base,
            line.effective_unit_cost_minor,
            batch.id,
            batch.available_quantity_base
        FROM procurement_goods_receipt_lines AS line
        INNER JOIN inventory_batches AS batch
            ON batch.goods_receipt_line_id = line.id
        WHERE line.goods_receipt_id = $receiptId;
        """;
        command.Parameters.AddWithValue("$receiptId", receiptId);

        var records = new Dictionary<string, ReturnReceiptLine>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var line = new ReturnReceiptLine(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt64(5),
                reader.GetInt64(6), reader.GetInt64(7), reader.GetString(8),
                reader.GetInt64(9));
            records.Add(line.Id, line);
        }
        return records;
    }

    private static async Task InsertSupplierReturnJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string journalId,
        string journalNumber,
        string journalDate,
        string returnId,
        string returnNumber,
        string supplierId,
        long total,
        string payableAccountId,
        string inventoryAccountId,
        ActiveShopContextRecord context,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText =
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
            header.Parameters.AddWithValue("$id", journalId);
            header.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            header.Parameters.AddWithValue("$shopId", context.ShopId);
            header.Parameters.AddWithValue("$journalNumber", journalNumber);
            header.Parameters.AddWithValue("$journalDate", journalDate);
            header.Parameters.AddWithValue("$currencyCode", context.CurrencyCode);
            header.Parameters.AddWithValue("$description", $"Supplier return {returnNumber}");
            header.Parameters.AddWithValue("$sourceId", $"supplier_return:{returnId}");
            header.Parameters.AddWithValue("$total", total);
            header.Parameters.AddWithValue("$userId", userId);
            header.Parameters.AddWithValue("$now", now.ToString("O"));
            await header.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var lines = connection.CreateCommand())
        {
            lines.Transaction = transaction;
            lines.CommandText =
            """
            INSERT INTO accounting_journal_lines
            (
                journal_id, line_number, account_id, shop_id,
                debit_minor, credit_minor, description,
                counterparty_type, counterparty_id
            )
            VALUES
            ($journalId, 1, $payableAccountId, $shopId, $total, 0,
             $payableDescription, 'supplier', $supplierId),
            ($journalId, 2, $inventoryAccountId, $shopId, 0, $total,
             $inventoryDescription, 'supplier', $supplierId);
            """;
            lines.Parameters.AddWithValue("$journalId", journalId);
            lines.Parameters.AddWithValue("$payableAccountId", payableAccountId);
            lines.Parameters.AddWithValue("$inventoryAccountId", inventoryAccountId);
            lines.Parameters.AddWithValue("$shopId", context.ShopId);
            lines.Parameters.AddWithValue("$total", total);
            lines.Parameters.AddWithValue("$payableDescription", $"Supplier credit for {returnNumber}");
            lines.Parameters.AddWithValue("$inventoryDescription", $"Inventory returned on {returnNumber}");
            lines.Parameters.AddWithValue("$supplierId", supplierId);
            await lines.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var post = connection.CreateCommand();
        post.Transaction = transaction;
        post.CommandText =
        """
        UPDATE accounting_journals
        SET status = 'posted',
            posted_by_user_id = $userId,
            posted_at_utc = $now,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $id
          AND status = 'draft';
        """;
        post.Parameters.AddWithValue("$userId", userId);
        post.Parameters.AddWithValue("$now", now.ToString("O"));
        post.Parameters.AddWithValue("$id", journalId);
        if (await post.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "supplier_return_journal_failed",
                "The supplier return accounting journal could not be posted.");
        }
    }

    private static async Task InsertSupplierReturnHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string returnId,
        string returnNumber,
        ReturnReceiptHeader receipt,
        long total,
        string reason,
        string journalId,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO procurement_supplier_returns
        (
            id, organization_id, shop_id, supplier_return_number,
            purchase_order_id, goods_receipt_id, supplier_id,
            status, total_minor, reason, credit_journal_id,
            returned_by_user_id, returned_at_utc, version
        )
        VALUES
        (
            $id, $organizationId, $shopId, $number,
            $orderId, $receiptId, $supplierId,
            'posted', $total, $reason, $journalId,
            $userId, $now, 1
        );
        """;
        command.Parameters.AddWithValue("$id", returnId);
        command.Parameters.AddWithValue("$organizationId", receipt.OrganizationId);
        command.Parameters.AddWithValue("$shopId", receipt.ShopId);
        command.Parameters.AddWithValue("$number", returnNumber);
        command.Parameters.AddWithValue("$orderId", receipt.PurchaseOrderId);
        command.Parameters.AddWithValue("$receiptId", receipt.Id);
        command.Parameters.AddWithValue("$supplierId", receipt.SupplierId);
        command.Parameters.AddWithValue("$total", total);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$journalId", journalId);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSupplierReturnLineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string lineId,
        string returnId,
        PreparedReturnLine prepared,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO procurement_supplier_return_lines
        (
            id, supplier_return_id, goods_receipt_line_id, product_id,
            product_name_snapshot, sku_snapshot, quantity_base_units,
            unit_cost_minor, line_total_minor, batch_id
        )
        VALUES
        (
            $id, $returnId, $receiptLineId, $productId,
            $productName, $sku, $quantity,
            $unitCost, $lineTotal, $batchId
        );
        """;
        command.Parameters.AddWithValue("$id", lineId);
        command.Parameters.AddWithValue("$returnId", returnId);
        command.Parameters.AddWithValue("$receiptLineId", prepared.ReceiptLine.Id);
        command.Parameters.AddWithValue("$productId", prepared.ReceiptLine.ProductId);
        command.Parameters.AddWithValue("$productName", prepared.ReceiptLine.ProductName);
        command.Parameters.AddWithValue("$sku", prepared.ReceiptLine.Sku);
        command.Parameters.AddWithValue("$quantity", prepared.Request.QuantityBaseUnits);
        command.Parameters.AddWithValue("$unitCost", prepared.ReceiptLine.EffectiveUnitCostMinor);
        command.Parameters.AddWithValue("$lineTotal", prepared.LineTotalMinor);
        command.Parameters.AddWithValue("$batchId", prepared.ReceiptLine.BatchId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateBatchAfterReturnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        long quantity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE inventory_batches
        SET available_quantity_base = available_quantity_base - $quantity,
            status = CASE
                WHEN available_quantity_base - $quantity = 0 THEN 'depleted'
                ELSE status END,
            version = version + 1,
            updated_at_utc = $now
        WHERE id = $id
          AND available_quantity_base >= $quantity;
        """;
        command.Parameters.AddWithValue("$quantity", quantity);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", batchId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "inventory_batch_changed",
                "The inventory batch changed. Reload and try again.");
        }
    }

    private static async Task UpdateReceiptLineAfterReturnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string receiptLineId,
        long quantity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE procurement_goods_receipt_lines
        SET returned_quantity_base = returned_quantity_base + $quantity
        WHERE id = $id
          AND returned_quantity_base + $quantity <= quantity_base_units;
        """;
        command.Parameters.AddWithValue("$quantity", quantity);
        command.Parameters.AddWithValue("$id", receiptLineId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "goods_receipt_line_changed",
                "The goods receipt line changed. Reload and try again.");
        }
    }

    private static async Task UpdateOrderLineAfterReturnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string orderLineId,
        long quantity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE procurement_purchase_order_lines
        SET returned_quantity_base = returned_quantity_base + $quantity
        WHERE id = $id
          AND returned_quantity_base + $quantity <= received_quantity_base;
        """;
        command.Parameters.AddWithValue("$quantity", quantity);
        command.Parameters.AddWithValue("$id", orderLineId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "purchase_order_line_changed",
                "The purchase order line changed. Reload and try again.");
        }
    }

    private static async Task<SupplierReturnRecord> ReadSupplierReturnAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ActiveShopContextRecord context,
        string returnId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            return_header.id,
            return_header.supplier_return_number,
            return_header.purchase_order_id,
            order_header.purchase_order_number,
            return_header.goods_receipt_id,
            receipt.goods_receipt_number,
            return_header.supplier_id,
            supplier.name,
            return_header.status,
            return_header.total_minor,
            return_header.reason,
            user.display_name,
            return_header.returned_at_utc,
            return_header.credit_journal_id
        FROM procurement_supplier_returns AS return_header
        INNER JOIN procurement_purchase_orders AS order_header
            ON order_header.id = return_header.purchase_order_id
        INNER JOIN procurement_goods_receipts AS receipt
            ON receipt.id = return_header.goods_receipt_id
        INNER JOIN suppliers AS supplier ON supplier.id = return_header.supplier_id
        INNER JOIN users AS user ON user.id = return_header.returned_by_user_id
        WHERE return_header.id = $id
          AND return_header.organization_id = $organizationId
          AND return_header.shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", returnId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "supplier_return_not_found",
                "The supplier return could not be found in the active branch.");
        }
        object[] values = new object[reader.FieldCount];
        reader.GetValues(values);
        await reader.DisposeAsync();

        IReadOnlyList<SupplierReturnLineRecord> lines =
            await ReadSupplierReturnLinesAsync(
                connection,
                transaction,
                returnId,
                cancellationToken);
        return ReadSupplierReturn(values, lines);
    }

    private static SupplierReturnRecord ReadSupplierReturn(
        SqliteDataReader reader,
        IReadOnlyList<SupplierReturnLineRecord> lines) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8),
            reader.GetInt64(9), reader.GetString(10), reader.GetString(11),
            DateTimeOffset.Parse(reader.GetString(12)), reader.GetString(13), lines);

    private static SupplierReturnRecord ReadSupplierReturn(
        object[] values,
        IReadOnlyList<SupplierReturnLineRecord> lines) =>
        new(
            Convert.ToString(values[0])!, Convert.ToString(values[1])!,
            Convert.ToString(values[2])!, Convert.ToString(values[3])!,
            Convert.ToString(values[4])!, Convert.ToString(values[5])!,
            Convert.ToString(values[6])!, Convert.ToString(values[7])!,
            Convert.ToString(values[8])!, Convert.ToInt64(values[9]),
            Convert.ToString(values[10])!, Convert.ToString(values[11])!,
            DateTimeOffset.Parse(Convert.ToString(values[12])!),
            Convert.ToString(values[13])!, lines);

    private static async Task<IReadOnlyList<SupplierReturnLineRecord>>
        ReadSupplierReturnLinesAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string returnId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            id, goods_receipt_line_id, product_id,
            product_name_snapshot, sku_snapshot,
            quantity_base_units, unit_cost_minor,
            line_total_minor, batch_id
        FROM procurement_supplier_return_lines
        WHERE supplier_return_id = $returnId
        ORDER BY product_name_snapshot COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$returnId", returnId);
        var lines = new List<SupplierReturnLineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new SupplierReturnLineRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt64(5),
                reader.GetInt64(6), reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return lines;
    }

    private sealed record NormalizedReturnLine(
        string GoodsReceiptLineId,
        long QuantityBaseUnits);

    private sealed record ReturnReceiptHeader(
        string Id,
        string GoodsReceiptNumber,
        string PurchaseOrderId,
        string PurchaseOrderNumber,
        string SupplierId,
        string SupplierName,
        string Status,
        string OrganizationId,
        string ShopId);

    private sealed record ReturnReceiptLine(
        string Id,
        string PurchaseOrderLineId,
        string ProductId,
        string ProductName,
        string Sku,
        long QuantityBaseUnits,
        long ReturnedQuantityBaseUnits,
        long EffectiveUnitCostMinor,
        string BatchId,
        long BatchAvailableQuantityBaseUnits);

    private sealed record PreparedReturnLine(
        NormalizedReturnLine Request,
        ReturnReceiptLine ReceiptLine,
        BalanceSnapshot Balance,
        long LineTotalMinor);
}
