using Microsoft.Data.Sqlite;

namespace Robo.Pos.Server.Data;

public sealed class DatabaseBootstrap
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public DatabaseBootstrap(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;

        DatabasePath = ResolveDatabasePath();

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(DatabasePath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The database directory could not be determined.");
        }

        Directory.CreateDirectory(directory);

        await using var connection =
            new SqliteConnection(ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText =
        """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 5000;

        CREATE TABLE IF NOT EXISTS schema_versions
        (
            version        INTEGER PRIMARY KEY,
            description    TEXT NOT NULL,
            applied_at_utc TEXT NOT NULL
        );

        INSERT OR IGNORE INTO schema_versions
        (
            version,
            description,
            applied_at_utc
        )
        VALUES
        (
            1,
            'Initial production database backbone',
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        );
        """;

        await command.ExecuteNonQueryAsync(cancellationToken);

        await AuthenticationMigration.ApplyAsync(connection, cancellationToken);
        await BusinessMigration.ApplyAsync(connection, cancellationToken);
        await MultiShopMigration.ApplyAsync(connection, cancellationToken);
        await ActiveShopContextMigration.ApplyAsync(connection, cancellationToken);
        await ShopScopedInventoryMigration.ApplyAsync(connection, cancellationToken);
        await ShopScopedSalesMigration.ApplyAsync(connection, cancellationToken);
        await StockTransferWorkflowMigration.ApplyAsync(connection, cancellationToken);
        await StockTransferInvariantMigration.ApplyAsync(connection, cancellationToken);
        await AccountingKernelMigration.ApplyAsync(connection, cancellationToken);
        await OperationalAccountingMigration.ApplyAsync(connection, cancellationToken);
        await FinanceSettlementMigration.ApplyAsync(connection, cancellationToken);
        await AdvancedProcurementMigration.ApplyAsync(connection, cancellationToken);
        await CrmCustomerManagementMigration.ApplyAsync(connection, cancellationToken);
        await HrmWorkforceMigration.ApplyAsync(connection, cancellationToken);
        await SaasTenantOperationsMigration.ApplyAsync(connection, cancellationToken);
        await SalesReturnsMigration.ApplyAsync(connection, cancellationToken);
        await CreditSalesReturnsMigration.ApplyAsync(connection, cancellationToken);
        await CashDrawerReconciliationMigration.ApplyAsync(connection, cancellationToken);
    }

    public async Task<DatabaseStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqliteConnection(ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText =
        """
        SELECT COALESCE(MAX(version), 0)
        FROM schema_versions;
        """;

        object? result =
            await command.ExecuteScalarAsync(cancellationToken);

        return new DatabaseStatus(
            Ready: true,
            Engine: "SQLite",
            FileName: Path.GetFileName(DatabasePath),
            SchemaVersion: Convert.ToInt32(result ?? 0));
    }

    private string ResolveDatabasePath()
    {
        string? configuredDirectory =
            _configuration["DataDirectory"]
            ?? Environment.GetEnvironmentVariable("ROBO_DATA_DIR");

        string dataDirectory;

        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            dataDirectory = Path.GetFullPath(configuredDirectory);
        }
        else if (OperatingSystem.IsWindows())
        {
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

            dataDirectory = Path.Combine(
                localAppData,
                "ROBO CASK TAP POS",
                "Data");
        }
        else
        {
            dataDirectory = Path.Combine(
                _environment.ContentRootPath,
                ".data");
        }

        return Path.Combine(dataDirectory, "robo-pos.db");
    }
}

public sealed record DatabaseStatus(
    bool Ready,
    string Engine,
    string FileName,
    int SchemaVersion);