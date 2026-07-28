using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;

namespace Robo.Pos.Server.Crm;

public sealed class CrmBootstrap
{
    private readonly DatabaseBootstrap _database;

    public CrmBootstrap(DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task EnsureAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        string? administratorId;
        await using (var user = connection.CreateCommand())
        {
            user.Transaction = transaction;
            user.CommandText =
            """
            SELECT id
            FROM users
            WHERE role = 'admin'
              AND is_active = 1
            ORDER BY created_at_utc, id
            LIMIT 1;
            """;
            administratorId = Convert.ToString(
                await user.ExecuteScalarAsync(cancellationToken));
        }

        if (string.IsNullOrWhiteSpace(administratorId))
        {
            throw new InvalidOperationException(
                "CRM initialization requires an active administrator account.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
