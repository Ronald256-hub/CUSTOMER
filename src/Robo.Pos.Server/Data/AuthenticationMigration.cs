using Microsoft.Data.Sqlite;

namespace Robo.Pos.Server.Data;

public static class AuthenticationMigration
{
    public const int Version = 2;

    public static async Task ApplyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        await using var versionCheck = connection.CreateCommand();
        versionCheck.Transaction = transaction;
        versionCheck.CommandText =
        """
        SELECT COUNT(1)
        FROM schema_versions
        WHERE version = $version;
        """;
        versionCheck.Parameters.AddWithValue("$version", Version);

        var alreadyApplied = Convert.ToInt32(
            await versionCheck.ExecuteScalarAsync(cancellationToken));

        if (alreadyApplied > 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText =
        """
        CREATE TABLE users
        (
            id                    TEXT PRIMARY KEY,
            username              TEXT NOT NULL,
            username_normalized   TEXT NOT NULL COLLATE NOCASE UNIQUE,
            display_name          TEXT NOT NULL,
            role                  TEXT NOT NULL
                                  CHECK (role IN ('admin', 'teller')),
            password_hash         TEXT NOT NULL,
            must_change_password  INTEGER NOT NULL DEFAULT 1
                                  CHECK (must_change_password IN (0, 1)),
            failed_login_attempts INTEGER NOT NULL DEFAULT 0
                                  CHECK (failed_login_attempts >= 0),
            locked_until_utc      TEXT NULL,
            is_active             INTEGER NOT NULL DEFAULT 1
                                  CHECK (is_active IN (0, 1)),
            password_changed_utc  TEXT NULL,
            created_at_utc        TEXT NOT NULL,
            updated_at_utc        TEXT NOT NULL
        );

        CREATE TABLE sessions
        (
            id                  TEXT PRIMARY KEY,
            user_id             TEXT NOT NULL,
            token_hash          TEXT NOT NULL UNIQUE,
            created_at_utc      TEXT NOT NULL,
            last_seen_at_utc    TEXT NOT NULL,
            expires_at_utc      TEXT NOT NULL,
            revoked_at_utc      TEXT NULL,
            created_ip_hash     TEXT NULL,
            user_agent          TEXT NULL,

            FOREIGN KEY (user_id)
                REFERENCES users(id)
                ON DELETE CASCADE
        );

        CREATE TABLE audit_logs
        (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at_utc   TEXT NOT NULL,
            user_id           TEXT NULL,
            username          TEXT NULL,
            event_type        TEXT NOT NULL,
            entity_type       TEXT NULL,
            entity_id         TEXT NULL,
            success           INTEGER NOT NULL
                              CHECK (success IN (0, 1)),
            details_json      TEXT NOT NULL DEFAULT '{}',
            client_ip_hash    TEXT NULL,

            FOREIGN KEY (user_id)
                REFERENCES users(id)
                ON DELETE SET NULL
        );

        CREATE INDEX ix_sessions_user_id
            ON sessions(user_id);

        CREATE INDEX ix_sessions_expires_at
            ON sessions(expires_at_utc);

        CREATE INDEX ix_sessions_active_token
            ON sessions(token_hash, revoked_at_utc);

        CREATE INDEX ix_audit_logs_occurred_at
            ON audit_logs(occurred_at_utc);

        CREATE INDEX ix_audit_logs_user_id
            ON audit_logs(user_id);

        CREATE INDEX ix_audit_logs_event_type
            ON audit_logs(event_type);

        INSERT INTO schema_versions
        (
            version,
            description,
            applied_at_utc
        )
        VALUES
        (
            2,
            'Secure users, sessions, account locking and audit records',
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        );
        """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
