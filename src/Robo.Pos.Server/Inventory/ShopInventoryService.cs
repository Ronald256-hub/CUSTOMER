using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Inventory;

public sealed record ShopStockMovementRecord(
    long Id,
    string ShopId,
    string ProductId,
    string ProductName,
    string MovementType,
    long QuantityDeltaBaseUnits,
    long BalanceAfterBaseUnits,
    long CostValueMinor,
    string ReferenceType,
    string ReferenceId,
    string Reason,
    string PerformedByDisplayName,
    DateTimeOffset OccurredAtUtc);

public sealed class ShopInventoryService
{
    private static readonly HashSet<string> AdjustmentMovementTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "adjustment",
            "stocktake",
            "damage",
            "expiry",
            "spillage"
        };

    private readonly DatabaseBootstrap _database;
    private readonly InventoryService _legacyInventory;

    public ShopInventoryService(
        DatabaseBootstrap database,
        InventoryService legacyInventory)
    {
        _database = database;
        _legacyInventory = legacyInventory;
    }

    public async Task<IReadOnlyList<ProductCatalogItem>> ListProductsAsync(
        string shopId,
        string? search,
        bool includeInactive,
        bool includeCostPrice,
        CancellationToken cancellationToken = default)
    {
        string searchValue = search?.Trim() ?? string.Empty;

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            p.id,
            p.category_id,
            c.name,
            p.sku,
            p.barcode,
            p.name,
            p.description,
            p.product_type,
            p.stock_unit,
            p.sale_unit,
            p.bottle_volume_ml,
            p.glass_size_ml,
            p.units_per_crate,
            p.cost_price_minor,
            p.selling_price_minor,
            p.low_stock_threshold,
            p.allow_negative_stock,
            p.track_expiry,
            p.is_active,
            p.version,
            COALESCE(sb.quantity_base_units, 0),
            COALESCE(sb.reserved_base_units, 0),
            COALESCE(sb.version, 1)
        FROM products AS p
        LEFT JOIN categories AS c
            ON c.id = p.category_id
        LEFT JOIN shop_stock_balances AS sb
            ON sb.product_id = p.id
           AND sb.shop_id = $shopId
        WHERE ($includeInactive = 1 OR p.is_active = 1)
          AND
          (
              $search = ''
              OR p.name LIKE '%' || $search || '%' COLLATE NOCASE
              OR p.sku LIKE '%' || $search || '%' COLLATE NOCASE
              OR COALESCE(p.barcode, '') LIKE '%' || $search || '%' COLLATE NOCASE
          )
        ORDER BY p.name COLLATE NOCASE, p.sku COLLATE NOCASE
        LIMIT 500;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue(
            "$includeInactive",
            includeInactive ? 1 : 0);
        command.Parameters.AddWithValue("$search", searchValue);

        var products = new List<ProductCatalogItem>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            long quantity = reader.GetInt64(20);
            long reserved = reader.GetInt64(21);
            long available = quantity - reserved;
            long threshold = reader.GetInt64(15);

            products.Add(new ProductCatalogItem(
                Id: reader.GetString(0),
                CategoryId: reader.IsDBNull(1) ? null : reader.GetString(1),
                CategoryName: reader.IsDBNull(2) ? null : reader.GetString(2),
                Sku: reader.GetString(3),
                Barcode: reader.IsDBNull(4) ? null : reader.GetString(4),
                Name: reader.GetString(5),
                Description: reader.GetString(6),
                ProductType: reader.GetString(7),
                StockUnit: reader.GetString(8),
                SaleUnit: reader.GetString(9),
                BottleVolumeMl: GetNullableInt(reader, 10),
                GlassSizeMl: GetNullableInt(reader, 11),
                UnitsPerCrate: GetNullableInt(reader, 12),
                CostPriceMinor: includeCostPrice ? reader.GetInt64(13) : null,
                SellingPriceMinor: reader.GetInt64(14),
                LowStockThreshold: threshold,
                QuantityBaseUnits: quantity,
                ReservedBaseUnits: reserved,
                AvailableBaseUnits: available,
                IsLowStock: available <= threshold,
                AllowNegativeStock: reader.GetInt32(16) == 1,
                TrackExpiry: reader.GetInt32(17) == 1,
                IsActive: reader.GetInt32(18) == 1,
                Version: reader.GetInt32(19),
                StockVersion: reader.GetInt32(22)));
        }

        return products;
    }

    public async Task<ProductCatalogItem> CreateProductAsync(
        AuthenticatedUser administrator,
        string shopId,
        CreateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        ProductCatalogItem created =
            await _legacyInventory.CreateProductAsync(
                administrator,
                request,
                cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        await using (var initialize = connection.CreateCommand())
        {
            initialize.Transaction = transaction;
            initialize.CommandText =
            """
            INSERT OR IGNORE INTO shop_stock_balances
            (
                shop_id,
                product_id,
                quantity_base_units,
                reserved_base_units,
                version,
                updated_at_utc
            )
            SELECT
                id,
                $productId,
                CASE WHEN id = $shopId THEN $openingStock ELSE 0 END,
                0,
                1,
                $updatedAtUtc
            FROM shops
            WHERE is_active = 1;

            UPDATE shop_stock_balances
            SET quantity_base_units = $openingStock,
                reserved_base_units = 0,
                version = CASE WHEN quantity_base_units = $openingStock THEN version ELSE version + 1 END,
                updated_at_utc = $updatedAtUtc
            WHERE shop_id = $shopId
              AND product_id = $productId;
            """;
            initialize.Parameters.AddWithValue("$productId", created.Id);
            initialize.Parameters.AddWithValue("$shopId", shopId);
            initialize.Parameters.AddWithValue(
                "$openingStock",
                request.OpeningStockBaseUnits);
            initialize.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            await initialize.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var classifyMovement = connection.CreateCommand())
        {
            classifyMovement.Transaction = transaction;
            classifyMovement.CommandText =
            """
            UPDATE stock_movements
            SET shop_id = $shopId
            WHERE reference_type = 'product'
              AND reference_id = $productId
              AND movement_type = 'opening'
              AND shop_id IS NULL;
            """;
            classifyMovement.Parameters.AddWithValue("$shopId", shopId);
            classifyMovement.Parameters.AddWithValue("$productId", created.Id);
            await classifyMovement.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditAsync(
            connection,
            transaction,
            administrator,
            "inventory.product.shop_initialized",
            "product",
            created.Id,
            new
            {
                shopId,
                openingStockBaseUnits = request.OpeningStockBaseUnits
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return (await ListProductsAsync(
                shopId,
                created.Sku,
                includeInactive: true,
                includeCostPrice: true,
                cancellationToken))
            .Single(product => product.Id == created.Id);
    }

    public async Task<StockAdjustmentRecord> AdjustStockAsync(
        AuthenticatedUser administrator,
        string shopId,
        string productId,
        StockAdjustmentRequest request,
        CancellationToken cancellationToken = default)
    {
        string movementType =
            request.MovementType?.Trim().ToLowerInvariant() ?? string.Empty;
        string reason = request.Reason?.Trim() ?? string.Empty;

        if (!AdjustmentMovementTypes.Contains(movementType))
        {
            throw Validation(
                "invalid_stock_movement_type",
                "Use adjustment, stocktake, damage, expiry or spillage.");
        }

        if (reason.Length is < 5 or > 250)
        {
            throw Validation(
                "invalid_stock_adjustment_reason",
                "A stock-adjustment reason containing 5 to 250 characters is required.");
        }

        bool hasDelta = request.QuantityDeltaBaseUnits is not null;
        bool hasNewQuantity = request.NewQuantityBaseUnits is not null;
        if (hasDelta == hasNewQuantity)
        {
            throw Validation(
                "invalid_stock_quantity",
                "Provide either a quantity change or a new stock count, but not both.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        await EnsureBalanceAsync(
            connection,
            transaction,
            shopId,
            productId,
            cancellationToken);

        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText =
        """
        SELECT
            p.name,
            p.allow_negative_stock,
            p.cost_price_minor,
            sb.quantity_base_units,
            sb.version
        FROM products AS p
        INNER JOIN shop_stock_balances AS sb
            ON sb.product_id = p.id
           AND sb.shop_id = $shopId
        WHERE p.id = $productId;
        """;
        find.Parameters.AddWithValue("$shopId", shopId);
        find.Parameters.AddWithValue("$productId", productId);

        await using var reader =
            await find.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "product_not_found",
                "The product could not be found.");
        }

        string productName = reader.GetString(0);
        bool allowNegative = reader.GetInt32(1) == 1;
        long costPrice = reader.GetInt64(2);
        long currentBalance = reader.GetInt64(3);
        int currentVersion = reader.GetInt32(4);
        await reader.DisposeAsync();

        if (currentVersion != request.ExpectedStockVersion)
        {
            throw Conflict(
                "stale_stock_version",
                "Stock was changed by another user. Reload before adjusting it.");
        }

        long delta = request.QuantityDeltaBaseUnits
            ?? request.NewQuantityBaseUnits!.Value - currentBalance;
        if (delta == 0)
        {
            throw Validation(
                "stock_unchanged",
                "The adjustment does not change the stock balance.");
        }

        long newBalance = checked(currentBalance + delta);
        if (!allowNegative && newBalance < 0)
        {
            throw Conflict(
                "insufficient_stock",
                "This adjustment would make the shop stock balance negative.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE shop_stock_balances
        SET quantity_base_units = $newBalance,
            version = version + 1,
            updated_at_utc = $updatedAtUtc
        WHERE shop_id = $shopId
          AND product_id = $productId
          AND version = $expectedVersion;
        """;
        update.Parameters.AddWithValue("$newBalance", newBalance);
        update.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        update.Parameters.AddWithValue("$shopId", shopId);
        update.Parameters.AddWithValue("$productId", productId);
        update.Parameters.AddWithValue(
            "$expectedVersion",
            request.ExpectedStockVersion);

        int affected = await update.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw Conflict(
                "stale_stock_version",
                "Stock was changed by another user. Reload before adjusting it.");
        }

        await InsertMovementAsync(
            connection,
            transaction,
            shopId,
            productId,
            movementType,
            delta,
            newBalance,
            costPrice,
            "product",
            productId,
            reason,
            administrator.Id,
            administrator.Id,
            now,
            cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            administrator,
            "inventory.shop_stock.adjusted",
            "product",
            productId,
            new
            {
                shopId,
                productName,
                movementType,
                previousBalance = currentBalance,
                quantityDelta = delta,
                newBalance,
                reason
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new StockAdjustmentRecord(
            productId,
            productName,
            movementType,
            delta,
            newBalance,
            request.ExpectedStockVersion + 1,
            now);
    }

    public async Task<IReadOnlyList<ShopStockMovementRecord>>
        ListMovementsAsync(
            string shopId,
            string? productId,
            int requestedLimit,
            CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(requestedLimit, 1, 500);
        string normalizedProductId = productId?.Trim() ?? string.Empty;

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            movement.id,
            movement.shop_id,
            movement.product_id,
            product.name,
            movement.movement_type,
            movement.quantity_delta_base,
            movement.balance_after_base,
            movement.cost_value_minor,
            movement.reference_type,
            movement.reference_id,
            movement.reason,
            COALESCE(user.display_name, ''),
            movement.occurred_at_utc
        FROM stock_movements AS movement
        INNER JOIN products AS product
            ON product.id = movement.product_id
        LEFT JOIN users AS user
            ON user.id = movement.performed_by_user_id
        WHERE movement.shop_id = $shopId
          AND ($productId = '' OR movement.product_id = $productId)
        ORDER BY movement.occurred_at_utc DESC, movement.id DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$productId", normalizedProductId);
        command.Parameters.AddWithValue("$limit", limit);

        var movements = new List<ShopStockMovementRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            movements.Add(new ShopStockMovementRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                DateTimeOffset.Parse(reader.GetString(12))));
        }

        return movements;
    }

    private static async Task EnsureBalanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string shopId,
        string productId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT OR IGNORE INTO shop_stock_balances
        (
            shop_id,
            product_id,
            quantity_base_units,
            reserved_base_units,
            version,
            updated_at_utc
        )
        SELECT
            $shopId,
            id,
            0,
            0,
            1,
            $updatedAtUtc
        FROM products
        WHERE id = $productId;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$productId", productId);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task InsertMovementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string shopId,
        string productId,
        string movementType,
        long quantityDelta,
        long balanceAfter,
        long costValue,
        string referenceType,
        string referenceId,
        string reason,
        string performedByUserId,
        string? approvedByUserId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO stock_movements
        (
            shop_id,
            product_id,
            movement_type,
            quantity_delta_base,
            balance_after_base,
            cost_value_minor,
            reference_type,
            reference_id,
            reason,
            performed_by_user_id,
            approved_by_user_id,
            occurred_at_utc
        )
        VALUES
        (
            $shopId,
            $productId,
            $movementType,
            $quantityDelta,
            $balanceAfter,
            $costValue,
            $referenceType,
            $referenceId,
            $reason,
            $performedByUserId,
            $approvedByUserId,
            $occurredAtUtc
        );
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$productId", productId);
        command.Parameters.AddWithValue("$movementType", movementType);
        command.Parameters.AddWithValue("$quantityDelta", quantityDelta);
        command.Parameters.AddWithValue("$balanceAfter", balanceAfter);
        command.Parameters.AddWithValue("$costValue", costValue);
        command.Parameters.AddWithValue("$referenceType", referenceType);
        command.Parameters.AddWithValue("$referenceId", referenceId);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$performedByUserId", performedByUserId);
        command.Parameters.AddWithValue(
            "$approvedByUserId",
            approvedByUserId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$occurredAtUtc",
            occurredAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
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
            occurred_at_utc,
            user_id,
            username,
            event_type,
            entity_type,
            entity_id,
            success,
            details_json,
            client_ip_hash
        )
        VALUES
        (
            $occurredAtUtc,
            $userId,
            $username,
            $eventType,
            $entityType,
            $entityId,
            1,
            $detailsJson,
            NULL
        );
        """;
        command.Parameters.AddWithValue(
            "$occurredAtUtc",
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

    private static int? GetNullableInt(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetInt32(ordinal);

    private static InventoryException Validation(
        string code,
        string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    private static InventoryException Conflict(
        string code,
        string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private static InventoryException NotFound(
        string code,
        string message) =>
        new(StatusCodes.Status404NotFound, code, message);
}
