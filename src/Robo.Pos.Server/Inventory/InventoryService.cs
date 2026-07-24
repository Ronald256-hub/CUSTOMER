using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Inventory;

public sealed class InventoryService
{
    private static readonly HashSet<string> ProductTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "standard",
            "bottle",
            "crate",
            "short_glass"
        };

    private static readonly HashSet<string> StockUnits =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "unit",
            "bottle",
            "crate",
            "ml"
        };

    private static readonly HashSet<string> SaleUnits =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "unit",
            "bottle",
            "crate",
            "glass"
        };

    private static readonly HashSet<string>
        AdjustmentMovementTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "adjustment",
            "stocktake",
            "damage",
            "expiry",
            "spillage"
        };

    private readonly DatabaseBootstrap _database;

    public InventoryService(
        DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<ProductCatalogItem>>
        ListProductsAsync(
            string? search,
            bool includeInactive,
            bool includeCostPrice,
            CancellationToken cancellationToken = default)
    {
        string searchValue =
            search?.Trim() ?? string.Empty;

        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            connection.CreateCommand();

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
        LEFT JOIN stock_balances AS sb
            ON sb.product_id = p.id
        WHERE
            ($includeInactive = 1 OR p.is_active = 1)
            AND
            (
                $search = ''
                OR p.name LIKE '%' || $search || '%'
                    COLLATE NOCASE
                OR p.sku LIKE '%' || $search || '%'
                    COLLATE NOCASE
                OR COALESCE(p.barcode, '')
                    LIKE '%' || $search || '%'
                    COLLATE NOCASE
            )
        ORDER BY
            p.name COLLATE NOCASE,
            p.sku COLLATE NOCASE
        LIMIT 500;
        """;

        command.Parameters.AddWithValue(
            "$includeInactive",
            includeInactive ? 1 : 0);

        command.Parameters.AddWithValue(
            "$search",
            searchValue);

        var products =
            new List<ProductCatalogItem>();

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            long quantity =
                reader.GetInt64(20);

            long reserved =
                reader.GetInt64(21);

            long available =
                quantity - reserved;

            long threshold =
                reader.GetInt64(15);

            products.Add(
                new ProductCatalogItem(
                    Id: reader.GetString(0),
                    CategoryId:
                        reader.IsDBNull(1)
                            ? null
                            : reader.GetString(1),
                    CategoryName:
                        reader.IsDBNull(2)
                            ? null
                            : reader.GetString(2),
                    Sku: reader.GetString(3),
                    Barcode:
                        reader.IsDBNull(4)
                            ? null
                            : reader.GetString(4),
                    Name: reader.GetString(5),
                    Description:
                        reader.GetString(6),
                    ProductType:
                        reader.GetString(7),
                    StockUnit:
                        reader.GetString(8),
                    SaleUnit:
                        reader.GetString(9),
                    BottleVolumeMl:
                        GetNullableInt(reader, 10),
                    GlassSizeMl:
                        GetNullableInt(reader, 11),
                    UnitsPerCrate:
                        GetNullableInt(reader, 12),
                    CostPriceMinor:
                        includeCostPrice
                            ? reader.GetInt64(13)
                            : null,
                    SellingPriceMinor:
                        reader.GetInt64(14),
                    LowStockThreshold:
                        threshold,
                    QuantityBaseUnits:
                        quantity,
                    ReservedBaseUnits:
                        reserved,
                    AvailableBaseUnits:
                        available,
                    IsLowStock:
                        available <= threshold,
                    AllowNegativeStock:
                        reader.GetInt32(16) == 1,
                    TrackExpiry:
                        reader.GetInt32(17) == 1,
                    IsActive:
                        reader.GetInt32(18) == 1,
                    Version:
                        reader.GetInt32(19),
                    StockVersion:
                        reader.GetInt32(22)));
        }

        return products;
    }

    public async Task<CategoryRecord>
        CreateCategoryAsync(
            AuthenticatedUser administrator,
            CreateCategoryRequest request,
            CancellationToken cancellationToken = default)
    {
        string name =
            request.Name?.Trim() ?? string.Empty;

        string description =
            request.Description?.Trim()
            ?? string.Empty;

        if (name.Length is < 2 or > 100)
        {
            throw Validation(
                "invalid_category_name",
                "Category names must contain between 2 and 100 characters.");
        }

        if (description.Length > 500)
        {
            throw Validation(
                "category_description_too_long",
                "The category description cannot exceed 500 characters.");
        }

        string id =
            Guid.NewGuid().ToString("N");

        DateTimeOffset now =
            DateTimeOffset.UtcNow;

        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(
                cancellationToken);

        try
        {
            await using var command =
                connection.CreateCommand();

            command.Transaction =
                transaction;

            command.CommandText =
            """
            INSERT INTO categories
            (
                id,
                name,
                name_normalized,
                description,
                display_order,
                is_active,
                created_by_user_id,
                updated_by_user_id,
                created_at_utc,
                updated_at_utc
            )
            VALUES
            (
                $id,
                $name,
                $normalizedName,
                $description,
                $displayOrder,
                1,
                $userId,
                $userId,
                $createdAtUtc,
                $updatedAtUtc
            );
            """;

            command.Parameters.AddWithValue(
                "$id",
                id);

            command.Parameters.AddWithValue(
                "$name",
                name);

            command.Parameters.AddWithValue(
                "$normalizedName",
                name.ToUpperInvariant());

            command.Parameters.AddWithValue(
                "$description",
                description);

            command.Parameters.AddWithValue(
                "$displayOrder",
                request.DisplayOrder);

            command.Parameters.AddWithValue(
                "$userId",
                administrator.Id);

            command.Parameters.AddWithValue(
                "$createdAtUtc",
                now.ToString("O"));

            command.Parameters.AddWithValue(
                "$updatedAtUtc",
                now.ToString("O"));

            await command.ExecuteNonQueryAsync(
                cancellationToken);

            await WriteAuditAsync(
                connection,
                transaction,
                administrator,
                "inventory.category.created",
                "category",
                id,
                new
                {
                    name,
                    request.DisplayOrder
                },
                cancellationToken);

            await transaction.CommitAsync(
                cancellationToken);
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "category_already_exists",
                "A category with this name already exists.");
        }

        return new CategoryRecord(
            id,
            name,
            description,
            request.DisplayOrder,
            true);
    }

    public async Task<ProductCatalogItem>
        CreateProductAsync(
            AuthenticatedUser administrator,
            CreateProductRequest request,
            CancellationToken cancellationToken = default)
    {
        ValidateProduct(request);

        string id =
            Guid.NewGuid().ToString("N");

        string sku =
            request.Sku.Trim();

        string name =
            request.Name.Trim();

        string? categoryId =
            string.IsNullOrWhiteSpace(
                request.CategoryId)
                ? null
                : request.CategoryId.Trim();

        string? barcode =
            string.IsNullOrWhiteSpace(
                request.Barcode)
                ? null
                : request.Barcode.Trim();

        string description =
            request.Description?.Trim()
            ?? string.Empty;

        DateTimeOffset now =
            DateTimeOffset.UtcNow;

        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(
                cancellationToken);

        if (categoryId is not null)
        {
            await using var categoryCheck =
                connection.CreateCommand();

            categoryCheck.Transaction =
                transaction;

            categoryCheck.CommandText =
            """
            SELECT COUNT(1)
            FROM categories
            WHERE id = $categoryId
              AND is_active = 1;
            """;

            categoryCheck.Parameters.AddWithValue(
                "$categoryId",
                categoryId);

            int exists =
                Convert.ToInt32(
                    await categoryCheck
                        .ExecuteScalarAsync(
                            cancellationToken));

            if (exists == 0)
            {
                throw Validation(
                    "invalid_category",
                    "The selected category does not exist or is inactive.");
            }
        }

        try
        {
            await using var insertProduct =
                connection.CreateCommand();

            insertProduct.Transaction =
                transaction;

            insertProduct.CommandText =
            """
            INSERT INTO products
            (
                id,
                category_id,
                sku,
                barcode,
                name,
                description,
                product_type,
                stock_unit,
                sale_unit,
                bottle_volume_ml,
                glass_size_ml,
                units_per_crate,
                cost_price_minor,
                selling_price_minor,
                low_stock_threshold,
                allow_negative_stock,
                track_expiry,
                is_active,
                version,
                created_by_user_id,
                updated_by_user_id,
                created_at_utc,
                updated_at_utc
            )
            VALUES
            (
                $id,
                $categoryId,
                $sku,
                $barcode,
                $name,
                $description,
                $productType,
                $stockUnit,
                $saleUnit,
                $bottleVolumeMl,
                $glassSizeMl,
                $unitsPerCrate,
                $costPriceMinor,
                $sellingPriceMinor,
                $lowStockThreshold,
                $allowNegativeStock,
                $trackExpiry,
                1,
                1,
                $userId,
                $userId,
                $createdAtUtc,
                $updatedAtUtc
            );
            """;

            AddProductParameters(
                insertProduct,
                id,
                categoryId,
                sku,
                barcode,
                name,
                description,
                request,
                administrator.Id,
                now);

            await insertProduct
                .ExecuteNonQueryAsync(
                    cancellationToken);

            await using var insertBalance =
                connection.CreateCommand();

            insertBalance.Transaction =
                transaction;

            insertBalance.CommandText =
            """
            INSERT INTO stock_balances
            (
                product_id,
                quantity_base_units,
                reserved_base_units,
                version,
                updated_at_utc
            )
            VALUES
            (
                $productId,
                $quantity,
                0,
                1,
                $updatedAtUtc
            );
            """;

            insertBalance.Parameters.AddWithValue(
                "$productId",
                id);

            insertBalance.Parameters.AddWithValue(
                "$quantity",
                request.OpeningStockBaseUnits);

            insertBalance.Parameters.AddWithValue(
                "$updatedAtUtc",
                now.ToString("O"));

            await insertBalance
                .ExecuteNonQueryAsync(
                    cancellationToken);

            if (request.OpeningStockBaseUnits != 0)
            {
                await InsertStockMovementAsync(
                    connection,
                    transaction,
                    id,
                    "opening",
                    request.OpeningStockBaseUnits,
                    request.OpeningStockBaseUnits,
                    request.CostPriceMinor,
                    "product",
                    id,
                    "Opening stock",
                    administrator.Id,
                    administrator.Id,
                    now,
                    cancellationToken);
            }

            await WriteAuditAsync(
                connection,
                transaction,
                administrator,
                "inventory.product.created",
                "product",
                id,
                new
                {
                    sku,
                    name,
                    request.ProductType,
                    request.CostPriceMinor,
                    request.SellingPriceMinor,
                    request.OpeningStockBaseUnits
                },
                cancellationToken);

            await transaction.CommitAsync(
                cancellationToken);
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "product_identifier_conflict",
                "The SKU or barcode already belongs to another product.");
        }

        return (
            await ListProductsAsync(
                sku,
                includeInactive: true,
                includeCostPrice: true,
                cancellationToken))
            .Single(product =>
                product.Id == id);
    }

    public async Task<PriceChangeRecord>
        UpdatePricesAsync(
            AuthenticatedUser administrator,
            string productId,
            UpdateProductPriceRequest request,
            CancellationToken cancellationToken = default)
    {
        string reason =
            request.Reason?.Trim()
            ?? string.Empty;

        if (reason.Length is < 5 or > 250)
        {
            throw Validation(
                "invalid_price_change_reason",
                "A price-change reason containing 5 to 250 characters is required.");
        }

        if (request.CostPriceMinor < 0 ||
            request.SellingPriceMinor < 0)
        {
            throw Validation(
                "invalid_price",
                "Cost and selling prices cannot be negative.");
        }

        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(
                cancellationToken);

        await using var findProduct =
            connection.CreateCommand();

        findProduct.Transaction =
            transaction;

        findProduct.CommandText =
        """
        SELECT
            name,
            cost_price_minor,
            selling_price_minor,
            version
        FROM products
        WHERE id = $productId;
        """;

        findProduct.Parameters.AddWithValue(
            "$productId",
            productId);

        await using var reader =
            await findProduct
                .ExecuteReaderAsync(
                    cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            throw NotFound(
                "product_not_found",
                "The product could not be found.");
        }

        string productName =
            reader.GetString(0);

        long previousCost =
            reader.GetInt64(1);

        long previousSelling =
            reader.GetInt64(2);

        int currentVersion =
            reader.GetInt32(3);

        await reader.DisposeAsync();

        if (currentVersion !=
            request.ExpectedVersion)
        {
            throw Conflict(
                "stale_product_version",
                "The product was changed by another user. Reload it before saving.");
        }

        if (previousCost ==
                request.CostPriceMinor &&
            previousSelling ==
                request.SellingPriceMinor)
        {
            throw Validation(
                "price_unchanged",
                "Enter a different cost or selling price.");
        }

        DateTimeOffset now =
            DateTimeOffset.UtcNow;

        await using var history =
            connection.CreateCommand();

        history.Transaction =
            transaction;

        history.CommandText =
        """
        INSERT INTO product_price_history
        (
            product_id,
            previous_cost_minor,
            new_cost_minor,
            previous_selling_minor,
            new_selling_minor,
            reason,
            changed_by_user_id,
            changed_at_utc
        )
        VALUES
        (
            $productId,
            $previousCost,
            $newCost,
            $previousSelling,
            $newSelling,
            $reason,
            $userId,
            $changedAtUtc
        );
        """;

        history.Parameters.AddWithValue(
            "$productId",
            productId);

        history.Parameters.AddWithValue(
            "$previousCost",
            previousCost);

        history.Parameters.AddWithValue(
            "$newCost",
            request.CostPriceMinor);

        history.Parameters.AddWithValue(
            "$previousSelling",
            previousSelling);

        history.Parameters.AddWithValue(
            "$newSelling",
            request.SellingPriceMinor);

        history.Parameters.AddWithValue(
            "$reason",
            reason);

        history.Parameters.AddWithValue(
            "$userId",
            administrator.Id);

        history.Parameters.AddWithValue(
            "$changedAtUtc",
            now.ToString("O"));

        await history.ExecuteNonQueryAsync(
            cancellationToken);

        await using var update =
            connection.CreateCommand();

        update.Transaction =
            transaction;

        update.CommandText =
        """
        UPDATE products
        SET cost_price_minor = $costPrice,
            selling_price_minor = $sellingPrice,
            version = version + 1,
            updated_by_user_id = $userId,
            updated_at_utc = $updatedAtUtc
        WHERE id = $productId
          AND version = $expectedVersion;
        """;

        update.Parameters.AddWithValue(
            "$costPrice",
            request.CostPriceMinor);

        update.Parameters.AddWithValue(
            "$sellingPrice",
            request.SellingPriceMinor);

        update.Parameters.AddWithValue(
            "$userId",
            administrator.Id);

        update.Parameters.AddWithValue(
            "$updatedAtUtc",
            now.ToString("O"));

        update.Parameters.AddWithValue(
            "$productId",
            productId);

        update.Parameters.AddWithValue(
            "$expectedVersion",
            request.ExpectedVersion);

        int affected =
            await update.ExecuteNonQueryAsync(
                cancellationToken);

        if (affected != 1)
        {
            throw Conflict(
                "stale_product_version",
                "The product was changed by another user. Reload it before saving.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            administrator,
            "inventory.product.price_changed",
            "product",
            productId,
            new
            {
                productName,
                previousCost,
                newCost =
                    request.CostPriceMinor,
                previousSelling,
                newSelling =
                    request.SellingPriceMinor,
                reason
            },
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);

        return new PriceChangeRecord(
            productId,
            productName,
            previousCost,
            request.CostPriceMinor,
            previousSelling,
            request.SellingPriceMinor,
            request.ExpectedVersion + 1,
            now);
    }

    public async Task<StockAdjustmentRecord>
        AdjustStockAsync(
            AuthenticatedUser administrator,
            string productId,
            StockAdjustmentRequest request,
            CancellationToken cancellationToken = default)
    {
        string movementType =
            request.MovementType?.Trim()
                .ToLowerInvariant()
            ?? string.Empty;

        string reason =
            request.Reason?.Trim()
            ?? string.Empty;

        if (!AdjustmentMovementTypes.Contains(
                movementType))
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

        bool hasDelta =
            request.QuantityDeltaBaseUnits
                is not null;

        bool hasNewQuantity =
            request.NewQuantityBaseUnits
                is not null;

        if (hasDelta == hasNewQuantity)
        {
            throw Validation(
                "invalid_stock_quantity",
                "Provide either a quantity change or a new stock count, but not both.");
        }

        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(
                cancellationToken);

        await using var find =
            connection.CreateCommand();

        find.Transaction =
            transaction;

        find.CommandText =
        """
        SELECT
            p.name,
            p.allow_negative_stock,
            p.cost_price_minor,
            sb.quantity_base_units,
            sb.version
        FROM products AS p
        INNER JOIN stock_balances AS sb
            ON sb.product_id = p.id
        WHERE p.id = $productId;
        """;

        find.Parameters.AddWithValue(
            "$productId",
            productId);

        await using var reader =
            await find.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            throw NotFound(
                "product_not_found",
                "The product could not be found.");
        }

        string productName =
            reader.GetString(0);

        bool allowNegative =
            reader.GetInt32(1) == 1;

        long costPrice =
            reader.GetInt64(2);

        long currentBalance =
            reader.GetInt64(3);

        int currentVersion =
            reader.GetInt32(4);

        await reader.DisposeAsync();

        if (currentVersion !=
            request.ExpectedStockVersion)
        {
            throw Conflict(
                "stale_stock_version",
                "Stock was changed by another user. Reload before adjusting it.");
        }

        long delta =
            request.QuantityDeltaBaseUnits
            ?? (
                request.NewQuantityBaseUnits!.Value
                - currentBalance
            );

        if (delta == 0)
        {
            throw Validation(
                "stock_unchanged",
                "The adjustment does not change the stock balance.");
        }

        long newBalance =
            checked(currentBalance + delta);

        if (!allowNegative &&
            newBalance < 0)
        {
            throw Conflict(
                "insufficient_stock",
                "This adjustment would make the stock balance negative.");
        }

        DateTimeOffset now =
            DateTimeOffset.UtcNow;

        await using var update =
            connection.CreateCommand();

        update.Transaction =
            transaction;

        update.CommandText =
        """
        UPDATE stock_balances
        SET quantity_base_units = $newBalance,
            version = version + 1,
            updated_at_utc = $updatedAtUtc
        WHERE product_id = $productId
          AND version = $expectedVersion;
        """;

        update.Parameters.AddWithValue(
            "$newBalance",
            newBalance);

        update.Parameters.AddWithValue(
            "$updatedAtUtc",
            now.ToString("O"));

        update.Parameters.AddWithValue(
            "$productId",
            productId);

        update.Parameters.AddWithValue(
            "$expectedVersion",
            request.ExpectedStockVersion);

        int affected =
            await update.ExecuteNonQueryAsync(
                cancellationToken);

        if (affected != 1)
        {
            throw Conflict(
                "stale_stock_version",
                "Stock was changed by another user. Reload before adjusting it.");
        }

        await InsertStockMovementAsync(
            connection,
            transaction,
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
            "inventory.stock.adjusted",
            "product",
            productId,
            new
            {
                productName,
                movementType,
                previousBalance =
                    currentBalance,
                quantityDelta =
                    delta,
                newBalance,
                reason
            },
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);

        return new StockAdjustmentRecord(
            productId,
            productName,
            movementType,
            delta,
            newBalance,
            request.ExpectedStockVersion + 1,
            now);
    }

    private static void ValidateProduct(
        CreateProductRequest request)
    {
        if (string.IsNullOrWhiteSpace(
                request.Sku) ||
            request.Sku.Trim().Length > 50)
        {
            throw Validation(
                "invalid_sku",
                "A SKU containing no more than 50 characters is required.");
        }

        if (string.IsNullOrWhiteSpace(
                request.Name) ||
            request.Name.Trim().Length > 150)
        {
            throw Validation(
                "invalid_product_name",
                "A product name containing no more than 150 characters is required.");
        }

        if ((request.Description?.Length ?? 0)
            > 1000)
        {
            throw Validation(
                "product_description_too_long",
                "The product description cannot exceed 1,000 characters.");
        }

        if (!ProductTypes.Contains(
                request.ProductType))
        {
            throw Validation(
                "invalid_product_type",
                "The selected product type is invalid.");
        }

        if (!StockUnits.Contains(
                request.StockUnit))
        {
            throw Validation(
                "invalid_stock_unit",
                "The selected stock unit is invalid.");
        }

        if (!SaleUnits.Contains(
                request.SaleUnit))
        {
            throw Validation(
                "invalid_sale_unit",
                "The selected sale unit is invalid.");
        }

        if (request.CostPriceMinor < 0 ||
            request.SellingPriceMinor < 0 ||
            request.LowStockThreshold < 0 ||
            request.OpeningStockBaseUnits < 0)
        {
            throw Validation(
                "invalid_product_values",
                "Prices, stock and low-stock thresholds cannot be negative.");
        }

        if (string.Equals(
                request.ProductType,
                "short_glass",
                StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(
                    request.StockUnit,
                    "ml",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    request.SaleUnit,
                    "glass",
                    StringComparison.OrdinalIgnoreCase) ||
                request.BottleVolumeMl is null or <= 0 ||
                request.GlassSizeMl is null or <= 0)
            {
                throw Validation(
                    "invalid_short_glass_configuration",
                    "Short-glass products require millilitre stock, glass sales, bottle volume and glass size.");
            }

            if (request.GlassSizeMl >
                request.BottleVolumeMl)
            {
                throw Validation(
                    "glass_size_exceeds_bottle",
                    "The glass size cannot exceed the bottle volume.");
            }
        }

        if (string.Equals(
                request.ProductType,
                "crate",
                StringComparison.OrdinalIgnoreCase) &&
            request.UnitsPerCrate is null or <= 0)
        {
            throw Validation(
                "units_per_crate_required",
                "Crate products require the number of units per crate.");
        }
    }

    private static void AddProductParameters(
        SqliteCommand command,
        string id,
        string? categoryId,
        string sku,
        string? barcode,
        string name,
        string description,
        CreateProductRequest request,
        string userId,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue(
            "$id",
            id);

        command.Parameters.AddWithValue(
            "$categoryId",
            categoryId ??
            (object)DBNull.Value);

        command.Parameters.AddWithValue(
            "$sku",
            sku);

        command.Parameters.AddWithValue(
            "$barcode",
            barcode ??
            (object)DBNull.Value);

        command.Parameters.AddWithValue(
            "$name",
            name);

        command.Parameters.AddWithValue(
            "$description",
            description);

        command.Parameters.AddWithValue(
            "$productType",
            request.ProductType
                .Trim()
                .ToLowerInvariant());

        command.Parameters.AddWithValue(
            "$stockUnit",
            request.StockUnit
                .Trim()
                .ToLowerInvariant());

        command.Parameters.AddWithValue(
            "$saleUnit",
            request.SaleUnit
                .Trim()
                .ToLowerInvariant());

        command.Parameters.AddWithValue(
            "$bottleVolumeMl",
            request.BottleVolumeMl
            ?? (object)DBNull.Value);

        command.Parameters.AddWithValue(
            "$glassSizeMl",
            request.GlassSizeMl
            ?? (object)DBNull.Value);

        command.Parameters.AddWithValue(
            "$unitsPerCrate",
            request.UnitsPerCrate
            ?? (object)DBNull.Value);

        command.Parameters.AddWithValue(
            "$costPriceMinor",
            request.CostPriceMinor);

        command.Parameters.AddWithValue(
            "$sellingPriceMinor",
            request.SellingPriceMinor);

        command.Parameters.AddWithValue(
            "$lowStockThreshold",
            request.LowStockThreshold);

        command.Parameters.AddWithValue(
            "$allowNegativeStock",
            request.AllowNegativeStock
                ? 1
                : 0);

        command.Parameters.AddWithValue(
            "$trackExpiry",
            request.TrackExpiry
                ? 1
                : 0);

        command.Parameters.AddWithValue(
            "$userId",
            userId);

        command.Parameters.AddWithValue(
            "$createdAtUtc",
            now.ToString("O"));

        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            now.ToString("O"));
    }

    private static int? GetNullableInt(
        SqliteDataReader reader,
        int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : reader.GetInt32(ordinal);
    }

    private static async Task
        InsertStockMovementAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string productId,
            string movementType,
            long quantityDelta,
            long balanceAfter,
            long costValue,
            string referenceType,
            string referenceId,
            string reason,
            string performedByUserId,
            string approvedByUserId,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
        """
        INSERT INTO stock_movements
        (
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

        command.Parameters.AddWithValue(
            "$productId",
            productId);

        command.Parameters.AddWithValue(
            "$movementType",
            movementType);

        command.Parameters.AddWithValue(
            "$quantityDelta",
            quantityDelta);

        command.Parameters.AddWithValue(
            "$balanceAfter",
            balanceAfter);

        command.Parameters.AddWithValue(
            "$costValue",
            costValue);

        command.Parameters.AddWithValue(
            "$referenceType",
            referenceType);

        command.Parameters.AddWithValue(
            "$referenceId",
            referenceId);

        command.Parameters.AddWithValue(
            "$reason",
            reason);

        command.Parameters.AddWithValue(
            "$performedByUserId",
            performedByUserId);

        command.Parameters.AddWithValue(
            "$approvedByUserId",
            approvedByUserId);

        command.Parameters.AddWithValue(
            "$occurredAtUtc",
            occurredAtUtc.ToString("O"));

        await command.ExecuteNonQueryAsync(
            cancellationToken);
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
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

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

        command.Parameters.AddWithValue(
            "$userId",
            user.Id);

        command.Parameters.AddWithValue(
            "$username",
            user.Username);

        command.Parameters.AddWithValue(
            "$eventType",
            eventType);

        command.Parameters.AddWithValue(
            "$entityType",
            entityType);

        command.Parameters.AddWithValue(
            "$entityId",
            entityId);

        command.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(details));

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static InventoryException Validation(
        string code,
        string message)
    {
        return new InventoryException(
            StatusCodes.Status400BadRequest,
            code,
            message);
    }

    private static InventoryException Conflict(
        string code,
        string message)
    {
        return new InventoryException(
            StatusCodes.Status409Conflict,
            code,
            message);
    }

    private static InventoryException NotFound(
        string code,
        string message)
    {
        return new InventoryException(
            StatusCodes.Status404NotFound,
            code,
            message);
    }
}
