using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;

namespace Robo.Pos.Server.Security;

public sealed class AdminTellerResetService
{
    private const int MaximumResetsPerWindow = 3;

    private static readonly TimeSpan ResetWindow =
        TimeSpan.FromHours(1);

    private readonly DatabaseBootstrap _database;
    private readonly IPasswordHasher<PosUser> _passwordHasher;

    public AdminTellerResetService(
        DatabaseBootstrap database,
        IPasswordHasher<PosUser> passwordHasher)
    {
        _database = database;
        _passwordHasher = passwordHasher;
    }

    public async Task<AdminTellerResetResult> ResetAsync(
        AuthenticatedUser administrator,
        string targetUserId,
        string? administratorPassword,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                administrator.Role,
                "admin",
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                AdminTellerResetStatus.AdministratorOnly,
                "administrator_required",
                "Administrator permission is required.");
        }

        if (administrator.MustChangePassword)
        {
            return Failure(
                AdminTellerResetStatus.AdministratorPasswordChangeRequired,
                "administrator_password_change_required",
                "The administrator must create a private password first.");
        }

        string resetReason = reason?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(resetReason))
        {
            return Failure(
                AdminTellerResetStatus.ReasonRequired,
                "reset_reason_required",
                "A reason for the password reset is required.");
        }

        if (resetReason.Length > 250)
        {
            return Failure(
                AdminTellerResetStatus.ReasonTooLong,
                "reset_reason_too_long",
                "The reset reason cannot exceed 250 characters.");
        }

        if (string.Equals(
                administrator.Id,
                targetUserId,
                StringComparison.Ordinal))
        {
            return Failure(
                AdminTellerResetStatus.CannotResetSelf,
                "cannot_reset_self",
                "Use the normal password-change process for the administrator account.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        UserResetRecord? administratorRecord =
            await FindUserAsync(
                connection,
                transaction,
                administrator.Id,
                cancellationToken);

        if (administratorRecord is null ||
            !administratorRecord.IsActive ||
            !string.Equals(
                administratorRecord.Role,
                "admin",
                StringComparison.OrdinalIgnoreCase))
        {
            await transaction.CommitAsync(cancellationToken);

            return Failure(
                AdminTellerResetStatus.AdministratorOnly,
                "administrator_required",
                "Administrator permission is required.");
        }

        if (administratorRecord.MustChangePassword)
        {
            await transaction.CommitAsync(cancellationToken);

            return Failure(
                AdminTellerResetStatus.AdministratorPasswordChangeRequired,
                "administrator_password_change_required",
                "The administrator must create a private password first.");
        }

        var passwordUser = new PosUser(
            administratorRecord.Id,
            administratorRecord.Username,
            administratorRecord.DisplayName,
            administratorRecord.Role);

        PasswordVerificationResult verification =
            _passwordHasher.VerifyHashedPassword(
                passwordUser,
                administratorRecord.PasswordHash,
                administratorPassword ?? string.Empty);

        if (verification ==
            PasswordVerificationResult.Failed)
        {
            await WriteAuditAsync(
                connection,
                transaction,
                administratorRecord,
                targetUserId,
                "auth.teller.password.reset.failed",
                false,
                new
                {
                    reason = "invalid_administrator_password"
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return Failure(
                AdminTellerResetStatus.InvalidAdministratorPassword,
                "invalid_administrator_password",
                "The administrator password is incorrect.");
        }

        UserResetRecord? target =
            await FindUserAsync(
                connection,
                transaction,
                targetUserId,
                cancellationToken);

        if (target is null)
        {
            await WriteAuditAsync(
                connection,
                transaction,
                administratorRecord,
                targetUserId,
                "auth.teller.password.reset.failed",
                false,
                new
                {
                    reason = "target_user_not_found"
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return Failure(
                AdminTellerResetStatus.TargetUserNotFound,
                "target_user_not_found",
                "The selected account could not be found.");
        }

        if (!string.Equals(
                target.Role,
                "teller",
                StringComparison.OrdinalIgnoreCase))
        {
            await WriteAuditAsync(
                connection,
                transaction,
                administratorRecord,
                target.Id,
                "auth.teller.password.reset.failed",
                false,
                new
                {
                    reason = "target_must_be_teller",
                    target.Username
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return Failure(
                AdminTellerResetStatus.TargetMustBeTeller,
                "target_must_be_teller",
                "Only teller accounts can be reset through this process.");
        }

        if (!target.IsActive)
        {
            await WriteAuditAsync(
                connection,
                transaction,
                administratorRecord,
                target.Id,
                "auth.teller.password.reset.failed",
                false,
                new
                {
                    reason = "target_account_disabled",
                    target.Username
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return Failure(
                AdminTellerResetStatus.TargetDisabled,
                "target_account_disabled",
                "The teller account is disabled.");
        }

        int recentResets =
            await CountRecentResetsAsync(
                connection,
                transaction,
                administratorRecord.Id,
                target.Id,
                DateTimeOffset.UtcNow.Subtract(ResetWindow),
                cancellationToken);

        if (recentResets >= MaximumResetsPerWindow)
        {
            await WriteAuditAsync(
                connection,
                transaction,
                administratorRecord,
                target.Id,
                "auth.teller.password.reset.rate_limited",
                false,
                new
                {
                    target.Username,
                    resetWindowMinutes =
                        (int)ResetWindow.TotalMinutes,
                    maximumResets =
                        MaximumResetsPerWindow
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return Failure(
                AdminTellerResetStatus.RateLimited,
                "password_reset_rate_limited",
                "Too many password resets were requested for this teller. Try again later.");
        }

        string temporaryPassword =
            TemporaryPasswordGenerator.Generate();

        var targetPasswordUser = new PosUser(
            target.Id,
            target.Username,
            target.DisplayName,
            target.Role);

        string temporaryPasswordHash =
            _passwordHasher.HashPassword(
                targetPasswordUser,
                temporaryPassword);

        DateTimeOffset resetAtUtc =
            DateTimeOffset.UtcNow;

        await using var updateUser =
            connection.CreateCommand();

        updateUser.Transaction = transaction;

        updateUser.CommandText =
        """
        UPDATE users
        SET password_hash = $passwordHash,
            must_change_password = 1,
            failed_login_attempts = 0,
            locked_until_utc = NULL,
            password_changed_utc = NULL,
            updated_at_utc = $updatedAtUtc
        WHERE id = $userId;
        """;

        updateUser.Parameters.AddWithValue(
            "$passwordHash",
            temporaryPasswordHash);

        updateUser.Parameters.AddWithValue(
            "$updatedAtUtc",
            resetAtUtc.ToString("O"));

        updateUser.Parameters.AddWithValue(
            "$userId",
            target.Id);

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
            resetAtUtc.ToString("O"));

        revokeSessions.Parameters.AddWithValue(
            "$lastSeenAtUtc",
            resetAtUtc.ToString("O"));

        revokeSessions.Parameters.AddWithValue(
            "$userId",
            target.Id);

        int revokedSessions =
            await revokeSessions.ExecuteNonQueryAsync(
                cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            administratorRecord,
            target.Id,
            "auth.teller.password.reset",
            true,
            new
            {
                targetUserId = target.Id,
                targetUsername = target.Username,
                targetDisplayName = target.DisplayName,
                reason = resetReason,
                revokedSessions,
                mustChangePassword = true
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new AdminTellerResetResult(
            Status: AdminTellerResetStatus.Success,
            TargetUserId: target.Id,
            Username: target.Username,
            DisplayName: target.DisplayName,
            TemporaryPassword: temporaryPassword,
            RevokedSessions: revokedSessions,
            ResetAtUtc: resetAtUtc);
    }

    private static AdminTellerResetResult Failure(
        AdminTellerResetStatus status,
        string errorCode,
        string message)
    {
        return new AdminTellerResetResult(
            Status: status,
            ErrorCode: errorCode,
            Message: message);
    }

    private static async Task<UserResetRecord?> FindUserAsync(
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
            id,
            username,
            display_name,
            role,
            password_hash,
            must_change_password,
            is_active
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

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new UserResetRecord(
            Id: reader.GetString(0),
            Username: reader.GetString(1),
            DisplayName: reader.GetString(2),
            Role: reader.GetString(3),
            PasswordHash: reader.GetString(4),
            MustChangePassword:
                reader.GetInt32(5) == 1,
            IsActive:
                reader.GetInt32(6) == 1);
    }

    private static async Task<int> CountRecentResetsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string administratorId,
        string targetUserId,
        DateTimeOffset windowStartUtc,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
        """
        SELECT COUNT(*)
        FROM audit_logs
        WHERE user_id = $administratorId
          AND entity_id = $targetUserId
          AND event_type = 'auth.teller.password.reset'
          AND success = 1
          AND occurred_at_utc >= $windowStartUtc;
        """;

        command.Parameters.AddWithValue(
            "$administratorId",
            administratorId);

        command.Parameters.AddWithValue(
            "$targetUserId",
            targetUserId);

        command.Parameters.AddWithValue(
            "$windowStartUtc",
            windowStartUtc.ToString("O"));

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(
                cancellationToken));
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UserResetRecord administrator,
        string targetUserId,
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
            $administratorId,
            $administratorUsername,
            $eventType,
            'user',
            $targetUserId,
            $success,
            $detailsJson,
            NULL
        );
        """;

        command.Parameters.AddWithValue(
            "$occurredAtUtc",
            DateTimeOffset.UtcNow.ToString("O"));

        command.Parameters.AddWithValue(
            "$administratorId",
            administrator.Id);

        command.Parameters.AddWithValue(
            "$administratorUsername",
            administrator.Username);

        command.Parameters.AddWithValue(
            "$eventType",
            eventType);

        command.Parameters.AddWithValue(
            "$targetUserId",
            targetUserId);

        command.Parameters.AddWithValue(
            "$success",
            success ? 1 : 0);

        command.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(details));

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private sealed record UserResetRecord(
        string Id,
        string Username,
        string DisplayName,
        string Role,
        string PasswordHash,
        bool MustChangePassword,
        bool IsActive);
}
