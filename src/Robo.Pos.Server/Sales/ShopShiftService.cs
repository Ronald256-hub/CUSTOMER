using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public sealed class ShopShiftService
{
    private readonly DatabaseBootstrap _database;

    public ShopShiftService(DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task<ShiftRecord?> GetOpenShiftAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            shift.id,
            shift.teller_user_id,
            user.display_name,
            shift.status,
            shift.opening_cash_minor,
            shift.expected_cash_minor,
            shift.counted_cash_minor,
            shift.cash_variance_minor,
            shift.opened_at_utc,
            shift.closed_at_utc,
            shop.id,
            shop.code,
            shop.name
        FROM teller_shifts AS shift
        INNER JOIN users AS user
            ON user.id = shift.teller_user_id
        INNER JOIN shops AS shop
            ON shop.id = shift.shop_id
        WHERE shift.teller_user_id = $userId
          AND shift.shop_id = $shopId
          AND shift.status = 'open'
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$shopId", context.ShopId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? ReadShift(reader)
            : null;
    }

    public async Task<ShiftRecord> OpenShiftAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        OpenShiftRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.OpeningCashMinor < 0)
        {
            throw Validation(
                "invalid_opening_cash",
                "Opening cash cannot be negative.");
        }

        string shiftId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText =
            """
            SELECT id
            FROM teller_shifts
            WHERE teller_user_id = $userId
              AND shop_id = $shopId
              AND status = 'open'
            LIMIT 1;
            """;
            existing.Parameters.AddWithValue("$userId", user.Id);
            existing.Parameters.AddWithValue("$shopId", context.ShopId);

            if (await existing.ExecuteScalarAsync(cancellationToken) is not null)
            {
                throw Conflict(
                    "shift_already_open",
                    $"This user already has an open shift at {context.ShopName}.");
            }
        }

        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
            """
            INSERT INTO teller_shifts
            (
                id,
                teller_user_id,
                shop_id,
                status,
                opening_cash_minor,
                opened_at_utc,
                notes
            )
            VALUES
            (
                $id,
                $userId,
                $shopId,
                'open',
                $openingCash,
                $openedAtUtc,
                ''
            );
            """;
            insert.Parameters.AddWithValue("$id", shiftId);
            insert.Parameters.AddWithValue("$userId", user.Id);
            insert.Parameters.AddWithValue("$shopId", context.ShopId);
            insert.Parameters.AddWithValue(
                "$openingCash",
                request.OpeningCashMinor);
            insert.Parameters.AddWithValue("$openedAtUtc", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "shift_already_open",
                $"This user already has an open shift at {context.ShopName}.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "shift.opened",
            shiftId,
            new
            {
                context.OrganizationId,
                context.ShopId,
                context.ShopCode,
                openingCashMinor = request.OpeningCashMinor
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new ShiftRecord(
            shiftId,
            user.Id,
            user.DisplayName,
            "open",
            request.OpeningCashMinor,
            null,
            null,
            null,
            now,
            null,
            context.ShopId,
            context.ShopCode,
            context.ShopName);
    }

    public async Task<ShiftRecord> CloseShiftAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CloseShiftRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.CountedCashMinor < 0)
        {
            throw Validation(
                "invalid_counted_cash",
                "Counted cash cannot be negative.");
        }

        string notes = request.Notes?.Trim() ?? string.Empty;
        if (notes.Length > 500)
        {
            throw Validation(
                "shift_notes_too_long",
                "Shift notes cannot exceed 500 characters.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        string shiftId;
        long openingCash;
        DateTimeOffset openedAt;

        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText =
            """
            SELECT id, opening_cash_minor, opened_at_utc
            FROM teller_shifts
            WHERE teller_user_id = $userId
              AND shop_id = $shopId
              AND status = 'open'
            LIMIT 1;
            """;
            find.Parameters.AddWithValue("$userId", user.Id);
            find.Parameters.AddWithValue("$shopId", context.ShopId);

            await using var reader =
                await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw Conflict(
                    "no_open_shift",
                    $"There is no open shift at {context.ShopName} to close.");
            }

            shiftId = reader.GetString(0);
            openingCash = reader.GetInt64(1);
            openedAt = DateTimeOffset.Parse(reader.GetString(2));
        }

        long cashSales;
        await using (var calculateSales = connection.CreateCommand())
        {
            calculateSales.Transaction = transaction;
            calculateSales.CommandText =
            """
            SELECT COALESCE(SUM(payment.amount_minor), 0)
            FROM sale_payments AS payment
            INNER JOIN sales AS sale
                ON sale.id = payment.sale_id
            WHERE sale.shift_id = $shiftId
              AND sale.shop_id = $shopId
              AND sale.status IN ('completed', 'partially_returned', 'returned')
              AND payment.payment_method = 'cash';
            """;
            calculateSales.Parameters.AddWithValue("$shiftId", shiftId);
            calculateSales.Parameters.AddWithValue("$shopId", context.ShopId);
            cashSales = Convert.ToInt64(
                await calculateSales.ExecuteScalarAsync(cancellationToken));
        }

        long cashRefunds;
        await using (var calculateRefunds = connection.CreateCommand())
        {
            calculateRefunds.Transaction = transaction;
            calculateRefunds.CommandText =
            """
            SELECT COALESCE(SUM(refund_amount_minor), 0)
            FROM sales_returns
            WHERE shift_id = $shiftId
              AND shop_id = $shopId
              AND status = 'completed'
              AND refund_method = 'cash';
            """;
            calculateRefunds.Parameters.AddWithValue("$shiftId", shiftId);
            calculateRefunds.Parameters.AddWithValue("$shopId", context.ShopId);
            cashRefunds = Convert.ToInt64(
                await calculateRefunds.ExecuteScalarAsync(cancellationToken));
        }

        long floatIn;
        long safeDrop;
        await using (var calculateCustody = connection.CreateCommand())
        {
            calculateCustody.Transaction = transaction;
            calculateCustody.CommandText =
            """
            SELECT
                COALESCE(SUM(CASE WHEN movement_type = 'float_in' THEN amount_minor ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN movement_type = 'safe_drop' THEN amount_minor ELSE 0 END), 0)
            FROM cash_drawer_movements
            WHERE shift_id = $shiftId
              AND shop_id = $shopId
              AND status = 'completed';
            """;
            calculateCustody.Parameters.AddWithValue("$shiftId", shiftId);
            calculateCustody.Parameters.AddWithValue("$shopId", context.ShopId);
            await using var reader =
                await calculateCustody.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            floatIn = reader.GetInt64(0);
            safeDrop = reader.GetInt64(1);
        }

        long expectedCash = checked(
            openingCash + cashSales - cashRefunds + floatIn - safeDrop);
        long variance = checked(request.CountedCashMinor - expectedCash);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE teller_shifts
            SET status = 'closed',
                expected_cash_minor = $expectedCash,
                counted_cash_minor = $countedCash,
                cash_variance_minor = $variance,
                closed_at_utc = $closedAtUtc,
                closed_by_user_id = $userId,
                notes = $notes
            WHERE id = $shiftId
              AND teller_user_id = $userId
              AND shop_id = $shopId
              AND status = 'open';
            """;
            update.Parameters.AddWithValue("$expectedCash", expectedCash);
            update.Parameters.AddWithValue(
                "$countedCash",
                request.CountedCashMinor);
            update.Parameters.AddWithValue("$variance", variance);
            update.Parameters.AddWithValue("$closedAtUtc", now.ToString("O"));
            update.Parameters.AddWithValue("$userId", user.Id);
            update.Parameters.AddWithValue("$notes", notes);
            update.Parameters.AddWithValue("$shiftId", shiftId);
            update.Parameters.AddWithValue("$shopId", context.ShopId);

            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "shift_close_conflict",
                    "The shift changed while it was being closed. Reload and try again.");
            }
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "shift.closed",
            shiftId,
            new
            {
                context.OrganizationId,
                context.ShopId,
                context.ShopCode,
                openingCash,
                cashSales,
                cashRefunds,
                floatIn,
                safeDrop,
                expectedCash,
                countedCash = request.CountedCashMinor,
                variance
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new ShiftRecord(
            shiftId,
            user.Id,
            user.DisplayName,
            "closed",
            openingCash,
            expectedCash,
            request.CountedCashMinor,
            variance,
            openedAt,
            now,
            context.ShopId,
            context.ShopCode,
            context.ShopName);
    }

    public async Task EnsureCanSwitchAsync(
        AuthenticatedUser user,
        string targetShopId,
        CancellationToken cancellationToken = default)
    {
        string normalizedTarget = targetShopId?.Trim() ?? string.Empty;
        if (normalizedTarget.Length == 0)
        {
            return;
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            shift.id,
            shop.code,
            shop.name
        FROM teller_shifts AS shift
        INNER JOIN shops AS shop
            ON shop.id = shift.shop_id
        WHERE shift.teller_user_id = $userId
          AND shift.status = 'open'
          AND shift.shop_id <> $targetShopId
        ORDER BY shift.opened_at_utc
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$targetShopId", normalizedTarget);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        string shiftId = reader.GetString(0);
        string shopCode = reader.GetString(1);
        string shopName = reader.GetString(2);

        throw new ShopContextException(
            StatusCodes.Status409Conflict,
            "open_shift_shop_switch_blocked",
            $"Close shift {shiftId} at {shopName} ({shopCode}) before switching shops.");
    }

    private static ShiftRecord ReadShift(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9)
                ? null
                : DateTimeOffset.Parse(reader.GetString(9)),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12));

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string eventType,
        string shiftId,
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
            'shift',
            $shiftId,
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
        command.Parameters.AddWithValue("$shiftId", shiftId);
        command.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SalesException Validation(
        string code,
        string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    private static SalesException Conflict(
        string code,
        string message) =>
        new(StatusCodes.Status409Conflict, code, message);
}
