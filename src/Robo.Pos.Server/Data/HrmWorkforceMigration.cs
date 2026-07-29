using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Robo.Pos.Server.Data;

/// <summary>
/// Applies the additive HRM workforce-management schema after CRM.
/// Existing users, shops and operational records remain unchanged, and the
/// migration is repeat-safe for both upgraded and newly installed databases.
/// </summary>
public static class HrmWorkforceMigration
{
    public const int Version = 15;

    public static async Task ApplyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var check = connection.CreateCommand();
        check.CommandText =
        """
        SELECT COUNT(1)
        FROM schema_versions
        WHERE version = $version;
        """;
        check.Parameters.AddWithValue("$version", Version);

        int alreadyApplied = Convert.ToInt32(
            await check.ExecuteScalarAsync(cancellationToken));
        if (alreadyApplied > 0)
        {
            return;
        }

        Assembly assembly = typeof(HrmWorkforceMigration).Assembly;
        string resourceName = assembly
            .GetManifestResourceNames()
            .Single(name => name.EndsWith(
                "015_hrm_workforce_management.sql",
                StringComparison.Ordinal));

        await using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                "The HRM workforce migration resource was not found.");
        using var reader = new StreamReader(stream);
        string sql = await reader.ReadToEndAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
