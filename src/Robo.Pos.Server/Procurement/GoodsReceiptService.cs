using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Inventory;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Procurement;

public sealed partial class ProcurementService
{
    public async Task<IReadOnlyList<GoodsReceiptRecord>> ListGoodsReceiptsAsync(
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
            receipt.id,
            receipt.goods_receipt_number,
            receipt.purchase_order_id,
            order_header.purchase_order_number,
            receipt.purchase_id,
            purchase.purchase_number,
            order_header.supplier_id,
            supplier.name,
            receipt.supplier_invoice_number,
            receipt.status,
            receipt.subtotal_minor,
            receipt.landed_cost_minor,
            receipt.total_minor,
            receipt.notes,
            user.display_name,
            receipt.received_at_utc
        FROM procurement_goods_receipts AS receipt
        INNER JOIN procurement_purchase_orders AS order_header
            ON order_header.id = receipt.purchase_order_id
        INNER JOIN purchases AS purchase ON purchase.id = receipt.purchase_id
        INNER JOIN suppliers AS supplier ON supplier.id = order_header.supplier_id
        INNER JOIN users AS user ON user.id = receipt.received_by_user_id
        WHERE receipt.organization_id = $organizationId
          AND receipt.shop_id = $shopId
        ORDER BY receipt.received_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$limit", limit);

        var receipts = new List<GoodsReceiptRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            receipts.Add(ReadGoodsReceipt(reader, Array.Empty<GoodsReceiptLineRecord>()));
        }
        return receipts;
    }

    public async Task<GoodsReceiptRecord> GetGoodsReceiptAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string goodsReceiptId,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(goodsReceiptId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireProcurementAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);
        return await ReadGoodsReceiptAsync(
            connection,
            transaction: null,
            context,
            id,
            cancellationToken);
    }

    public async Task<GoodsReceiptRecord> ReceiveGoodsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string purchaseOrderId,
        ReceiveGoodsRequest request,
        CancellationToken cancellationToken = default)
    {
        string orderId = NormalizeId(purchaseOrderId);
        string supplierInvoiceNumber = OptionalText(
            request.SupplierInvoiceNumber,
            100);
        string notes = OptionalText(request.Notes, 1000);
        IReadOnlyList<NormalizedReceiptLine> requestedLines =
            NormalizeReceiptLines(request.Items);

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

        PurchaseOrderHeader order = await RequirePurchaseOrderHeaderAsync(
            connection,
            transaction,
            context,
            orderId,
            cancellationToken);
        if (order.Status is not ("approved" or "partially_received"))
        {
            throw Conflict(
                "purchase_order_not_receivable",
                "Only an approved or partially received purchase order can receive goods.");
        }

        IReadOnlyDictionary<string, PurchaseOrderLineState> orderLines =
            await ReadOrderLineStatesAsync(
                connection,
                transaction,
                orderId,
                cancellationToken);
        var prepared = new List<PreparedReceiptLine>();
        long subtotal = 0;
        long landedCost = 0;
        foreach (NormalizedReceiptLine requested in requestedLines)
        {
            if (!orderLines.TryGetValue(requested.PurchaseOrderLineId, out PurchaseOrderLineState? line))
            {
                throw NotFound(
                    "purchase_order_line_not_found",
                    "A selected purchase order line could not be found.");
            }
            long outstanding = line.OrderedQuantityBaseUnits - line.ReceivedQuantityBaseUnits;
            if (requested.QuantityBaseUnits > outstanding)
            {
                throw Conflict(
                    "receipt_quantity_exceeds_order",
                    $"The receipt quantity for {line.ProductName} exceeds the outstanding purchase order quantity.");
            }
            if (line.TrackExpiry &&
                (string.IsNullOrWhiteSpace(requested.BatchNumber) || requested.ExpiryDate is null))
            {
                throw Validation(
                    "batch_and_expiry_required",
                    $"{line.ProductName} requires a batch number and expiry date.");
            }

            long baseValue = checked(requested.QuantityBaseUnits * line.UnitCostMinor);
            long lineTotal = checked(baseValue + requested.LandedCostMinor);
            if (lineTotal % requested.QuantityBaseUnits != 0)
            {
                throw Validation(
                    "landed_cost_allocation_not_divisible",
                    $"The landed cost allocated to {line.ProductName} must produce an exact cost per base unit.");
            }
            long effectiveUnitCost = lineTotal / requested.QuantityBaseUnits;
            subtotal = checked(subtotal + baseValue);
            landedCost = checked(landedCost + requested.LandedCostMinor);
            prepared.Add(new PreparedReceiptLine(
                requested,
                line,
                baseValue,
                lineTotal,
                effectiveUnitCost));
        }
        long total = checked(subtotal + landedCost);
        if (total <= 0)
        {
            throw Validation(
                "goods_receipt_total_invalid",
                "The goods receipt total must be greater than zero.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string receiptId = Guid.NewGuid().ToString("N");
        string receiptNumber = await NextDocumentNumberAsync(
            connection,
            transaction,
            context,
            "goods_receipt",
            now,
            cancellationToken);
        string purchaseId = Guid.NewGuid().ToString("N");
        string purchaseNumber = $"PUR-{receiptNumber}";

        await InsertLegacyPurchaseHeaderAsync(
            connection,
            transaction,
            purchaseId,
            purchaseNumber,
            order,
            supplierInvoiceNumber,
            notes,
            user.Id,
            now,
            cancellationToken);
        await InsertGoodsReceiptHeaderAsync(
            connection,
            transaction,
            receiptId,
            receiptNumber,
            purchaseId,
            order,
            supplierInvoiceNumber,
            subtotal,
            landedCost,
            total,
            notes,
            user.Id,
            now,
            cancellationToken);

        int purchaseLineNumber = 1;
        foreach (PreparedReceiptLine preparedLine in prepared)
        {
            string receiptLineId = Guid.NewGuid().ToString("N");
            string batchNumber = string.IsNullOrWhiteSpace(preparedLine.Request.BatchNumber)
                ? $"UNBATCHED-{receiptNumber}-{purchaseLineNumber:000}"
                : OptionalText(preparedLine.Request.BatchNumber, 100);

            await InsertLegacyPurchaseLineAsync(
                connection,
                transaction,
                purchaseId,
                preparedLine,
                batchNumber,
                cancellationToken);
            await InsertGoodsReceiptLineAsync(
                connection,
                transaction,
                receiptLineId,
                receiptId,
                preparedLine,
                batchNumber,
                cancellationToken);

            await EnsureShopBalanceAsync(
                connection,
                transaction,
                context.ShopId,
                preparedLine.OrderLine.ProductId,
                now,
                cancellationToken);
            BalanceSnapshot balance = await ReadBalanceAsync(
                connection,
                transaction,
                context.ShopId,
                preparedLine.OrderLine.ProductId,
                cancellationToken);
            long newBalance = checked(
                balance.QuantityBaseUnits + preparedLine.Request.QuantityBaseUnits);

            await UpdateShopBalanceAsync(
                connection,
                transaction,
                context.ShopId,
                preparedLine.OrderLine.ProductId,
                newBalance,
                balance.Version,
                now,
                cancellationToken);
            await UpdateLegacyBalanceAsync(
                connection,
                transaction,
                preparedLine.OrderLine.ProductId,
                preparedLine.Request.QuantityBaseUnits,
                now,
                cancellationToken);
            await ShopInventoryService.InsertMovementAsync(
                connection,
                transaction,
                context.ShopId,
                preparedLine.OrderLine.ProductId,
                "purchase",
                preparedLine.Request.QuantityBaseUnits,
                newBalance,
                preparedLine.LineTotalMinor,
                "goods_receipt",
                receiptId,
                $"Goods receipt {receiptNumber}",
                user.Id,
                order.ApprovedBy is null ? null : user.Id,
                now,
                cancellationToken);

            string batchId = Guid.NewGuid().ToString("N");
            await InsertInventoryBatchAsync(
                connection,
                transaction,
                batchId,
                context,
                receiptLineId,
                preparedLine,
                batchNumber,
                now,
                cancellationToken);
            await UpdateOrderLineReceivedAsync(
                connection,
                transaction,
                preparedLine.OrderLine.Id,
                preparedLine.Request.QuantityBaseUnits,
                cancellationToken);
            purchaseLineNumber++;
        }

        await UpdateLegacyPurchaseTotalsAsync(
            connection,
            transaction,
            purchaseId,
            subtotal,
            total,
            now,
            cancellationToken);
        await UpdatePurchaseOrderAfterReceiptAsync(
            connection,
            transaction,
            orderId,
            landedCost,
            now,
            cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "procurement.goods_receipt.posted",
            "goods_receipt",
            receiptId,
            new
            {
                receiptNumber,
                order.Number,
                purchaseNumber,
                supplierInvoiceNumber,
                subtotalMinor = subtotal,
                landedCostMinor = landedCost,
                totalMinor = total,
                itemCount = prepared.Count
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetGoodsReceiptAsync(
            user,
            context,
            receiptId,
            cancellationToken);
    }

    private static IReadOnlyList<NormalizedReceiptLine> NormalizeReceiptLines(
    IReadOnlyList<GoodsReceiptLineRequest>? items)
{
    if (items is null || items.Count == 0)
    {
        throw Validation(
            "goods_receipt_items_required",
            "Receive at least one purchase order line.");
    }
    if (items.Count > 250)
    {
        throw Validation(
            "too_many_goods_receipt_items",
            "A goods receipt cannot contain more than 250 lines.");
    }

    var normalized = new List<NormalizedReceiptLine>();
    foreach (IGrouping<string, GoodsReceiptLineRequest> group in
             items.GroupBy(item => NormalizeId(item.PurchaseOrderLineId), StringComparer.Ordinal))
    {
        List<string> batches = group
            .Select(item => item.BatchNumber?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        List<string> expiries = group
            .Select(item => item.ExpiryDate?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (batches.Count != 1 || expiries.Count != 1)
        {
            throw Validation(
                "mixed_batch_for_order_line",
                "Use one batch number and expiry date per purchase order line in a goods receipt.");
        }

        long quantity = checked(group.Sum(item => item.QuantityBaseUnits));
        long landedCost = checked(group.Sum(item => item.LandedCostMinor));
        if (quantity <= 0 || landedCost < 0)
        {
            throw Validation(
                "invalid_goods_receipt_item",
                "Every goods receipt line requires positive quantity and non-negative landed cost.");
        }
        normalized.Add(new NormalizedReceiptLine(
            group.Key,
            quantity,
            landedCost,
            batches[0],
            NormalizeOptionalDate(expiries[0], "invalid_expiry_date")));
    }
    return normalized;
}

private static async Task<IReadOnlyDictionary<string, PurchaseOrderLineState>>
        ReadOrderLineStatesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string orderId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            line.id,
            line.line_number,
            line.product_id,
            line.product_name_snapshot,
            line.sku_snapshot,
            line.ordered_quantity_base,
            line.received_quantity_base,
            line.returned_quantity_base,
            line.unit_cost_minor,
            product.track_expiry
        FROM procurement_purchase_order_lines AS line
        INNER JOIN products AS product ON product.id = line.product_id
        WHERE line.purchase_order_id = $orderId;
        """;
        command.Parameters.AddWithValue("$orderId", orderId);

        var lines = new Dictionary<string, PurchaseOrderLineState>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var line = new PurchaseOrderLineState(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt32(9) == 1);
            lines.Add(line.Id, line);
        }
        return lines;
    }

    private static async Task InsertLegacyPurchaseHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string purchaseId,
        string purchaseNumber,
        PurchaseOrderHeader order,
        string supplierInvoiceNumber,
        string notes,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO purchases
        (
            id, shop_id, purchase_number, supplier_id, supplier_invoice_number,
            status, subtotal_minor, total_minor, notes,
            received_by_user_id, received_at_utc, created_at_utc, updated_at_utc
        )
        VALUES
        (
            $id, $shopId, $purchaseNumber, $supplierId, $invoiceNumber,
            'received', 0, 0, $notes,
            $userId, $now, $now, $now
        );
        """;
        command.Parameters.AddWithValue("$id", purchaseId);
        command.Parameters.AddWithValue("$shopId", order.ShopId);
        command.Parameters.AddWithValue("$purchaseNumber", purchaseNumber);
        command.Parameters.AddWithValue("$supplierId", order.SupplierId);
        command.Parameters.AddWithValue("$invoiceNumber", supplierInvoiceNumber);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertGoodsReceiptHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string receiptId,
        string receiptNumber,
        string purchaseId,
        PurchaseOrderHeader order,
        string supplierInvoiceNumber,
        long subtotal,
        long landedCost,
        long total,
        string notes,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO procurement_goods_receipts
        (
            id, organization_id, shop_id, goods_receipt_number,
            purchase_order_id, purchase_id, supplier_invoice_number,
            status, subtotal_minor, landed_cost_minor, total_minor,
            notes, received_by_user_id, received_at_utc, version
        )
        VALUES
        (
            $id, $organizationId, $shopId, $number,
            $orderId, $purchaseId, $invoiceNumber,
            'posted', $subtotal, $landedCost, $total,
            $notes, $userId, $now, 1
        );
        """;
        command.Parameters.AddWithValue("$id", receiptId);
        command.Parameters.AddWithValue("$organizationId", order.OrganizationId);
        command.Parameters.AddWithValue("$shopId", order.ShopId);
        command.Parameters.AddWithValue("$number", receiptNumber);
        command.Parameters.AddWithValue("$orderId", order.Id);
        command.Parameters.AddWithValue("$purchaseId", purchaseId);
        command.Parameters.AddWithValue("$invoiceNumber", supplierInvoiceNumber);
        command.Parameters.AddWithValue("$subtotal", subtotal);
        command.Parameters.AddWithValue("$landedCost", landedCost);
        command.Parameters.AddWithValue("$total", total);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLegacyPurchaseLineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string purchaseId,
        PreparedReceiptLine prepared,
        string batchNumber,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO purchase_items
        (
            purchase_id, product_id, product_name_snapshot, sku_snapshot,
            quantity_base_units, unit_cost_minor, line_total_minor,
            batch_number, expiry_date
        )
        VALUES
        (
            $purchaseId, $productId, $productName, $sku,
            $quantity, $unitCost, $lineTotal,
            $batchNumber, $expiryDate
        );
        """;
        command.Parameters.AddWithValue("$purchaseId", purchaseId);
        command.Parameters.AddWithValue("$productId", prepared.OrderLine.ProductId);
        command.Parameters.AddWithValue("$productName", prepared.OrderLine.ProductName);
        command.Parameters.AddWithValue("$sku", prepared.OrderLine.Sku);
        command.Parameters.AddWithValue("$quantity", prepared.Request.QuantityBaseUnits);
        command.Parameters.AddWithValue("$unitCost", prepared.OrderLine.UnitCostMinor);
        command.Parameters.AddWithValue("$lineTotal", prepared.LineTotalMinor);
        command.Parameters.AddWithValue("$batchNumber", batchNumber);
        command.Parameters.AddWithValue(
            "$expiryDate",
            prepared.Request.ExpiryDate ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertGoodsReceiptLineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string receiptLineId,
        string receiptId,
        PreparedReceiptLine prepared,
        string batchNumber,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO procurement_goods_receipt_lines
        (
            id, goods_receipt_id, purchase_order_line_id, product_id,
            product_name_snapshot, sku_snapshot, quantity_base_units,
            unit_cost_minor, landed_cost_minor, effective_unit_cost_minor,
            line_total_minor, batch_number, expiry_date, returned_quantity_base
        )
        VALUES
        (
            $id, $receiptId, $orderLineId, $productId,
            $productName, $sku, $quantity,
            $unitCost, $landedCost, $effectiveUnitCost,
            $lineTotal, $batchNumber, $expiryDate, 0
        );
        """;
        command.Parameters.AddWithValue("$id", receiptLineId);
        command.Parameters.AddWithValue("$receiptId", receiptId);
        command.Parameters.AddWithValue("$orderLineId", prepared.OrderLine.Id);
        command.Parameters.AddWithValue("$productId", prepared.OrderLine.ProductId);
        command.Parameters.AddWithValue("$productName", prepared.OrderLine.ProductName);
        command.Parameters.AddWithValue("$sku", prepared.OrderLine.Sku);
        command.Parameters.AddWithValue("$quantity", prepared.Request.QuantityBaseUnits);
        command.Parameters.AddWithValue("$unitCost", prepared.OrderLine.UnitCostMinor);
        command.Parameters.AddWithValue("$landedCost", prepared.Request.LandedCostMinor);
        command.Parameters.AddWithValue("$effectiveUnitCost", prepared.EffectiveUnitCostMinor);
        command.Parameters.AddWithValue("$lineTotal", prepared.LineTotalMinor);
        command.Parameters.AddWithValue("$batchNumber", batchNumber);
        command.Parameters.AddWithValue(
            "$expiryDate",
            prepared.Request.ExpiryDate ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateShopBalanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string shopId,
        string productId,
        long newBalance,
        int expectedVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE shop_stock_balances
        SET quantity_base_units = $newBalance,
            version = version + 1,
            updated_at_utc = $now
        WHERE shop_id = $shopId
          AND product_id = $productId
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$newBalance", newBalance);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$productId", productId);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "stock_changed",
                "Stock changed while receiving goods. Reload and try again.");
        }
    }

    private static async Task UpdateLegacyBalanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string productId,
        long quantityDelta,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT OR IGNORE INTO stock_balances
        (product_id, quantity_base_units, reserved_base_units, version, updated_at_utc)
        VALUES ($productId, 0, 0, 1, $now);

        UPDATE stock_balances
        SET quantity_base_units = quantity_base_units + $delta,
            version = version + 1,
            updated_at_utc = $now
        WHERE product_id = $productId;
        """;
        command.Parameters.AddWithValue("$productId", productId);
        command.Parameters.AddWithValue("$delta", quantityDelta);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInventoryBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        ActiveShopContextRecord context,
        string receiptLineId,
        PreparedReceiptLine prepared,
        string batchNumber,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO inventory_batches
        (
            id, organization_id, shop_id, product_id, goods_receipt_line_id,
            batch_number, expiry_date, received_quantity_base,
            available_quantity_base, unit_cost_minor, landed_cost_minor,
            status, version, received_at_utc, updated_at_utc
        )
        VALUES
        (
            $id, $organizationId, $shopId, $productId, $receiptLineId,
            $batchNumber, $expiryDate, $quantity,
            $quantity, $unitCost, $landedCost,
            'active', 1, $now, $now
        );
        """;
        command.Parameters.AddWithValue("$id", batchId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$productId", prepared.OrderLine.ProductId);
        command.Parameters.AddWithValue("$receiptLineId", receiptLineId);
        command.Parameters.AddWithValue("$batchNumber", batchNumber);
        command.Parameters.AddWithValue(
            "$expiryDate",
            prepared.Request.ExpiryDate ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$quantity", prepared.Request.QuantityBaseUnits);
        command.Parameters.AddWithValue("$unitCost", prepared.EffectiveUnitCostMinor);
        command.Parameters.AddWithValue("$landedCost", prepared.Request.LandedCostMinor);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateOrderLineReceivedAsync(
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
        SET received_quantity_base = received_quantity_base + $quantity
        WHERE id = $id
          AND received_quantity_base + $quantity <= ordered_quantity_base;
        """;
        command.Parameters.AddWithValue("$quantity", quantity);
        command.Parameters.AddWithValue("$id", orderLineId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "receipt_quantity_conflict",
                "The purchase order receipt quantity changed. Reload and try again.");
        }
    }

    private static async Task UpdateLegacyPurchaseTotalsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string purchaseId,
        long subtotal,
        long total,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE purchases
        SET subtotal_minor = $subtotal,
            total_minor = $total,
            updated_at_utc = $now
        WHERE id = $id
          AND total_minor = 0;
        """;
        command.Parameters.AddWithValue("$subtotal", subtotal);
        command.Parameters.AddWithValue("$total", total);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", purchaseId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "purchase_posting_conflict",
                "The immutable purchase record could not be posted.");
        }
    }

    private static async Task UpdatePurchaseOrderAfterReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string orderId,
        long landedCost,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE procurement_purchase_orders
        SET status = CASE
                WHEN NOT EXISTS
                (
                    SELECT 1
                    FROM procurement_purchase_order_lines
                    WHERE purchase_order_id = $orderId
                      AND received_quantity_base < ordered_quantity_base
                ) THEN 'received'
                ELSE 'partially_received'
            END,
            landed_cost_minor = landed_cost_minor + $landedCost,
            total_minor = subtotal_minor + landed_cost_minor + $landedCost,
            completed_at_utc = CASE
                WHEN NOT EXISTS
                (
                    SELECT 1
                    FROM procurement_purchase_order_lines
                    WHERE purchase_order_id = $orderId
                      AND received_quantity_base < ordered_quantity_base
                ) THEN $now ELSE completed_at_utc END,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $orderId
          AND status IN ('approved', 'partially_received');
        """;
        command.Parameters.AddWithValue("$orderId", orderId);
        command.Parameters.AddWithValue("$landedCost", landedCost);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "purchase_order_receipt_conflict",
                "The purchase order changed while receiving goods.");
        }
    }

    private static async Task<GoodsReceiptRecord> ReadGoodsReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ActiveShopContextRecord context,
        string id,
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
            receipt.purchase_id,
            purchase.purchase_number,
            order_header.supplier_id,
            supplier.name,
            receipt.supplier_invoice_number,
            receipt.status,
            receipt.subtotal_minor,
            receipt.landed_cost_minor,
            receipt.total_minor,
            receipt.notes,
            user.display_name,
            receipt.received_at_utc
        FROM procurement_goods_receipts AS receipt
        INNER JOIN procurement_purchase_orders AS order_header
            ON order_header.id = receipt.purchase_order_id
        INNER JOIN purchases AS purchase ON purchase.id = receipt.purchase_id
        INNER JOIN suppliers AS supplier ON supplier.id = order_header.supplier_id
        INNER JOIN users AS user ON user.id = receipt.received_by_user_id
        WHERE receipt.id = $id
          AND receipt.organization_id = $organizationId
          AND receipt.shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "goods_receipt_not_found",
                "The goods receipt could not be found in the active branch.");
        }
        object[] values = new object[reader.FieldCount];
        reader.GetValues(values);
        await reader.DisposeAsync();

        IReadOnlyList<GoodsReceiptLineRecord> lines = await ReadGoodsReceiptLinesAsync(
            connection,
            transaction,
            id,
            cancellationToken);
        return ReadGoodsReceipt(values, lines);
    }

    private static GoodsReceiptRecord ReadGoodsReceipt(
        SqliteDataReader reader,
        IReadOnlyList<GoodsReceiptLineRecord> lines) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetInt64(10), reader.GetInt64(11),
            reader.GetInt64(12), reader.GetString(13), reader.GetString(14),
            DateTimeOffset.Parse(reader.GetString(15)), lines);

    private static GoodsReceiptRecord ReadGoodsReceipt(
        object[] values,
        IReadOnlyList<GoodsReceiptLineRecord> lines) =>
        new(
            Convert.ToString(values[0])!, Convert.ToString(values[1])!,
            Convert.ToString(values[2])!, Convert.ToString(values[3])!,
            Convert.ToString(values[4])!, Convert.ToString(values[5])!,
            Convert.ToString(values[6])!, Convert.ToString(values[7])!,
            Convert.ToString(values[8])!, Convert.ToString(values[9])!,
            Convert.ToInt64(values[10]), Convert.ToInt64(values[11]),
            Convert.ToInt64(values[12]), Convert.ToString(values[13])!,
            Convert.ToString(values[14])!, DateTimeOffset.Parse(Convert.ToString(values[15])!),
            lines);

    private static async Task<IReadOnlyList<GoodsReceiptLineRecord>> ReadGoodsReceiptLinesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string receiptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            line.id, line.purchase_order_line_id, line.product_id,
            line.product_name_snapshot, line.sku_snapshot,
            line.quantity_base_units, line.unit_cost_minor,
            line.landed_cost_minor, line.effective_unit_cost_minor,
            line.line_total_minor, line.batch_number, line.expiry_date,
            batch.id
        FROM procurement_goods_receipt_lines AS line
        LEFT JOIN inventory_batches AS batch
            ON batch.goods_receipt_line_id = line.id
        WHERE line.goods_receipt_id = $receiptId
        ORDER BY line.product_name_snapshot COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$receiptId", receiptId);

        var lines = new List<GoodsReceiptLineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new GoodsReceiptLineRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt64(5),
                reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
                reader.GetInt64(9), reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        return lines;
    }

    private sealed record NormalizedReceiptLine(
        string PurchaseOrderLineId,
        long QuantityBaseUnits,
        long LandedCostMinor,
        string BatchNumber,
        string? ExpiryDate);

    private sealed record PreparedReceiptLine(
        NormalizedReceiptLine Request,
        PurchaseOrderLineState OrderLine,
        long BaseValueMinor,
        long LineTotalMinor,
        long EffectiveUnitCostMinor);
}
