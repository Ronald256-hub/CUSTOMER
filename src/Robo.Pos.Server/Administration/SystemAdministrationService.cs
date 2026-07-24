using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Sales;
using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Administration;

public sealed class SystemAdministrationService
{
    private static readonly Regex BackupFilePattern =
        new(
            @"^ROBO-POS-\d{8}-\d{6}-[a-f0-9]{8}\.db$",
            RegexOptions.Compiled |
            RegexOptions.CultureInvariant |
            RegexOptions.IgnoreCase);

    private readonly DatabaseBootstrap _database;
    private readonly AuditDocumentWriter _documents;
    private readonly SemaphoreSlim _backupGate = new(1, 1);

    public SystemAdministrationService(
        DatabaseBootstrap database,
        AuditDocumentWriter documents)
    {
        _database = database;
        _documents = documents;

        string? dataDirectory =
            Path.GetDirectoryName(_database.DatabasePath);

        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new InvalidOperationException(
                "The database directory could not be determined.");
        }

        BackupRootPath = Path.Combine(
            dataDirectory,
            "Backups");
    }

    public string BackupRootPath { get; }

    public async Task<BusinessSettingsResult>
        GetSettingsAsync(
            CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            business_name,
            address,
            phone,
            email,
            currency_code,
            receipt_footer,
            receipt_verification_enabled,
            updated_at_utc
        FROM business_settings
        WHERE id = 1;
        """;

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Error(
                StatusCodes.Status500InternalServerError,
                "business_settings_missing",
                "Business settings are missing.");
        }

        return new BusinessSettingsResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            _documents.RootPath,
            _database.DatabasePath,
            BackupRootPath,
            reader.GetInt32(6) == 1,
            DateTimeOffset.Parse(reader.GetString(7)));
    }

    public async Task<BusinessSettingsResult>
        UpdateSettingsAsync(
            AuthenticatedUser user,
            UpdateBusinessSettingsRequest request,
            CancellationToken cancellationToken = default)
    {
        string businessName = Required(
            request.BusinessName,
            150,
            "business_name_required",
            "Enter the business name.");

        string address = Optional(
            request.Address,
            500,
            "Business address");

        string phone = Optional(
            request.Phone,
            100,
            "Business phone");

        string email = Optional(
            request.Email,
            200,
            "Business email");

        string receiptFooter = Required(
            request.ReceiptFooter,
            500,
            "receipt_footer_required",
            "Enter the receipt footer.");

        ValidateEmail(email);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(
                cancellationToken);

        await using var command = connection.CreateCommand();

        command.Transaction =
            (SqliteTransaction)transaction;

        command.CommandText =
        """
        UPDATE business_settings
        SET business_name = $businessName,
            address = $address,
            phone = $phone,
            email = $email,
            currency_code = 'UGX',
            receipt_footer = $receiptFooter,
            receipt_verification_enabled = 0,
            updated_by_user_id = $userId,
            updated_at_utc = $now
        WHERE id = 1;
        """;

        command.Parameters.AddWithValue(
            "$businessName",
            businessName);

        command.Parameters.AddWithValue(
            "$address",
            address);

        command.Parameters.AddWithValue(
            "$phone",
            phone);

        command.Parameters.AddWithValue(
            "$email",
            email);

        command.Parameters.AddWithValue(
            "$receiptFooter",
            receiptFooter);

        command.Parameters.AddWithValue(
            "$userId",
            user.Id);

        command.Parameters.AddWithValue(
            "$now",
            now.ToString("O"));

        int changed =
            await command.ExecuteNonQueryAsync(
                cancellationToken);

        if (changed != 1)
        {
            throw Error(
                StatusCodes.Status500InternalServerError,
                "business_settings_update_failed",
                "Business settings could not be saved.");
        }

        await WriteAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            user,
            "settings.business.updated",
            "business_settings",
            "1",
            new
            {
                businessName,
                address,
                phone,
                email,
                receiptVerificationEnabled = false
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new BusinessSettingsResult(
            businessName,
            address,
            phone,
            email,
            "UGX",
            receiptFooter,
            _documents.RootPath,
            _database.DatabasePath,
            BackupRootPath,
            false,
            now);
    }

    public async Task<IReadOnlyList<BackupVerificationResult>>
        ListBackupsAsync(
            CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BackupRootPath);

        string[] files = Directory.GetFiles(
            BackupRootPath,
            "ROBO-POS-*.db",
            SearchOption.TopDirectoryOnly);

        var results =
            new List<BackupVerificationResult>();

        foreach (string path in files
                     .OrderByDescending(
                         File.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();

            results.Add(
                await VerifyBackupFileAsync(
                    path,
                    cancellationToken));
        }

        return results;
    }

    public async Task<BackupVerificationResult>
        CreateBackupAsync(
            AuthenticatedUser user,
            CancellationToken cancellationToken = default)
    {
        await _backupGate.WaitAsync(cancellationToken);

        string? backupPath = null;

        try
        {
            Directory.CreateDirectory(BackupRootPath);

            string fileName =
                $"ROBO-POS-" +
                $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-" +
                $"{Guid.NewGuid():N}"[..8] +
                ".db";

            backupPath = ResolveBackupPath(fileName);

            await using (
                var source =
                    new SqliteConnection(
                        _database.ConnectionString))
            await using (
                var destination =
                    new SqliteConnection(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = backupPath,
                            Mode =
                                SqliteOpenMode.ReadWriteCreate,
                            Pooling = false,
                            ForeignKeys = true
                        }.ToString()))
            {
                await source.OpenAsync(cancellationToken);
                await destination.OpenAsync(cancellationToken);

                source.BackupDatabase(destination);
            }

            BackupVerificationResult result =
                await VerifyBackupFileAsync(
                    backupPath,
                    cancellationToken);

            if (!result.IntegrityOk)
            {
                TryDelete(backupPath);

                throw Error(
                    StatusCodes.Status500InternalServerError,
                    "backup_verification_failed",
                    "The backup failed its integrity check.");
            }

            await WriteAuditAsync(
                user,
                "backup.created",
                "database_backup",
                result.FileName,
                new
                {
                    result.FileName,
                    result.SizeBytes,
                    result.Sha256,
                    result.SchemaVersion,
                    result.IntegrityOk
                },
                cancellationToken);

            return result;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(backupPath))
            {
                TryDelete(backupPath);
            }

            throw;
        }
        finally
        {
            _backupGate.Release();
        }
    }

    public async Task<BackupVerificationResult>
        VerifyBackupAsync(
            AuthenticatedUser user,
            string fileName,
            CancellationToken cancellationToken = default)
    {
        string path = ResolveExistingBackup(fileName);

        BackupVerificationResult result =
            await VerifyBackupFileAsync(
                path,
                cancellationToken);

        await WriteAuditAsync(
            user,
            "backup.verified",
            "database_backup",
            result.FileName,
            new
            {
                result.FileName,
                result.SizeBytes,
                result.Sha256,
                result.SchemaVersion,
                result.IntegrityOk
            },
            cancellationToken);

        return result;
    }

    public async Task<BackupDownloadResult>
        PrepareDownloadAsync(
            AuthenticatedUser user,
            string fileName,
            CancellationToken cancellationToken = default)
    {
        string path = ResolveExistingBackup(fileName);

        FileInfo file = new(path);

        await WriteAuditAsync(
            user,
            "backup.downloaded",
            "database_backup",
            file.Name,
            new
            {
                fileName = file.Name,
                sizeBytes = file.Length
            },
            cancellationToken);

        return new BackupDownloadResult(
            file.Name,
            file.FullName);
    }

    private async Task<BackupVerificationResult>
        VerifyBackupFileAsync(
            string path,
            CancellationToken cancellationToken)
    {
        FileInfo file = new(path);

        if (!file.Exists)
        {
            throw Error(
                StatusCodes.Status404NotFound,
                "backup_not_found",
                "The requested backup does not exist.");
        }

        string sha256 =
            await ComputeSha256Async(
                path,
                cancellationToken);

        bool integrityOk = false;
        string integrityMessage =
            "SQLite verification failed.";

        int schemaVersion = 0;

        try
        {
            await using var connection =
                new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                        ForeignKeys = true
                    }.ToString());

            await connection.OpenAsync(cancellationToken);

            var messages = new List<string>();

            await using (
                var integrityCommand =
                    connection.CreateCommand())
            {
                integrityCommand.CommandText =
                    "PRAGMA integrity_check;";

                await using var reader =
                    await integrityCommand.ExecuteReaderAsync(
                        cancellationToken);

                while (await reader.ReadAsync(
                           cancellationToken))
                {
                    messages.Add(reader.GetString(0));
                }
            }

            integrityOk =
                messages.Count == 1 &&
                string.Equals(
                    messages[0],
                    "ok",
                    StringComparison.OrdinalIgnoreCase);

            integrityMessage =
                messages.Count == 0
                    ? "No integrity result was returned."
                    : string.Join("; ", messages);

            if (integrityOk)
            {
                await using var versionCommand =
                    connection.CreateCommand();

                versionCommand.CommandText =
                """
                SELECT COALESCE(MAX(version), 0)
                FROM schema_versions;
                """;

                object? value =
                    await versionCommand.ExecuteScalarAsync(
                        cancellationToken);

                schemaVersion =
                    Convert.ToInt32(value ?? 0);
            }
        }
        catch (SqliteException)
        {
            integrityOk = false;
            integrityMessage =
                "The file is not a valid readable SQLite backup.";
        }
        finally
        {
            DeleteBackupSidecars(path);
        }

        return new BackupVerificationResult(
            file.Name,
            new DateTimeOffset(file.LastWriteTimeUtc),
            file.Length,
            sha256,
            integrityOk,
            integrityMessage,
            schemaVersion);
    }

    private string ResolveExistingBackup(
        string fileName)
    {
        string path = ResolveBackupPath(fileName);

        if (!File.Exists(path))
        {
            throw Error(
                StatusCodes.Status404NotFound,
                "backup_not_found",
                "The requested backup does not exist.");
        }

        FileAttributes attributes =
            File.GetAttributes(path);

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                "invalid_backup_file",
                "Linked backup files are not allowed.");
        }

        return path;
    }

    private string ResolveBackupPath(
        string fileName)
    {
        string trimmed = fileName?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(trimmed) ||
            Path.GetFileName(trimmed) != trimmed ||
            !BackupFilePattern.IsMatch(trimmed))
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                "invalid_backup_name",
                "The backup filename is invalid.");
        }

        string fullRoot =
            Path.GetFullPath(BackupRootPath)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        string candidate =
            Path.GetFullPath(
                Path.Combine(
                    BackupRootPath,
                    trimmed));

        StringComparison comparison =
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        if (!candidate.StartsWith(
                fullRoot,
                comparison))
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                "invalid_backup_path",
                "The backup path is invalid.");
        }

        return candidate;
    }

    private static async Task<string>
        ComputeSha256Async(
            string path,
            CancellationToken cancellationToken)
    {
        await using var stream =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 128,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);

        byte[] hash =
            await SHA256.HashDataAsync(
                stream,
                cancellationToken);

        return Convert.ToHexString(hash)
            .ToLowerInvariant();
    }

    private async Task WriteAuditAsync(
        AuthenticatedUser user,
        string eventType,
        string entityType,
        string entityId,
        object details,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(
                cancellationToken);

        await WriteAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            user,
            eventType,
            entityType,
            entityId,
            details,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string eventType,
        string entityType,
        string entityId,
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
            $now,
            $userId,
            $username,
            $eventType,
            $entityType,
            $entityId,
            1,
            $details,
            NULL
        );
        """;

        command.Parameters.AddWithValue(
            "$now",
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
            "$entityType",
            entityType);

        command.Parameters.AddWithValue(
            "$entityId",
            entityId);

        command.Parameters.AddWithValue(
            "$details",
            JsonSerializer.Serialize(details));

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static string Required(
        string? value,
        int maximumLength,
        string errorCode,
        string message)
    {
        string trimmed = value?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                errorCode,
                message);
        }

        if (trimmed.Length > maximumLength)
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                "value_too_long",
                $"{message} Maximum length is {maximumLength}.");
        }

        return trimmed;
    }

    private static string Optional(
        string? value,
        int maximumLength,
        string fieldName)
    {
        string trimmed = value?.Trim() ?? "";

        if (trimmed.Length > maximumLength)
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                "value_too_long",
                $"{fieldName} is too long.");
        }

        return trimmed;
    }

    private static void ValidateEmail(
        string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        try
        {
            _ = new MailAddress(email);
        }
        catch (FormatException)
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                "invalid_email",
                "Enter a valid business email address.");
        }
    }

    private static void DeleteBackupSidecars(
        string databasePath)
    {
        TryDelete(databasePath + "-wal");
        TryDelete(databasePath + "-shm");
        TryDelete(databasePath + "-journal");
    }

    private static void TryDelete(
        string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original operation error.
        }
    }

    private static SystemAdministrationException Error(
        int statusCode,
        string errorCode,
        string message)
    {
        return new SystemAdministrationException(
            statusCode,
            errorCode,
            message);
    }
}
