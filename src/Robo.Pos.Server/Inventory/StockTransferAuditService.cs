using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Inventory;

public sealed record StockTransferAuditLineRecord(
    long Id,
    long? EventId,
    string SnapshotKind,
    string EventType,
    string EventDetailsJson,
    string? PerformedByUserId,
    string PerformedByDisplayName,
    DateTimeOffset CapturedAtUtc,
    string ProductId,
    string Sku,
    string ProductName,
    long RequestedQuantityBaseUnits,
    long ReservedQuantityBaseUnits,
    long DispatchedQuantityBaseUnits,
    long CumulativeReceivedBaseUnits,
    long CumulativeDamagedBaseUnits,
    long ReceivedDeltaBaseUnits,
    long DamagedDeltaBaseUnits,
    long OutstandingQuantityBaseUnits,
    long UnitCostMinor,
    string DiscrepancyReason,
    long? SourceBalanceBefore,
    long? SourceBalanceAfter,
    long? DestinationBalanceBefore,
    long? DestinationBalanceAfter);

public sealed record StockTransferAuditTrailRecord(
    string TransferId,
    string TransferNumber,
    string SourceShopId,
    string DestinationShopId,
    IReadOnlyList<StockTransferAuditLineRecord> Lines);

public sealed class StockTransferAuditService
{
    private readonly DatabaseBootstrap _database;

    public StockTransferAuditService(DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task<StockTransferAuditTrailRecord> GetAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string transferId,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(transferId);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        TransferAuditHeader? header = await ReadHeaderAsync(
            connection,
            id,
            cancellationToken);
        if (header is null ||
            !string.Equals(
                header.OrganizationId,
                context.OrganizationId,
                StringComparison.Ordinal))
        {
            throw NotFound();
        }

        bool involved =
            string.Equals(
                header.SourceShopId,
                context.ShopId,
                StringComparison.Ordinal) ||
            string.Equals(
                header.DestinationShopId,
                context.ShopId,
                StringComparison.Ordinal);
        if (!involved && !IsAdministrator(user))
        {
            throw NotFound();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            audit.id,
            audit.event_id,
            audit.snapshot_kind,
            COALESCE(workflow_event.event_type, 'migration.baseline'),
            COALESCE(workflow_event.details_json, '{}'),
            workflow_event.performed_by_user_id,
            COALESCE(performed.display_name, ''),
            audit.captured_at_utc,
            audit.product_id,
            product.sku,
            product.name,
            audit.requested_quantity_base_units,
            audit.reserved_quantity_base_units,
            audit.dispatched_quantity_base_units,
            audit.cumulative_received_base_units,
            audit.cumulative_damaged_base_units,
            audit.received_delta_base_units,
            audit.damaged_delta_base_units,
            audit.outstanding_quantity_base_units,
            audit.unit_cost_minor,
            audit.discrepancy_reason,
            audit.source_balance_before,
            audit.source_balance_after,
            audit.destination_balance_before,
            audit.destination_balance_after
        FROM stock_transfer_audit_lines AS audit
        INNER JOIN products AS product
            ON product.id = audit.product_id
        LEFT JOIN stock_transfer_events AS workflow_event
            ON workflow_event.id = audit.event_id
        LEFT JOIN users AS performed
            ON performed.id = workflow_event.performed_by_user_id
        WHERE audit.transfer_id = $transferId
        ORDER BY audit.captured_at_utc, audit.id;
        """;
        command.Parameters.AddWithValue("$transferId", id);

        var lines = new List<StockTransferAuditLineRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new StockTransferAuditLineRecord(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                ParseDate(reader.GetString(7)),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.GetInt64(14),
                reader.GetInt64(15),
                reader.GetInt64(16),
                reader.GetInt64(17),
                reader.GetInt64(18),
                reader.GetInt64(19),
                reader.GetString(20),
                GetNullableLong(reader, 21),
                GetNullableLong(reader, 22),
                GetNullableLong(reader, 23),
                GetNullableLong(reader, 24)));
        }

        return new StockTransferAuditTrailRecord(
            id,
            header.TransferNumber,
            header.SourceShopId,
            header.DestinationShopId,
            lines);
    }

    private static async Task<TransferAuditHeader?> ReadHeaderAsync(
        SqliteConnection connection,
        string transferId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            transfer.transfer_number,
            source.organization_id,
            transfer.source_shop_id,
            transfer.destination_shop_id
        FROM stock_transfers AS transfer
        INNER JOIN shops AS source
            ON source.id = transfer.source_shop_id
        INNER JOIN shops AS destination
            ON destination.id = transfer.destination_shop_id
           AND destination.organization_id = source.organization_id
        WHERE transfer.id = $transferId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$transferId", transferId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TransferAuditHeader(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3))
            : null;
    }

    private static string NormalizeId(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 100)
        {
            throw new StockTransferException(
                StatusCodes.Status400BadRequest,
                "invalid_identifier",
                "The supplied identifier is invalid.");
        }

        return normalized;
    }

    private static StockTransferException NotFound() =>
        new(
            StatusCodes.Status404NotFound,
            "stock_transfer_not_found",
            "The stock transfer could not be found.");

    private static bool IsAdministrator(AuthenticatedUser user) =>
        string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value).ToUniversalTime();

    private static long? GetNullableLong(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private sealed record TransferAuditHeader(
        string TransferNumber,
        string OrganizationId,
        string SourceShopId,
        string DestinationShopId);
}
