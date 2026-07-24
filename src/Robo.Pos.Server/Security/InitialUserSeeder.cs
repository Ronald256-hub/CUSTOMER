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
    private static readonly BootstrapUser[] RequiredUsers =
    [
        new(
            Username: "baron",
            DisplayName: "Baron",
            Role: "admin",
            PasswordEnvironmentVariable:
                "ROBO_ADMIN_INITIAL_PASSWORD"),

        new(
            Username: "teller1",
            DisplayName: "Teller One",
            Role: "teller",
            PasswordEnvironmentVariable:
                "ROBO_TELLER1_INITIAL_PASSWORD"),

        new(
            Username: "teller2",
            DisplayName: "Teller Two",
            Role: "teller",
            PasswordEnvironmentVariable:
                "ROBO_TELLER2_INITIAL_PASSWORD")
    ];

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

        var pendingUsers =
            new List<(BootstrapUser Definition, string Password)>();

        foreach (BootstrapUser definition in RequiredUsers)
        {
            await using var check = connection.CreateCommand();

            check.CommandText =
            """
            SELECT COUNT(1)
            FROM users
            WHERE username_normalized = $username;
            """;

            check.Parameters.AddWithValue(
                "$username",
                NormalizeUsername(definition.Username));

            int exists = Convert.ToInt32(
                await check.ExecuteScalarAsync(cancellationToken));

            if (exists > 0)
            {
                continue;
            }

            string? password =
                Environment.GetEnvironmentVariable(
                    definition.PasswordEnvironmentVariable);

            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException(
                    $"Initial account '{definition.Username}' is missing " +
                    $"the environment variable " +
                    $"'{definition.PasswordEnvironmentVariable}'.");
            }

            ValidatePassword(
                definition.Username,
                password);

            pendingUsers.Add((definition, password));
        }

        if (pendingUsers.Count == 0)
        {
            return;
        }

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        foreach (var pending in pendingUsers)
        {
            string userId = Guid.NewGuid().ToString("N");
            string timestamp =
                DateTimeOffset.UtcNow.ToString("O");

            var user = new PosUser(
                Id: userId,
                Username: pending.Definition.Username,
                DisplayName: pending.Definition.DisplayName,
                Role: pending.Definition.Role);

            string passwordHash =
                _passwordHasher.HashPassword(
                    user,
                    pending.Password);

            await using var insertUser =
                connection.CreateCommand();

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
                $role,
                $passwordHash,
                1,
                0,
                NULL,
                1,
                NULL,
                $createdAt,
                $updatedAt
            );
            """;

            insertUser.Parameters.AddWithValue(
                "$id",
                user.Id);

            insertUser.Parameters.AddWithValue(
                "$username",
                user.Username);

            insertUser.Parameters.AddWithValue(
                "$usernameNormalized",
                NormalizeUsername(user.Username));

            insertUser.Parameters.AddWithValue(
                "$displayName",
                user.DisplayName);

            insertUser.Parameters.AddWithValue(
                "$role",
                user.Role);

            insertUser.Parameters.AddWithValue(
                "$passwordHash",
                passwordHash);

            insertUser.Parameters.AddWithValue(
                "$createdAt",
                timestamp);

            insertUser.Parameters.AddWithValue(
                "$updatedAt",
                timestamp);

            await insertUser.ExecuteNonQueryAsync(
                cancellationToken);

            await using var audit =
                connection.CreateCommand();

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
                $occurredAt,
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

            audit.Parameters.AddWithValue(
                "$occurredAt",
                timestamp);

            audit.Parameters.AddWithValue(
                "$userId",
                user.Id);

            audit.Parameters.AddWithValue(
                "$username",
                user.Username);

            audit.Parameters.AddWithValue(
                "$details",
                JsonSerializer.Serialize(new
                {
                    user.DisplayName,
                    user.Role,
                    mustChangePassword = true
                }));

            await audit.ExecuteNonQueryAsync(
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string NormalizeUsername(
        string username)
    {
        return username.Trim().ToUpperInvariant();
    }

    private static void ValidatePassword(
        string username,
        string password)
    {
        bool valid =
            password.Length >= 12 &&
            password.Any(char.IsUpper) &&
            password.Any(char.IsLower) &&
            password.Any(char.IsDigit) &&
            password.Any(character =>
                !char.IsLetterOrDigit(character));

        if (!valid)
        {
            throw new InvalidOperationException(
                $"The initial password for '{username}' must contain " +
                "at least 12 characters, uppercase, lowercase, " +
                "a number and a symbol.");
        }
    }

    private sealed record BootstrapUser(
        string Username,
        string DisplayName,
        string Role,
        string PasswordEnvironmentVariable);
}
