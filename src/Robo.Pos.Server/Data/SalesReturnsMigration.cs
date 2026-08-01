using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Robo.Pos.Server.Data;

public static class SalesReturnsMigration
{
    public const int Version = 17;

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

        Assembly assembly = typeof(SalesReturnsMigration).Assembly;
        string[] resourceNames = assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains(
                ".017_sales_returns_",
                StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (resourceNames.Length != 2)
        {
            throw new InvalidOperationException(
                $"Expected two sales-return migration resources, found {resourceNames.Length}.");
        }

        var sql = new List<string>(resourceNames.Length);
        foreach (string resourceName in resourceNames)
        {
            await using Stream stream =
                assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"The sales-return migration resource {resourceName} was not found.");
            using var reader = new StreamReader(stream);
            sql.Add(await reader.ReadToEndAsync(cancellationToken));
        }

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Join(Environment.NewLine, sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
