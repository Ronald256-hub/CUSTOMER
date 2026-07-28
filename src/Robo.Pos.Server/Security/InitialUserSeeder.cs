using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;

namespace Robo.Pos.Server.Security;

public sealed record PosUser(
    string Id,
    string Username,
    string DisplayName,
    string Role);

public sealed class InitialUserSeeder
{
    private readonly DatabaseBootstrap _database;
    private readonly IPasswordHasher<PosUser> _passwordHasher;

    public InitialUserSeeder(
        DatabaseBootstrap database,
        IPasswordHasher<PosUser> passwordHasher)
    {
        _database = database;
        _passwordHasher = passwordHasher;
    }

    public async Task SeedAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(1) FROM users;";

            int existingUsers = Convert.ToInt32(
                await countCommand.ExecuteScalarAsync(cancellationToken));

            if (existingUsers > 0)
            {
                await EnsureCrmLoyaltySettingsAsync(
                    connection,
                    cancellationToken);
                return;
            }
        }

        string username =
            Environment.GetEnvironmentVariable("NEXUS_ADMIN_USERNAME")
                ?.Trim()
            ?? "admin";

        string displayName =
            Environment.GetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME")
                ?.Trim()
            ?? "Business Owner";

        string? password =
            Environment.GetEnvironmentVariable(
                "NEXUS_ADMIN_INITIAL_PASSWORD")
            ?? Environment.GetEnvironmentVariable(
                "ROBO_ADMIN_INITIAL_PASSWORD");

        ValidateUsername(username);
        ValidateDisplayName(displayName);

        PasswordPolicyResult policy =
            PasswordPolicy.Validate(password);

        if (!policy.IsValid)
        {
            throw new InvalidOperationException(
                "The first administrator password is missing or invalid: " +
                policy.Message);
        }

        string userId = Guid.NewGuid().ToString("N");
        string timestamp = DateTimeOffset.UtcNow.ToString("O");

        var user = new PosUser(
            userId,
            username,
            displayName,
            "admin");

        string passwordHash =
            _passwordHasher.HashPassword(user, password!);

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        await using (var insertUser = connection.CreateCommand())
        {
            insertUser.Transaction = transaction;
            insertUser.CommandText =
            """
            INSERT INTO users
            (
                id,
                username,
                username_normalized,
                display_name,
                role,
                password_hash,
                must_change_password,
                failed_login_attempts,
                locked_until_utc,
                is_active,
                password_changed_utc,
                created_at_utc,
                updated_at_utc
            )
            VALUES
            (
                $id,
                $username,
                $usernameNormalized,
                $displayName,
                'admin',
                $passwordHash,
                1,
                0,
                NULL,
                1,
                NULL,
                $timestamp,
                $timestamp
            );
            """;

            insertUser.Parameters.AddWithValue("$id", user.Id);
            insertUser.Parameters.AddWithValue("$username", user.Username);
            insertUser.Parameters.AddWithValue(
                "$usernameNormalized",
                user.Username.ToUpperInvariant());
            insertUser.Parameters.AddWithValue(
                "$displayName",
                user.DisplayName);
            insertUser.Parameters.AddWithValue(
                "$passwordHash",
                passwordHash);
            insertUser.Parameters.AddWithValue(
                "$timestamp",
                timestamp);

            await insertUser.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var audit = connection.CreateCommand())
        {
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
                $timestamp,
                $userId,
                $username,
                'user.bootstrap.created',
                'user',
                $userId,
                1,
                $details,
                NULL
            );
            """;

            audit.Parameters.AddWithValue("$timestamp", timestamp);
            audit.Parameters.AddWithValue("$userId", user.Id);
            audit.Parameters.AddWithValue("$username", user.Username);
            audit.Parameters.AddWithValue(
                "$details",
                JsonSerializer.Serialize(new
                {
                    user.DisplayName,
                    user.Role,
                    mustChangePassword = true,
                    tellerAccountsCreated = 0
                }));

            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await EnsureCrmLoyaltySettingsAsync(
            connection,
            cancellationToken);
    }

    private static async Task EnsureCrmLoyaltySettingsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        string? administratorId;
        await using (var administrator = connection.CreateCommand())
        {
            administrator.CommandText =
            """
            SELECT id
            FROM users
            WHERE role = 'admin'
              AND is_active = 1
            ORDER BY created_at_utc, id
            LIMIT 1;
            """;
            administratorId = Convert.ToString(
                await administrator.ExecuteScalarAsync(cancellationToken));
        }

        if (string.IsNullOrWhiteSpace(administratorId))
        {
            throw new InvalidOperationException(
                "CRM initialization requires an active administrator account.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        INSERT OR IGNORE INTO crm_loyalty_settings
        (
            organization_id,
            is_enabled,
            spend_minor_per_point,
            minimum_redeem_points,
            silver_threshold_points,
            gold_threshold_points,
            platinum_threshold_points,
            version,
            updated_by_user_id,
            created_at_utc,
            updated_at_utc
        )
        SELECT
            organization.id,
            0,
            1000,
            1,
            100,
            500,
            1000,
            1,
            $administratorId,
            $now,
            $now
        FROM organizations AS organization;
        """;
        command.Parameters.AddWithValue("$administratorId", administratorId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateUsername(string username)
    {
        if (username.Length is < 3 or > 50 ||
            username.Any(character =>
                !(char.IsLetterOrDigit(character) ||
                  character is '.' or '_' or '-')))
        {
            throw new InvalidOperationException(
                "The first administrator username must contain 3 to 50 " +
                "letters, numbers, dots, underscores or hyphens.");
        }
    }

    private static void ValidateDisplayName(string displayName)
    {
        if (displayName.Length is < 2 or > 100)
        {
            throw new InvalidOperationException(
                "The first administrator display name must contain " +
                "between 2 and 100 characters.");
        }
    }
}
