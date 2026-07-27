using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Inventory;
using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Sales;

public sealed class ShopSaleVoidService
{
    private readonly DatabaseBootstrap _database;

    public ShopSaleVoidService(DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task<VoidSaleResult> VoidAsync(
        AuthenticatedUser administrator,
        string saleId,
        VoidSaleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                administrator.Role,
                "admin",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SalesException(
                StatusCodes.Status403Forbidden,
                "administrator_required",
                "Only an administrator can void a completed sale.");
        }

        string normalizedSaleId = saleId?.Trim() ?? string.Empty;
        if (normalizedSaleId.Length == 0 || normalizedSaleId.Length > 100)
        {
            throw Validation("invalid_sale_id", "The sale identifier is invalid.");
        }

        string reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 5 or > 500)
        {
            throw Validation(
                "void_reason_required",
                "Enter a clear void reason containing 5 to 500 characters.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        SaleHeader? sale = await ReadSaleHeaderAsync(
            connection,
            transaction,
            normalizedSaleId,
            cancellationToken);

        if (sale is null)
        {
            throw NotFound("sale_not_found", "The requested sale could not be found.");
        }

        if (sale.Status == "voided")
        {
            throw Conflict(
                "sale_already_voided",
                "This sale has already been voided.");
        }

        if (sale.Status != "completed")
        {
            throw Conflict(
                "sale_not_voidable",
                "Only a completed sale can be voided.");
        }

        IReadOnlyList<VoidLine> lines = await ReadVoidLinesAsync(
            connection,
            transaction,
            normalizedSaleId,
            cancellationToken);

        if (lines.Count == 0)
        {
            throw new SalesException(
                StatusCodes.Status500InternalServerError,
                "sale_items_missing",
                "The completed sale has no stock lines to restore.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using (var markVoided = connection.CreateCommand())
        {
            markVoided.Transaction = transaction;
            markVoided.CommandText =
            """
            UPDATE sales
            SET status = 'voided',
                voided_at_utc = $voidedAtUtc,
                voided_by_user_id = $voidedByUserId,
                void_reason = $voidReason
            WHERE id = $saleId
              AND status = 'completed';
            """;
            markVoided.Parameters.AddWithValue("$voidedAtUtc", now.ToString("O"));
            markVoided.Parameters.AddWithValue("$voidedByUserId", administrator.Id);
            markVoided.Parameters.AddWithValue("$voidReason", reason);
            markVoided.Parameters.AddWithValue("$saleId", normalizedSaleId);

            if (await markVoided.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "sale_void_conflict",
                    "The sale changed while it was being voided. Reload and try again.");
            }
        }

        long restoredBaseUnits = 0;
        foreach (VoidLine line in lines)
        {
            StockBalance balance = await ReadStockBalanceAsync(
                connection,
                transaction,
                sale.ShopId,
                line.ProductId,
                cancellationToken);

            long newBalance = checked(
                balance.QuantityBaseUnits + line.BaseUnitsToRestore);

            await using var restore = connection.CreateCommand();
            restore.Transaction = transaction;
            restore.CommandText =
            """
            UPDATE shop_stock_balances
            SET quantity_base_units = $newBalance,
                version = version + 1,
                updated_at_utc = $updatedAtUtc
            WHERE shop_id = $shopId
              AND product_id = $productId
              AND version = $expectedVersion;
            """;
            restore.Parameters.AddWithValue("$newBalance", newBalance);
            restore.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            restore.Parameters.AddWithValue("$shopId", sale.ShopId);
            restore.Parameters.AddWithValue("$productId", line.ProductId);
            restore.Parameters.AddWithValue("$expectedVersion", balance.Version);

            if (await restore.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "stock_changed_during_void",
                    $"Stock changed while restoring {line.ProductName}. Reload and try again.");
            }

            await ShopInventoryService.InsertMovementAsync(
                connection,
                transaction,
                sale.ShopId,
                line.ProductId,
                "sale_void",
                line.BaseUnitsToRestore,
                newBalance,
                line.CostValueMinor,
                "sale",
                normalizedSaleId,
                reason,
                administrator.Id,
                administrator.Id,
                now,
                cancellationToken);

            restoredBaseUnits = checked(
                restoredBaseUnits + line.BaseUnitsToRestore);
        }

        await WriteAuditAsync(
            connection,
            transaction,
            administrator,
            normalizedSaleId,
            sale,
            reason,
            lines.Count,
            restoredBaseUnits,
            now,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new VoidSaleResult(
            normalizedSaleId,
            sale.ReceiptNumber,
            sale.InvoiceNumber,
            "voided",
            reason,
            now,
            administrator.Id,
            administrator.DisplayName,
            lines.Count,
            restoredBaseUnits);
    }

    public async Task<SaleVoidMetadata?> GetMetadataAsync(
        string saleId,
        CancellationToken cancellationToken = default)
    {
        string normalizedSaleId = saleId?.Trim() ?? string.Empty;
        if (normalizedSaleId.Length == 0)
        {
            return null;
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            sale.void_reason,
            sale.voided_at_utc,
            user.display_name
        FROM sales AS sale
        LEFT JOIN users AS user
            ON user.id = sale.voided_by_user_id
        WHERE sale.id = $saleId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$saleId", normalizedSaleId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SaleVoidMetadata(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1)
                ? null
                : DateTimeOffset.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task<SaleHeader?> ReadSaleHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string saleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            receipt_number,
            invoice_number,
            status,
            total_minor,
            COALESCE(shop_id, 'main-shop')
        FROM sales
        WHERE id = $saleId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$saleId", saleId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SaleHeader(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4));
    }

    private static async Task<IReadOnlyList<VoidLine>> ReadVoidLinesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string saleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            product_id,
            MAX(product_name_snapshot),
            SUM(base_units_deducted),
            SUM(unit_cost_minor * quantity)
        FROM sale_items
        WHERE sale_id = $saleId
        GROUP BY product_id
        ORDER BY product_id;
        """;
        command.Parameters.AddWithValue("$saleId", saleId);

        var lines = new List<VoidLine>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new VoidLine(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return lines;
    }

    private static async Task<StockBalance> ReadStockBalanceAsync(
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
        SELECT quantity_base_units, version
        FROM shop_stock_balances
        WHERE shop_id = $shopId
          AND product_id = $productId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$productId", productId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new SalesException(
                StatusCodes.Status500InternalServerError,
                "shop_stock_balance_missing",
                "A shop stock balance required for the void is missing.");
        }

        return new StockBalance(reader.GetInt64(0), reader.GetInt32(1));
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser administrator,
        string saleId,
        SaleHeader sale,
        string reason,
        int restoredProductCount,
        long restoredBaseUnits,
        DateTimeOffset now,
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
            'sale.voided',
            'sale',
            $saleId,
            1,
            $detailsJson,
            NULL
        );
        """;
        command.Parameters.AddWithValue("$occurredAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$userId", administrator.Id);
        command.Parameters.AddWithValue("$username", administrator.Username);
        command.Parameters.AddWithValue("$saleId", saleId);
        command.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(new
            {
                shopId = sale.ShopId,
                sale.ReceiptNumber,
                sale.InvoiceNumber,
                sale.TotalMinor,
                reason,
                restoredProductCount,
                restoredBaseUnits
            }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SalesException Validation(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    private static SalesException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private static SalesException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);

    private sealed record SaleHeader(
        string ReceiptNumber,
        string? InvoiceNumber,
        string Status,
        long TotalMinor,
        string ShopId);

    private sealed record VoidLine(
        string ProductId,
        string ProductName,
        long BaseUnitsToRestore,
        long CostValueMinor);

    private sealed record StockBalance(
        long QuantityBaseUnits,
        int Version);
}
