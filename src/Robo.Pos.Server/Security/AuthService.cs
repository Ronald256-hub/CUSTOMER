using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;

namespace Robo.Pos.Server.Security;

public sealed class AuthService
{
    private const int MaximumFailedAttempts = 5;
    private static readonly TimeSpan LockDuration =
        TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SessionDuration =
        TimeSpan.FromHours(12);

    private readonly DatabaseBootstrap _database;
    private readonly IPasswordHasher<PosUser> _passwordHasher;

    public AuthService(
        DatabaseBootstrap database,
        IPasswordHasher<PosUser> passwordHasher)
    {
        _database = database;
        _passwordHasher = passwordHasher;
    }

    public async Task<LoginResult> LoginAsync(
        string? username,
        string? password,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        string suppliedUsername = username?.Trim() ?? string.Empty;
        string suppliedPassword = password ?? string.Empty;
        string normalizedUsername =
            suppliedUsername.ToUpperInvariant();

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        UserRecord? record = await FindUserAsync(
            connection,
            transaction,
            normalizedUsername,
            cancellationToken);

        if (record is null ||
            string.IsNullOrWhiteSpace(suppliedPassword))
        {
            await WriteAuditAsync(
                connection,
                transaction,
                userId: null,
                username: suppliedUsername,
                eventType: "auth.login.failed",
                success: false,
                details: new { reason = "invalid_credentials" },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new LoginResult(
                LoginStatus.InvalidCredentials);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (!record.IsActive)
        {
            await WriteAuditAsync(
                connection,
                transaction,
                record.Id,
                record.Username,
                "auth.login.disabled",
                false,
                new { reason = "account_disabled" },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new LoginResult(LoginStatus.Disabled);
        }

        if (record.LockedUntilUtc is not null &&
            record.LockedUntilUtc > now)
        {
            await WriteAuditAsync(
                connection,
                transaction,
                record.Id,
                record.Username,
                "auth.login.locked",
                false,
                new
                {
                    reason = "account_locked",
                    lockedUntilUtc = record.LockedUntilUtc
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new LoginResult(
                LoginStatus.Locked,
                LockedUntilUtc: record.LockedUntilUtc);
        }

        var user = new PosUser(
            record.Id,
            record.Username,
            record.DisplayName,
            record.Role);

        PasswordVerificationResult verification =
            _passwordHasher.VerifyHashedPassword(
                user,
                record.PasswordHash,
                suppliedPassword);

        if (verification == PasswordVerificationResult.Failed)
        {
            int failedAttempts =
                record.FailedLoginAttempts + 1;

            DateTimeOffset? lockedUntil =
                failedAttempts >= MaximumFailedAttempts
                    ? now.Add(LockDuration)
                    : null;

            await UpdateFailedLoginAsync(
                connection,
                transaction,
                record.Id,
                failedAttempts,
                lockedUntil,
                now,
                cancellationToken);

            await WriteAuditAsync(
                connection,
                transaction,
                record.Id,
                record.Username,
                lockedUntil is null
                    ? "auth.login.failed"
                    : "auth.login.locked",
                false,
                new
                {
                    reason = "invalid_credentials",
                    failedAttempts,
                    lockedUntilUtc = lockedUntil
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return lockedUntil is null
                ? new LoginResult(
                    LoginStatus.InvalidCredentials)
                : new LoginResult(
                    LoginStatus.Locked,
                    LockedUntilUtc: lockedUntil);
        }

        string passwordHash = record.PasswordHash;

        if (verification ==
            PasswordVerificationResult.SuccessRehashNeeded)
        {
            passwordHash = _passwordHasher.HashPassword(
                user,
                suppliedPassword);
        }

        await ResetSuccessfulLoginAsync(
            connection,
            transaction,
            record.Id,
            passwordHash,
            now,
            cancellationToken);

        string sessionToken = CreateSessionToken();
        string tokenHash = HashSessionToken(sessionToken);
        DateTimeOffset expiresAtUtc =
            now.Add(SessionDuration);

        await InsertSessionAsync(
            connection,
            transaction,
            record.Id,
            tokenHash,
            now,
            expiresAtUtc,
            userAgent,
            cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            record.Id,
            record.Username,
            "auth.login.succeeded",
            true,
            new
            {
                role = record.Role,
                expiresAtUtc
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new LoginResult(
            LoginStatus.Success,
            new AuthenticatedUser(
                record.Id,
                record.Username,
                record.DisplayName,
                record.Role,
                record.MustChangePassword),
            sessionToken,
            expiresAtUtc);
    }

    private static async Task<UserRecord?> FindUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedUsername,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText =
        """
        SELECT
            id,
            username,
            display_name,
            role,
            password_hash,
            must_change_password,
            failed_login_attempts,
            locked_until_utc,
            is_active
        FROM users
        WHERE username_normalized = $username;
        """;

        command.Parameters.AddWithValue(
            "$username",
            normalizedUsername);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        DateTimeOffset? lockedUntil = reader.IsDBNull(7)
            ? null
            : DateTimeOffset.Parse(
                reader.GetString(7),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

        return new UserRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5) == 1,
            reader.GetInt32(6),
            lockedUntil,
            reader.GetInt32(8) == 1);
    }

    private static async Task UpdateFailedLoginAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        int failedAttempts,
        DateTimeOffset? lockedUntilUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText =
        """
        UPDATE users
        SET failed_login_attempts = $failedAttempts,
            locked_until_utc = $lockedUntilUtc,
            updated_at_utc = $updatedAtUtc
        WHERE id = $userId;
        """;

        command.Parameters.AddWithValue(
            "$failedAttempts",
            failedAttempts);

        command.Parameters.AddWithValue(
            "$lockedUntilUtc",
            lockedUntilUtc?.ToString("O")
                ?? (object)DBNull.Value);

        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            updatedAtUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$userId",
            userId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ResetSuccessfulLoginAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        string passwordHash,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText =
        """
        UPDATE users
        SET password_hash = $passwordHash,
            failed_login_attempts = 0,
            locked_until_utc = NULL,
            updated_at_utc = $updatedAtUtc
        WHERE id = $userId;
        """;

        command.Parameters.AddWithValue(
            "$passwordHash",
            passwordHash);

        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            updatedAtUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$userId",
            userId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        string tokenHash,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText =
        """
        INSERT INTO sessions
        (
            id,
            user_id,
            token_hash,
            created_at_utc,
            last_seen_at_utc,
            expires_at_utc,
            revoked_at_utc,
            created_ip_hash,
            user_agent
        )
        VALUES
        (
            $id,
            $userId,
            $tokenHash,
            $createdAtUtc,
            $lastSeenAtUtc,
            $expiresAtUtc,
            NULL,
            NULL,
            $userAgent
        );
        """;

        command.Parameters.AddWithValue(
            "$id",
            Guid.NewGuid().ToString("N"));

        command.Parameters.AddWithValue(
            "$userId",
            userId);

        command.Parameters.AddWithValue(
            "$tokenHash",
            tokenHash);

        command.Parameters.AddWithValue(
            "$createdAtUtc",
            createdAtUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$lastSeenAtUtc",
            createdAtUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$expiresAtUtc",
            expiresAtUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$userAgent",
            string.IsNullOrWhiteSpace(userAgent)
                ? DBNull.Value
                : userAgent);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? userId,
        string? username,
        string eventType,
        bool success,
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
            'session',
            NULL,
            $success,
            $detailsJson,
            NULL
        );
        """;

        command.Parameters.AddWithValue(
            "$occurredAtUtc",
            DateTimeOffset.UtcNow.ToString("O"));

        command.Parameters.AddWithValue(
            "$userId",
            userId ?? (object)DBNull.Value);

        command.Parameters.AddWithValue(
            "$username",
            username ?? (object)DBNull.Value);

        command.Parameters.AddWithValue(
            "$eventType",
            eventType);

        command.Parameters.AddWithValue(
            "$success",
            success ? 1 : 0);

        command.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(details));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateSessionToken()
    {
        return Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
    }

    private static string HashSessionToken(
        string sessionToken)
    {
        byte[] tokenBytes =
            Encoding.UTF8.GetBytes(sessionToken);

        return Convert.ToHexString(
            SHA256.HashData(tokenBytes))
            .ToLowerInvariant();
    }

    private sealed record UserRecord(
        string Id,
        string Username,
        string DisplayName,
        string Role,
        string PasswordHash,
        bool MustChangePassword,
        int FailedLoginAttempts,
        DateTimeOffset? LockedUntilUtc,
        bool IsActive);
}
