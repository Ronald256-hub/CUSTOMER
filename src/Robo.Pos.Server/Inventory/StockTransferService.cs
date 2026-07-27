using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Inventory;

public sealed class StockTransferService
{
    private static readonly HashSet<string> ValidStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "draft",
            "submitted",
            "approved",
            "in_transit",
            "received",
            "cancelled"
        };

    private readonly DatabaseBootstrap _database;

    public StockTransferService(DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<StockTransferListItem>> ListAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? requestedScope,
        string? requestedStatus,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        string scope = requestedScope?.Trim().ToLowerInvariant() ?? "shop";
        bool consolidated = scope == "consolidated";
        if (scope is not ("shop" or "consolidated"))
        {
            throw Validation(
                "invalid_transfer_scope",
                "Transfer scope must be shop or consolidated.");
        }

        if (consolidated && !IsAdministrator(user))
        {
            throw Forbidden(
                "administrator_required",
                "Only an administrator can view consolidated transfers.");
        }

        string status = requestedStatus?.Trim().ToLowerInvariant() ?? string.Empty;
        if (status.Length > 0 && !ValidStatuses.Contains(status))
        {
            throw Validation(
                "invalid_transfer_status",
                "The requested transfer status is invalid.");
        }

        int limit = Math.Clamp(requestedLimit, 1, 500);
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            transfer.id,
            transfer.transfer_number,
            source.id,
            source.code,
            source.name,
            destination.id,
            destination.code,
            destination.name,
            transfer.status,
            COALESCE(SUM(item.quantity_base_units), 0),
            COALESCE(SUM(item.dispatched_quantity_base_units), 0),
            COALESCE(SUM(item.received_quantity_base_units), 0),
            COALESCE(SUM(item.damaged_quantity_base_units), 0),
            transfer.created_at_utc,
            COALESCE(transfer.updated_at_utc, transfer.created_at_utc),
            transfer.version
        FROM stock_transfers AS transfer
        INNER JOIN shops AS source
            ON source.id = transfer.source_shop_id
        INNER JOIN shops AS destination
            ON destination.id = transfer.destination_shop_id
        LEFT JOIN stock_transfer_items AS item
            ON item.transfer_id = transfer.id
        WHERE source.organization_id = $organizationId
          AND destination.organization_id = $organizationId
          AND
          (
              $consolidated = 1
              OR transfer.source_shop_id = $shopId
              OR transfer.destination_shop_id = $shopId
          )
          AND ($status = '' OR transfer.status = $status)
        GROUP BY
            transfer.id,
            transfer.transfer_number,
            source.id,
            source.code,
            source.name,
            destination.id,
            destination.code,
            destination.name,
            transfer.status,
            transfer.created_at_utc,
            transfer.updated_at_utc,
            transfer.version
        ORDER BY
            COALESCE(transfer.updated_at_utc, transfer.created_at_utc) DESC,
            transfer.transfer_number DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$consolidated", consolidated ? 1 : 0);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<StockTransferListItem>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new StockTransferListItem(
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
                ParseDate(reader.GetString(13)),
                ParseDate(reader.GetString(14)),
                reader.GetInt32(15)));
        }

        return results;
    }

    public async Task<StockTransferRecord> GetAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string transferId,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(transferId);
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        TransferHeader? header = await ReadHeaderAsync(
            connection,
            transaction: null,
            id,
            cancellationToken);
        if (header is null ||
            !string.Equals(
                header.OrganizationId,
                context.OrganizationId,
                StringComparison.Ordinal))
        {
            throw NotFound(
                "stock_transfer_not_found",
                "The stock transfer could not be found.");
        }

        bool involved =
            string.Equals(header.SourceShopId, context.ShopId, StringComparison.Ordinal) ||
            string.Equals(header.DestinationShopId, context.ShopId, StringComparison.Ordinal);
        if (!involved && !IsAdministrator(user))
        {
            throw NotFound(
                "stock_transfer_not_found",
                "The stock transfer could not be found.");
        }

        IReadOnlyList<StockTransferItemRecord> items =
            await ReadItemsAsync(
                connection,
                transaction: null,
                id,
                cancellationToken);
        IReadOnlyList<StockTransferEventRecord> events =
            await ReadEventsAsync(connection, id, cancellationToken);

        return ToRecord(header, items, events);
    }

    public async Task<StockTransferRecord> CreateDraftAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateStockTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        string destinationShopId = NormalizeId(request.DestinationShopId);
        string notes = NormalizeNotes(request.Notes, 500);
        IReadOnlyList<NormalizedItem> items = NormalizeItems(request.Items);

        if (string.Equals(
                destinationShopId,
                context.ShopId,
                StringComparison.Ordinal))
        {
            throw Validation(
                "same_transfer_shop",
                "The source and destination shops must be different.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        await RequireShopPermissionAsync(
            connection,
            transaction,
            user,
            context.ShopId,
            allowSupervisor: true,
            cancellationToken);

        ShopIdentity? destination = await ReadShopAsync(
            connection,
            transaction,
            destinationShopId,
            cancellationToken);
        if (destination is null ||
            !destination.IsActive ||
            !string.Equals(
                destination.OrganizationId,
                context.OrganizationId,
                StringComparison.Ordinal))
        {
            throw Validation(
                "invalid_destination_shop",
                "Select an active destination shop in the same organization.");
        }

        await ValidateProductsAsync(
            connection,
            transaction,
            items,
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string id = Guid.NewGuid().ToString("N");
        string transferNumber = await NextTransferNumberAsync(
            connection,
            transaction,
            context.ShopId,
            context.ShopCode,
            now,
            cancellationToken);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
            """
            INSERT INTO stock_transfers
            (
                id,
                transfer_number,
                source_shop_id,
                destination_shop_id,
                status,
                notes,
                created_by_user_id,
                created_at_utc,
                updated_at_utc,
                version
            )
            VALUES
            (
                $id,
                $transferNumber,
                $sourceShopId,
                $destinationShopId,
                'draft',
                $notes,
                $createdByUserId,
                $createdAtUtc,
                $updatedAtUtc,
                1
            );
            """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$transferNumber", transferNumber);
            insert.Parameters.AddWithValue("$sourceShopId", context.ShopId);
            insert.Parameters.AddWithValue("$destinationShopId", destinationShopId);
            insert.Parameters.AddWithValue("$notes", notes);
            insert.Parameters.AddWithValue("$createdByUserId", user.Id);
            insert.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
            insert.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceItemsAsync(
            connection,
            transaction,
            id,
            items,
            cancellationToken);

        await WriteEventAndAuditAsync(
            connection,
            transaction,
            user,
            id,
            transferNumber,
            "transfer.created",
            fromStatus: null,
            toStatus: "draft",
            new
            {
                sourceShopId = context.ShopId,
                destinationShopId,
                lineCount = items.Count,
                requestedQuantityBaseUnits = items.Sum(item => item.Quantity)
            },
            now,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(user, context, id, cancellationToken);
    }

    public async Task<StockTransferRecord> UpdateDraftAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string transferId,
        UpdateStockTransferDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(transferId);
        string notes = NormalizeNotes(request.Notes, 500);
        IReadOnlyList<NormalizedItem> items = NormalizeItems(request.Items);
        RequireExpectedVersion(request.ExpectedVersion);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        TransferHeader header = await RequireHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        RequireSourceContext(header, context);
        RequireStatus(header, "draft");
        RequireVersion(header, request.ExpectedVersion);
        await RequireShopPermissionAsync(
            connection,
            transaction,
            user,
            header.SourceShopId,
            allowSupervisor: true,
            cancellationToken);
        await ValidateProductsAsync(
            connection,
            transaction,
            items,
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE stock_transfers
            SET notes = $notes,
                updated_at_utc = $updatedAtUtc,
                version = version + 1
            WHERE id = $id
              AND status = 'draft'
              AND version = $expectedVersion;
            """;
            update.Parameters.AddWithValue("$notes", notes);
            update.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "stock_transfer_changed",
                    "The stock transfer changed. Reload it and try again.");
            }
        }

        await ReplaceItemsAsync(
            connection,
            transaction,
            id,
            items,
            cancellationToken);
        await WriteEventAndAuditAsync(
            connection,
            transaction,
            user,
            id,
            header.TransferNumber,
            "transfer.draft_updated",
            "draft",
            "draft",
            new
            {
                lineCount = items.Count,
                requestedQuantityBaseUnits = items.Sum(item => item.Quantity)
            },
            now,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(user, context, id, cancellationToken);
    }

    public async Task<StockTransferRecord> SubmitAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string transferId,
        StockTransferTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        return await ChangeSimpleStatusAsync(
            user,
            context,
            transferId,
            request.ExpectedVersion,
            fromStatus: "draft",
            toStatus: "submitted",
            eventType: "transfer.submitted",
            request.Notes,
            requireManager: false,
            cancellationToken);
    }

    public async Task<StockTransferRecord> ApproveAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string transferId,
        StockTransferTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(transferId);
        RequireExpectedVersion(request.ExpectedVersion);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        TransferHeader header = await RequireHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        RequireSourceContext(header, context);
        RequireStatus(header, "submitted");
        RequireVersion(header, request.ExpectedVersion);
        await RequireShopPermissionAsync(
            connection,
            transaction,
            user,
            header.SourceShopId,
            allowSupervisor: false,
            cancellationToken);

        if (!IsAdministrator(user) &&
            string.Equals(header.CreatedByUserId, user.Id, StringComparison.Ordinal))
        {
            throw Forbidden(
                "transfer_approval_separation_required",
                "A manager cannot approve a transfer they created. Ask another manager or an administrator.");
        }

        IReadOnlyList<ReservationLine> lines = await ReadReservationLinesAsync(
            connection,
            transaction,
            id,
            header.SourceShopId,
            cancellationToken);
        if (lines.Count == 0)
        {
            throw Conflict(
                "stock_transfer_items_required",
                "Add at least one product before approving the transfer.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (ReservationLine line in lines)
        {
            if (line.AvailableQuantity < line.RequestedQuantity)
            {
                throw Conflict(
                    "insufficient_source_stock",
                    $"Insufficient available stock for {line.ProductName} at the source shop.");
            }

            await using var reserve = connection.CreateCommand();
            reserve.Transaction = transaction;
            reserve.CommandText =
            """
            UPDATE shop_stock_balances
            SET reserved_base_units = reserved_base_units + $quantity,
                version = version + 1,
                updated_at_utc = $updatedAtUtc
            WHERE shop_id = $shopId
              AND product_id = $productId
              AND version = $stockVersion
              AND quantity_base_units - reserved_base_units >= $quantity;
            """;
            reserve.Parameters.AddWithValue("$quantity", line.RequestedQuantity);
            reserve.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            reserve.Parameters.AddWithValue("$shopId", header.SourceShopId);
            reserve.Parameters.AddWithValue("$productId", line.ProductId);
            reserve.Parameters.AddWithValue("$stockVersion", line.StockVersion);
            if (await reserve.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "source_stock_changed",
                    $"Stock changed while reserving {line.ProductName}. Reload and approve again.");
            }

            await using var updateItem = connection.CreateCommand();
            updateItem.Transaction = transaction;
            updateItem.CommandText =
            """
            UPDATE stock_transfer_items
            SET reserved_quantity_base_units = quantity_base_units,
                unit_cost_minor = $unitCostMinor
            WHERE id = $itemId;
            """;
            updateItem.Parameters.AddWithValue("$unitCostMinor", line.UnitCostMinor);
            updateItem.Parameters.AddWithValue("$itemId", line.ItemId);
            await updateItem.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var approve = connection.CreateCommand())
        {
            approve.Transaction = transaction;
            approve.CommandText =
            """
            UPDATE stock_transfers
            SET status = 'approved',
                approved_by_user_id = $approvedByUserId,
                approved_at_utc = $approvedAtUtc,
                updated_at_utc = $approvedAtUtc,
                version = version + 1
            WHERE id = $id
              AND status = 'submitted'
              AND version = $expectedVersion;
            """;
            approve.Parameters.AddWithValue("$approvedByUserId", user.Id);
            approve.Parameters.AddWithValue("$approvedAtUtc", now.ToString("O"));
            approve.Parameters.AddWithValue("$id", id);
            approve.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
            if (await approve.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "stock_transfer_changed",
                    "The stock transfer changed. Reload it and try again.");
            }
        }

        await WriteEventAndAuditAsync(
            connection,
            transaction,
            user,
            id,
            header.TransferNumber,
            "transfer.approved",
            "submitted",
            "approved",
            new
            {
                notes = NormalizeNotes(request.Notes, 500),
                reservedQuantityBaseUnits = lines.Sum(line => line.RequestedQuantity)
            },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetAsync(user, context, id, cancellationToken);
    }

    public async Task<StockTransferRecord> RejectAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string transferId,
        CancelStockTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        return await CancelOrRejectAsync(
            user,
            context,
            transferId,
            request,
            requiredStatus: "submitted",
            cancellationKind: "rejected",
            cancellationToken);
    }

    public async Task<StockTransferRecord> DispatchAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string transferId,
        StockTransferTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(transferId);
        RequireExpectedVersion(request.ExpectedVersion);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        TransferHeader header = await RequireHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        RequireSourceContext(header, context);
        RequireStatus(header, "approved");
        RequireVersion(header, request.ExpectedVersion);
        await RequireShopPermissionAsync(
            connection,
            transaction,
            user,
            header.SourceShopId,
            allowSupervisor: true,
            cancellationToken);

        IReadOnlyList<DispatchLine> lines = await ReadDispatchLinesAsync(
            connection,
            transaction,
            id,
            header.SourceShopId,
            cancellationToken);
        if (lines.Count == 0)
        {
            throw Conflict(
                "stock_transfer_items_required",
                "The approved transfer has no products to dispatch.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (DispatchLine line in lines)
        {
            if (line.ReservedQuantity != line.RequestedQuantity)
            {
                throw Conflict(
                    "transfer_reservation_incomplete",
                    $"The reservation for {line.ProductName} is incomplete.");
            }

            long newBalance = checked(line.SourceBalance - line.RequestedQuantity);
            await using var deduct = connection.CreateCommand();
            deduct.Transaction = transaction;
            deduct.CommandText =
            """
            UPDATE shop_stock_balances
            SET quantity_base_units = $newBalance,
                reserved_base_units = reserved_base_units - $quantity,
                version = version + 1,
                updated_at_utc = $updatedAtUtc
            WHERE shop_id = $shopId
              AND product_id = $productId
              AND version = $stockVersion
              AND reserved_base_units >= $quantity
              AND quantity_base_units >= $quantity;
            """;
            deduct.Parameters.AddWithValue("$newBalance", newBalance);
            deduct.Parameters.AddWithValue("$quantity", line.RequestedQuantity);
            deduct.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            deduct.Parameters.AddWithValue("$shopId", header.SourceShopId);
            deduct.Parameters.AddWithValue("$productId", line.ProductId);
            deduct.Parameters.AddWithValue("$stockVersion", line.StockVersion);
            if (await deduct.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "source_stock_changed",
                    $"Stock changed while dispatching {line.ProductName}. Reload and try again.");
            }

            await using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText =
            """
            UPDATE stock_transfer_items
            SET reserved_quantity_base_units = 0,
                dispatched_quantity_base_units = quantity_base_units,
                source_balance_before = $sourceBefore,
                source_balance_after = $sourceAfter
            WHERE id = $itemId;
            """;
            item.Parameters.AddWithValue("$sourceBefore", line.SourceBalance);
            item.Parameters.AddWithValue("$sourceAfter", newBalance);
            item.Parameters.AddWithValue("$itemId", line.ItemId);
            await item.ExecuteNonQueryAsync(cancellationToken);

            await ShopInventoryService.InsertMovementAsync(
                connection,
                transaction,
                header.SourceShopId,
                line.ProductId,
                "transfer_out",
                -line.RequestedQuantity,
                newBalance,
                checked(line.UnitCostMinor * line.RequestedQuantity),
                "stock_transfer",
                id,
                $"Dispatched on {header.TransferNumber}",
                user.Id,
                header.ApprovedByUserId,
                now,
                cancellationToken);
        }

        await using (var dispatch = connection.CreateCommand())
        {
            dispatch.Transaction = transaction;
            dispatch.CommandText =
            """
            UPDATE stock_transfers
            SET status = 'in_transit',
                dispatched_by_user_id = $dispatchedByUserId,
                dispatched_at_utc = $dispatchedAtUtc,
                updated_at_utc = $dispatchedAtUtc,
                version = version + 1
            WHERE id = $id
              AND status = 'approved'
              AND version = $expectedVersion;
            """;
            dispatch.Parameters.AddWithValue("$dispatchedByUserId", user.Id);
            dispatch.Parameters.AddWithValue("$dispatchedAtUtc", now.ToString("O"));
            dispatch.Parameters.AddWithValue("$id", id);
            dispatch.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
            if (await dispatch.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "stock_transfer_changed",
                    "The stock transfer changed. Reload it and try again.");
            }
        }

        await WriteEventAndAuditAsync(
            connection,
            transaction,
            user,
            id,
            header.TransferNumber,
            "transfer.dispatched",
            "approved",
            "in_transit",
            new
            {
                notes = NormalizeNotes(request.Notes, 500),
                dispatchedQuantityBaseUnits = lines.Sum(line => line.RequestedQuantity)
            },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetAsync(user, context, id, cancellationToken);
    }

    public async Task<StockTransferRecord> ReceiveAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string transferId,
        ReceiveStockTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(transferId);
        RequireExpectedVersion(request.ExpectedVersion);
        IReadOnlyList<NormalizedReceipt> receipts = NormalizeReceipts(request.Items);
        if (!request.Finalize && receipts.Count == 0)
        {
            throw Validation(
                "transfer_receipt_items_required",
                "Enter at least one received or damaged quantity.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        TransferHeader header = await RequireHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        RequireDestinationContext(header, context);
        RequireStatus(header, "in_transit");
        RequireVersion(header, request.ExpectedVersion);
        await RequireShopPermissionAsync(
            connection,
            transaction,
            user,
            header.DestinationShopId,
            allowSupervisor: true,
            cancellationToken);

        Dictionary<string, ReceivingLine> lines =
            (await ReadReceivingLinesAsync(
                    connection,
                    transaction,
                    id,
                    header.DestinationShopId,
                    cancellationToken))
                .ToDictionary(line => line.ProductId, StringComparer.Ordinal);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        long acceptedTotal = 0;
        long damagedTotal = 0;
        foreach (NormalizedReceipt receipt in receipts)
        {
            if (!lines.TryGetValue(receipt.ProductId, out ReceivingLine? line))
            {
                throw Validation(
                    "transfer_product_not_found",
                    "A received product is not part of this transfer.");
            }

            long remaining = checked(
                line.DispatchedQuantity -
                line.ReceivedQuantity -
                line.DamagedQuantity);
            long accounted = checked(
                receipt.ReceivedQuantity + receipt.DamagedQuantity);
            if (accounted <= 0 || accounted > remaining)
            {
                throw Conflict(
                    "invalid_received_quantity",
                    $"The quantity entered for {line.ProductName} exceeds the outstanding transfer quantity.");
            }

            if (receipt.DamagedQuantity > 0 && receipt.Reason.Length < 5)
            {
                throw Validation(
                    "transfer_discrepancy_reason_required",
                    $"Enter a clear discrepancy reason for damaged or missing {line.ProductName} stock.");
            }

            long destinationAfter = line.DestinationBalance;
            if (receipt.ReceivedQuantity > 0)
            {
                destinationAfter = checked(
                    line.DestinationBalance + receipt.ReceivedQuantity);
                await using var add = connection.CreateCommand();
                add.Transaction = transaction;
                add.CommandText =
                """
                UPDATE shop_stock_balances
                SET quantity_base_units = $newBalance,
                    version = version + 1,
                    updated_at_utc = $updatedAtUtc
                WHERE shop_id = $shopId
                  AND product_id = $productId
                  AND version = $stockVersion;
                """;
                add.Parameters.AddWithValue("$newBalance", destinationAfter);
                add.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
                add.Parameters.AddWithValue("$shopId", header.DestinationShopId);
                add.Parameters.AddWithValue("$productId", line.ProductId);
                add.Parameters.AddWithValue("$stockVersion", line.DestinationStockVersion);
                if (await add.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw Conflict(
                        "destination_stock_changed",
                        $"Destination stock changed while receiving {line.ProductName}. Reload and try again.");
                }

                await ShopInventoryService.InsertMovementAsync(
                    connection,
                    transaction,
                    header.DestinationShopId,
                    line.ProductId,
                    "transfer_in",
                    receipt.ReceivedQuantity,
                    destinationAfter,
                    checked(line.UnitCostMinor * receipt.ReceivedQuantity),
                    "stock_transfer",
                    id,
                    $"Received on {header.TransferNumber}",
                    user.Id,
                    header.ApprovedByUserId,
                    now,
                    cancellationToken);
            }

            await using var updateItem = connection.CreateCommand();
            updateItem.Transaction = transaction;
            updateItem.CommandText =
            """
            UPDATE stock_transfer_items
            SET received_quantity_base_units =
                    received_quantity_base_units + $receivedQuantity,
                damaged_quantity_base_units =
                    damaged_quantity_base_units + $damagedQuantity,
                destination_before = COALESCE(
                    destination_before,
                    $destinationBefore),
                destination_after = $destinationAfter,
                discrepancy_reason = CASE
                    WHEN $reason = '' THEN discrepancy_reason
                    WHEN discrepancy_reason = '' THEN $reason
                    ELSE discrepancy_reason || '; ' || $reason
                END
            WHERE id = $itemId;
            """;
            updateItem.Parameters.AddWithValue(
                "$receivedQuantity",
                receipt.ReceivedQuantity);
            updateItem.Parameters.AddWithValue(
                "$damagedQuantity",
                receipt.DamagedQuantity);
            updateItem.Parameters.AddWithValue(
                "$destinationBefore",
                line.DestinationBalance);
            updateItem.Parameters.AddWithValue(
                "$destinationAfter",
                destinationAfter);
            updateItem.Parameters.AddWithValue("$reason", receipt.Reason);
            updateItem.Parameters.AddWithValue("$itemId", line.ItemId);
            await updateItem.ExecuteNonQueryAsync(cancellationToken);

            acceptedTotal = checked(acceptedTotal + receipt.ReceivedQuantity);
            damagedTotal = checked(damagedTotal + receipt.DamagedQuantity);
        }

        long outstanding = await ReadOutstandingQuantityAsync(
            connection,
            transaction,
            id,
            cancellationToken);
        if (request.Finalize && outstanding != 0)
        {
            throw Conflict(
                "transfer_quantities_outstanding",
                "All dispatched quantities must be received or recorded as a discrepancy before finalizing the transfer.");
        }

        string toStatus = request.Finalize ? "received" : "in_transit";
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = request.Finalize
                ?
                """
                UPDATE stock_transfers
                SET status = 'received',
                    received_by_user_id = $receivedByUserId,
                    received_at_utc = $receivedAtUtc,
                    updated_at_utc = $receivedAtUtc,
                    version = version + 1
                WHERE id = $id
                  AND status = 'in_transit'
                  AND version = $expectedVersion;
                """
                :
                """
                UPDATE stock_transfers
                SET updated_at_utc = $receivedAtUtc,
                    version = version + 1
                WHERE id = $id
                  AND status = 'in_transit'
                  AND version = $expectedVersion;
                """;
            update.Parameters.AddWithValue("$receivedByUserId", user.Id);
            update.Parameters.AddWithValue("$receivedAtUtc", now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "stock_transfer_changed",
                    "The stock transfer changed. Reload it and try again.");
            }
        }

        await WriteEventAndAuditAsync(
            connection,
            transaction,
            user,
            id,
            header.TransferNumber,
            request.Finalize
                ? "transfer.received"
                : "transfer.partially_received",
            "in_transit",
            toStatus,
            new
            {
                notes = NormalizeNotes(request.Notes, 500),
                receivedQuantityBaseUnits = acceptedTotal,
                damagedQuantityBaseUnits = damagedTotal,
                outstandingQuantityBaseUnits = outstanding,
                finalized = request.Finalize
            },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetAsync(user, context, id, cancellationToken);
    }

    public async Task<StockTransferRecord> CancelAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string transferId,
        CancelStockTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        return await CancelOrRejectAsync(
            user,
            context,
            transferId,
            request,
            requiredStatus: null,
            cancellationKind: "cancelled",
            cancellationToken);
    }

    public async Task<StockTransferReport> GetReportAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? requestedScope,
        DateTimeOffset? requestedFromUtc,
        DateTimeOffset? requestedToUtc,
        CancellationToken cancellationToken = default)
    {
        string scope = requestedScope?.Trim().ToLowerInvariant() ?? "shop";
        bool consolidated = scope == "consolidated";
        if (scope is not ("shop" or "consolidated"))
        {
            throw Validation(
                "invalid_transfer_scope",
                "Transfer scope must be shop or consolidated.");
        }

        if (consolidated && !IsAdministrator(user))
        {
            throw Forbidden(
                "administrator_required",
                "Only an administrator can view consolidated transfer reports.");
        }

        DateTimeOffset toUtc = requestedToUtc?.ToUniversalTime()
            ?? DateTimeOffset.UtcNow;
        DateTimeOffset fromUtc = requestedFromUtc?.ToUniversalTime()
            ?? toUtc.AddDays(-30);
        if (fromUtc >= toUtc)
        {
            throw Validation(
                "invalid_report_period",
                "The report start time must be earlier than the end time.");
        }
        if (toUtc - fromUtc > TimeSpan.FromDays(366))
        {
            throw Validation(
                "report_period_too_large",
                "A transfer report cannot cover more than 366 days at once.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        WITH transfer_totals AS
        (
            SELECT
                transfer.id,
                transfer.status,
                SUM(item.quantity_base_units) AS requested_quantity,
                SUM(item.dispatched_quantity_base_units) AS dispatched_quantity,
                SUM(item.received_quantity_base_units) AS received_quantity,
                SUM(item.damaged_quantity_base_units) AS damaged_quantity
            FROM stock_transfers AS transfer
            INNER JOIN shops AS source
                ON source.id = transfer.source_shop_id
            INNER JOIN stock_transfer_items AS item
                ON item.transfer_id = transfer.id
            WHERE source.organization_id = $organizationId
              AND
              (
                  $consolidated = 1
                  OR transfer.source_shop_id = $shopId
                  OR transfer.destination_shop_id = $shopId
              )
              AND transfer.created_at_utc >= $fromUtc
              AND transfer.created_at_utc < $toUtc
            GROUP BY transfer.id, transfer.status
        )
        SELECT
            status,
            COUNT(*),
            COALESCE(SUM(requested_quantity), 0),
            COALESCE(SUM(dispatched_quantity), 0),
            COALESCE(SUM(received_quantity), 0),
            COALESCE(SUM(damaged_quantity), 0),
            COALESCE(SUM(
                MAX(dispatched_quantity - received_quantity - damaged_quantity, 0)), 0)
        FROM transfer_totals
        GROUP BY status
        ORDER BY status;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$consolidated", consolidated ? 1 : 0);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$fromUtc", fromUtc.ToString("O"));
        command.Parameters.AddWithValue("$toUtc", toUtc.ToString("O"));

        var statuses = new List<StockTransferStatusSummary>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            statuses.Add(new StockTransferStatusSummary(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6)));
        }

        return new StockTransferReport(
            scope,
            context.OrganizationId,
            context.OrganizationName,
            consolidated ? null : context.ShopId,
            consolidated ? null : context.ShopCode,
            fromUtc,
            toUtc,
            statuses.Sum(status => status.TransferCount),
            statuses.Sum(status => status.RequestedQuantityBaseUnits),
            statuses.Sum(status => status.DispatchedQuantityBaseUnits),
            statuses.Sum(status => status.ReceivedQuantityBaseUnits),
            statuses.Sum(status => status.DamagedQuantityBaseUnits),
            statuses.Sum(status => status.OutstandingQuantityBaseUnits),
            statuses);
    }

    public async Task<string> BuildDocumentHtmlAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string transferId,
        CancellationToken cancellationToken = default)
    {
        StockTransferRecord transfer = await GetAsync(
            user,
            context,
            transferId,
            cancellationToken);
        HtmlEncoder encoder = HtmlEncoder.Default;
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        html.Append("<title>").Append(encoder.Encode(transfer.TransferNumber));
        html.Append("</title><style>body{font-family:Arial,sans-serif;margin:32px;color:#111}");
        html.Append("table{border-collapse:collapse;width:100%;margin-top:20px}");
        html.Append("th,td{border:1px solid #bbb;padding:8px;text-align:left}");
        html.Append("th{background:#eee}.meta{display:grid;grid-template-columns:1fr 1fr;gap:8px}");
        html.Append(".status{font-weight:700;text-transform:uppercase}</style></head><body>");
        html.Append("<h1>Inter-shop Stock Transfer</h1><div class=\"meta\">");
        AppendMeta(html, encoder, "Transfer", transfer.TransferNumber);
        AppendMeta(html, encoder, "Status", transfer.Status);
        AppendMeta(html, encoder, "Source", $"{transfer.SourceShopCode} — {transfer.SourceShopName}");
        AppendMeta(html, encoder, "Destination", $"{transfer.DestinationShopCode} — {transfer.DestinationShopName}");
        AppendMeta(html, encoder, "Created by", transfer.CreatedByDisplayName);
        AppendMeta(html, encoder, "Created", transfer.CreatedAtUtc.ToString("u"));
        html.Append("</div><p><strong>Notes:</strong> ")
            .Append(encoder.Encode(transfer.Notes))
            .Append("</p><table><thead><tr><th>SKU</th><th>Product</th><th>Requested</th><th>Dispatched</th><th>Received</th><th>Damaged / Missing</th><th>Outstanding</th></tr></thead><tbody>");
        foreach (StockTransferItemRecord item in transfer.Items)
        {
            html.Append("<tr><td>").Append(encoder.Encode(item.Sku))
                .Append("</td><td>").Append(encoder.Encode(item.ProductName))
                .Append("</td><td>").Append(item.RequestedQuantityBaseUnits)
                .Append("</td><td>").Append(item.DispatchedQuantityBaseUnits)
                .Append("</td><td>").Append(item.ReceivedQuantityBaseUnits)
                .Append("</td><td>").Append(item.DamagedQuantityBaseUnits)
                .Append("</td><td>").Append(item.OutstandingQuantityBaseUnits)
                .Append("</td></tr>");
        }
        html.Append("</tbody></table><h2>Audit history</h2><table><thead><tr><th>Time</th><th>Event</th><th>User</th><th>Status</th></tr></thead><tbody>");
        foreach (StockTransferEventRecord item in transfer.Events)
        {
            html.Append("<tr><td>").Append(encoder.Encode(item.OccurredAtUtc.ToString("u")))
                .Append("</td><td>").Append(encoder.Encode(item.EventType))
                .Append("</td><td>").Append(encoder.Encode(item.PerformedByDisplayName))
                .Append("</td><td>").Append(encoder.Encode(item.ToStatus))
                .Append("</td></tr>");
        }
        html.Append("</tbody></table><p>Generated by Nexus POS, a product of Ecatu Ronald — www.ecaturonald.tech</p></body></html>");
        return html.ToString();
    }

    private async Task<StockTransferRecord> ChangeSimpleStatusAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string transferId,
        int expectedVersion,
        string fromStatus,
        string toStatus,
        string eventType,
        string? notes,
        bool requireManager,
        CancellationToken cancellationToken)
    {
        string id = NormalizeId(transferId);
        RequireExpectedVersion(expectedVersion);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        TransferHeader header = await RequireHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        RequireSourceContext(header, context);
        RequireStatus(header, fromStatus);
        RequireVersion(header, expectedVersion);
        await RequireShopPermissionAsync(
            connection,
            transaction,
            user,
            header.SourceShopId,
            allowSupervisor: !requireManager,
            cancellationToken);

        if (fromStatus == "draft")
        {
            long itemCount = await CountItemsAsync(
                connection,
                transaction,
                id,
                cancellationToken);
            if (itemCount == 0)
            {
                throw Conflict(
                    "stock_transfer_items_required",
                    "Add at least one product before submitting the transfer.");
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE stock_transfers
        SET status = $toStatus,
            submitted_by_user_id = CASE
                WHEN $toStatus = 'submitted' THEN $userId
                ELSE submitted_by_user_id
            END,
            submitted_at_utc = CASE
                WHEN $toStatus = 'submitted' THEN $updatedAtUtc
                ELSE submitted_at_utc
            END,
            updated_at_utc = $updatedAtUtc,
            version = version + 1
        WHERE id = $id
          AND status = $fromStatus
          AND version = $expectedVersion;
        """;
        update.Parameters.AddWithValue("$toStatus", toStatus);
        update.Parameters.AddWithValue("$userId", user.Id);
        update.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$fromStatus", fromStatus);
        update.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "stock_transfer_changed",
                "The stock transfer changed. Reload it and try again.");
        }

        await WriteEventAndAuditAsync(
            connection,
            transaction,
            user,
            id,
            header.TransferNumber,
            eventType,
            fromStatus,
            toStatus,
            new { notes = NormalizeNotes(notes, 500) },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetAsync(user, context, id, cancellationToken);
    }

    private async Task<StockTransferRecord> CancelOrRejectAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string transferId,
        CancelStockTransferRequest request,
        string? requiredStatus,
        string cancellationKind,
        CancellationToken cancellationToken)
    {
        string id = NormalizeId(transferId);
        RequireExpectedVersion(request.ExpectedVersion);
        string reason = NormalizeReason(request.Reason);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        TransferHeader header = await RequireHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        RequireSourceContext(header, context);
        RequireVersion(header, request.ExpectedVersion);
        if (requiredStatus is not null)
        {
            RequireStatus(header, requiredStatus);
        }
        else if (header.Status is not ("draft" or "submitted" or "approved"))
        {
            throw Conflict(
                "stock_transfer_not_cancellable",
                "Only a draft, submitted or approved transfer can be cancelled.");
        }

        await RequireShopPermissionAsync(
            connection,
            transaction,
            user,
            header.SourceShopId,
            allowSupervisor: header.Status is "draft" or "submitted",
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (header.Status == "approved")
        {
            IReadOnlyList<ReleaseLine> lines = await ReadReleaseLinesAsync(
                connection,
                transaction,
                id,
                header.SourceShopId,
                cancellationToken);
            foreach (ReleaseLine line in lines)
            {
                if (line.ReservedQuantity <= 0)
                {
                    continue;
                }

                await using var release = connection.CreateCommand();
                release.Transaction = transaction;
                release.CommandText =
                """
                UPDATE shop_stock_balances
                SET reserved_base_units = reserved_base_units - $quantity,
                    version = version + 1,
                    updated_at_utc = $updatedAtUtc
                WHERE shop_id = $shopId
                  AND product_id = $productId
                  AND reserved_base_units >= $quantity;
                """;
                release.Parameters.AddWithValue("$quantity", line.ReservedQuantity);
                release.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
                release.Parameters.AddWithValue("$shopId", header.SourceShopId);
                release.Parameters.AddWithValue("$productId", line.ProductId);
                if (await release.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw Conflict(
                        "transfer_reservation_changed",
                        $"The reservation for {line.ProductName} changed. Reload and try again.");
                }
            }

            await using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText =
            """
            UPDATE stock_transfer_items
            SET reserved_quantity_base_units = 0
            WHERE transfer_id = $transferId;
            """;
            clear.Parameters.AddWithValue("$transferId", id);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cancel = connection.CreateCommand())
        {
            cancel.Transaction = transaction;
            cancel.CommandText =
            """
            UPDATE stock_transfers
            SET status = 'cancelled',
                cancelled_by_user_id = $cancelledByUserId,
                cancelled_at_utc = $cancelledAtUtc,
                cancellation_kind = $cancellationKind,
                cancellation_reason = $reason,
                updated_at_utc = $cancelledAtUtc,
                version = version + 1
            WHERE id = $id
              AND status = $currentStatus
              AND version = $expectedVersion;
            """;
            cancel.Parameters.AddWithValue("$cancelledByUserId", user.Id);
            cancel.Parameters.AddWithValue("$cancelledAtUtc", now.ToString("O"));
            cancel.Parameters.AddWithValue("$cancellationKind", cancellationKind);
            cancel.Parameters.AddWithValue("$reason", reason);
            cancel.Parameters.AddWithValue("$id", id);
            cancel.Parameters.AddWithValue("$currentStatus", header.Status);
            cancel.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
            if (await cancel.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "stock_transfer_changed",
                    "The stock transfer changed. Reload it and try again.");
            }
        }

        string eventType = cancellationKind == "rejected"
            ? "transfer.rejected"
            : "transfer.cancelled";
        await WriteEventAndAuditAsync(
            connection,
            transaction,
            user,
            id,
            header.TransferNumber,
            eventType,
            header.Status,
            "cancelled",
            new { cancellationKind, reason },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetAsync(user, context, id, cancellationToken);
    }

    private static async Task<TransferHeader?> ReadHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string transferId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            transfer.id,
            transfer.transfer_number,
            source.organization_id,
            source.id,
            source.code,
            source.name,
            destination.id,
            destination.code,
            destination.name,
            transfer.status,
            transfer.notes,
            transfer.created_by_user_id,
            COALESCE(created.display_name, ''),
            transfer.submitted_by_user_id,
            transfer.approved_by_user_id,
            transfer.dispatched_by_user_id,
            transfer.received_by_user_id,
            transfer.cancelled_by_user_id,
            transfer.cancellation_kind,
            transfer.cancellation_reason,
            transfer.created_at_utc,
            transfer.submitted_at_utc,
            transfer.approved_at_utc,
            transfer.dispatched_at_utc,
            transfer.received_at_utc,
            transfer.cancelled_at_utc,
            COALESCE(transfer.updated_at_utc, transfer.created_at_utc),
            transfer.version
        FROM stock_transfers AS transfer
        INNER JOIN shops AS source
            ON source.id = transfer.source_shop_id
        INNER JOIN shops AS destination
            ON destination.id = transfer.destination_shop_id
        LEFT JOIN users AS created
            ON created.id = transfer.created_by_user_id
        WHERE transfer.id = $transferId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$transferId", transferId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TransferHeader(
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
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            GetNullableString(reader, 13),
            GetNullableString(reader, 14),
            GetNullableString(reader, 15),
            GetNullableString(reader, 16),
            GetNullableString(reader, 17),
            GetNullableString(reader, 18),
            GetNullableString(reader, 19),
            ParseDate(reader.GetString(20)),
            GetNullableDate(reader, 21),
            GetNullableDate(reader, 22),
            GetNullableDate(reader, 23),
            GetNullableDate(reader, 24),
            GetNullableDate(reader, 25),
            ParseDate(reader.GetString(26)),
            reader.GetInt32(27));
    }

    private static async Task<TransferHeader> RequireHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string transferId,
        CancellationToken cancellationToken)
    {
        TransferHeader? header = await ReadHeaderAsync(
            connection,
            transaction,
            transferId,
            cancellationToken);
        if (header is null ||
            !string.Equals(
                header.OrganizationId,
                context.OrganizationId,
                StringComparison.Ordinal))
        {
            throw NotFound(
                "stock_transfer_not_found",
                "The stock transfer could not be found.");
        }

        return header;
    }

    private static async Task<IReadOnlyList<StockTransferItemRecord>>
        ReadItemsAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string transferId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            item.id,
            item.product_id,
            product.sku,
            product.name,
            item.quantity_base_units,
            item.reserved_quantity_base_units,
            item.dispatched_quantity_base_units,
            item.received_quantity_base_units,
            item.damaged_quantity_base_units,
            MAX(
                item.dispatched_quantity_base_units -
                item.received_quantity_base_units -
                item.damaged_quantity_base_units,
                0),
            item.unit_cost_minor,
            item.discrepancy_reason,
            item.source_balance_before,
            item.source_balance_after,
            item.destination_before,
            item.destination_after
        FROM stock_transfer_items AS item
        INNER JOIN products AS product
            ON product.id = item.product_id
        WHERE item.transfer_id = $transferId
        ORDER BY product.name COLLATE NOCASE, product.sku COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$transferId", transferId);

        var items = new List<StockTransferItemRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new StockTransferItemRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetString(11),
                GetNullableLong(reader, 12),
                GetNullableLong(reader, 13),
                GetNullableLong(reader, 14),
                GetNullableLong(reader, 15)));
        }

        return items;
    }

    private static async Task<IReadOnlyList<StockTransferEventRecord>>
        ReadEventsAsync(
            SqliteConnection connection,
            string transferId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            event.id,
            event.event_type,
            event.from_status,
            event.to_status,
            event.details_json,
            event.performed_by_user_id,
            COALESCE(user.display_name, ''),
            event.occurred_at_utc
        FROM stock_transfer_events AS event
        LEFT JOIN users AS user
            ON user.id = event.performed_by_user_id
        WHERE event.transfer_id = $transferId
        ORDER BY event.occurred_at_utc, event.id;
        """;
        command.Parameters.AddWithValue("$transferId", transferId);

        var events = new List<StockTransferEventRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new StockTransferEventRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                GetNullableString(reader, 2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                ParseDate(reader.GetString(7))));
        }

        return events;
    }

    private static StockTransferRecord ToRecord(
        TransferHeader header,
        IReadOnlyList<StockTransferItemRecord> items,
        IReadOnlyList<StockTransferEventRecord> events)
    {
        return new StockTransferRecord(
            header.Id,
            header.TransferNumber,
            header.OrganizationId,
            header.SourceShopId,
            header.SourceShopCode,
            header.SourceShopName,
            header.DestinationShopId,
            header.DestinationShopCode,
            header.DestinationShopName,
            header.Status,
            header.Notes,
            header.CreatedByUserId,
            header.CreatedByDisplayName,
            header.SubmittedByUserId,
            header.ApprovedByUserId,
            header.DispatchedByUserId,
            header.ReceivedByUserId,
            header.CancelledByUserId,
            header.CancellationKind,
            header.CancellationReason,
            header.CreatedAtUtc,
            header.SubmittedAtUtc,
            header.ApprovedAtUtc,
            header.DispatchedAtUtc,
            header.ReceivedAtUtc,
            header.CancelledAtUtc,
            header.UpdatedAtUtc,
            header.Version,
            items.Sum(item => item.RequestedQuantityBaseUnits),
            items.Sum(item => item.ReservedQuantityBaseUnits),
            items.Sum(item => item.DispatchedQuantityBaseUnits),
            items.Sum(item => item.ReceivedQuantityBaseUnits),
            items.Sum(item => item.DamagedQuantityBaseUnits),
            items.Sum(item => item.OutstandingQuantityBaseUnits),
            items,
            events);
    }

    private static async Task ReplaceItemsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string transferId,
        IReadOnlyList<NormalizedItem> items,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
            """
            DELETE FROM stock_transfer_items
            WHERE transfer_id = $transferId;
            """;
            delete.Parameters.AddWithValue("$transferId", transferId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (NormalizedItem item in items)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
            """
            INSERT INTO stock_transfer_items
            (
                transfer_id,
                product_id,
                quantity_base_units
            )
            VALUES
            (
                $transferId,
                $productId,
                $quantity
            );
            """;
            insert.Parameters.AddWithValue("$transferId", transferId);
            insert.Parameters.AddWithValue("$productId", item.ProductId);
            insert.Parameters.AddWithValue("$quantity", item.Quantity);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ValidateProductsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<NormalizedItem> items,
        CancellationToken cancellationToken)
    {
        foreach (NormalizedItem item in items)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            SELECT COUNT(1)
            FROM products
            WHERE id = $productId
              AND is_active = 1;
            """;
            command.Parameters.AddWithValue("$productId", item.ProductId);
            int count = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken));
            if (count != 1)
            {
                throw Validation(
                    "transfer_product_not_available",
                    "Every transfer item must reference an active product.");
            }
        }
    }

    private static async Task<ShopIdentity?> ReadShopAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string shopId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT id, organization_id, code, name, is_active
        FROM shops
        WHERE id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ShopIdentity(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4) == 1)
            : null;
    }

    private static async Task<string> NextTransferNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceShopId,
        string sourceShopCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = transaction;
            ensure.CommandText =
            """
            INSERT OR IGNORE INTO shop_transfer_sequences
            (
                shop_id,
                next_value,
                updated_at_utc
            )
            VALUES
            (
                $shopId,
                1,
                $updatedAtUtc
            );
            """;
            ensure.Parameters.AddWithValue("$shopId", sourceShopId);
            ensure.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        long nextValue;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
            """
            SELECT next_value
            FROM shop_transfer_sequences
            WHERE shop_id = $shopId;
            """;
            read.Parameters.AddWithValue("$shopId", sourceShopId);
            nextValue = Convert.ToInt64(
                await read.ExecuteScalarAsync(cancellationToken));
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE shop_transfer_sequences
            SET next_value = next_value + 1,
                updated_at_utc = $updatedAtUtc
            WHERE shop_id = $shopId
              AND next_value = $expectedValue;
            """;
            update.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            update.Parameters.AddWithValue("$shopId", sourceShopId);
            update.Parameters.AddWithValue("$expectedValue", nextValue);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "transfer_number_conflict",
                    "Another transfer was created simultaneously. Try again.");
            }
        }

        string code = new(
            sourceShopCode
                .Trim()
                .ToUpperInvariant()
                .Where(character =>
                    char.IsLetterOrDigit(character) || character == '-')
                .Take(24)
                .ToArray());
        if (code.Length == 0)
        {
            code = "SHOP";
        }

        return $"TRF-{code}-{now:yyyyMMdd}-{nextValue:000000}";
    }

    private static async Task RequireShopPermissionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string shopId,
        bool allowSupervisor,
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

        bool allowed = string.Equals(
            accessLevel,
            "manager",
            StringComparison.OrdinalIgnoreCase) ||
            (allowSupervisor && string.Equals(
                accessLevel,
                "supervisor",
                StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            throw Forbidden(
                "stock_transfer_permission_required",
                allowSupervisor
                    ? "A shop supervisor, manager or administrator is required."
                    : "A shop manager or administrator is required.");
        }
    }

    private static async Task<IReadOnlyList<ReservationLine>>
        ReadReservationLinesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string transferId,
            string sourceShopId,
            CancellationToken cancellationToken)
    {
        await EnsureTransferBalancesAsync(
            connection,
            transaction,
            transferId,
            sourceShopId,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            item.id,
            item.product_id,
            product.name,
            item.quantity_base_units,
            product.cost_price_minor,
            balance.quantity_base_units - balance.reserved_base_units,
            balance.version
        FROM stock_transfer_items AS item
        INNER JOIN products AS product
            ON product.id = item.product_id
        INNER JOIN shop_stock_balances AS balance
            ON balance.shop_id = $sourceShopId
           AND balance.product_id = item.product_id
        WHERE item.transfer_id = $transferId
        ORDER BY item.id;
        """;
        command.Parameters.AddWithValue("$sourceShopId", sourceShopId);
        command.Parameters.AddWithValue("$transferId", transferId);

        var lines = new List<ReservationLine>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new ReservationLine(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt32(6)));
        }
        return lines;
    }

    private static async Task<IReadOnlyList<DispatchLine>>
        ReadDispatchLinesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string transferId,
            string sourceShopId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            item.id,
            item.product_id,
            product.name,
            item.quantity_base_units,
            item.reserved_quantity_base_units,
            item.unit_cost_minor,
            balance.quantity_base_units,
            balance.version
        FROM stock_transfer_items AS item
        INNER JOIN products AS product
            ON product.id = item.product_id
        INNER JOIN shop_stock_balances AS balance
            ON balance.shop_id = $sourceShopId
           AND balance.product_id = item.product_id
        WHERE item.transfer_id = $transferId
        ORDER BY item.id;
        """;
        command.Parameters.AddWithValue("$sourceShopId", sourceShopId);
        command.Parameters.AddWithValue("$transferId", transferId);

        var lines = new List<DispatchLine>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new DispatchLine(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt32(7)));
        }
        return lines;
    }

    private static async Task<IReadOnlyList<ReceivingLine>>
        ReadReceivingLinesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string transferId,
            string destinationShopId,
            CancellationToken cancellationToken)
    {
        await EnsureTransferBalancesAsync(
            connection,
            transaction,
            transferId,
            destinationShopId,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            item.id,
            item.product_id,
            product.name,
            item.dispatched_quantity_base_units,
            item.received_quantity_base_units,
            item.damaged_quantity_base_units,
            item.unit_cost_minor,
            balance.quantity_base_units,
            balance.version
        FROM stock_transfer_items AS item
        INNER JOIN products AS product
            ON product.id = item.product_id
        INNER JOIN shop_stock_balances AS balance
            ON balance.shop_id = $destinationShopId
           AND balance.product_id = item.product_id
        WHERE item.transfer_id = $transferId
        ORDER BY item.id;
        """;
        command.Parameters.AddWithValue("$destinationShopId", destinationShopId);
        command.Parameters.AddWithValue("$transferId", transferId);

        var lines = new List<ReceivingLine>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new ReceivingLine(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt32(8)));
        }
        return lines;
    }

    private static async Task<IReadOnlyList<ReleaseLine>>
        ReadReleaseLinesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string transferId,
            string sourceShopId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            item.product_id,
            product.name,
            item.reserved_quantity_base_units
        FROM stock_transfer_items AS item
        INNER JOIN products AS product
            ON product.id = item.product_id
        WHERE item.transfer_id = $transferId;
        """;
        command.Parameters.AddWithValue("$transferId", transferId);

        var lines = new List<ReleaseLine>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new ReleaseLine(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2)));
        }
        return lines;
    }

    private static async Task EnsureTransferBalancesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string transferId,
        string shopId,
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
            item.product_id,
            0,
            0,
            1,
            $updatedAtUtc
        FROM stock_transfer_items AS item
        WHERE item.transfer_id = $transferId;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$transferId", transferId);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ReadOutstandingQuantityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string transferId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COALESCE(SUM(
            dispatched_quantity_base_units -
            received_quantity_base_units -
            damaged_quantity_base_units), 0)
        FROM stock_transfer_items
        WHERE transfer_id = $transferId;
        """;
        command.Parameters.AddWithValue("$transferId", transferId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<long> CountItemsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string transferId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COUNT(*)
        FROM stock_transfer_items
        WHERE transfer_id = $transferId;
        """;
        command.Parameters.AddWithValue("$transferId", transferId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task WriteEventAndAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string transferId,
        string transferNumber,
        string eventType,
        string? fromStatus,
        string toStatus,
        object details,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string detailsJson = JsonSerializer.Serialize(details);
        await using (var eventCommand = connection.CreateCommand())
        {
            eventCommand.Transaction = transaction;
            eventCommand.CommandText =
            """
            INSERT INTO stock_transfer_events
            (
                transfer_id,
                event_type,
                from_status,
                to_status,
                details_json,
                performed_by_user_id,
                occurred_at_utc
            )
            VALUES
            (
                $transferId,
                $eventType,
                $fromStatus,
                $toStatus,
                $detailsJson,
                $performedByUserId,
                $occurredAtUtc
            );
            """;
            eventCommand.Parameters.AddWithValue("$transferId", transferId);
            eventCommand.Parameters.AddWithValue("$eventType", eventType);
            eventCommand.Parameters.AddWithValue(
                "$fromStatus",
                fromStatus ?? (object)DBNull.Value);
            eventCommand.Parameters.AddWithValue("$toStatus", toStatus);
            eventCommand.Parameters.AddWithValue("$detailsJson", detailsJson);
            eventCommand.Parameters.AddWithValue("$performedByUserId", user.Id);
            eventCommand.Parameters.AddWithValue("$occurredAtUtc", now.ToString("O"));
            await eventCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText =
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
            'stock_transfer',
            $transferId,
            1,
            $detailsJson,
            NULL
        );
        """;
        audit.Parameters.AddWithValue("$occurredAtUtc", now.ToString("O"));
        audit.Parameters.AddWithValue("$userId", user.Id);
        audit.Parameters.AddWithValue("$username", user.Username);
        audit.Parameters.AddWithValue("$eventType", eventType);
        audit.Parameters.AddWithValue("$transferId", transferId);
        audit.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(new
            {
                transferNumber,
                fromStatus,
                toStatus,
                details
            }));
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<NormalizedItem> NormalizeItems(
        IReadOnlyList<StockTransferItemInput>? items)
    {
        if (items is null || items.Count == 0)
        {
            throw Validation(
                "stock_transfer_items_required",
                "Add at least one product to the transfer.");
        }
        if (items.Count > 250)
        {
            throw Validation(
                "too_many_transfer_items",
                "A transfer cannot contain more than 250 product lines.");
        }

        var grouped = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (StockTransferItemInput item in items)
        {
            string productId = NormalizeId(item.ProductId);
            if (item.QuantityBaseUnits <= 0)
            {
                throw Validation(
                    "invalid_transfer_quantity",
                    "Every transfer quantity must be greater than zero.");
            }
            grouped[productId] = checked(
                grouped.GetValueOrDefault(productId) +
                item.QuantityBaseUnits);
        }

        return grouped
            .Select(pair => new NormalizedItem(pair.Key, pair.Value))
            .OrderBy(item => item.ProductId, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<NormalizedReceipt> NormalizeReceipts(
        IReadOnlyList<ReceiveStockTransferItemRequest>? items)
    {
        if (items is null)
        {
            return Array.Empty<NormalizedReceipt>();
        }
        if (items.Count > 250)
        {
            throw Validation(
                "too_many_transfer_items",
                "A receiving transaction cannot contain more than 250 product lines.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<NormalizedReceipt>();
        foreach (ReceiveStockTransferItemRequest item in items)
        {
            string productId = NormalizeId(item.ProductId);
            if (!seen.Add(productId))
            {
                throw Validation(
                    "duplicate_received_product",
                    "Each product may appear only once in a receiving transaction.");
            }
            if (item.QuantityReceivedBaseUnits < 0 ||
                item.QuantityDamagedBaseUnits < 0)
            {
                throw Validation(
                    "invalid_received_quantity",
                    "Received and damaged quantities cannot be negative.");
            }

            results.Add(new NormalizedReceipt(
                productId,
                item.QuantityReceivedBaseUnits,
                item.QuantityDamagedBaseUnits,
                NormalizeNotes(item.DiscrepancyReason, 500)));
        }
        return results;
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

    private static string NormalizeNotes(string? value, int maximumLength)
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

    private static string NormalizeReason(string? value)
    {
        string reason = value?.Trim() ?? string.Empty;
        if (reason.Length is < 5 or > 500)
        {
            throw Validation(
                "transfer_reason_required",
                "Enter a clear reason containing 5 to 500 characters.");
        }
        return reason;
    }

    private static void RequireExpectedVersion(int version)
    {
        if (version < 1)
        {
            throw Validation(
                "invalid_transfer_version",
                "The expected transfer version is invalid.");
        }
    }

    private static void RequireVersion(
        TransferHeader header,
        int expectedVersion)
    {
        if (header.Version != expectedVersion)
        {
            throw Conflict(
                "stock_transfer_changed",
                "The stock transfer changed. Reload it and try again.");
        }
    }

    private static void RequireStatus(
        TransferHeader header,
        string expectedStatus)
    {
        if (!string.Equals(
                header.Status,
                expectedStatus,
                StringComparison.Ordinal))
        {
            throw Conflict(
                "invalid_transfer_status",
                $"This operation requires a {expectedStatus} transfer.");
        }
    }

    private static void RequireSourceContext(
        TransferHeader header,
        ActiveShopContextRecord context)
    {
        if (!string.Equals(
                header.SourceShopId,
                context.ShopId,
                StringComparison.Ordinal))
        {
            throw Forbidden(
                "source_shop_context_required",
                "Switch to the transfer source shop before performing this operation.");
        }
    }

    private static void RequireDestinationContext(
        TransferHeader header,
        ActiveShopContextRecord context)
    {
        if (!string.Equals(
                header.DestinationShopId,
                context.ShopId,
                StringComparison.Ordinal))
        {
            throw Forbidden(
                "destination_shop_context_required",
                "Switch to the transfer destination shop before receiving it.");
        }
    }

    private static void AppendMeta(
        StringBuilder html,
        HtmlEncoder encoder,
        string label,
        string value)
    {
        html.Append("<div><strong>")
            .Append(encoder.Encode(label))
            .Append(":</strong> ")
            .Append(encoder.Encode(value))
            .Append("</div>");
    }

    private static bool IsAdministrator(AuthenticatedUser user) =>
        string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value).ToUniversalTime();

    private static DateTimeOffset? GetNullableDate(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : ParseDate(reader.GetString(ordinal));

    private static string? GetNullableString(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableLong(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static StockTransferException Validation(
        string code,
        string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    private static StockTransferException Forbidden(
        string code,
        string message) =>
        new(StatusCodes.Status403Forbidden, code, message);

    private static StockTransferException NotFound(
        string code,
        string message) =>
        new(StatusCodes.Status404NotFound, code, message);

    private static StockTransferException Conflict(
        string code,
        string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private sealed record NormalizedItem(
        string ProductId,
        long Quantity);

    private sealed record NormalizedReceipt(
        string ProductId,
        long ReceivedQuantity,
        long DamagedQuantity,
        string Reason);

    private sealed record ShopIdentity(
        string Id,
        string OrganizationId,
        string Code,
        string Name,
        bool IsActive);

    private sealed record ReservationLine(
        long ItemId,
        string ProductId,
        string ProductName,
        long RequestedQuantity,
        long UnitCostMinor,
        long AvailableQuantity,
        int StockVersion);

    private sealed record DispatchLine(
        long ItemId,
        string ProductId,
        string ProductName,
        long RequestedQuantity,
        long ReservedQuantity,
        long UnitCostMinor,
        long SourceBalance,
        int StockVersion);

    private sealed record ReceivingLine(
        long ItemId,
        string ProductId,
        string ProductName,
        long DispatchedQuantity,
        long ReceivedQuantity,
        long DamagedQuantity,
        long UnitCostMinor,
        long DestinationBalance,
        int DestinationStockVersion);

    private sealed record ReleaseLine(
        string ProductId,
        string ProductName,
        long ReservedQuantity);

    private sealed record TransferHeader(
        string Id,
        string TransferNumber,
        string OrganizationId,
        string SourceShopId,
        string SourceShopCode,
        string SourceShopName,
        string DestinationShopId,
        string DestinationShopCode,
        string DestinationShopName,
        string Status,
        string Notes,
        string CreatedByUserId,
        string CreatedByDisplayName,
        string? SubmittedByUserId,
        string? ApprovedByUserId,
        string? DispatchedByUserId,
        string? ReceivedByUserId,
        string? CancelledByUserId,
        string? CancellationKind,
        string? CancellationReason,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? SubmittedAtUtc,
        DateTimeOffset? ApprovedAtUtc,
        DateTimeOffset? DispatchedAtUtc,
        DateTimeOffset? ReceivedAtUtc,
        DateTimeOffset? CancelledAtUtc,
        DateTimeOffset UpdatedAtUtc,
        int Version);
}