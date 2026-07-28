using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Procurement;

public sealed partial class ProcurementService
{
    private readonly DatabaseBootstrap _database;

    public ProcurementService(DatabaseBootstrap database)
    {
        _database = database;
    }

    private static bool IsAdministrator(AuthenticatedUser user) =>
        string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);

    private static async Task RequireProcurementAccessAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        AuthenticatedUser user,
        string shopId,
        CancellationToken cancellationToken)
    {
        if (IsAdministrator(user))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT access_level
        FROM user_shop_access
        WHERE user_id = $userId
          AND shop_id = $shopId
          AND is_active = 1
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$shopId", shopId);

        string? accessLevel = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(accessLevel, "manager", StringComparison.OrdinalIgnoreCase))
        {
            throw Forbidden(
                "procurement_permission_required",
                "A branch manager or administrator is required for procurement operations.");
        }
    }

    private static void RequireAdministrator(AuthenticatedUser user, string action)
    {
        if (!IsAdministrator(user))
        {
            throw Forbidden(
                "administrator_required",
                $"Only an administrator can {action}.");
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
            throw Validation(errorCode, "Use a valid date in YYYY-MM-DD format.");
        }
        return normalized;
    }

    private static string? NormalizeOptionalDate(string? value, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return NormalizeDate(value, errorCode);
    }

    private static IReadOnlyList<NormalizedOrderLine> NormalizeOrderLines(
        IReadOnlyList<PurchaseOrderLineRequest>? lines)
    {
        if (lines is null || lines.Count == 0)
        {
            throw Validation(
                "purchase_order_items_required",
                "Add at least one product to the purchase order.");
        }
        if (lines.Count > 250)
        {
            throw Validation(
                "too_many_purchase_order_items",
                "A purchase order cannot contain more than 250 product lines.");
        }

        var normalized = lines
            .GroupBy(line => NormalizeId(line.ProductId), StringComparer.Ordinal)
            .Select(group => new NormalizedOrderLine(
                group.Key,
                checked(group.Sum(item => item.QuantityBaseUnits)),
                group.Select(item => item.UnitCostMinor).Distinct().SingleOrDefault()))
            .ToList();

        if (normalized.Any(line =>
                line.QuantityBaseUnits <= 0 ||
                line.UnitCostMinor < 0 ||
                groupHasMixedCost(lines, line.ProductId)))
        {
            throw Validation(
                "invalid_purchase_order_item",
                "Every purchase order line requires positive quantity and one non-negative unit cost.");
        }
        return normalized;

        static bool groupHasMixedCost(
            IReadOnlyList<PurchaseOrderLineRequest> source,
            string productId) =>
            source.Where(item => string.Equals(
                    item.ProductId?.Trim(),
                    productId,
                    StringComparison.Ordinal))
                .Select(item => item.UnitCostMinor)
                .Distinct()
                .Skip(1)
                .Any();
    }

    private static async Task<string> RequireSupplierAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string supplierId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT name
        FROM suppliers
        WHERE id = $supplierId
          AND organization_id = $organizationId
          AND is_active = 1
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$supplierId", supplierId);
        command.Parameters.AddWithValue("$organizationId", organizationId);

        string? name = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw NotFound(
                "supplier_not_found",
                "The active supplier could not be found in this organization.");
        }
        return name;
    }

    private static async Task<ProductSnapshot> RequireProductAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string productId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT id, name, sku, cost_price_minor, track_expiry, is_active
        FROM products
        WHERE id = $productId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$productId", productId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound("product_not_found", "A selected product could not be found.");
        }
        if (reader.GetInt32(5) != 1)
        {
            throw Conflict("product_inactive", $"{reader.GetString(1)} is inactive.");
        }
        return new ProductSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt32(4) == 1);
    }

    private static async Task EnsureShopBalanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string shopId,
        string productId,
        DateTimeOffset now,
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
        SELECT $shopId, id, 0, 0, 1, $now
        FROM products
        WHERE id = $productId;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$productId", productId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<BalanceSnapshot> ReadBalanceAsync(
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
        SELECT quantity_base_units, reserved_base_units, version
        FROM shop_stock_balances
        WHERE shop_id = $shopId
          AND product_id = $productId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$productId", productId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Conflict("shop_stock_missing", "The shop stock balance could not be initialized.");
        }
        return new BalanceSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt32(2));
    }

    private static async Task<string> NextDocumentNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string documentType,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string basePrefix = documentType switch
        {
            "purchase_order" => "PO",
            "goods_receipt" => "GRN",
            "supplier_return" => "SRN",
            "stock_count" => "COUNT",
            _ => throw new InvalidOperationException("Unsupported procurement document type.")
        };
        string prefix = $"{basePrefix}-{NormalizeShopCode(context.ShopCode)}";

        await using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = transaction;
            ensure.CommandText =
            """
            INSERT OR IGNORE INTO procurement_document_sequences
            (shop_id, document_type, prefix, next_value, updated_at_utc)
            VALUES ($shopId, $documentType, $prefix, 1, $now);
            """;
            ensure.Parameters.AddWithValue("$shopId", context.ShopId);
            ensure.Parameters.AddWithValue("$documentType", documentType);
            ensure.Parameters.AddWithValue("$prefix", prefix);
            ensure.Parameters.AddWithValue("$now", now.ToString("O"));
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        long nextValue;
        string storedPrefix;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
            """
            SELECT prefix, next_value
            FROM procurement_document_sequences
            WHERE shop_id = $shopId
              AND document_type = $documentType;
            """;
            read.Parameters.AddWithValue("$shopId", context.ShopId);
            read.Parameters.AddWithValue("$documentType", documentType);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw Conflict("document_sequence_missing", "The procurement document sequence is missing.");
            }
            storedPrefix = reader.GetString(0);
            nextValue = reader.GetInt64(1);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE procurement_document_sequences
        SET next_value = next_value + 1,
            updated_at_utc = $now
        WHERE shop_id = $shopId
          AND document_type = $documentType
          AND next_value = $expected;
        """;
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$shopId", context.ShopId);
        update.Parameters.AddWithValue("$documentType", documentType);
        update.Parameters.AddWithValue("$expected", nextValue);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "document_sequence_conflict",
                "Another procurement document was created simultaneously. Try again.");
        }
        return $"{storedPrefix}-{now:yyyyMMdd}-{nextValue:000000}";
    }

    private static string NormalizeShopCode(string value)
    {
        char[] normalized = value.Trim().ToUpperInvariant()
            .Where(character => char.IsLetterOrDigit(character) || character == '-')
            .Take(16)
            .ToArray();
        return normalized.Length == 0 ? "SHOP" : new string(normalized);
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

    private static async Task<string> NextAccountingJournalNumberAsync(
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
            INSERT OR IGNORE INTO accounting_journal_sequences
            (organization_id, next_value, updated_at_utc)
            VALUES ($organizationId, 1, $now);
            """;
            ensure.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            ensure.Parameters.AddWithValue("$now", now.ToString("O"));
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        long nextValue;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
            """
            SELECT next_value
            FROM accounting_journal_sequences
            WHERE organization_id = $organizationId;
            """;
            read.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            nextValue = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken));
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE accounting_journal_sequences
        SET next_value = next_value + 1,
            updated_at_utc = $now
        WHERE organization_id = $organizationId
          AND next_value = $expected;
        """;
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        update.Parameters.AddWithValue("$expected", nextValue);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("journal_sequence_conflict", "The accounting journal sequence changed. Try again.");
        }
        return $"JV-{NormalizeShopCode(context.ShopCode)}-{now:yyyyMMdd}-{nextValue:000000}";
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
        VALUES ($now, $userId, $username, $eventType, $entityType, $entityId, 1, $details, NULL);
        """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$details", JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ProcurementException Validation(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    private static ProcurementException Forbidden(string code, string message) =>
        new(StatusCodes.Status403Forbidden, code, message);

    private static ProcurementException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);

    private static ProcurementException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private sealed record NormalizedOrderLine(
        string ProductId,
        long QuantityBaseUnits,
        long UnitCostMinor);

    private sealed record ProductSnapshot(
        string Id,
        string Name,
        string Sku,
        long CurrentCostMinor,
        bool TrackExpiry);

    private sealed record BalanceSnapshot(
        long QuantityBaseUnits,
        long ReservedBaseUnits,
        int Version);

    private sealed record PurchaseOrderHeader(
        string Id,
        string Number,
        string OrganizationId,
        string ShopId,
        string SupplierId,
        string SupplierName,
        string Status,
        string OrderDate,
        string? ExpectedDate,
        string CurrencyCode,
        long SubtotalMinor,
        long LandedCostMinor,
        long TotalMinor,
        string Notes,
        int Version,
        string CreatedBy,
        string? ApprovedBy,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? SubmittedAtUtc,
        DateTimeOffset? ApprovedAtUtc,
        DateTimeOffset? CompletedAtUtc);

    private sealed record PurchaseOrderLineState(
        string Id,
        int LineNumber,
        string ProductId,
        string ProductName,
        string Sku,
        long OrderedQuantityBaseUnits,
        long ReceivedQuantityBaseUnits,
        long ReturnedQuantityBaseUnits,
        long UnitCostMinor,
        bool TrackExpiry);
}
