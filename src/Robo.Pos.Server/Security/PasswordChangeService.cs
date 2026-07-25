using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;

namespace Robo.Pos.Server.Security;

public sealed class PasswordChangeService
{
    private readonly DatabaseBootstrap _database;
    private readonly IPasswordHasher<PosUser> _passwordHasher;

    public PasswordChangeService(
        DatabaseBootstrap database,
        IPasswordHasher<PosUser> passwordHasher)
    {
        _database = database;
        _passwordHasher = passwordHasher;
    }

    public async Task<PasswordChangeResult> ChangeAsync(
        AuthenticatedUser authenticatedUser,
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        PasswordPolicyResult policy =
            PasswordPolicy.Validate(newPassword);

        if (!policy.IsValid)
        {
            return new PasswordChangeResult(
                PasswordChangeStatus.WeakPassword,
                policy.ErrorCode,
                policy.Message);
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        UserPasswordRecord? record =
            await FindUserAsync(
                connection,
                transaction,
                authenticatedUser.Id,
                cancellationToken);

        if (record is null)
        {
            await transaction.CommitAsync(cancellationToken);

            return new PasswordChangeResult(
                PasswordChangeStatus.UserNotFound,
                "user_not_found",
                "The account could not be found.");
        }

        if (!record.IsActive)
        {
            await WriteAuditAsync(
                connection,
                transaction,
                authenticatedUser,
                "auth.password.change.failed",
                false,
                new
                {
                    reason = "account_disabled"
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new PasswordChangeResult(
                PasswordChangeStatus.Disabled,
                "account_disabled",
                "This account is disabled.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (!record.MustChangePassword &&
            record.PasswordChangedUtc is not null)
        {
            DateTimeOffset nextAllowedChange =
                record.PasswordChangedUtc.Value.AddDays(30);

            if (now < nextAllowedChange)
            {
                await WriteAuditAsync(
                    connection,
                    transaction,
                    authenticatedUser,
                    "auth.password.change.failed",
                    false,
                    new
                    {
                        reason = "change_too_soon",
                        nextAllowedChangeUtc =
                            nextAllowedChange.ToString("O")
                    },
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                return new PasswordChangeResult(
                    PasswordChangeStatus.ChangeTooSoon,
                    "change_too_soon",
                    "This password was changed recently. " +
                    "It may be changed again on " +
                    nextAllowedChange.ToLocalTime()
                        .ToString("dd MMMM yyyy") +
                    ".");
            }
        }

        var passwordUser = new PosUser(
            authenticatedUser.Id,
            authenticatedUser.Username,
            authenticatedUser.DisplayName,
            authenticatedUser.Role);

        PasswordVerificationResult currentVerification =
            _passwordHasher.VerifyHashedPassword(
                passwordUser,
                record.PasswordHash,
                currentPassword ?? string.Empty);

        if (currentVerification ==
            PasswordVerificationResult.Failed)
        {
            await WriteAuditAsync(
                connection,
                transaction,
                authenticatedUser,
                "auth.password.change.failed",
                false,
                new
                {
                    reason = "invalid_current_password"
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new PasswordChangeResult(
                PasswordChangeStatus.InvalidCurrentPassword,
                "invalid_current_password",
                "The current password is incorrect.");
        }

        PasswordVerificationResult newPasswordVerification =
            _passwordHasher.VerifyHashedPassword(
                passwordUser,
                record.PasswordHash,
                newPassword!);

        if (newPasswordVerification !=
            PasswordVerificationResult.Failed)
        {
            await WriteAuditAsync(
                connection,
                transaction,
                authenticatedUser,
                "auth.password.change.failed",
                false,
                new
                {
                    reason = "password_reuse"
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new PasswordChangeResult(
                PasswordChangeStatus.SameAsCurrentPassword,
                "password_reuse",
                "The new password must be different from the current password.");
        }

        string newPasswordHash =
            _passwordHasher.HashPassword(
                passwordUser,
                newPassword!);

        await using var updateUser =
            connection.CreateCommand();

        updateUser.Transaction = transaction;

        updateUser.CommandText =
        """
        UPDATE users
        SET password_hash = $passwordHash,
            must_change_password = 0,
            failed_login_attempts = 0,
            locked_until_utc = NULL,
            password_changed_utc = $changedAtUtc,
            updated_at_utc = $updatedAtUtc
        WHERE id = $userId;
        """;

        updateUser.Parameters.AddWithValue(
            "$passwordHash",
            newPasswordHash);

        updateUser.Parameters.AddWithValue(
            "$changedAtUtc",
            now.ToString("O"));

        updateUser.Parameters.AddWithValue(
            "$updatedAtUtc",
            now.ToString("O"));

        updateUser.Parameters.AddWithValue(
            "$userId",
            authenticatedUser.Id);

        await updateUser.ExecuteNonQueryAsync(
            cancellationToken);

        await using var revokeSessions =
            connection.CreateCommand();

        revokeSessions.Transaction = transaction;

        revokeSessions.CommandText =
        """
        UPDATE sessions
        SET revoked_at_utc = $revokedAtUtc,
            last_seen_at_utc = $lastSeenAtUtc
        WHERE user_id = $userId
          AND revoked_at_utc IS NULL;
        """;

        revokeSessions.Parameters.AddWithValue(
            "$revokedAtUtc",
            now.ToString("O"));

        revokeSessions.Parameters.AddWithValue(
            "$lastSeenAtUtc",
            now.ToString("O"));

        revokeSessions.Parameters.AddWithValue(
            "$userId",
            authenticatedUser.Id);

        int revokedSessions =
            await revokeSessions.ExecuteNonQueryAsync(
                cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            authenticatedUser,
            "auth.password.changed",
            true,
            new
            {
                revokedSessions,
                mustChangePassword = false
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new PasswordChangeResult(
            PasswordChangeStatus.Success,
            RevokedSessions: revokedSessions);
    }

    private static async Task<UserPasswordRecord?> FindUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
        """
        SELECT
            password_hash,
            is_active,
            must_change_password,
            password_changed_utc
        FROM users
        WHERE id = $userId
        LIMIT 1;
        """;

        command.Parameters.AddWithValue(
            "$userId",
            userId);

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            return null;
        }

        DateTimeOffset? passwordChangedUtc = null;

        if (!reader.IsDBNull(3) &&
            DateTimeOffset.TryParse(
                reader.GetString(3),
                out DateTimeOffset parsedPasswordChangedUtc))
        {
            passwordChangedUtc = parsedPasswordChangedUtc;
        }

        return new UserPasswordRecord(
            PasswordHash: reader.GetString(0),
            IsActive: reader.GetInt32(1) == 1,
            MustChangePassword: reader.GetInt32(2) == 1,
            PasswordChangedUtc: passwordChangedUtc);
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string eventType,
        bool success,
        object details,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

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
            'user',
            $userId,
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
            user.Id);

        command.Parameters.AddWithValue(
            "$username",
            user.Username);

        command.Parameters.AddWithValue(
            "$eventType",
            eventType);

        command.Parameters.AddWithValue(
            "$success",
            success ? 1 : 0);

        command.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(details));

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private sealed record UserPasswordRecord(
        string PasswordHash,
        bool IsActive,
        bool MustChangePassword,
        DateTimeOffset? PasswordChangedUtc);
}
