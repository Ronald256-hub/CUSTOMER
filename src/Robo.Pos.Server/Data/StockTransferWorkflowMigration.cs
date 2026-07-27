using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Robo.Pos.Server.Data;

public static class StockTransferWorkflowMigration
{
    public const int Version = 8;

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

        Assembly assembly = typeof(StockTransferWorkflowMigration).Assembly;
        string resourceName = assembly
            .GetManifestResourceNames()
            .Single(name => name.EndsWith(
                "008_stock_transfer_workflow.sql",
                StringComparison.Ordinal));

        await using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                "The stock-transfer migration SQL resource was not found.");
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