using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public sealed class CashDrawerService
{
    private readonly DatabaseBootstrap _database;

    public CashDrawerService(DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task<CashDrawerSnapshot> GetCurrentAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        string shiftId = await GetOpenShiftIdAsync(connection, null, user.Id, context.ShopId, cancellationToken);

        long opening = await ScalarAsync(connection, null,
            "SELECT opening_cash_minor FROM teller_shifts WHERE id = $shiftId;",
            ("$shiftId", shiftId), cancellationToken);
        long cashSales = await ScalarAsync(connection, null,
            """
            SELECT COALESCE(SUM(payment.amount_minor), 0)
            FROM sale_payments AS payment
            INNER JOIN sales AS sale ON sale.id = payment.sale_id
            WHERE sale.shift_id = $shiftId
              AND sale.shop_id = $shopId
              AND sale.status IN ('completed', 'partially_returned', 'returned')
              AND payment.payment_method = 'cash';
            """,
            ("$shiftId", shiftId), ("$shopId", context.ShopId), cancellationToken);
        long refunds = await ScalarAsync(connection, null,
            """
            SELECT COALESCE(SUM(refund_amount_minor), 0)
            FROM sales_returns
            WHERE shift_id = $shiftId
              AND shop_id = $shopId
              AND status = 'completed'
              AND refund_method = 'cash';
            """,
            ("$shiftId", shiftId), ("$shopId", context.ShopId), cancellationToken);
        long floatIn = await MovementTotalAsync(connection, null, shiftId, "float_in", cancellationToken);
        long safeDrop = await MovementTotalAsync(connection, null, shiftId, "safe_drop", cancellationToken);

        return new CashDrawerSnapshot(
            shiftId,
            context.ShopId,
            context.ShopCode,
            context.ShopName,
            opening,
            cashSales,
            refunds,
            floatIn,
            safeDrop,
            checked(opening + cashSales - refunds + floatIn - safeDrop),
            await ReadMovementsAsync(connection, shiftId, cancellationToken),
            await ReadCountsAsync(connection, shiftId, cancellationToken));
    }

    public async Task<CashDrawerMovementRecord> CreateMovementAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateCashDrawerMovementRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string type = request.MovementType.Trim().ToLowerInvariant();
        if (type is not ("float_in" or "safe_drop"))
            throw Validation("invalid_drawer_movement", "Use float_in or safe_drop.");
        if (request.AmountMinor <= 0)
            throw Validation("invalid_drawer_amount", "Drawer movement amount must be greater than zero.");
        string reason = Required(request.Reason, 250, "drawer_reason_required", "Enter the reason.");
        string reference = Optional(request.Reference, 100);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        string shiftId = await GetOpenShiftIdAsync(connection, transaction, user.Id, context.ShopId, cancellationToken);

        if (type == "safe_drop")
        {
            CashDrawerSnapshot snapshot = await GetCurrentWithinTransactionAsync(
                connection, transaction, user, context, shiftId, cancellationToken);
            if (request.AmountMinor > snapshot.ExpectedDrawerCashMinor)
                throw Conflict("safe_drop_exceeds_drawer", "The safe drop exceeds expected drawer cash.");
        }

        string id = Guid.NewGuid().ToString("N");
        string number = $"CDM-{DateTimeOffset.UtcNow:yyyyMMdd}-{id[..8].ToUpperInvariant()}";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO cash_drawer_movements
        (id, organization_id, shop_id, shift_id, movement_number, movement_type,
         amount_minor, reason, reference, status, created_by_user_id,
         approved_by_user_id, created_at_utc)
        VALUES
        ($id, $organizationId, $shopId, $shiftId, $number, $type,
         $amount, $reason, $reference, 'completed', $userId, $userId, $createdAtUtc);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$shiftId", shiftId);
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$amount", request.AmountMinor);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$reference", reference);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await WriteAuditAsync(connection, transaction, user, "cash_drawer.movement.completed", id,
            new { context.OrganizationId, context.ShopId, shiftId, number, type, request.AmountMinor, reason, reference },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CashDrawerMovementRecord(id, number, type, request.AmountMinor, reason,
            reference, user.DisplayName, user.DisplayName, now);
    }

    public async Task<CashCountRecord> RecordCountAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        RecordCashCountRequest request,
        CancellationToken cancellationToken = default)
    {
        string countType = request.CountType.Trim().ToLowerInvariant();
        if (countType is not ("interim" or "closing"))
            throw Validation("invalid_cash_count_type", "Use interim or closing.");
        var lines = (request.Denominations ?? Array.Empty<CashDenominationLine>())
            .Where(line => line.DenominationMinor > 0 && line.Quantity >= 0)
            .GroupBy(line => line.DenominationMinor)
            .Select(group => new CashDenominationLine(group.Key, group.Sum(line => line.Quantity)))
            .OrderByDescending(line => line.DenominationMinor)
            .ToList();
        if (lines.Count == 0)
            throw Validation("cash_denominations_required", "Enter at least one denomination.");
        long total = checked(lines.Sum(line => checked(line.DenominationMinor * line.Quantity)));
        string notes = Optional(request.Notes, 500);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        string shiftId = await GetOpenShiftIdAsync(connection, transaction, user.Id, context.ShopId, cancellationToken);
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO shift_cash_counts
        (id, organization_id, shop_id, shift_id, count_type, total_minor,
         denominations_json, notes, counted_by_user_id, created_at_utc)
        VALUES
        ($id, $organizationId, $shopId, $shiftId, $countType, $total,
         $denominations, $notes, $userId, $createdAtUtc);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$shiftId", shiftId);
        command.Parameters.AddWithValue("$countType", countType);
        command.Parameters.AddWithValue("$total", total);
        command.Parameters.AddWithValue("$denominations", JsonSerializer.Serialize(lines));
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await WriteAuditAsync(connection, transaction, user, "cash_drawer.count.recorded", id,
            new { context.OrganizationId, context.ShopId, shiftId, countType, total, denominations = lines },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CashCountRecord(id, countType, total, lines, notes, user.DisplayName, now);
    }

    public async Task<IReadOnlyList<ShiftReconciliationReviewRecord>> ListReviewsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? status,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string normalized = string.IsNullOrWhiteSpace(status) ? "" : status.Trim().ToLowerInvariant();
        if (normalized.Length > 0 && normalized is not ("pending" or "approved" or "rejected"))
            throw Validation("invalid_review_status", "Use pending, approved or rejected.");

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT review.shift_id, review.shop_id, shop.code, shop.name, teller.display_name,
               review.review_status, review.expected_cash_minor, review.counted_cash_minor,
               review.variance_minor, review.review_notes, reviewer.display_name,
               review.created_at_utc, review.reviewed_at_utc
        FROM shift_reconciliation_reviews AS review
        INNER JOIN shops AS shop ON shop.id = review.shop_id
        INNER JOIN teller_shifts AS shift ON shift.id = review.shift_id
        INNER JOIN users AS teller ON teller.id = shift.teller_user_id
        LEFT JOIN users AS reviewer ON reviewer.id = review.reviewed_by_user_id
        WHERE review.organization_id = $organizationId
          AND review.shop_id = $shopId
          AND ($status = '' OR review.review_status = $status)
        ORDER BY review.created_at_utc DESC
        LIMIT 200;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$status", normalized);
        var records = new List<ShiftReconciliationReviewRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) records.Add(ReadReview(reader));
        return records;
    }

    public async Task<ShiftReconciliationReviewRecord> ReviewAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string shiftId,
        ReviewShiftReconciliationRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string decision = request.Decision.Trim().ToLowerInvariant();
        if (decision is not ("approved" or "rejected"))
            throw Validation("invalid_review_decision", "Use approved or rejected.");
        string notes = Optional(request.Notes, 500);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE shift_reconciliation_reviews
        SET review_status = $decision,
            review_notes = $notes,
            reviewed_by_user_id = $userId,
            reviewed_at_utc = $reviewedAtUtc
        WHERE shift_id = $shiftId
          AND organization_id = $organizationId
          AND shop_id = $shopId
          AND review_status = 'pending';
        """;
        command.Parameters.AddWithValue("$decision", decision);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$reviewedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$shiftId", shiftId.Trim());
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw Conflict("shift_review_unavailable", "The reconciliation is unavailable or already reviewed.");
        await WriteAuditAsync(connection, transaction, user, $"cash_drawer.reconciliation.{decision}", shiftId,
            new { context.OrganizationId, context.ShopId, decision, notes }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        IReadOnlyList<ShiftReconciliationReviewRecord> records =
            await ListReviewsAsync(user, context, decision, cancellationToken);
        return records.Single(record => record.ShiftId == shiftId);
    }

    private async Task<CashDrawerSnapshot> GetCurrentWithinTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string shiftId,
        CancellationToken cancellationToken)
    {
        long opening = await ScalarAsync(connection, transaction,
            "SELECT opening_cash_minor FROM teller_shifts WHERE id = $shiftId;",
            ("$shiftId", shiftId), cancellationToken);
        long sales = await ScalarAsync(connection, transaction,
            "SELECT COALESCE(SUM(p.amount_minor),0) FROM sale_payments p INNER JOIN sales s ON s.id=p.sale_id WHERE s.shift_id=$shiftId AND s.status IN ('completed','partially_returned','returned') AND p.payment_method='cash';",
            ("$shiftId", shiftId), cancellationToken);
        long refunds = await ScalarAsync(connection, transaction,
            "SELECT COALESCE(SUM(refund_amount_minor),0) FROM sales_returns WHERE shift_id=$shiftId AND status='completed' AND refund_method='cash';",
            ("$shiftId", shiftId), cancellationToken);
        long floatIn = await MovementTotalAsync(connection, transaction, shiftId, "float_in", cancellationToken);
        long safeDrop = await MovementTotalAsync(connection, transaction, shiftId, "safe_drop", cancellationToken);
        return new CashDrawerSnapshot(shiftId, context.ShopId, context.ShopCode, context.ShopName,
            opening, sales, refunds, floatIn, safeDrop,
            checked(opening + sales - refunds + floatIn - safeDrop),
            Array.Empty<CashDrawerMovementRecord>(), Array.Empty<CashCountRecord>());
    }

    private static async Task<string> GetOpenShiftIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string userId,
        string shopId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM teller_shifts WHERE teller_user_id=$userId AND shop_id=$shopId AND status='open' LIMIT 1;";
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$shopId", shopId);
        string? id = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        return string.IsNullOrWhiteSpace(id)
            ? throw Conflict("no_open_shift", "Open a teller shift before using cash drawer controls.")
            : id;
    }

    private static async Task<long> MovementTotalAsync(
        SqliteConnection connection, SqliteTransaction? transaction,
        string shiftId, string type, CancellationToken cancellationToken) =>
        await ScalarAsync(connection, transaction,
            "SELECT COALESCE(SUM(amount_minor),0) FROM cash_drawer_movements WHERE shift_id=$shiftId AND movement_type=$type;",
            ("$shiftId", shiftId), ("$type", type), cancellationToken);

    private static async Task<long> ScalarAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql,
        (string Name, object Value) parameter,
        CancellationToken cancellationToken) =>
        await ScalarAsync(connection, transaction, sql, parameter, (null!, null!), cancellationToken);

    private static async Task<long> ScalarAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql,
        (string Name, object Value) first, (string Name, object Value) second,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(first.Name, first.Value);
        if (!string.IsNullOrWhiteSpace(second.Name)) command.Parameters.AddWithValue(second.Name, second.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyList<CashDrawerMovementRecord>> ReadMovementsAsync(
        SqliteConnection connection, string shiftId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT movement.id, movement.movement_number, movement.movement_type,
               movement.amount_minor, movement.reason, movement.reference,
               creator.display_name, approver.display_name, movement.created_at_utc
        FROM cash_drawer_movements movement
        INNER JOIN users creator ON creator.id = movement.created_by_user_id
        INNER JOIN users approver ON approver.id = movement.approved_by_user_id
        WHERE movement.shift_id = $shiftId
        ORDER BY movement.created_at_utc DESC;
        """;
        command.Parameters.AddWithValue("$shiftId", shiftId);
        var records = new List<CashDrawerMovementRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8))));
        return records;
    }

    private static async Task<IReadOnlyList<CashCountRecord>> ReadCountsAsync(
        SqliteConnection connection, string shiftId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT count.id, count.count_type, count.total_minor, count.denominations_json,
               count.notes, user.display_name, count.created_at_utc
        FROM shift_cash_counts count
        INNER JOIN users user ON user.id = count.counted_by_user_id
        WHERE count.shift_id = $shiftId
        ORDER BY count.created_at_utc DESC;
        """;
        command.Parameters.AddWithValue("$shiftId", shiftId);
        var records = new List<CashCountRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                JsonSerializer.Deserialize<List<CashDenominationLine>>(reader.GetString(3)) ?? new(),
                reader.GetString(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6))));
        return records;
    }

    private static ShiftReconciliationReviewRecord ReadReview(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7),
            reader.GetInt64(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
            DateTimeOffset.Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)));

    private static void RequireAdministrator(AuthenticatedUser user)
    {
        if (!string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase))
            throw new CashDrawerException(403, "administrator_required", "Only an administrator can perform this cash-control action.");
    }

    private static string Required(string? value, int max, string code, string message)
    {
        string normalized = value?.Trim() ?? "";
        if (normalized.Length == 0) throw Validation(code, message);
        if (normalized.Length > max) throw Validation("text_too_long", $"Text cannot exceed {max} characters.");
        return normalized;
    }

    private static string Optional(string? value, int max)
    {
        string normalized = value?.Trim() ?? "";
        if (normalized.Length > max) throw Validation("text_too_long", $"Text cannot exceed {max} characters.");
        return normalized;
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection, SqliteTransaction transaction, AuthenticatedUser user,
        string eventType, string entityId, object details, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO audit_logs
        (occurred_at_utc, user_id, username, event_type, entity_type, entity_id,
         success, details_json, client_ip_hash)
        VALUES ($now, $userId, $username, $eventType, 'cash_drawer', $entityId, 1, $details, NULL);
        """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$details", JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CashDrawerException Validation(string code, string message) => new(400, code, message);
    private static CashDrawerException Conflict(string code, string message) => new(409, code, message);
}