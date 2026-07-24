using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;

namespace Robo.Pos.Server.Security;

public sealed class SessionService
{
    private readonly DatabaseBootstrap _database;

    public SessionService(DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task<SessionValidationResult> ValidateAsync(
        string? sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return new SessionValidationResult(
                SessionValidationStatus.Missing);
        }

        string tokenHash = HashSessionToken(sessionToken);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        SessionRecord? record = await FindSessionAsync(
            connection,
            transaction,
            tokenHash,
            cancellationToken);

        if (record is null)
        {
            await transaction.CommitAsync(cancellationToken);

            return new SessionValidationResult(
                SessionValidationStatus.Invalid);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (!record.IsActive)
        {
            await transaction.CommitAsync(cancellationToken);

            return new SessionValidationResult(
                SessionValidationStatus.Disabled);
        }

        if (record.RevokedAtUtc is not null)
        {
            await transaction.CommitAsync(cancellationToken);

            return new SessionValidationResult(
                SessionValidationStatus.Revoked);
        }

        if (record.ExpiresAtUtc <= now)
        {
            await RevokeSessionRecordAsync(
                connection,
                transaction,
                record.SessionId,
                now,
                cancellationToken);

            await WriteAuditAsync(
                connection,
                transaction,
                record.UserId,
                record.Username,
                "auth.session.expired",
                true,
                new
                {
                    record.SessionId,
                    record.ExpiresAtUtc
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new SessionValidationResult(
                SessionValidationStatus.Expired);
        }

        await TouchSessionAsync(
            connection,
            transaction,
            record.SessionId,
            now,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new SessionValidationResult(
            SessionValidationStatus.Success,
            new AuthenticatedUser(
                record.UserId,
                record.Username,
                record.DisplayName,
                record.Role,
                record.MustChangePassword),
            record.SessionId,
            record.ExpiresAtUtc);
    }

    public async Task<bool> RevokeAsync(
        string? sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return false;
        }

        string tokenHash = HashSessionToken(sessionToken);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        SessionRecord? record = await FindSessionAsync(
            connection,
            transaction,
            tokenHash,
            cancellationToken);

        if (record is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (record.RevokedAtUtc is null)
        {
            await RevokeSessionRecordAsync(
                connection,
                transaction,
                record.SessionId,
                now,
                cancellationToken);

            await WriteAuditAsync(
                connection,
                transaction,
                record.UserId,
                record.Username,
                "auth.logout",
                true,
                new
                {
                    record.SessionId
                },
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<SessionRecord?> FindSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tokenHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText =
        """
        SELECT
            s.id,
            s.user_id,
            s.expires_at_utc,
            s.revoked_at_utc,
            u.username,
            u.display_name,
            u.role,
            u.must_change_password,
            u.is_active
        FROM sessions AS s
        INNER JOIN users AS u
            ON u.id = s.user_id
        WHERE s.token_hash = $tokenHash
        LIMIT 1;
        """;

        command.Parameters.AddWithValue(
            "$tokenHash",
            tokenHash);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        DateTimeOffset expiresAtUtc = DateTimeOffset.Parse(
            reader.GetString(2),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        DateTimeOffset? revokedAtUtc = reader.IsDBNull(3)
            ? null
            : DateTimeOffset.Parse(
                reader.GetString(3),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

        return new SessionRecord(
            SessionId: reader.GetString(0),
            UserId: reader.GetString(1),
            ExpiresAtUtc: expiresAtUtc,
            RevokedAtUtc: revokedAtUtc,
            Username: reader.GetString(4),
            DisplayName: reader.GetString(5),
            Role: reader.GetString(6),
            MustChangePassword: reader.GetInt32(7) == 1,
            IsActive: reader.GetInt32(8) == 1);
    }

    private static async Task TouchSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText =
        """
        UPDATE sessions
        SET last_seen_at_utc = $lastSeenAtUtc
        WHERE id = $sessionId
          AND revoked_at_utc IS NULL;
        """;

        command.Parameters.AddWithValue(
            "$lastSeenAtUtc",
            now.ToString("O"));

        command.Parameters.AddWithValue(
            "$sessionId",
            sessionId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RevokeSessionRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText =
        """
        UPDATE sessions
        SET revoked_at_utc = COALESCE(
                revoked_at_utc,
                $revokedAtUtc
            ),
            last_seen_at_utc = $lastSeenAtUtc
        WHERE id = $sessionId;
        """;

        command.Parameters.AddWithValue(
            "$revokedAtUtc",
            now.ToString("O"));

        command.Parameters.AddWithValue(
            "$lastSeenAtUtc",
            now.ToString("O"));

        command.Parameters.AddWithValue(
            "$sessionId",
            sessionId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        string username,
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
            userId);

        command.Parameters.AddWithValue(
            "$username",
            username);

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

    private static string HashSessionToken(
        string sessionToken)
    {
        byte[] tokenBytes =
            Encoding.UTF8.GetBytes(sessionToken);

        return Convert.ToHexString(
            SHA256.HashData(tokenBytes))
            .ToLowerInvariant();
    }

    private sealed record SessionRecord(
        string SessionId,
        string UserId,
        DateTimeOffset ExpiresAtUtc,
        DateTimeOffset? RevokedAtUtc,
        string Username,
        string DisplayName,
        string Role,
        bool MustChangePassword,
        bool IsActive);
}
