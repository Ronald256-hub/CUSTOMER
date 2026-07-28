using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Robo.Pos.Server.Data;

public static class CrmCustomerManagementMigration
{
    public const int Version = 14;

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

        bool alreadyApplied = Convert.ToInt32(
            await check.ExecuteScalarAsync(cancellationToken)) > 0;
        string hardeningSql = await LoadSqlAsync(
            "014_crm_customer_management_hardening.sql",
            cancellationToken);

        if (alreadyApplied)
        {
            await using var hardening = connection.CreateCommand();
            hardening.CommandText = hardeningSql;
            await hardening.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        string migrationSql = await LoadSqlAsync(
            "014_crm_customer_management.sql",
            cancellationToken);

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = migrationSql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var hardeningCommand = connection.CreateCommand();
        hardeningCommand.Transaction = transaction;
        hardeningCommand.CommandText = hardeningSql;
        await hardeningCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<string> LoadSqlAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        Assembly assembly = typeof(CrmCustomerManagementMigration).Assembly;
        string resourceName = assembly
            .GetManifestResourceNames()
            .Single(name => name.EndsWith(fileName, StringComparison.Ordinal));

        await using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The CRM migration resource {fileName} was not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
