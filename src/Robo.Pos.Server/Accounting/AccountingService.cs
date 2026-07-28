using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Accounting;

public sealed partial class AccountingService
{
    private static readonly HashSet<string> AccountTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "asset",
            "liability",
            "equity",
            "income",
            "expense"
        };

    private static readonly HashSet<string> CounterpartyTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "customer",
            "supplier",
            "employee",
            "other"
        };

    private static readonly HashSet<string> JournalStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "draft",
            "posted",
            "reversed"
        };

    private readonly DatabaseBootstrap _database;

    public AccountingService(DatabaseBootstrap database)
    {
        _database = database;
    }

    private static bool IsAdministrator(AuthenticatedUser user) =>
        string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);

    private static void RequireAdministrator(AuthenticatedUser user)
    {
        if (!IsAdministrator(user))
        {
            throw Forbidden(
                "administrator_required",
                "Only an administrator can manage organization-wide accounting settings.");
        }
    }

    private static async Task RequireAccountingAccessAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        AuthenticatedUser user,
        string shopId,
        CancellationToken cancellationToken)
    {
        if (IsAdministrator(user))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT access_level
        FROM user_shop_access
        WHERE user_id = $userId
          AND shop_id = $shopId
          AND is_active = 1
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$shopId", shopId);

        string? accessLevel = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(
                accessLevel,
                "manager",
                StringComparison.OrdinalIgnoreCase))
        {
            throw Forbidden(
                "accounting_permission_required",
                "A branch manager or administrator is required for accounting operations.");
        }
    }

    private static string NormalizeId(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 100)
        {
            throw Validation(
                "invalid_identifier",
                "The supplied identifier is invalid.");
        }

        return normalized;
    }

    private static string RequiredText(
        string? value,
        int maximumLength,
        string errorCode,
        string message)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw Validation(errorCode, message);
        }
        if (normalized.Length > maximumLength)
        {
            throw Validation(
                "text_too_long",
                $"Text cannot exceed {maximumLength} characters.");
        }

        return normalized;
    }

    private static string OptionalText(string? value, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximumLength)
        {
            throw Validation(
                "text_too_long",
                $"Text cannot exceed {maximumLength} characters.");
        }

        return normalized;
    }

    private static string NormalizeAccountCode(string? value)
    {
        string code = RequiredText(
                value,
                20,
                "account_code_required",
                "Enter an account code.")
            .ToUpperInvariant();
        if (code.Any(character =>
                !char.IsLetterOrDigit(character) &&
                character is not '-' and not '.'))
        {
            throw Validation(
                "invalid_account_code",
                "Account codes may contain letters, numbers, hyphens and full stops only.");
        }

        return code;
    }

    private static string NormalizeAccountType(string? value)
    {
        string accountType = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!AccountTypes.Contains(accountType))
        {
            throw Validation(
                "invalid_account_type",
                "Use asset, liability, equity, income or expense.");
        }

        return accountType;
    }

    private static string NormalBalanceFor(string accountType) =>
        accountType is "asset" or "expense" ? "debit" : "credit";

    private static string NormalizeDate(string? value, string errorCode)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (!DateOnly.TryParseExact(
                normalized,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw Validation(
                errorCode,
                "Use a valid date in YYYY-MM-DD format.");
        }

        return normalized;
    }

    private static IReadOnlyList<NormalizedJournalLine> NormalizeLines(
        IReadOnlyList<AccountingJournalLineInput>? lines)
    {
        if (lines is null || lines.Count < 2)
        {
            throw Validation(
                "journal_lines_required",
                "A journal requires at least two lines.");
        }
        if (lines.Count > 500)
        {
            throw Validation(
                "too_many_journal_lines",
                "A journal cannot contain more than 500 lines.");
        }

        var results = new List<NormalizedJournalLine>(lines.Count);
        int lineNumber = 1;
        foreach (AccountingJournalLineInput line in lines)
        {
            string accountId = NormalizeId(line.AccountId);
            if (line.DebitMinor < 0 || line.CreditMinor < 0)
            {
                throw Validation(
                    "invalid_journal_amount",
                    "Debit and credit amounts cannot be negative.");
            }
            if ((line.DebitMinor > 0) == (line.CreditMinor > 0))
            {
                throw Validation(
                    "invalid_journal_line",
                    "Each journal line must contain either a debit or a credit, but not both.");
            }

            string description = OptionalText(line.Description, 250);
            string? counterpartyType = string.IsNullOrWhiteSpace(line.CounterpartyType)
                ? null
                : line.CounterpartyType.Trim().ToLowerInvariant();
            string? counterpartyId = string.IsNullOrWhiteSpace(line.CounterpartyId)
                ? null
                : NormalizeId(line.CounterpartyId);

            if (counterpartyType is not null &&
                !CounterpartyTypes.Contains(counterpartyType))
            {
                throw Validation(
                    "invalid_counterparty_type",
                    "Use customer, supplier, employee or other as the counterparty type.");
            }
            if ((counterpartyType is null) != (counterpartyId is null))
            {
                throw Validation(
                    "counterparty_incomplete",
                    "Counterparty type and identifier must be supplied together.");
            }

            results.Add(new NormalizedJournalLine(
                lineNumber++,
                accountId,
                line.DebitMinor,
                line.CreditMinor,
                description,
                counterpartyType,
                counterpartyId));
        }

        return results;
    }

    private static (long Debit, long Credit) CalculateTotals(
        IReadOnlyList<NormalizedJournalLine> lines)
    {
        long debit = 0;
        long credit = 0;
        foreach (NormalizedJournalLine line in lines)
        {
            debit = checked(debit + line.DebitMinor);
            credit = checked(credit + line.CreditMinor);
        }

        return (debit, credit);
    }

    private static async Task ValidateAccountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        IReadOnlyList<NormalizedJournalLine> lines,
        bool requireManualPosting,
        CancellationToken cancellationToken)
    {
        foreach (string accountId in lines
                     .Select(line => line.AccountId)
                     .Distinct(StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            SELECT is_active, allow_manual_posting
            FROM accounting_accounts
            WHERE id = $accountId
              AND organization_id = $organizationId
            LIMIT 1;
            """;
            command.Parameters.AddWithValue("$accountId", accountId);
            command.Parameters.AddWithValue("$organizationId", organizationId);

            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw Validation(
                    "account_not_available",
                    "Every journal line must use an account from the active organization.");
            }
            if (reader.GetInt32(0) != 1)
            {
                throw Conflict(
                    "account_inactive",
                    "An inactive account cannot be used in a journal.");
            }
            if (requireManualPosting && reader.GetInt32(1) != 1)
            {
                throw Forbidden(
                    "manual_posting_not_allowed",
                    "A selected system account does not allow manual posting.");
            }
        }
    }

    private static async Task EnsureOpenPeriodAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string journalDate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COUNT(1)
        FROM accounting_periods
        WHERE organization_id = $organizationId
          AND status = 'open'
          AND $journalDate BETWEEN start_date AND end_date;
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$journalDate", journalDate);

        int count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
        if (count != 1)
        {
            throw Conflict(
                "accounting_period_closed",
                "The journal date is not inside an open accounting period.");
        }
    }

    private static async Task<string> NextJournalNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = transaction;
            ensure.CommandText =
            """
            INSERT OR IGNORE INTO accounting_journal_sequences
            (
                organization_id,
                next_value,
                updated_at_utc
            )
            VALUES
            (
                $organizationId,
                1,
                $updatedAtUtc
            );
            """;
            ensure.Parameters.AddWithValue(
                "$organizationId",
                context.OrganizationId);
            ensure.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        long nextValue;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
            """
            SELECT next_value
            FROM accounting_journal_sequences
            WHERE organization_id = $organizationId;
            """;
            read.Parameters.AddWithValue(
                "$organizationId",
                context.OrganizationId);
            nextValue = Convert.ToInt64(
                await read.ExecuteScalarAsync(cancellationToken));
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE accounting_journal_sequences
            SET next_value = next_value + 1,
                updated_at_utc = $updatedAtUtc
            WHERE organization_id = $organizationId
              AND next_value = $expectedValue;
            """;
            update.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            update.Parameters.AddWithValue(
                "$organizationId",
                context.OrganizationId);
            update.Parameters.AddWithValue("$expectedValue", nextValue);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "journal_number_conflict",
                    "Another journal was numbered simultaneously. Try again.");
            }
        }

        string shopCode = new(
            context.ShopCode
                .Trim()
                .ToUpperInvariant()
                .Where(character =>
                    char.IsLetterOrDigit(character) || character == '-')
                .Take(20)
                .ToArray());
        if (shopCode.Length == 0)
        {
            shopCode = "SHOP";
        }

        return $"JRN-{shopCode}-{now:yyyyMMdd}-{nextValue:000000}";
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
            $entityType,
            $entityId,
            1,
            $detailsJson,
            NULL
        );
        """;
        command.Parameters.AddWithValue(
            "$occurredAtUtc",
            DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset ParseDateTime(string value) =>
        DateTimeOffset.Parse(value).ToUniversalTime();

    private static DateTimeOffset? GetNullableDateTime(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : ParseDateTime(reader.GetString(ordinal));

    private static string? GetNullableString(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static AccountingException Validation(
        string code,
        string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    private static AccountingException Forbidden(
        string code,
        string message) =>
        new(StatusCodes.Status403Forbidden, code, message);

    private static AccountingException NotFound(
        string code,
        string message) =>
        new(StatusCodes.Status404NotFound, code, message);

    private static AccountingException Conflict(
        string code,
        string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private sealed record NormalizedJournalLine(
        int LineNumber,
        string AccountId,
        long DebitMinor,
        long CreditMinor,
        string Description,
        string? CounterpartyType,
        string? CounterpartyId);

    private sealed record AccountIdentity(
        string Id,
        string OrganizationId,
        string Code,
        string Name,
        string AccountType,
        string NormalBalance,
        string? ParentAccountId,
        string? SystemKey,
        bool AllowManualPosting,
        bool IsActive,
        int Version,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc);

    private sealed record JournalHeader(
        string Id,
        string OrganizationId,
        string ShopId,
        string ShopCode,
        string ShopName,
        string JournalNumber,
        string JournalDate,
        string CurrencyCode,
        string Description,
        string SourceType,
        string? SourceId,
        string Status,
        string? ReversalOfJournalId,
        string? ReversedByJournalId,
        long TotalDebitMinor,
        long TotalCreditMinor,
        int Version,
        string CreatedByUserId,
        string CreatedByDisplayName,
        string? PostedByUserId,
        string PostedByDisplayName,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? PostedAtUtc);
}
