using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Shops;

public sealed partial class ShopService
{
    private const string DefaultOrganizationId = "default-organization";

    private static readonly HashSet<string> AccessLevels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "manager",
            "supervisor",
            "teller",
            "viewer"
        };

    private readonly DatabaseBootstrap _database;

    public ShopService(DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<AvailableShopRecord>>
        ListAvailableAsync(
            AuthenticatedUser user,
            CancellationToken cancellationToken = default)
    {
        bool isAdmin = string.Equals(
            user.Role,
            "admin",
            StringComparison.OrdinalIgnoreCase);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            s.id,
            s.code,
            s.name,
            s.address,
            s.currency_code,
            s.timezone_id,
            s.is_head_office,
            CASE
                WHEN $isAdmin = 1 THEN COALESCE(a.access_level, 'manager')
                ELSE a.access_level
            END AS access_level,
            COALESCE(a.is_primary, CASE WHEN s.is_head_office = 1 THEN 1 ELSE 0 END)
        FROM shops AS s
        LEFT JOIN user_shop_access AS a
            ON a.shop_id = s.id
           AND a.user_id = $userId
           AND a.is_active = 1
        WHERE s.is_active = 1
          AND ($isAdmin = 1 OR a.user_id IS NOT NULL)
        ORDER BY
            COALESCE(a.is_primary, 0) DESC,
            s.is_head_office DESC,
            s.name COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$isAdmin", isAdmin ? 1 : 0);
        command.Parameters.AddWithValue("$userId", user.Id);

        var shops = new List<AvailableShopRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            shops.Add(new AvailableShopRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6) == 1,
                reader.GetString(7),
                reader.GetInt32(8) == 1));
        }

        return shops;
    }

    public async Task<IReadOnlyList<ShopRecord>> ListAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            id,
            organization_id,
            code,
            name,
            address,
            phone,
            email,
            tax_number,
            currency_code,
            timezone_id,
            is_head_office,
            is_active,
            version,
            created_at_utc,
            updated_at_utc
        FROM shops
        ORDER BY is_head_office DESC, name COLLATE NOCASE;
        """;

        var shops = new List<ShopRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            shops.Add(ReadShop(reader));
        }

        return shops;
    }

    public async Task<ShopRecord> CreateAsync(
        AuthenticatedUser administrator,
        CreateShopRequest request,
        CancellationToken cancellationToken = default)
    {
        string code = NormalizeCode(request.Code);
        string name = Required(
            request.Name,
            150,
            "shop_name_required",
            "Enter the shop name.");
        string address = Optional(request.Address, 250, "Shop address");
        string phone = Optional(request.Phone, 50, "Shop phone");
        string email = Optional(request.Email, 200, "Shop email");
        string taxNumber = Optional(request.TaxNumber, 100, "Shop tax number");
        string currencyCode = NormalizeCurrency(request.CurrencyCode);
        string timezoneId = Required(
            request.TimezoneId,
            100,
            "shop_timezone_required",
            "Enter the shop timezone.");
        ValidateEmail(email);

        string shopId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        try
        {
            if (request.IsHeadOffice)
            {
                await ClearHeadOfficeAsync(
                    connection,
                    transaction,
                    administrator.Id,
                    now,
                    cancellationToken);
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
            """
            INSERT INTO shops
            (
                id,
                organization_id,
                code,
                name,
                address,
                phone,
                email,
                tax_number,
                currency_code,
                timezone_id,
                is_head_office,
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
                $organizationId,
                $code,
                $name,
                $address,
                $phone,
                $email,
                $taxNumber,
                $currencyCode,
                $timezoneId,
                $isHeadOffice,
                1,
                1,
                $userId,
                $userId,
                $now,
                $now
            );
            """;
            insert.Parameters.AddWithValue("$id", shopId);
            insert.Parameters.AddWithValue("$organizationId", DefaultOrganizationId);
            insert.Parameters.AddWithValue("$code", code);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$address", address);
            insert.Parameters.AddWithValue("$phone", phone);
            insert.Parameters.AddWithValue("$email", email);
            insert.Parameters.AddWithValue("$taxNumber", taxNumber);
            insert.Parameters.AddWithValue("$currencyCode", currencyCode);
            insert.Parameters.AddWithValue("$timezoneId", timezoneId);
            insert.Parameters.AddWithValue("$isHeadOffice", request.IsHeadOffice ? 1 : 0);
            insert.Parameters.AddWithValue("$userId", administrator.Id);
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);

            await using var assign = connection.CreateCommand();
            assign.Transaction = transaction;
            assign.CommandText =
            """
            INSERT INTO user_shop_access
            (
                user_id,
                shop_id,
                access_level,
                is_primary,
                is_active,
                assigned_by_user_id,
                assigned_at_utc,
                updated_at_utc
            )
            VALUES
            (
                $userId,
                $shopId,
                'manager',
                0,
                1,
                $userId,
                $now,
                $now
            );
            """;
            assign.Parameters.AddWithValue("$userId", administrator.Id);
            assign.Parameters.AddWithValue("$shopId", shopId);
            assign.Parameters.AddWithValue("$now", now.ToString("O"));
            await assign.ExecuteNonQueryAsync(cancellationToken);

            await using var initializeStock = connection.CreateCommand();
            initializeStock.Transaction = transaction;
            initializeStock.CommandText =
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
                $now
            FROM products;
            """;
            initializeStock.Parameters.AddWithValue("$shopId", shopId);
            initializeStock.Parameters.AddWithValue("$now", now.ToString("O"));
            await initializeStock.ExecuteNonQueryAsync(cancellationToken);

            await WriteAuditAsync(
                connection,
                transaction,
                administrator,
                "shop.created",
                shopId,
                new
                {
                    code,
                    name,
                    currencyCode,
                    timezoneId,
                    request.IsHeadOffice
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "shop_code_conflict",
                "Another shop already uses this code, or the requested head-office setting conflicts with an existing shop.");
        }

        return await GetRequiredAsync(shopId, cancellationToken);
    }

    public async Task<ShopRecord> UpdateAsync(
        AuthenticatedUser administrator,
        string shopId,
        UpdateShopRequest request,
        CancellationToken cancellationToken = default)
    {
        string normalizedId = Required(
            shopId,
            64,
            "shop_id_required",
            "The shop identifier is required.");
        string name = Required(
            request.Name,
            150,
            "shop_name_required",
            "Enter the shop name.");
        string address = Optional(request.Address, 250, "Shop address");
        string phone = Optional(request.Phone, 50, "Shop phone");
        string email = Optional(request.Email, 200, "Shop email");
        string taxNumber = Optional(request.TaxNumber, 100, "Shop tax number");
        string currencyCode = NormalizeCurrency(request.CurrencyCode);
        string timezoneId = Required(
            request.TimezoneId,
            100,
            "shop_timezone_required",
            "Enter the shop timezone.");
        ValidateEmail(email);

        if (request.ExpectedVersion < 1)
        {
            throw Validation(
                "invalid_shop_version",
                "Reload the shop before saving changes.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        ShopRecord current = await ReadRequiredAsync(
            connection,
            transaction,
            normalizedId,
            cancellationToken);

        if (current.IsHeadOffice && !request.IsActive)
        {
            throw Conflict(
                "head_office_cannot_be_disabled",
                "Select another active head office before disabling this shop.");
        }

        if (request.IsHeadOffice && !current.IsHeadOffice)
        {
            await ClearHeadOfficeAsync(
                connection,
                transaction,
                administrator.Id,
                now,
                cancellationToken);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE shops
        SET name = $name,
            address = $address,
            phone = $phone,
            email = $email,
            tax_number = $taxNumber,
            currency_code = $currencyCode,
            timezone_id = $timezoneId,
            is_head_office = $isHeadOffice,
            is_active = $isActive,
            version = version + 1,
            updated_by_user_id = $userId,
            updated_at_utc = $now
        WHERE id = $id
          AND version = $expectedVersion;
        """;
        update.Parameters.AddWithValue("$name", name);
        update.Parameters.AddWithValue("$address", address);
        update.Parameters.AddWithValue("$phone", phone);
        update.Parameters.AddWithValue("$email", email);
        update.Parameters.AddWithValue("$taxNumber", taxNumber);
        update.Parameters.AddWithValue("$currencyCode", currencyCode);
        update.Parameters.AddWithValue("$timezoneId", timezoneId);
        update.Parameters.AddWithValue("$isHeadOffice", request.IsHeadOffice ? 1 : 0);
        update.Parameters.AddWithValue("$isActive", request.IsActive ? 1 : 0);
        update.Parameters.AddWithValue("$userId", administrator.Id);
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$id", normalizedId);
        update.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);

        int affected = await update.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw Conflict(
                "shop_changed",
                "The shop changed while you were editing it. Reload and try again.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            administrator,
            "shop.updated",
            normalizedId,
            new
            {
                previousVersion = current.Version,
                name,
                currencyCode,
                timezoneId,
                request.IsHeadOffice,
                request.IsActive
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return await GetRequiredAsync(normalizedId, cancellationToken);
    }

    public async Task<ShopUserAccessRecord> AssignUserAsync(
        AuthenticatedUser administrator,
        string shopId,
        string userId,
        AssignShopUserRequest request,
        CancellationToken cancellationToken = default)
    {
        string normalizedShopId = Required(
            shopId,
            64,
            "shop_id_required",
            "The shop identifier is required.");
        string normalizedUserId = Required(
            userId,
            64,
            "user_id_required",
            "The user identifier is required.");
        string accessLevel = request.AccessLevel.Trim().ToLowerInvariant();

        if (!AccessLevels.Contains(accessLevel))
        {
            throw Validation(
                "invalid_shop_access_level",
                "Use manager, supervisor, teller or viewer access.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        await EnsureActiveShopAsync(
            connection,
            transaction,
            normalizedShopId,
            cancellationToken);
        await EnsureActiveUserAsync(
            connection,
            transaction,
            normalizedUserId,
            cancellationToken);

        if (request.IsPrimary && request.IsActive)
        {
            await using var clearPrimary = connection.CreateCommand();
            clearPrimary.Transaction = transaction;
            clearPrimary.CommandText =
            """
            UPDATE user_shop_access
            SET is_primary = 0,
                updated_at_utc = $now
            WHERE user_id = $userId
              AND is_primary = 1;
            """;
            clearPrimary.Parameters.AddWithValue("$now", now.ToString("O"));
            clearPrimary.Parameters.AddWithValue("$userId", normalizedUserId);
            await clearPrimary.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText =
        """
        INSERT INTO user_shop_access
        (
            user_id,
            shop_id,
            access_level,
            is_primary,
            is_active,
            assigned_by_user_id,
            assigned_at_utc,
            updated_at_utc
        )
        VALUES
        (
            $userId,
            $shopId,
            $accessLevel,
            $isPrimary,
            $isActive,
            $administratorId,
            $now,
            $now
        )
        ON CONFLICT(user_id, shop_id)
        DO UPDATE SET
            access_level = excluded.access_level,
            is_primary = excluded.is_primary,
            is_active = excluded.is_active,
            assigned_by_user_id = excluded.assigned_by_user_id,
            updated_at_utc = excluded.updated_at_utc;
        """;
        upsert.Parameters.AddWithValue("$userId", normalizedUserId);
        upsert.Parameters.AddWithValue("$shopId", normalizedShopId);
        upsert.Parameters.AddWithValue("$accessLevel", accessLevel);
        upsert.Parameters.AddWithValue(
            "$isPrimary",
            request.IsPrimary && request.IsActive ? 1 : 0);
        upsert.Parameters.AddWithValue("$isActive", request.IsActive ? 1 : 0);
        upsert.Parameters.AddWithValue("$administratorId", administrator.Id);
        upsert.Parameters.AddWithValue("$now", now.ToString("O"));
        await upsert.ExecuteNonQueryAsync(cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            administrator,
            "shop.user_access_updated",
            normalizedShopId,
            new
            {
                userId = normalizedUserId,
                accessLevel,
                request.IsPrimary,
                request.IsActive
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return await GetUserAccessRequiredAsync(
            normalizedShopId,
            normalizedUserId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ShopUserAccessRecord>> ListUsersAsync(
        string shopId,
        CancellationToken cancellationToken = default)
    {
        string normalizedShopId = Required(
            shopId,
            64,
            "shop_id_required",
            "The shop identifier is required.");

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            u.id,
            u.username,
            u.display_name,
            u.role,
            a.access_level,
            a.is_primary,
            a.is_active,
            a.assigned_at_utc,
            a.updated_at_utc
        FROM user_shop_access AS a
        INNER JOIN users AS u
            ON u.id = a.user_id
        WHERE a.shop_id = $shopId
        ORDER BY a.is_active DESC, a.is_primary DESC, u.display_name COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$shopId", normalizedShopId);

        var users = new List<ShopUserAccessRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(ReadUserAccess(reader));
        }

        return users;
    }

    private async Task<ShopRecord> GetRequiredAsync(
        string shopId,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadRequiredAsync(
            connection,
            null,
            shopId,
            cancellationToken);
    }

    private static async Task<ShopRecord> ReadRequiredAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string shopId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            id,
            organization_id,
            code,
            name,
            address,
            phone,
            email,
            tax_number,
            currency_code,
            timezone_id,
            is_head_office,
            is_active,
            version,
            created_at_utc,
            updated_at_utc
        FROM shops
        WHERE id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound("shop_not_found", "The shop could not be found.");
        }

        return ReadShop(reader);
    }

    private async Task<ShopUserAccessRecord> GetUserAccessRequiredAsync(
        string shopId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            u.id,
            u.username,
            u.display_name,
            u.role,
            a.access_level,
            a.is_primary,
            a.is_active,
            a.assigned_at_utc,
            a.updated_at_utc
        FROM user_shop_access AS a
        INNER JOIN users AS u
            ON u.id = a.user_id
        WHERE a.shop_id = $shopId
          AND a.user_id = $userId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$userId", userId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "shop_user_access_not_found",
                "The shop assignment could not be found.");
        }

        return ReadUserAccess(reader);
    }

    private static ShopRecord ReadShop(SqliteDataReader reader)
    {
        return new ShopRecord(
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
            reader.GetInt32(10) == 1,
            reader.GetInt32(11) == 1,
            reader.GetInt32(12),
            DateTimeOffset.Parse(reader.GetString(13)),
            DateTimeOffset.Parse(reader.GetString(14)));
    }

    private static ShopUserAccessRecord ReadUserAccess(
        SqliteDataReader reader)
    {
        return new ShopUserAccessRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5) == 1,
            reader.GetInt32(6) == 1,
            DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)));
    }

    private static async Task EnsureActiveShopAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string shopId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COUNT(1)
        FROM shops
        WHERE id = $shopId
          AND is_active = 1;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        int count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
        if (count != 1)
        {
            throw NotFound(
                "active_shop_not_found",
                "The active shop could not be found.");
        }
    }

    private static async Task EnsureActiveUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COUNT(1)
        FROM users
        WHERE id = $userId
          AND is_active = 1;
        """;
        command.Parameters.AddWithValue("$userId", userId);
        int count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
        if (count != 1)
        {
            throw NotFound(
                "active_user_not_found",
                "The active user could not be found.");
        }
    }

    private static async Task ClearHeadOfficeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE shops
        SET is_head_office = 0,
            version = version + 1,
            updated_by_user_id = $userId,
            updated_at_utc = $now
        WHERE organization_id = $organizationId
          AND is_head_office = 1;
        """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$organizationId", DefaultOrganizationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string eventType,
        string shopId,
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
            $now,
            $userId,
            $username,
            $eventType,
            'shop',
            $shopId,
            1,
            $details,
            NULL
        );
        """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$details", JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeCode(string? value)
    {
        string code = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!ShopCodePattern().IsMatch(code))
        {
            throw Validation(
                "invalid_shop_code",
                "Shop code must contain 2 to 20 letters, numbers, hyphens or underscores and must begin with a letter or number.");
        }

        return code;
    }

    private static string NormalizeCurrency(string? value)
    {
        string code = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CurrencyPattern().IsMatch(code))
        {
            throw Validation(
                "invalid_currency_code",
                "Currency code must contain exactly three letters.");
        }

        return code;
    }

    private static string Required(
        string? value,
        int maximumLength,
        string errorCode,
        string message)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw Validation(errorCode, message);
        }
        if (trimmed.Length > maximumLength)
        {
            throw Validation(
                "value_too_long",
                $"{message} Maximum length is {maximumLength}.");
        }
        return trimmed;
    }

    private static string Optional(
        string? value,
        int maximumLength,
        string fieldName)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length > maximumLength)
        {
            throw Validation(
                "value_too_long",
                $"{fieldName} cannot exceed {maximumLength} characters.");
        }
        return trimmed;
    }

    private static void ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        try
        {
            _ = new MailAddress(email);
        }
        catch (FormatException)
        {
            throw Validation(
                "invalid_shop_email",
                "Enter a valid shop email address.");
        }
    }

    private static ShopException Validation(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    private static ShopException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private static ShopException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShopCodePattern();

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();
}