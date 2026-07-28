using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Procurement;

public sealed partial class ProcurementService
{
    public async Task<IReadOnlyList<PurchaseOrderRecord>> ListPurchaseOrdersAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? requestedStatus,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        string status = NormalizeOrderStatusFilter(requestedStatus);
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
            order_header.id,
            order_header.purchase_order_number,
            order_header.organization_id,
            order_header.shop_id,
            shop.code,
            order_header.supplier_id,
            supplier.name,
            order_header.status,
            order_header.order_date,
            order_header.expected_date,
            order_header.currency_code,
            order_header.subtotal_minor,
            order_header.landed_cost_minor,
            order_header.total_minor,
            order_header.notes,
            order_header.version,
            creator.display_name,
            approver.display_name,
            order_header.created_at_utc,
            order_header.submitted_at_utc,
            order_header.approved_at_utc,
            order_header.completed_at_utc
        FROM procurement_purchase_orders AS order_header
        INNER JOIN shops AS shop ON shop.id = order_header.shop_id
        INNER JOIN suppliers AS supplier ON supplier.id = order_header.supplier_id
        INNER JOIN users AS creator ON creator.id = order_header.created_by_user_id
        LEFT JOIN users AS approver ON approver.id = order_header.approved_by_user_id
        WHERE order_header.organization_id = $organizationId
          AND order_header.shop_id = $shopId
          AND ($status = '' OR order_header.status = $status)
        ORDER BY order_header.order_date DESC, order_header.created_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$limit", limit);

        var orders = new List<PurchaseOrderRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            orders.Add(ReadOrderRecord(reader, Array.Empty<PurchaseOrderLineRecord>()));
        }
        return orders;
    }

    public async Task<PurchaseOrderRecord> GetPurchaseOrderAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string purchaseOrderId,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(purchaseOrderId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireProcurementAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);
        return await ReadPurchaseOrderAsync(
            connection,
            transaction: null,
            context,
            id,
            cancellationToken);
    }

    public async Task<PurchaseOrderRecord> CreatePurchaseOrderAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreatePurchaseOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        string supplierId = NormalizeId(request.SupplierId);
        string orderDate = string.IsNullOrWhiteSpace(request.OrderDate)
            ? DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")
            : NormalizeDate(request.OrderDate, "invalid_order_date");
        string? expectedDate = NormalizeOptionalDate(
            request.ExpectedDate,
            "invalid_expected_date");
        ValidateExpectedDate(orderDate, expectedDate);
        string notes = OptionalText(request.Notes, 1000);
        IReadOnlyList<NormalizedOrderLine> requestedLines =
            NormalizeOrderLines(request.Items);

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
        await RequireSupplierAsync(
            connection,
            transaction,
            context.OrganizationId,
            supplierId,
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string id = Guid.NewGuid().ToString("N");
        string orderNumber = await NextDocumentNumberAsync(
            connection,
            transaction,
            context,
            "purchase_order",
            now,
            cancellationToken);

        var lines = new List<(NormalizedOrderLine Input, ProductSnapshot Product)>();
        long subtotal = 0;
        foreach (NormalizedOrderLine requested in requestedLines)
        {
            ProductSnapshot product = await RequireProductAsync(
                connection,
                transaction,
                requested.ProductId,
                cancellationToken);
            subtotal = checked(subtotal + requested.QuantityBaseUnits * requested.UnitCostMinor);
            lines.Add((requested, product));
        }

        await InsertPurchaseOrderHeaderAsync(
            connection,
            transaction,
            id,
            context,
            orderNumber,
            supplierId,
            orderDate,
            expectedDate,
            subtotal,
            notes,
            user.Id,
            now,
            cancellationToken);
        await ReplacePurchaseOrderLinesAsync(
            connection,
            transaction,
            id,
            lines,
            cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "procurement.purchase_order.created",
            "purchase_order",
            id,
            new
            {
                orderNumber,
                context.OrganizationId,
                context.ShopId,
                supplierId,
                subtotalMinor = subtotal,
                itemCount = lines.Count
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetPurchaseOrderAsync(user, context, id, cancellationToken);
    }

    public async Task<PurchaseOrderRecord> UpdatePurchaseOrderAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string purchaseOrderId,
        UpdatePurchaseOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(purchaseOrderId);
        string supplierId = NormalizeId(request.SupplierId);
        string orderDate = NormalizeDate(request.OrderDate, "invalid_order_date");
        string? expectedDate = NormalizeOptionalDate(
            request.ExpectedDate,
            "invalid_expected_date");
        ValidateExpectedDate(orderDate, expectedDate);
        string notes = OptionalText(request.Notes, 1000);
        IReadOnlyList<NormalizedOrderLine> requestedLines =
            NormalizeOrderLines(request.Items);
        if (request.ExpectedVersion < 1)
        {
            throw Validation("invalid_order_version", "The expected order version is invalid.");
        }

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
        PurchaseOrderHeader current = await RequirePurchaseOrderHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        if (current.Status != "draft")
        {
            throw Conflict(
                "purchase_order_not_draft",
                "Only a draft purchase order can be edited.");
        }
        if (current.Version != request.ExpectedVersion)
        {
            throw Conflict(
                "purchase_order_changed",
                "The purchase order changed. Reload it and try again.");
        }
        await RequireSupplierAsync(
            connection,
            transaction,
            context.OrganizationId,
            supplierId,
            cancellationToken);

        var lines = new List<(NormalizedOrderLine Input, ProductSnapshot Product)>();
        long subtotal = 0;
        foreach (NormalizedOrderLine requested in requestedLines)
        {
            ProductSnapshot product = await RequireProductAsync(
                connection,
                transaction,
                requested.ProductId,
                cancellationToken);
            subtotal = checked(subtotal + requested.QuantityBaseUnits * requested.UnitCostMinor);
            lines.Add((requested, product));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE procurement_purchase_orders
            SET supplier_id = $supplierId,
                order_date = $orderDate,
                expected_date = $expectedDate,
                subtotal_minor = $subtotal,
                total_minor = $subtotal + landed_cost_minor,
                notes = $notes,
                version = version + 1,
                updated_at_utc = $now
            WHERE id = $id
              AND organization_id = $organizationId
              AND shop_id = $shopId
              AND status = 'draft'
              AND version = $expectedVersion;
            """;
            update.Parameters.AddWithValue("$supplierId", supplierId);
            update.Parameters.AddWithValue("$orderDate", orderDate);
            update.Parameters.AddWithValue("$expectedDate", expectedDate ?? (object)DBNull.Value);
            update.Parameters.AddWithValue("$subtotal", subtotal);
            update.Parameters.AddWithValue("$notes", notes);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            update.Parameters.AddWithValue("$shopId", context.ShopId);
            update.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "purchase_order_changed",
                    "The purchase order changed. Reload it and try again.");
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
            "DELETE FROM procurement_purchase_order_lines WHERE purchase_order_id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await ReplacePurchaseOrderLinesAsync(
            connection,
            transaction,
            id,
            lines,
            cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "procurement.purchase_order.updated",
            "purchase_order",
            id,
            new
            {
                current.Number,
                supplierId,
                subtotalMinor = subtotal,
                itemCount = lines.Count,
                previousVersion = request.ExpectedVersion
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetPurchaseOrderAsync(user, context, id, cancellationToken);
    }

    public Task<PurchaseOrderRecord> SubmitPurchaseOrderAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string purchaseOrderId,
        VersionedActionRequest request,
        CancellationToken cancellationToken = default) =>
        TransitionPurchaseOrderAsync(
            user,
            context,
            purchaseOrderId,
            request.ExpectedVersion,
            "draft",
            "submitted",
            cancellationToken);

    public async Task<PurchaseOrderRecord> ApprovePurchaseOrderAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string purchaseOrderId,
        VersionedActionRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(purchaseOrderId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation("invalid_order_version", "The expected order version is invalid.");
        }

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
        PurchaseOrderHeader header = await RequirePurchaseOrderHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        if (header.Status != "submitted")
        {
            throw Conflict(
                "purchase_order_not_submitted",
                "Only a submitted purchase order can be approved.");
        }
        if (header.Version != request.ExpectedVersion)
        {
            throw Conflict(
                "purchase_order_changed",
                "The purchase order changed. Reload it and try again.");
        }

        string creatorId = await ReadOrderCreatorIdAsync(
            connection,
            transaction,
            id,
            cancellationToken);
        if (!IsAdministrator(user) &&
            string.Equals(creatorId, user.Id, StringComparison.Ordinal))
        {
            throw Forbidden(
                "purchase_order_approval_separation_required",
                "A branch manager cannot approve a purchase order they created. Ask another manager or an administrator.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE procurement_purchase_orders
        SET status = 'approved',
            approved_by_user_id = $userId,
            approved_at_utc = $now,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $id
          AND organization_id = $organizationId
          AND shop_id = $shopId
          AND status = 'submitted'
          AND version = $expectedVersion;
        """;
        update.Parameters.AddWithValue("$userId", user.Id);
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        update.Parameters.AddWithValue("$shopId", context.ShopId);
        update.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "purchase_order_changed",
                "The purchase order changed. Reload it and try again.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "procurement.purchase_order.approved",
            "purchase_order",
            id,
            new { header.Number, previousVersion = request.ExpectedVersion },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetPurchaseOrderAsync(user, context, id, cancellationToken);
    }

    public async Task<PurchaseOrderRecord> CancelPurchaseOrderAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string purchaseOrderId,
        CancelPurchaseOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(purchaseOrderId);
        string reason = RequiredText(
            request.Reason,
            500,
            "cancellation_reason_required",
            "Enter the reason for cancelling the purchase order.");
        if (reason.Length < 5)
        {
            throw Validation(
                "cancellation_reason_too_short",
                "The cancellation reason must contain at least five characters.");
        }

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
        PurchaseOrderHeader header = await RequirePurchaseOrderHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        if (header.Version != request.ExpectedVersion)
        {
            throw Conflict(
                "purchase_order_changed",
                "The purchase order changed. Reload it and try again.");
        }
        if (header.Status is "received" or "cancelled")
        {
            throw Conflict(
                "purchase_order_cannot_be_cancelled",
                "This purchase order cannot be cancelled in its current state.");
        }

        long received = await ReadTotalReceivedQuantityAsync(
            connection,
            transaction,
            id,
            cancellationToken);
        if (received > 0)
        {
            throw Conflict(
                "received_purchase_order_cannot_be_cancelled",
                "A purchase order with posted goods receipts cannot be cancelled.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE procurement_purchase_orders
        SET status = 'cancelled',
            cancellation_reason = $reason,
            cancelled_by_user_id = $userId,
            cancelled_at_utc = $now,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $id
          AND organization_id = $organizationId
          AND shop_id = $shopId
          AND status IN ('draft', 'submitted', 'approved', 'partially_received')
          AND version = $expectedVersion;
        """;
        update.Parameters.AddWithValue("$reason", reason);
        update.Parameters.AddWithValue("$userId", user.Id);
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        update.Parameters.AddWithValue("$shopId", context.ShopId);
        update.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "purchase_order_changed",
                "The purchase order changed. Reload it and try again.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "procurement.purchase_order.cancelled",
            "purchase_order",
            id,
            new { header.Number, reason, previousVersion = request.ExpectedVersion },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetPurchaseOrderAsync(user, context, id, cancellationToken);
    }

    private async Task<PurchaseOrderRecord> TransitionPurchaseOrderAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string purchaseOrderId,
        int expectedVersion,
        string expectedStatus,
        string newStatus,
        CancellationToken cancellationToken)
    {
        string id = NormalizeId(purchaseOrderId);
        if (expectedVersion < 1)
        {
            throw Validation("invalid_order_version", "The expected order version is invalid.");
        }

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
        PurchaseOrderHeader header = await RequirePurchaseOrderHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        if (header.Status != expectedStatus)
        {
            throw Conflict(
                "purchase_order_invalid_state",
                $"The purchase order must be {expectedStatus} before it can be {newStatus}.");
        }
        if (header.Version != expectedVersion)
        {
            throw Conflict(
                "purchase_order_changed",
                "The purchase order changed. Reload it and try again.");
        }
        if (header.TotalMinor <= 0)
        {
            throw Conflict(
                "purchase_order_total_invalid",
                "The purchase order must have a positive total before submission.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE procurement_purchase_orders
        SET status = $newStatus,
            submitted_by_user_id = CASE WHEN $newStatus = 'submitted' THEN $userId ELSE submitted_by_user_id END,
            submitted_at_utc = CASE WHEN $newStatus = 'submitted' THEN $now ELSE submitted_at_utc END,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $id
          AND organization_id = $organizationId
          AND shop_id = $shopId
          AND status = $expectedStatus
          AND version = $expectedVersion;
        """;
        update.Parameters.AddWithValue("$newStatus", newStatus);
        update.Parameters.AddWithValue("$userId", user.Id);
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        update.Parameters.AddWithValue("$shopId", context.ShopId);
        update.Parameters.AddWithValue("$expectedStatus", expectedStatus);
        update.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "purchase_order_changed",
                "The purchase order changed. Reload it and try again.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            $"procurement.purchase_order.{newStatus}",
            "purchase_order",
            id,
            new { header.Number, previousVersion = expectedVersion },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetPurchaseOrderAsync(user, context, id, cancellationToken);
    }

    private static async Task InsertPurchaseOrderHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        ActiveShopContextRecord context,
        string orderNumber,
        string supplierId,
        string orderDate,
        string? expectedDate,
        long subtotalMinor,
        string notes,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO procurement_purchase_orders
        (
            id, organization_id, shop_id, purchase_order_number, supplier_id,
            status, order_date, expected_date, currency_code,
            subtotal_minor, landed_cost_minor, total_minor, notes, version,
            created_by_user_id, created_at_utc, updated_at_utc
        )
        VALUES
        (
            $id, $organizationId, $shopId, $number, $supplierId,
            'draft', $orderDate, $expectedDate, $currencyCode,
            $subtotal, 0, $subtotal, $notes, 1,
            $userId, $now, $now
        );
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$number", orderNumber);
        command.Parameters.AddWithValue("$supplierId", supplierId);
        command.Parameters.AddWithValue("$orderDate", orderDate);
        command.Parameters.AddWithValue("$expectedDate", expectedDate ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$currencyCode", context.CurrencyCode);
        command.Parameters.AddWithValue("$subtotal", subtotalMinor);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplacePurchaseOrderLinesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string orderId,
        IReadOnlyList<(NormalizedOrderLine Input, ProductSnapshot Product)> lines,
        CancellationToken cancellationToken)
    {
        int lineNumber = 1;
        foreach ((NormalizedOrderLine input, ProductSnapshot product) in lines)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            INSERT INTO procurement_purchase_order_lines
            (
                id, purchase_order_id, line_number, product_id,
                product_name_snapshot, sku_snapshot,
                ordered_quantity_base, received_quantity_base, returned_quantity_base,
                unit_cost_minor, line_total_minor
            )
            VALUES
            (
                $id, $orderId, $lineNumber, $productId,
                $productName, $sku,
                $quantity, 0, 0,
                $unitCost, $lineTotal
            );
            """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$orderId", orderId);
            command.Parameters.AddWithValue("$lineNumber", lineNumber++);
            command.Parameters.AddWithValue("$productId", product.Id);
            command.Parameters.AddWithValue("$productName", product.Name);
            command.Parameters.AddWithValue("$sku", product.Sku);
            command.Parameters.AddWithValue("$quantity", input.QuantityBaseUnits);
            command.Parameters.AddWithValue("$unitCost", input.UnitCostMinor);
            command.Parameters.AddWithValue(
                "$lineTotal",
                checked(input.QuantityBaseUnits * input.UnitCostMinor));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<PurchaseOrderRecord> ReadPurchaseOrderAsync(
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
            order_header.id,
            order_header.purchase_order_number,
            order_header.organization_id,
            order_header.shop_id,
            shop.code,
            order_header.supplier_id,
            supplier.name,
            order_header.status,
            order_header.order_date,
            order_header.expected_date,
            order_header.currency_code,
            order_header.subtotal_minor,
            order_header.landed_cost_minor,
            order_header.total_minor,
            order_header.notes,
            order_header.version,
            creator.display_name,
            approver.display_name,
            order_header.created_at_utc,
            order_header.submitted_at_utc,
            order_header.approved_at_utc,
            order_header.completed_at_utc
        FROM procurement_purchase_orders AS order_header
        INNER JOIN shops AS shop ON shop.id = order_header.shop_id
        INNER JOIN suppliers AS supplier ON supplier.id = order_header.supplier_id
        INNER JOIN users AS creator ON creator.id = order_header.created_by_user_id
        LEFT JOIN users AS approver ON approver.id = order_header.approved_by_user_id
        WHERE order_header.id = $id
          AND order_header.organization_id = $organizationId
          AND order_header.shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "purchase_order_not_found",
                "The purchase order could not be found in the active branch.");
        }
        object[] values = new object[reader.FieldCount];
        reader.GetValues(values);
        await reader.DisposeAsync();

        IReadOnlyList<PurchaseOrderLineRecord> lines = await ReadPurchaseOrderLinesAsync(
            connection,
            transaction,
            id,
            cancellationToken);
        return ReadOrderRecord(values, lines);
    }

    private static PurchaseOrderRecord ReadOrderRecord(
        SqliteDataReader reader,
        IReadOnlyList<PurchaseOrderLineRecord> lines) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetString(14),
            reader.GetInt32(15),
            reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            DateTimeOffset.Parse(reader.GetString(18)),
            reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19)),
            reader.IsDBNull(20) ? null : DateTimeOffset.Parse(reader.GetString(20)),
            reader.IsDBNull(21) ? null : DateTimeOffset.Parse(reader.GetString(21)),
            lines);

    private static PurchaseOrderRecord ReadOrderRecord(
        object[] values,
        IReadOnlyList<PurchaseOrderLineRecord> lines) =>
        new(
            Convert.ToString(values[0])!,
            Convert.ToString(values[1])!,
            Convert.ToString(values[2])!,
            Convert.ToString(values[3])!,
            Convert.ToString(values[4])!,
            Convert.ToString(values[5])!,
            Convert.ToString(values[6])!,
            Convert.ToString(values[7])!,
            Convert.ToString(values[8])!,
            values[9] is DBNull ? null : Convert.ToString(values[9]),
            Convert.ToInt64(values[11]),
            Convert.ToInt64(values[12]),
            Convert.ToInt64(values[13]),
            Convert.ToString(values[14])!,
            Convert.ToInt32(values[15]),
            Convert.ToString(values[16])!,
            values[17] is DBNull ? null : Convert.ToString(values[17]),
            DateTimeOffset.Parse(Convert.ToString(values[18])!),
            values[19] is DBNull ? null : DateTimeOffset.Parse(Convert.ToString(values[19])!),
            values[20] is DBNull ? null : DateTimeOffset.Parse(Convert.ToString(values[20])!),
            values[21] is DBNull ? null : DateTimeOffset.Parse(Convert.ToString(values[21])!),
            lines);

    private static async Task<IReadOnlyList<PurchaseOrderLineRecord>> ReadPurchaseOrderLinesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string orderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            id, line_number, product_id, product_name_snapshot, sku_snapshot,
            ordered_quantity_base, received_quantity_base, returned_quantity_base,
            ordered_quantity_base - received_quantity_base,
            unit_cost_minor, line_total_minor
        FROM procurement_purchase_order_lines
        WHERE purchase_order_id = $orderId
        ORDER BY line_number;
        """;
        command.Parameters.AddWithValue("$orderId", orderId);

        var lines = new List<PurchaseOrderLineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new PurchaseOrderLineRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10)));
        }
        return lines;
    }

    private static async Task<PurchaseOrderHeader> RequirePurchaseOrderHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            order_header.id,
            order_header.purchase_order_number,
            order_header.organization_id,
            order_header.shop_id,
            order_header.supplier_id,
            supplier.name,
            order_header.status,
            order_header.order_date,
            order_header.expected_date,
            order_header.currency_code,
            order_header.subtotal_minor,
            order_header.landed_cost_minor,
            order_header.total_minor,
            order_header.notes,
            order_header.version,
            creator.display_name,
            approver.display_name,
            order_header.created_at_utc,
            order_header.submitted_at_utc,
            order_header.approved_at_utc,
            order_header.completed_at_utc
        FROM procurement_purchase_orders AS order_header
        INNER JOIN suppliers AS supplier ON supplier.id = order_header.supplier_id
        INNER JOIN users AS creator ON creator.id = order_header.created_by_user_id
        LEFT JOIN users AS approver ON approver.id = order_header.approved_by_user_id
        WHERE order_header.id = $id
          AND order_header.organization_id = $organizationId
          AND order_header.shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "purchase_order_not_found",
                "The purchase order could not be found in the active branch.");
        }
        return new PurchaseOrderHeader(
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
            reader.GetInt32(14),
            reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            DateTimeOffset.Parse(reader.GetString(17)),
            reader.IsDBNull(18) ? null : DateTimeOffset.Parse(reader.GetString(18)),
            reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19)),
            reader.IsDBNull(20) ? null : DateTimeOffset.Parse(reader.GetString(20)));
    }

    private static async Task<string> ReadOrderCreatorIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string orderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT created_by_user_id FROM procurement_purchase_orders WHERE id = $id;";
        command.Parameters.AddWithValue("$id", orderId);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> ReadTotalReceivedQuantityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string orderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COALESCE(SUM(received_quantity_base), 0)
        FROM procurement_purchase_order_lines
        WHERE purchase_order_id = $id;
        """;
        command.Parameters.AddWithValue("$id", orderId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string NormalizeOrderStatusFilter(string? requested)
    {
        string status = requested?.Trim().ToLowerInvariant() ?? string.Empty;
        if (status.Length == 0)
        {
            return string.Empty;
        }
        string[] allowed =
        [
            "draft", "submitted", "approved", "partially_received", "received", "cancelled"
        ];
        if (!allowed.Contains(status, StringComparer.Ordinal))
        {
            throw Validation("invalid_order_status", "The purchase order status filter is invalid.");
        }
        return status;
    }

    private static void ValidateExpectedDate(string orderDate, string? expectedDate)
    {
        if (expectedDate is not null &&
            string.CompareOrdinal(expectedDate, orderDate) < 0)
        {
            throw Validation(
                "expected_date_before_order_date",
                "The expected date cannot be before the order date.");
        }
    }
}
