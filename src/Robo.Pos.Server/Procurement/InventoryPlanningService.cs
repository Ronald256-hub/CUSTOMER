using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Inventory;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Procurement;

public sealed partial class ProcurementService
{
    public async Task<IReadOnlyList<ReorderPolicyRecord>> ListReorderPoliciesAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
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
            policy.id,
            policy.shop_id,
            policy.product_id,
            product.name,
            product.sku,
            policy.reorder_point_base,
            policy.target_stock_base,
            policy.lead_time_days,
            policy.preferred_supplier_id,
            COALESCE(supplier.name, ''),
            policy.is_active,
            policy.version,
            policy.updated_at_utc
        FROM procurement_reorder_policies AS policy
        INNER JOIN products AS product ON product.id = policy.product_id
        LEFT JOIN suppliers AS supplier ON supplier.id = policy.preferred_supplier_id
        WHERE policy.organization_id = $organizationId
          AND policy.shop_id = $shopId
          AND ($includeInactive = 1 OR policy.is_active = 1)
        ORDER BY product.name COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$includeInactive", includeInactive ? 1 : 0);

        var policies = new List<ReorderPolicyRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            policies.Add(new ReorderPolicyRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt64(5),
                reader.GetInt64(6), reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9),
                reader.GetInt32(10) == 1, reader.GetInt32(11),
                DateTimeOffset.Parse(reader.GetString(12))));
        }
        return policies;
    }

    public async Task<ReorderPolicyRecord> UpsertReorderPolicyAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        ReorderPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        string productId = NormalizeId(request.ProductId);
        if (request.ReorderPointBaseUnits < 0 ||
            request.TargetStockBaseUnits < request.ReorderPointBaseUnits)
        {
            throw Validation(
                "invalid_reorder_levels",
                "The target stock must be greater than or equal to the non-negative reorder point.");
        }
        if (request.LeadTimeDays is < 0 or > 3650)
        {
            throw Validation(
                "invalid_lead_time",
                "Lead time must be between 0 and 3650 days.");
        }
        string? supplierId = string.IsNullOrWhiteSpace(request.PreferredSupplierId)
            ? null
            : NormalizeId(request.PreferredSupplierId);

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
        ProductSnapshot product = await RequireProductAsync(
            connection,
            transaction,
            productId,
            cancellationToken);
        if (supplierId is not null)
        {
            await RequireSupplierAsync(
                connection,
                transaction,
                context.OrganizationId,
                supplierId,
                cancellationToken);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string? existingId = null;
        int? existingVersion = null;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
            """
            SELECT id, version
            FROM procurement_reorder_policies
            WHERE shop_id = $shopId
              AND product_id = $productId
            LIMIT 1;
            """;
            read.Parameters.AddWithValue("$shopId", context.ShopId);
            read.Parameters.AddWithValue("$productId", productId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existingId = reader.GetString(0);
                existingVersion = reader.GetInt32(1);
            }
        }

        string policyId;
        if (existingId is null)
        {
            if (request.ExpectedVersion is not null)
            {
                throw Conflict(
                    "reorder_policy_not_found",
                    "The reorder policy does not exist. Reload and try again.");
            }
            policyId = Guid.NewGuid().ToString("N");
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
            """
            INSERT INTO procurement_reorder_policies
            (
                id, organization_id, shop_id, product_id,
                reorder_point_base, target_stock_base, lead_time_days,
                preferred_supplier_id, is_active, version,
                updated_by_user_id, created_at_utc, updated_at_utc
            )
            VALUES
            (
                $id, $organizationId, $shopId, $productId,
                $reorderPoint, $targetStock, $leadTime,
                $supplierId, $isActive, 1,
                $userId, $now, $now
            );
            """;
            insert.Parameters.AddWithValue("$id", policyId);
            insert.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            insert.Parameters.AddWithValue("$shopId", context.ShopId);
            insert.Parameters.AddWithValue("$productId", productId);
            insert.Parameters.AddWithValue("$reorderPoint", request.ReorderPointBaseUnits);
            insert.Parameters.AddWithValue("$targetStock", request.TargetStockBaseUnits);
            insert.Parameters.AddWithValue("$leadTime", request.LeadTimeDays);
            insert.Parameters.AddWithValue("$supplierId", supplierId ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$isActive", request.IsActive ? 1 : 0);
            insert.Parameters.AddWithValue("$userId", user.Id);
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            if (request.ExpectedVersion is null ||
                request.ExpectedVersion.Value != existingVersion)
            {
                throw Conflict(
                    "reorder_policy_changed",
                    "The reorder policy changed. Reload and try again.");
            }
            policyId = existingId;
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE procurement_reorder_policies
            SET reorder_point_base = $reorderPoint,
                target_stock_base = $targetStock,
                lead_time_days = $leadTime,
                preferred_supplier_id = $supplierId,
                is_active = $isActive,
                version = version + 1,
                updated_by_user_id = $userId,
                updated_at_utc = $now
            WHERE id = $id
              AND organization_id = $organizationId
              AND shop_id = $shopId
              AND version = $expectedVersion;
            """;
            update.Parameters.AddWithValue("$reorderPoint", request.ReorderPointBaseUnits);
            update.Parameters.AddWithValue("$targetStock", request.TargetStockBaseUnits);
            update.Parameters.AddWithValue("$leadTime", request.LeadTimeDays);
            update.Parameters.AddWithValue("$supplierId", supplierId ?? (object)DBNull.Value);
            update.Parameters.AddWithValue("$isActive", request.IsActive ? 1 : 0);
            update.Parameters.AddWithValue("$userId", user.Id);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", policyId);
            update.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            update.Parameters.AddWithValue("$shopId", context.ShopId);
            update.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "reorder_policy_changed",
                    "The reorder policy changed. Reload and try again.");
            }
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            existingId is null
                ? "procurement.reorder_policy.created"
                : "procurement.reorder_policy.updated",
            "reorder_policy",
            policyId,
            new
            {
                context.ShopId,
                productId,
                product.Name,
                request.ReorderPointBaseUnits,
                request.TargetStockBaseUnits,
                request.LeadTimeDays,
                preferredSupplierId = supplierId,
                request.IsActive
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return (await ListReorderPoliciesAsync(
                user,
                context,
                includeInactive: true,
                cancellationToken))
            .Single(policy => policy.Id == policyId);
    }

    public async Task<IReadOnlyList<ReorderRecommendationRecord>>
        ListReorderRecommendationsAsync(
            AuthenticatedUser user,
            ActiveShopContextRecord context,
            CancellationToken cancellationToken = default)
    {
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
            shop_id, product_id, product_name, sku,
            available_base_units, on_order_base_units,
            reorder_point_base, target_stock_base,
            suggested_order_base_units, lead_time_days,
            preferred_supplier_id, preferred_supplier_name
        FROM procurement_reorder_recommendations
        WHERE organization_id = $organizationId
          AND shop_id = $shopId
        ORDER BY suggested_order_base_units DESC, product_name COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);

        var records = new List<ReorderRecommendationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new ReorderRecommendationRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
                reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11)));
        }
        return records;
    }

    public async Task<IReadOnlyList<InventoryBatchRecord>> ListInventoryBatchesAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? productId,
        string? requestedStatus,
        int? expiringWithinDays,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        string product = string.IsNullOrWhiteSpace(productId)
            ? string.Empty
            : NormalizeId(productId);
        string status = requestedStatus?.Trim().ToLowerInvariant() ?? string.Empty;
        if (status.Length > 0 &&
            !new[] { "active", "depleted", "expired", "quarantined" }
                .Contains(status, StringComparer.Ordinal))
        {
            throw Validation("invalid_batch_status", "The inventory batch status is invalid.");
        }
        int expiryDays = Math.Clamp(expiringWithinDays ?? -1, -1, 3650);
        int limit = Math.Clamp(requestedLimit, 1, 5000);

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
            batch.id, batch.shop_id, batch.product_id,
            product.name, product.sku, batch.batch_number,
            batch.expiry_date, batch.received_quantity_base,
            batch.available_quantity_base, batch.unit_cost_minor,
            batch.landed_cost_minor, batch.status, batch.received_at_utc
        FROM inventory_batches AS batch
        INNER JOIN products AS product ON product.id = batch.product_id
        WHERE batch.organization_id = $organizationId
          AND batch.shop_id = $shopId
          AND ($productId = '' OR batch.product_id = $productId)
          AND ($status = '' OR batch.status = $status)
          AND
          (
              $expiryDays < 0
              OR
              (
                  batch.expiry_date IS NOT NULL
                  AND date(batch.expiry_date) <= date('now', '+' || $expiryDays || ' days')
              )
          )
        ORDER BY
            CASE WHEN batch.expiry_date IS NULL THEN 1 ELSE 0 END,
            batch.expiry_date,
            batch.received_at_utc
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$productId", product);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$expiryDays", expiryDays);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<InventoryBatchRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new InventoryBatchRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7),
                reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10),
                reader.GetString(11), DateTimeOffset.Parse(reader.GetString(12))));
        }
        return records;
    }

    public async Task<IReadOnlyList<StockCountRecord>> ListStockCountsAsync(
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
            count_header.id, count_header.stock_count_number,
            count_header.organization_id, count_header.shop_id, shop.code,
            count_header.status, count_header.notes, count_header.version,
            creator.display_name, submitter.display_name, approver.display_name,
            count_header.created_at_utc, count_header.submitted_at_utc,
            count_header.approved_at_utc
        FROM procurement_stock_counts AS count_header
        INNER JOIN shops AS shop ON shop.id = count_header.shop_id
        INNER JOIN users AS creator ON creator.id = count_header.created_by_user_id
        LEFT JOIN users AS submitter ON submitter.id = count_header.submitted_by_user_id
        LEFT JOIN users AS approver ON approver.id = count_header.approved_by_user_id
        WHERE count_header.organization_id = $organizationId
          AND count_header.shop_id = $shopId
        ORDER BY count_header.created_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<StockCountRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadStockCount(reader, Array.Empty<StockCountLineRecord>()));
        }
        return records;
    }

    public async Task<StockCountRecord> GetStockCountAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string stockCountId,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(stockCountId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireProcurementAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);
        return await ReadStockCountAsync(
            connection,
            transaction: null,
            context,
            id,
            cancellationToken);
    }

    public async Task<StockCountRecord> CreateStockCountAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateStockCountRequest request,
        CancellationToken cancellationToken = default)
    {
        string notes = OptionalText(request.Notes, 1000);
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

        await using (var conflict = connection.CreateCommand())
        {
            conflict.Transaction = transaction;
            conflict.CommandText =
            """
            SELECT COUNT(1)
            FROM procurement_stock_counts
            WHERE organization_id = $organizationId
              AND shop_id = $shopId
              AND status IN ('draft', 'submitted');
            """;
            conflict.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            conflict.Parameters.AddWithValue("$shopId", context.ShopId);
            if (Convert.ToInt32(await conflict.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                throw Conflict(
                    "open_stock_count_exists",
                    "Complete or cancel the existing open stock count before starting another.");
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string id = Guid.NewGuid().ToString("N");
        string number = await NextDocumentNumberAsync(
            connection,
            transaction,
            context,
            "stock_count",
            now,
            cancellationToken);
        await using (var header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText =
            """
            INSERT INTO procurement_stock_counts
            (
                id, organization_id, shop_id, stock_count_number,
                status, notes, version, created_by_user_id,
                created_at_utc, updated_at_utc
            )
            VALUES
            ($id, $organizationId, $shopId, $number,
             'draft', $notes, 1, $userId, $now, $now);
            """;
            header.Parameters.AddWithValue("$id", id);
            header.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            header.Parameters.AddWithValue("$shopId", context.ShopId);
            header.Parameters.AddWithValue("$number", number);
            header.Parameters.AddWithValue("$notes", notes);
            header.Parameters.AddWithValue("$userId", user.Id);
            header.Parameters.AddWithValue("$now", now.ToString("O"));
            await header.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var lines = connection.CreateCommand())
        {
            lines.Transaction = transaction;
            lines.CommandText =
            """
            INSERT INTO procurement_stock_count_lines
            (
                id, stock_count_id, product_id, product_name_snapshot,
                sku_snapshot, system_quantity_base, counted_quantity_base,
                unit_cost_minor, stock_version_snapshot
            )
            SELECT
                lower(hex(randomblob(16))), $countId, product.id, product.name,
                product.sku, COALESCE(balance.quantity_base_units, 0), NULL,
                product.cost_price_minor, COALESCE(balance.version, 1)
            FROM products AS product
            LEFT JOIN shop_stock_balances AS balance
                ON balance.product_id = product.id
               AND balance.shop_id = $shopId
            WHERE product.is_active = 1
            ORDER BY product.name COLLATE NOCASE;
            """;
            lines.Parameters.AddWithValue("$countId", id);
            lines.Parameters.AddWithValue("$shopId", context.ShopId);
            await lines.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "procurement.stock_count.created",
            "stock_count",
            id,
            new { number, context.ShopId },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetStockCountAsync(user, context, id, cancellationToken);
    }

    public async Task<StockCountRecord> SubmitStockCountAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string stockCountId,
        SubmitStockCountRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(stockCountId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation("invalid_stock_count_version", "The stock count version is invalid.");
        }
        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw Validation(
                "stock_count_lines_required",
                "Enter counted quantities for the stock count.");
        }
        var lines = request.Lines
            .GroupBy(line => NormalizeId(line.StockCountLineId), StringComparer.Ordinal)
            .Select(group =>
            {
                if (group.Count() != 1)
                {
                    throw Validation(
                        "duplicate_stock_count_line",
                        "Each stock count line can be submitted only once.");
                }
                StockCountLineRequest line = group.Single();
                if (line.CountedQuantityBaseUnits < 0)
                {
                    throw Validation(
                        "invalid_counted_quantity",
                        "Counted quantities cannot be negative.");
                }
                return line;
            })
            .ToList();

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
        StockCountHeader header = await RequireStockCountHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        if (header.Status != "draft" || header.Version != request.ExpectedVersion)
        {
            throw Conflict(
                "stock_count_changed",
                "The stock count changed or is no longer a draft. Reload and try again.");
        }

        int expectedLineCount;
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText =
                "SELECT COUNT(1) FROM procurement_stock_count_lines WHERE stock_count_id = $id;";
            count.Parameters.AddWithValue("$id", id);
            expectedLineCount = Convert.ToInt32(
                await count.ExecuteScalarAsync(cancellationToken));
        }
        if (lines.Count != expectedLineCount)
        {
            throw Validation(
                "incomplete_stock_count",
                "Every stock count line requires a counted quantity before submission.");
        }

        foreach (StockCountLineRequest line in lines)
        {
            await using var updateLine = connection.CreateCommand();
            updateLine.Transaction = transaction;
            updateLine.CommandText =
            """
            UPDATE procurement_stock_count_lines
            SET counted_quantity_base = $counted
            WHERE id = $lineId
              AND stock_count_id = $countId;
            """;
            updateLine.Parameters.AddWithValue("$counted", line.CountedQuantityBaseUnits);
            updateLine.Parameters.AddWithValue("$lineId", line.StockCountLineId);
            updateLine.Parameters.AddWithValue("$countId", id);
            if (await updateLine.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw NotFound(
                    "stock_count_line_not_found",
                    "A submitted stock count line could not be found.");
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE procurement_stock_counts
        SET status = 'submitted',
            submitted_by_user_id = $userId,
            submitted_at_utc = $now,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $id
          AND organization_id = $organizationId
          AND shop_id = $shopId
          AND status = 'draft'
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
                "stock_count_changed",
                "The stock count changed. Reload and try again.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "procurement.stock_count.submitted",
            "stock_count",
            id,
            new { header.Number, lineCount = lines.Count },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetStockCountAsync(user, context, id, cancellationToken);
    }

    public async Task<StockCountRecord> ApproveStockCountAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string stockCountId,
        ApproveStockCountRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user, "approve a stock count");
        string id = NormalizeId(stockCountId);
        string reason = RequiredText(
            request.Reason,
            500,
            "stock_count_reason_required",
            "Enter the reason for approving the stock count variances.");
        if (reason.Length < 5)
        {
            throw Validation(
                "stock_count_reason_too_short",
                "The stock count approval reason must contain at least five characters.");
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
        StockCountHeader header = await RequireStockCountHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        if (header.Status != "submitted" || header.Version != request.ExpectedVersion)
        {
            throw Conflict(
                "stock_count_changed",
                "The stock count changed or is not submitted. Reload and try again.");
        }

        IReadOnlyList<StockCountLineState> lines = await ReadStockCountLineStatesAsync(
            connection,
            transaction,
            id,
            cancellationToken);
        if (lines.Count == 0 || lines.Any(line => line.CountedQuantityBaseUnits is null))
        {
            throw Conflict(
                "stock_count_incomplete",
                "Every stock count line must be counted before approval.");
        }

        long positiveValue = 0;
        long negativeValue = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (StockCountLineState line in lines)
        {
            await EnsureShopBalanceAsync(
                connection,
                transaction,
                context.ShopId,
                line.ProductId,
                now,
                cancellationToken);
            BalanceSnapshot current = await ReadBalanceAsync(
                connection,
                transaction,
                context.ShopId,
                line.ProductId,
                cancellationToken);
            if (current.Version != line.StockVersionSnapshot ||
                current.QuantityBaseUnits != line.SystemQuantityBaseUnits)
            {
                throw Conflict(
                    "stock_changed_during_count",
                    $"Stock for {line.ProductName} changed after the count started. Start a new count.");
            }

            long counted = line.CountedQuantityBaseUnits!.Value;
            long variance = counted - line.SystemQuantityBaseUnits;
            if (variance == 0)
            {
                continue;
            }
            long value = checked(Math.Abs(variance) * line.UnitCostMinor);
            if (variance > 0)
            {
                positiveValue = checked(positiveValue + value);
            }
            else
            {
                negativeValue = checked(negativeValue + value);
            }

            await UpdateShopBalanceAsync(
                connection,
                transaction,
                context.ShopId,
                line.ProductId,
                counted,
                current.Version,
                now,
                cancellationToken);
            await UpdateLegacyBalanceAsync(
                connection,
                transaction,
                line.ProductId,
                variance,
                now,
                cancellationToken);
            await ShopInventoryService.InsertMovementAsync(
                connection,
                transaction,
                context.ShopId,
                line.ProductId,
                "stocktake",
                variance,
                counted,
                value,
                "stock_count",
                id,
                $"Stock count {header.Number}: {reason}",
                user.Id,
                user.Id,
                now,
                cancellationToken);
        }

        string? journalId = null;
        if (positiveValue > 0 || negativeValue > 0)
        {
            string journalDate = DateOnly.FromDateTime(DateTime.UtcNow)
                .ToString("yyyy-MM-dd");
            await EnsureOpenPeriodAsync(
                connection,
                transaction,
                context.OrganizationId,
                journalDate,
                cancellationToken);
            journalId = await InsertStockCountJournalAsync(
                connection,
                transaction,
                context,
                id,
                header.Number,
                journalDate,
                positiveValue,
                negativeValue,
                user.Id,
                now,
                cancellationToken);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE procurement_stock_counts
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
                    "stock_count_changed",
                    "The stock count changed. Reload and try again.");
            }
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "procurement.stock_count.approved",
            "stock_count",
            id,
            new
            {
                header.Number,
                reason,
                positiveVarianceValueMinor = positiveValue,
                negativeVarianceValueMinor = negativeValue,
                journalId
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetStockCountAsync(user, context, id, cancellationToken);
    }

    public async Task<ProcurementSummaryRecord> GetProcurementSummaryAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? requestedFromDate,
        string? requestedToDate,
        CancellationToken cancellationToken = default)
    {
        string fromDate = string.IsNullOrWhiteSpace(requestedFromDate)
            ? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)).ToString("yyyy-MM-dd")
            : NormalizeDate(requestedFromDate, "invalid_from_date");
        string toDate = string.IsNullOrWhiteSpace(requestedToDate)
            ? DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")
            : NormalizeDate(requestedToDate, "invalid_to_date");
        if (string.CompareOrdinal(fromDate, toDate) > 0)
        {
            throw Validation("invalid_report_period", "The report start date cannot be after the end date.");
        }

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
            (SELECT COUNT(1)
             FROM procurement_purchase_orders
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND order_date BETWEEN $fromDate AND $toDate),
            (SELECT COALESCE(SUM(total_minor), 0)
             FROM procurement_purchase_orders
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND status IN ('approved', 'partially_received', 'received')
               AND order_date BETWEEN $fromDate AND $toDate),
            (SELECT COUNT(1)
             FROM procurement_goods_receipts
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND substr(received_at_utc, 1, 10) BETWEEN $fromDate AND $toDate),
            (SELECT COALESCE(SUM(total_minor), 0)
             FROM procurement_goods_receipts
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND status = 'posted'
               AND substr(received_at_utc, 1, 10) BETWEEN $fromDate AND $toDate),
            (SELECT COALESCE(SUM(landed_cost_minor), 0)
             FROM procurement_goods_receipts
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND status = 'posted'
               AND substr(received_at_utc, 1, 10) BETWEEN $fromDate AND $toDate),
            (SELECT COUNT(1)
             FROM procurement_supplier_returns
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND substr(returned_at_utc, 1, 10) BETWEEN $fromDate AND $toDate),
            (SELECT COALESCE(SUM(total_minor), 0)
             FROM procurement_supplier_returns
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND status = 'posted'
               AND substr(returned_at_utc, 1, 10) BETWEEN $fromDate AND $toDate),
            (SELECT COUNT(1)
             FROM procurement_purchase_orders
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND status IN ('submitted', 'approved', 'partially_received')),
            (SELECT COUNT(1)
             FROM procurement_purchase_orders
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND status IN ('submitted', 'approved', 'partially_received')
               AND expected_date IS NOT NULL AND expected_date < date('now')),
            (SELECT COUNT(1)
             FROM procurement_reorder_recommendations
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND suggested_order_base_units > 0),
            (SELECT COUNT(1)
             FROM procurement_expiry_alerts
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND days_to_expiry BETWEEN 0 AND 90);
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$fromDate", fromDate);
        command.Parameters.AddWithValue("$toDate", toDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Conflict("procurement_report_failed", "The procurement summary could not be calculated.");
        }
        return new ProcurementSummaryRecord(
            fromDate, toDate,
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
            reader.GetInt64(9), reader.GetInt64(10));
    }

    private static async Task<StockCountRecord> ReadStockCountAsync(
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
            count_header.id, count_header.stock_count_number,
            count_header.organization_id, count_header.shop_id, shop.code,
            count_header.status, count_header.notes, count_header.version,
            creator.display_name, submitter.display_name, approver.display_name,
            count_header.created_at_utc, count_header.submitted_at_utc,
            count_header.approved_at_utc
        FROM procurement_stock_counts AS count_header
        INNER JOIN shops AS shop ON shop.id = count_header.shop_id
        INNER JOIN users AS creator ON creator.id = count_header.created_by_user_id
        LEFT JOIN users AS submitter ON submitter.id = count_header.submitted_by_user_id
        LEFT JOIN users AS approver ON approver.id = count_header.approved_by_user_id
        WHERE count_header.id = $id
          AND count_header.organization_id = $organizationId
          AND count_header.shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "stock_count_not_found",
                "The stock count could not be found in the active branch.");
        }
        object[] values = new object[reader.FieldCount];
        reader.GetValues(values);
        await reader.DisposeAsync();

        IReadOnlyList<StockCountLineRecord> lines = await ReadStockCountLinesAsync(
            connection,
            transaction,
            id,
            cancellationToken);
        return ReadStockCount(values, lines);
    }

    private static StockCountRecord ReadStockCount(
        SqliteDataReader reader,
        IReadOnlyList<StockCountLineRecord> lines) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetInt32(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            DateTimeOffset.Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
            lines);

    private static StockCountRecord ReadStockCount(
        object[] values,
        IReadOnlyList<StockCountLineRecord> lines) =>
        new(
            Convert.ToString(values[0])!, Convert.ToString(values[1])!,
            Convert.ToString(values[2])!, Convert.ToString(values[3])!,
            Convert.ToString(values[4])!, Convert.ToString(values[5])!,
            Convert.ToString(values[6])!, Convert.ToInt32(values[7]),
            Convert.ToString(values[8])!,
            values[9] is DBNull ? null : Convert.ToString(values[9]),
            values[10] is DBNull ? null : Convert.ToString(values[10]),
            DateTimeOffset.Parse(Convert.ToString(values[11])!),
            values[12] is DBNull ? null : DateTimeOffset.Parse(Convert.ToString(values[12])!),
            values[13] is DBNull ? null : DateTimeOffset.Parse(Convert.ToString(values[13])!),
            lines);

    private static async Task<IReadOnlyList<StockCountLineRecord>> ReadStockCountLinesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string countId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            id, product_id, product_name_snapshot, sku_snapshot,
            system_quantity_base, counted_quantity_base,
            CASE WHEN counted_quantity_base IS NULL THEN NULL
                 ELSE counted_quantity_base - system_quantity_base END,
            unit_cost_minor,
            CASE WHEN counted_quantity_base IS NULL THEN 0
                 ELSE ABS(counted_quantity_base - system_quantity_base) * unit_cost_minor END
        FROM procurement_stock_count_lines
        WHERE stock_count_id = $countId
        ORDER BY product_name_snapshot COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$countId", countId);
        var lines = new List<StockCountLineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new StockCountLineRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetInt64(7), reader.GetInt64(8)));
        }
        return lines;
    }

    private static async Task<StockCountHeader> RequireStockCountHeaderAsync(
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
        SELECT id, stock_count_number, status, version, created_by_user_id
        FROM procurement_stock_counts
        WHERE id = $id
          AND organization_id = $organizationId
          AND shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "stock_count_not_found",
                "The stock count could not be found in the active branch.");
        }
        return new StockCountHeader(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt32(3), reader.GetString(4));
    }

    private static async Task<IReadOnlyList<StockCountLineState>> ReadStockCountLineStatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string countId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            id, product_id, product_name_snapshot,
            system_quantity_base, counted_quantity_base,
            unit_cost_minor, stock_version_snapshot
        FROM procurement_stock_count_lines
        WHERE stock_count_id = $countId;
        """;
        command.Parameters.AddWithValue("$countId", countId);
        var records = new List<StockCountLineState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new StockCountLineState(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.GetInt64(5), reader.GetInt32(6)));
        }
        return records;
    }

    private static async Task<string> InsertStockCountJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string stockCountId,
        string stockCountNumber,
        string journalDate,
        long positiveValue,
        long negativeValue,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string inventoryId = await ResolveSystemAccountAsync(
            connection, transaction, context.OrganizationId, "inventory", cancellationToken);
        string gainId = await ResolveSystemAccountAsync(
            connection, transaction, context.OrganizationId, "other_income", cancellationToken);
        string lossId = await ResolveSystemAccountAsync(
            connection, transaction, context.OrganizationId, "inventory_loss_damage", cancellationToken);
        long total = checked(positiveValue + negativeValue);
        string journalId = Guid.NewGuid().ToString("N");
        string journalNumber = await NextAccountingJournalNumberAsync(
            connection, transaction, context, now, cancellationToken);

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
            ($id, $organizationId, $shopId, $journalNumber, $journalDate,
             $currencyCode, $description, 'system', $sourceId, 'draft',
             $total, $total, 1, $userId, $now, $now);
            """;
            header.Parameters.AddWithValue("$id", journalId);
            header.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            header.Parameters.AddWithValue("$shopId", context.ShopId);
            header.Parameters.AddWithValue("$journalNumber", journalNumber);
            header.Parameters.AddWithValue("$journalDate", journalDate);
            header.Parameters.AddWithValue("$currencyCode", context.CurrencyCode);
            header.Parameters.AddWithValue("$description", $"Stock count {stockCountNumber}");
            header.Parameters.AddWithValue("$sourceId", $"stock_count:{stockCountId}");
            header.Parameters.AddWithValue("$total", total);
            header.Parameters.AddWithValue("$userId", userId);
            header.Parameters.AddWithValue("$now", now.ToString("O"));
            await header.ExecuteNonQueryAsync(cancellationToken);
        }

        int lineNumber = 1;
        if (positiveValue > 0)
        {
            await InsertJournalLineAsync(
                connection, transaction, journalId, lineNumber++, inventoryId,
                context.ShopId, positiveValue, 0,
                $"Inventory gain on {stockCountNumber}", cancellationToken);
            await InsertJournalLineAsync(
                connection, transaction, journalId, lineNumber++, gainId,
                context.ShopId, 0, positiveValue,
                $"Stock count gain on {stockCountNumber}", cancellationToken);
        }
        if (negativeValue > 0)
        {
            await InsertJournalLineAsync(
                connection, transaction, journalId, lineNumber++, lossId,
                context.ShopId, negativeValue, 0,
                $"Inventory loss on {stockCountNumber}", cancellationToken);
            await InsertJournalLineAsync(
                connection, transaction, journalId, lineNumber, inventoryId,
                context.ShopId, 0, negativeValue,
                $"Inventory shortage on {stockCountNumber}", cancellationToken);
        }

        await using var post = connection.CreateCommand();
        post.Transaction = transaction;
        post.CommandText =
        """
        UPDATE accounting_journals
        SET status = 'posted', posted_by_user_id = $userId,
            posted_at_utc = $now, updated_at_utc = $now,
            version = version + 1
        WHERE id = $id AND status = 'draft';
        """;
        post.Parameters.AddWithValue("$userId", userId);
        post.Parameters.AddWithValue("$now", now.ToString("O"));
        post.Parameters.AddWithValue("$id", journalId);
        if (await post.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "stock_count_journal_failed",
                "The stock count accounting journal could not be posted.");
        }
        return journalId;
    }

    private static async Task InsertJournalLineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string journalId,
        int lineNumber,
        string accountId,
        string shopId,
        long debitMinor,
        long creditMinor,
        string description,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO accounting_journal_lines
        (journal_id, line_number, account_id, shop_id,
         debit_minor, credit_minor, description)
        VALUES
        ($journalId, $lineNumber, $accountId, $shopId,
         $debit, $credit, $description);
        """;
        command.Parameters.AddWithValue("$journalId", journalId);
        command.Parameters.AddWithValue("$lineNumber", lineNumber);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$debit", debitMinor);
        command.Parameters.AddWithValue("$credit", creditMinor);
        command.Parameters.AddWithValue("$description", description);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record StockCountHeader(
        string Id,
        string Number,
        string Status,
        int Version,
        string CreatedByUserId);

    private sealed record StockCountLineState(
        string Id,
        string ProductId,
        string ProductName,
        long SystemQuantityBaseUnits,
        long? CountedQuantityBaseUnits,
        long UnitCostMinor,
        int StockVersionSnapshot);
}
