using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Shops;

public sealed class ShopContextService
{
    private readonly DatabaseBootstrap _database;

    public ShopContextService(DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task<ActiveShopContextRecord> GetOrCreateAsync(
        AuthenticatedUser user,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        RequireSessionId(sessionId);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        ActiveShopContextRecord? current =
            await ReadCurrentAsync(
                connection,
                transaction,
                user,
                sessionId,
                cancellationToken);

        if (current is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return current;
        }

        SelectableShopRecord? fallback =
            await ReadDefaultShopAsync(
                connection,
                transaction,
                user,
                cancellationToken);

        if (fallback is null)
        {
            throw Error(
                StatusCodes.Status409Conflict,
                "shop_access_required",
                "This account has no active shop assignment. Ask an administrator to assign a shop.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await UpsertContextAsync(
            connection,
            transaction,
            sessionId,
            user.Id,
            fallback.OrganizationId,
            fallback.ShopId,
            now,
            cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            sessionId,
            "session.shop_context.initialized",
            previousShopId: null,
            fallback.ShopId,
            fallback.ShopCode,
            cancellationToken);

        ActiveShopContextRecord selected =
            await ReadCurrentAsync(
                connection,
                transaction,
                user,
                sessionId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "The active shop context could not be read after initialization.");

        await transaction.CommitAsync(cancellationToken);
        return selected;
    }

    public async Task<ActiveShopContextRecord> SetAsync(
        AuthenticatedUser user,
        string sessionId,
        SetActiveShopContextRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireSessionId(sessionId);

        string shopId = request.ShopId?.Trim() ?? string.Empty;
        if (shopId.Length == 0 || shopId.Length > 100)
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                "invalid_shop_id",
                "Select a valid shop.");
        }

        if (request.ExpectedVersion is < 0)
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                "invalid_context_version",
                "The expected context version cannot be negative.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        ActiveShopContextRecord? current =
            await ReadCurrentAsync(
                connection,
                transaction,
                user,
                sessionId,
                cancellationToken);

        int currentVersion = current?.Version ?? 0;
        if (request.ExpectedVersion is int expectedVersion &&
            expectedVersion != currentVersion)
        {
            throw Error(
                StatusCodes.Status409Conflict,
                "shop_context_changed",
                "The active shop changed in another request. Reload the session context and try again.");
        }

        SelectableShopRecord? target =
            await ReadSelectableShopAsync(
                connection,
                transaction,
                user,
                shopId,
                cancellationToken);

        if (target is null)
        {
            throw Error(
                StatusCodes.Status403Forbidden,
                "shop_not_available",
                "The selected shop is inactive or this account has no access to it.");
        }

        if (current is not null &&
            string.Equals(
                current.ShopId,
                target.ShopId,
                StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return current;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await UpsertContextAsync(
            connection,
            transaction,
            sessionId,
            user.Id,
            target.OrganizationId,
            target.ShopId,
            now,
            cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            sessionId,
            "session.shop_context.changed",
            current?.ShopId,
            target.ShopId,
            target.ShopCode,
            cancellationToken);

        ActiveShopContextRecord selected =
            await ReadCurrentAsync(
                connection,
                transaction,
                user,
                sessionId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "The active shop context could not be read after selection.");

        await transaction.CommitAsync(cancellationToken);
        return selected;
    }

    private static async Task<ActiveShopContextRecord?> ReadCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string sessionId,
        CancellationToken cancellationToken)
    {
        bool isAdmin = IsAdministrator(user);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            organization.id,
            organization.name,
            shop.id,
            shop.code,
            shop.name,
            shop.currency_code,
            shop.timezone_id,
            shop.is_head_office,
            CASE
                WHEN $isAdmin = 1
                    THEN COALESCE(access.access_level, 'manager')
                ELSE access.access_level
            END,
            context.selected_at_utc,
            context.version
        FROM session_shop_contexts AS context
        INNER JOIN organizations AS organization
            ON organization.id = context.organization_id
        INNER JOIN shops AS shop
            ON shop.id = context.shop_id
           AND shop.organization_id = context.organization_id
        LEFT JOIN user_shop_access AS access
            ON access.shop_id = shop.id
           AND access.user_id = $userId
           AND access.is_active = 1
        WHERE context.session_id = $sessionId
          AND context.user_id = $userId
          AND shop.is_active = 1
          AND ($isAdmin = 1 OR access.user_id IS NOT NULL)
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$isAdmin", isAdmin ? 1 : 0);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$userId", user.Id);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadContext(reader);
    }

    private static async Task<SelectableShopRecord?> ReadDefaultShopAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        CancellationToken cancellationToken)
    {
        bool isAdmin = IsAdministrator(user);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            organization.id,
            organization.name,
            shop.id,
            shop.code,
            shop.name,
            shop.currency_code,
            shop.timezone_id,
            shop.is_head_office,
            CASE
                WHEN $isAdmin = 1
                    THEN COALESCE(access.access_level, 'manager')
                ELSE access.access_level
            END
        FROM shops AS shop
        INNER JOIN organizations AS organization
            ON organization.id = shop.organization_id
        LEFT JOIN user_shop_access AS access
            ON access.shop_id = shop.id
           AND access.user_id = $userId
           AND access.is_active = 1
        WHERE shop.is_active = 1
          AND ($isAdmin = 1 OR access.user_id IS NOT NULL)
        ORDER BY
            COALESCE(access.is_primary, 0) DESC,
            shop.is_head_office DESC,
            shop.name COLLATE NOCASE
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$isAdmin", isAdmin ? 1 : 0);
        command.Parameters.AddWithValue("$userId", user.Id);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? ReadSelectableShop(reader)
            : null;
    }

    private static async Task<SelectableShopRecord?> ReadSelectableShopAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string shopId,
        CancellationToken cancellationToken)
    {
        bool isAdmin = IsAdministrator(user);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            organization.id,
            organization.name,
            shop.id,
            shop.code,
            shop.name,
            shop.currency_code,
            shop.timezone_id,
            shop.is_head_office,
            CASE
                WHEN $isAdmin = 1
                    THEN COALESCE(access.access_level, 'manager')
                ELSE access.access_level
            END
        FROM shops AS shop
        INNER JOIN organizations AS organization
            ON organization.id = shop.organization_id
        LEFT JOIN user_shop_access AS access
            ON access.shop_id = shop.id
           AND access.user_id = $userId
           AND access.is_active = 1
        WHERE shop.id = $shopId
          AND shop.is_active = 1
          AND ($isAdmin = 1 OR access.user_id IS NOT NULL)
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$isAdmin", isAdmin ? 1 : 0);
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$userId", user.Id);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? ReadSelectableShop(reader)
            : null;
    }

    private static async Task UpsertContextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string userId,
        string organizationId,
        string shopId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO session_shop_contexts
        (
            session_id,
            user_id,
            organization_id,
            shop_id,
            selected_at_utc,
            updated_at_utc,
            version
        )
        VALUES
        (
            $sessionId,
            $userId,
            $organizationId,
            $shopId,
            $now,
            $now,
            1
        )
        ON CONFLICT(session_id) DO UPDATE SET
            user_id = excluded.user_id,
            organization_id = excluded.organization_id,
            shop_id = excluded.shop_id,
            selected_at_utc = excluded.selected_at_utc,
            updated_at_utc = excluded.updated_at_utc,
            version = session_shop_contexts.version + 1;
        """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string sessionId,
        string eventType,
        string? previousShopId,
        string selectedShopId,
        string selectedShopCode,
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
            'shop_context',
            $selectedShopId,
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
        command.Parameters.AddWithValue("$selectedShopId", selectedShopId);
        command.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(new
            {
                sessionId,
                previousShopId,
                selectedShopId,
                selectedShopCode
            }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ActiveShopContextRecord ReadContext(
        SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7) == 1,
            reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9)),
            reader.GetInt32(10));

    private static SelectableShopRecord ReadSelectableShop(
        SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7) == 1,
            reader.GetString(8));

    private static bool IsAdministrator(AuthenticatedUser user) =>
        string.Equals(
            user.Role,
            "admin",
            StringComparison.OrdinalIgnoreCase);

    private static void RequireSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw Error(
                StatusCodes.Status401Unauthorized,
                "session_context_unavailable",
                "A valid authenticated session is required before selecting a shop.");
        }
    }

    private static ShopContextException Error(
        int statusCode,
        string code,
        string message) =>
        new(statusCode, code, message);

    private sealed record SelectableShopRecord(
        string OrganizationId,
        string OrganizationName,
        string ShopId,
        string ShopCode,
        string ShopName,
        string CurrencyCode,
        string TimezoneId,
        bool IsHeadOffice,
        string AccessLevel);
}
