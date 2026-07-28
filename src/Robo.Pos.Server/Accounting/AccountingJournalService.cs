using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Accounting;

public sealed partial class AccountingService
{
    public async Task<IReadOnlyList<AccountingJournalListItem>> ListJournalsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? requestedScope,
        string? requestedStatus,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        string scope = requestedScope?.Trim().ToLowerInvariant() ?? "shop";
        bool consolidated = scope == "consolidated";
        if (scope is not ("shop" or "consolidated"))
        {
            throw Validation(
                "invalid_accounting_scope",
                "Accounting scope must be shop or consolidated.");
        }
        if (consolidated && !IsAdministrator(user))
        {
            throw Forbidden(
                "administrator_required",
                "Only an administrator can view consolidated journals.");
        }

        string status = requestedStatus?.Trim().ToLowerInvariant() ?? string.Empty;
        if (status.Length > 0 && !JournalStatuses.Contains(status))
        {
            throw Validation(
                "invalid_journal_status",
                "The requested journal status is invalid.");
        }
        int limit = Math.Clamp(requestedLimit, 1, 500);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireAccountingAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            journal.id,
            journal.shop_id,
            shop.code,
            journal.journal_number,
            journal.journal_date,
            journal.description,
            journal.source_type,
            journal.status,
            journal.total_debit_minor,
            journal.total_credit_minor,
            journal.version,
            journal.updated_at_utc
        FROM accounting_journals AS journal
        INNER JOIN shops AS shop
            ON shop.id = journal.shop_id
        WHERE journal.organization_id = $organizationId
          AND ($consolidated = 1 OR journal.shop_id = $shopId)
          AND ($status = '' OR journal.status = $status)
        ORDER BY journal.journal_date DESC,
                 journal.updated_at_utc DESC,
                 journal.journal_number DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$consolidated", consolidated ? 1 : 0);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$limit", limit);

        var journals = new List<AccountingJournalListItem>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            journals.Add(new AccountingJournalListItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt32(10),
                ParseDateTime(reader.GetString(11))));
        }

        return journals;
    }

    public async Task<AccountingJournalRecord> GetJournalAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string journalId,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(journalId);
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        JournalHeader? header = await ReadJournalHeaderAsync(
            connection,
            transaction: null,
            id,
            cancellationToken);
        if (header is null ||
            !string.Equals(
                header.OrganizationId,
                context.OrganizationId,
                StringComparison.Ordinal))
        {
            throw NotFound(
                "journal_not_found",
                "The accounting journal could not be found.");
        }
        if (!string.Equals(header.ShopId, context.ShopId, StringComparison.Ordinal) &&
            !IsAdministrator(user))
        {
            throw NotFound(
                "journal_not_found",
                "The accounting journal could not be found.");
        }

        await RequireAccountingAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);
        IReadOnlyList<AccountingJournalLineRecord> lines =
            await ReadJournalLinesAsync(
                connection,
                transaction: null,
                id,
                cancellationToken);
        return ToJournalRecord(header, lines);
    }

    public async Task<AccountingJournalRecord> CreateJournalAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateAccountingJournalRequest request,
        CancellationToken cancellationToken = default)
    {
        string journalDate = NormalizeDate(
            request.JournalDate,
            "invalid_journal_date");
        string description = RequiredText(
            request.Description,
            500,
            "journal_description_required",
            "Enter the journal description.");
        IReadOnlyList<NormalizedJournalLine> lines = NormalizeLines(request.Lines);
        (long debit, long credit) = CalculateTotals(lines);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await RequireAccountingAccessAsync(
            connection,
            transaction,
            user,
            context.ShopId,
            cancellationToken);
        await ValidateAccountsAsync(
            connection,
            transaction,
            context.OrganizationId,
            lines,
            requireManualPosting: true,
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string id = Guid.NewGuid().ToString("N");
        string journalNumber = await NextJournalNumberAsync(
            connection,
            transaction,
            context,
            now,
            cancellationToken);

        await InsertJournalHeaderAsync(
            connection,
            transaction,
            id,
            context,
            journalNumber,
            journalDate,
            description,
            sourceType: "manual",
            sourceId: null,
            reversalOfJournalId: null,
            debit,
            credit,
            user.Id,
            now,
            cancellationToken);
        await InsertJournalLinesAsync(
            connection,
            transaction,
            id,
            context.ShopId,
            lines,
            cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "accounting.journal.created",
            "accounting_journal",
            id,
            new
            {
                journalNumber,
                context.OrganizationId,
                context.ShopId,
                journalDate,
                debitMinor = debit,
                creditMinor = credit,
                lineCount = lines.Count
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetJournalAsync(user, context, id, cancellationToken);
    }

    public async Task<AccountingJournalRecord> UpdateJournalAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string journalId,
        UpdateAccountingJournalRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(journalId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation(
                "invalid_journal_version",
                "The expected journal version is invalid.");
        }
        string journalDate = NormalizeDate(
            request.JournalDate,
            "invalid_journal_date");
        string description = RequiredText(
            request.Description,
            500,
            "journal_description_required",
            "Enter the journal description.");
        IReadOnlyList<NormalizedJournalLine> lines = NormalizeLines(request.Lines);
        (long debit, long credit) = CalculateTotals(lines);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        JournalHeader header = await RequireJournalHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        RequireJournalShop(header, context);
        RequireDraft(header);
        RequireJournalVersion(header, request.ExpectedVersion);
        await RequireAccountingAccessAsync(
            connection,
            transaction,
            user,
            context.ShopId,
            cancellationToken);
        await ValidateAccountsAsync(
            connection,
            transaction,
            context.OrganizationId,
            lines,
            requireManualPosting: true,
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE accounting_journals
            SET journal_date = $journalDate,
                description = $description,
                total_debit_minor = $totalDebitMinor,
                total_credit_minor = $totalCreditMinor,
                updated_at_utc = $updatedAtUtc,
                version = version + 1
            WHERE id = $id
              AND organization_id = $organizationId
              AND shop_id = $shopId
              AND status = 'draft'
              AND version = $expectedVersion;
            """;
            update.Parameters.AddWithValue("$journalDate", journalDate);
            update.Parameters.AddWithValue("$description", description);
            update.Parameters.AddWithValue("$totalDebitMinor", debit);
            update.Parameters.AddWithValue("$totalCreditMinor", credit);
            update.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue(
                "$organizationId",
                context.OrganizationId);
            update.Parameters.AddWithValue("$shopId", context.ShopId);
            update.Parameters.AddWithValue(
                "$expectedVersion",
                request.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "journal_changed",
                    "The journal changed. Reload it and try again.");
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
            """
            DELETE FROM accounting_journal_lines
            WHERE journal_id = $journalId;
            """;
            delete.Parameters.AddWithValue("$journalId", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertJournalLinesAsync(
            connection,
            transaction,
            id,
            context.ShopId,
            lines,
            cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "accounting.journal.updated",
            "accounting_journal",
            id,
            new
            {
                header.JournalNumber,
                journalDate,
                debitMinor = debit,
                creditMinor = credit,
                lineCount = lines.Count,
                previousVersion = header.Version
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetJournalAsync(user, context, id, cancellationToken);
    }

    public async Task<AccountingJournalRecord> PostJournalAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string journalId,
        PostAccountingJournalRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(journalId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation(
                "invalid_journal_version",
                "The expected journal version is invalid.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        JournalHeader header = await RequireJournalHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        RequireJournalShop(header, context);
        RequireDraft(header);
        RequireJournalVersion(header, request.ExpectedVersion);
        await RequireAccountingAccessAsync(
            connection,
            transaction,
            user,
            context.ShopId,
            cancellationToken);
        if (!IsAdministrator(user) &&
            string.Equals(header.CreatedByUserId, user.Id, StringComparison.Ordinal))
        {
            throw Forbidden(
                "journal_posting_separation_required",
                "A branch manager cannot post a manual journal they created. Ask another manager or an administrator.");
        }

        IReadOnlyList<AccountingJournalLineRecord> storedLines =
            await ReadJournalLinesAsync(
                connection,
                transaction,
                id,
                cancellationToken);
        if (storedLines.Count < 2 ||
            header.TotalDebitMinor <= 0 ||
            header.TotalDebitMinor != header.TotalCreditMinor ||
            storedLines.Sum(line => line.DebitMinor) != header.TotalDebitMinor ||
            storedLines.Sum(line => line.CreditMinor) != header.TotalCreditMinor)
        {
            throw Conflict(
                "journal_not_balanced",
                "The journal must contain equal, positive debit and credit totals before posting.");
        }

        IReadOnlyList<NormalizedJournalLine> normalizedLines = storedLines
            .Select(line => new NormalizedJournalLine(
                line.LineNumber,
                line.AccountId,
                line.DebitMinor,
                line.CreditMinor,
                line.Description,
                line.CounterpartyType,
                line.CounterpartyId))
            .ToList();
        await ValidateAccountsAsync(
            connection,
            transaction,
            context.OrganizationId,
            normalizedLines,
            requireManualPosting: true,
            cancellationToken);
        await EnsureOpenPeriodAsync(
            connection,
            transaction,
            context.OrganizationId,
            header.JournalDate,
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE accounting_journals
            SET status = 'posted',
                posted_by_user_id = $postedByUserId,
                posted_at_utc = $postedAtUtc,
                updated_at_utc = $updatedAtUtc,
                version = version + 1
            WHERE id = $id
              AND organization_id = $organizationId
              AND shop_id = $shopId
              AND status = 'draft'
              AND version = $expectedVersion;
            """;
            update.Parameters.AddWithValue("$postedByUserId", user.Id);
            update.Parameters.AddWithValue("$postedAtUtc", now.ToString("O"));
            update.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue(
                "$organizationId",
                context.OrganizationId);
            update.Parameters.AddWithValue("$shopId", context.ShopId);
            update.Parameters.AddWithValue(
                "$expectedVersion",
                request.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "journal_changed",
                    "The journal changed. Reload it and try again.");
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "journal_posting_control_failed",
                "The journal failed a database posting control.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "accounting.journal.posted",
            "accounting_journal",
            id,
            new
            {
                header.JournalNumber,
                header.JournalDate,
                header.TotalDebitMinor,
                header.TotalCreditMinor,
                previousVersion = header.Version
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetJournalAsync(user, context, id, cancellationToken);
    }

    public async Task<AccountingJournalReversalResult> ReverseJournalAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string journalId,
        ReverseAccountingJournalRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(journalId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation(
                "invalid_journal_version",
                "The expected journal version is invalid.");
        }
        string reversalDate = NormalizeDate(
            request.ReversalDate,
            "invalid_reversal_date");
        string reason = RequiredText(
            request.Reason,
            500,
            "reversal_reason_required",
            "Enter the reason for reversing the journal.");
        if (reason.Length < 5)
        {
            throw Validation(
                "reversal_reason_too_short",
                "The reversal reason must contain at least five characters.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        JournalHeader original = await RequireJournalHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        RequireJournalShop(original, context);
        RequireJournalVersion(original, request.ExpectedVersion);
        if (original.Status != "posted")
        {
            throw Conflict(
                "journal_not_posted",
                "Only a posted journal can be reversed.");
        }
        if (original.SourceType == "system")
        {
            throw Conflict(
                "system_journal_reversal_requires_source_workflow",
                "System-generated journals must be reversed through their originating sale, expense, receipt or payment workflow.");
        }
        await RequireAccountingAccessAsync(
            connection,
            transaction,
            user,
            context.ShopId,
            cancellationToken);
        if (!IsAdministrator(user) &&
            string.Equals(original.PostedByUserId, user.Id, StringComparison.Ordinal))
        {
            throw Forbidden(
                "journal_reversal_separation_required",
                "A branch manager cannot reverse a journal they posted. Ask another manager or an administrator.");
        }
        await EnsureOpenPeriodAsync(
            connection,
            transaction,
            context.OrganizationId,
            reversalDate,
            cancellationToken);

        IReadOnlyList<AccountingJournalLineRecord> originalLines =
            await ReadJournalLinesAsync(
                connection,
                transaction,
                id,
                cancellationToken);
        if (originalLines.Count < 2)
        {
            throw Conflict(
                "journal_lines_missing",
                "The posted journal does not contain the required ledger lines.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string reversalId = Guid.NewGuid().ToString("N");
        string reversalNumber = await NextJournalNumberAsync(
            connection,
            transaction,
            context,
            now,
            cancellationToken);
        string reversalDescription = OptionalText(
            $"Reversal of {original.JournalNumber}: {reason}",
            500);

        await InsertJournalHeaderAsync(
            connection,
            transaction,
            reversalId,
            context,
            reversalNumber,
            reversalDate,
            reversalDescription,
            sourceType: "reversal",
            sourceId: original.Id,
            reversalOfJournalId: original.Id,
            original.TotalDebitMinor,
            original.TotalCreditMinor,
            user.Id,
            now,
            cancellationToken);

        int lineNumber = 1;
        foreach (AccountingJournalLineRecord line in originalLines)
        {
            await using var insertLine = connection.CreateCommand();
            insertLine.Transaction = transaction;
            insertLine.CommandText =
            """
            INSERT INTO accounting_journal_lines
            (
                journal_id,
                line_number,
                account_id,
                shop_id,
                debit_minor,
                credit_minor,
                description,
                counterparty_type,
                counterparty_id
            )
            VALUES
            (
                $journalId,
                $lineNumber,
                $accountId,
                $shopId,
                $debitMinor,
                $creditMinor,
                $description,
                $counterpartyType,
                $counterpartyId
            );
            """;
            insertLine.Parameters.AddWithValue("$journalId", reversalId);
            insertLine.Parameters.AddWithValue("$lineNumber", lineNumber++);
            insertLine.Parameters.AddWithValue("$accountId", line.AccountId);
            insertLine.Parameters.AddWithValue("$shopId", context.ShopId);
            insertLine.Parameters.AddWithValue("$debitMinor", line.CreditMinor);
            insertLine.Parameters.AddWithValue("$creditMinor", line.DebitMinor);
            insertLine.Parameters.AddWithValue(
                "$description",
                OptionalText($"Reversal: {line.Description}", 250));
            insertLine.Parameters.AddWithValue(
                "$counterpartyType",
                line.CounterpartyType ?? (object)DBNull.Value);
            insertLine.Parameters.AddWithValue(
                "$counterpartyId",
                line.CounterpartyId ?? (object)DBNull.Value);
            await insertLine.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await using (var postReversal = connection.CreateCommand())
            {
                postReversal.Transaction = transaction;
                postReversal.CommandText =
                """
                UPDATE accounting_journals
                SET status = 'posted',
                    posted_by_user_id = $userId,
                    posted_at_utc = $postedAtUtc,
                    updated_at_utc = $updatedAtUtc,
                    version = version + 1
                WHERE id = $id
                  AND status = 'draft'
                  AND version = 1;
                """;
                postReversal.Parameters.AddWithValue("$userId", user.Id);
                postReversal.Parameters.AddWithValue("$postedAtUtc", now.ToString("O"));
                postReversal.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
                postReversal.Parameters.AddWithValue("$id", reversalId);
                if (await postReversal.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw Conflict(
                        "reversal_posting_failed",
                        "The reversal journal could not be posted.");
                }
            }

            await using (var markOriginal = connection.CreateCommand())
            {
                markOriginal.Transaction = transaction;
                markOriginal.CommandText =
                """
                UPDATE accounting_journals
                SET status = 'reversed',
                    reversed_by_journal_id = $reversalId,
                    updated_at_utc = $updatedAtUtc,
                    version = version + 1
                WHERE id = $id
                  AND status = 'posted'
                  AND version = $expectedVersion;
                """;
                markOriginal.Parameters.AddWithValue("$reversalId", reversalId);
                markOriginal.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
                markOriginal.Parameters.AddWithValue("$id", original.Id);
                markOriginal.Parameters.AddWithValue(
                    "$expectedVersion",
                    request.ExpectedVersion);
                if (await markOriginal.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw Conflict(
                        "journal_changed",
                        "The original journal changed before it could be reversed.");
                }
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "journal_reversal_control_failed",
                "The reversal failed a database ledger control.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "accounting.journal.reversed",
            "accounting_journal",
            original.Id,
            new
            {
                original.JournalNumber,
                reversalId,
                reversalNumber,
                reversalDate,
                reason,
                original.TotalDebitMinor,
                original.TotalCreditMinor
            },
            cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "accounting.reversal.posted",
            "accounting_journal",
            reversalId,
            new
            {
                reversalNumber,
                originalJournalId = original.Id,
                original.JournalNumber,
                reversalDate,
                reason
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AccountingJournalRecord originalRecord =
            await GetJournalAsync(user, context, original.Id, cancellationToken);
        AccountingJournalRecord reversalRecord =
            await GetJournalAsync(user, context, reversalId, cancellationToken);
        return new AccountingJournalReversalResult(
            originalRecord,
            reversalRecord);
    }

    private static async Task InsertJournalHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        ActiveShopContextRecord context,
        string journalNumber,
        string journalDate,
        string description,
        string sourceType,
        string? sourceId,
        string? reversalOfJournalId,
        long totalDebitMinor,
        long totalCreditMinor,
        string createdByUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO accounting_journals
        (
            id,
            organization_id,
            shop_id,
            journal_number,
            journal_date,
            currency_code,
            description,
            source_type,
            source_id,
            status,
            reversal_of_journal_id,
            total_debit_minor,
            total_credit_minor,
            version,
            created_by_user_id,
            created_at_utc,
            updated_at_utc
        )
        VALUES
        (
            $id,
            $organizationId,
            $shopId,
            $journalNumber,
            $journalDate,
            $currencyCode,
            $description,
            $sourceType,
            $sourceId,
            'draft',
            $reversalOfJournalId,
            $totalDebitMinor,
            $totalCreditMinor,
            1,
            $createdByUserId,
            $createdAtUtc,
            $updatedAtUtc
        );
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$journalNumber", journalNumber);
        command.Parameters.AddWithValue("$journalDate", journalDate);
        command.Parameters.AddWithValue("$currencyCode", context.CurrencyCode);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$sourceType", sourceType);
        command.Parameters.AddWithValue(
            "$sourceId",
            sourceId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$reversalOfJournalId",
            reversalOfJournalId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$totalDebitMinor", totalDebitMinor);
        command.Parameters.AddWithValue("$totalCreditMinor", totalCreditMinor);
        command.Parameters.AddWithValue("$createdByUserId", createdByUserId);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertJournalLinesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string journalId,
        string shopId,
        IReadOnlyList<NormalizedJournalLine> lines,
        CancellationToken cancellationToken)
    {
        foreach (NormalizedJournalLine line in lines)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            INSERT INTO accounting_journal_lines
            (
                journal_id,
                line_number,
                account_id,
                shop_id,
                debit_minor,
                credit_minor,
                description,
                counterparty_type,
                counterparty_id
            )
            VALUES
            (
                $journalId,
                $lineNumber,
                $accountId,
                $shopId,
                $debitMinor,
                $creditMinor,
                $description,
                $counterpartyType,
                $counterpartyId
            );
            """;
            command.Parameters.AddWithValue("$journalId", journalId);
            command.Parameters.AddWithValue("$lineNumber", line.LineNumber);
            command.Parameters.AddWithValue("$accountId", line.AccountId);
            command.Parameters.AddWithValue("$shopId", shopId);
            command.Parameters.AddWithValue("$debitMinor", line.DebitMinor);
            command.Parameters.AddWithValue("$creditMinor", line.CreditMinor);
            command.Parameters.AddWithValue("$description", line.Description);
            command.Parameters.AddWithValue(
                "$counterpartyType",
                line.CounterpartyType ?? (object)DBNull.Value);
            command.Parameters.AddWithValue(
                "$counterpartyId",
                line.CounterpartyId ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<JournalHeader?> ReadJournalHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string journalId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            journal.id,
            journal.organization_id,
            journal.shop_id,
            shop.code,
            shop.name,
            journal.journal_number,
            journal.journal_date,
            journal.currency_code,
            journal.description,
            journal.source_type,
            journal.source_id,
            journal.status,
            journal.reversal_of_journal_id,
            journal.reversed_by_journal_id,
            journal.total_debit_minor,
            journal.total_credit_minor,
            journal.version,
            journal.created_by_user_id,
            COALESCE(created.display_name, ''),
            journal.posted_by_user_id,
            COALESCE(posted.display_name, ''),
            journal.created_at_utc,
            journal.updated_at_utc,
            journal.posted_at_utc
        FROM accounting_journals AS journal
        INNER JOIN shops AS shop
            ON shop.id = journal.shop_id
        LEFT JOIN users AS created
            ON created.id = journal.created_by_user_id
        LEFT JOIN users AS posted
            ON posted.id = journal.posted_by_user_id
        WHERE journal.id = $journalId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$journalId", journalId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new JournalHeader(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            GetNullableString(reader, 10),
            reader.GetString(11),
            GetNullableString(reader, 12),
            GetNullableString(reader, 13),
            reader.GetInt64(14),
            reader.GetInt64(15),
            reader.GetInt32(16),
            reader.GetString(17),
            reader.GetString(18),
            GetNullableString(reader, 19),
            reader.GetString(20),
            ParseDateTime(reader.GetString(21)),
            ParseDateTime(reader.GetString(22)),
            GetNullableDateTime(reader, 23));
    }

    private static async Task<JournalHeader> RequireJournalHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string journalId,
        CancellationToken cancellationToken)
    {
        JournalHeader? header = await ReadJournalHeaderAsync(
            connection,
            transaction,
            journalId,
            cancellationToken);
        if (header is null ||
            !string.Equals(
                header.OrganizationId,
                context.OrganizationId,
                StringComparison.Ordinal))
        {
            throw NotFound(
                "journal_not_found",
                "The accounting journal could not be found.");
        }

        return header;
    }

    private static async Task<IReadOnlyList<AccountingJournalLineRecord>>
        ReadJournalLinesAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string journalId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            line.id,
            line.line_number,
            line.account_id,
            account.code,
            account.name,
            account.account_type,
            account.normal_balance,
            line.debit_minor,
            line.credit_minor,
            line.description,
            line.counterparty_type,
            line.counterparty_id
        FROM accounting_journal_lines AS line
        INNER JOIN accounting_accounts AS account
            ON account.id = line.account_id
        WHERE line.journal_id = $journalId
        ORDER BY line.line_number, line.id;
        """;
        command.Parameters.AddWithValue("$journalId", journalId);

        var lines = new List<AccountingJournalLineRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new AccountingJournalLineRecord(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetString(9),
                GetNullableString(reader, 10),
                GetNullableString(reader, 11)));
        }

        return lines;
    }

    private static AccountingJournalRecord ToJournalRecord(
        JournalHeader header,
        IReadOnlyList<AccountingJournalLineRecord> lines) =>
        new(
            header.Id,
            header.OrganizationId,
            header.ShopId,
            header.ShopCode,
            header.ShopName,
            header.JournalNumber,
            header.JournalDate,
            header.CurrencyCode,
            header.Description,
            header.SourceType,
            header.SourceId,
            header.Status,
            header.ReversalOfJournalId,
            header.ReversedByJournalId,
            header.TotalDebitMinor,
            header.TotalCreditMinor,
            header.Version,
            header.CreatedByUserId,
            header.CreatedByDisplayName,
            header.PostedByUserId,
            header.PostedByDisplayName,
            header.CreatedAtUtc,
            header.UpdatedAtUtc,
            header.PostedAtUtc,
            lines);

    private static void RequireJournalShop(
        JournalHeader header,
        ActiveShopContextRecord context)
    {
        if (!string.Equals(header.ShopId, context.ShopId, StringComparison.Ordinal))
        {
            throw Forbidden(
                "journal_shop_context_required",
                "Switch to the journal branch before performing this operation.");
        }
    }

    private static void RequireDraft(JournalHeader header)
    {
        if (header.Status != "draft")
        {
            throw Conflict(
                "journal_not_draft",
                "Only a draft journal can be edited or posted.");
        }
    }

    private static void RequireJournalVersion(
        JournalHeader header,
        int expectedVersion)
    {
        if (header.Version != expectedVersion)
        {
            throw Conflict(
                "journal_changed",
                "The journal changed. Reload it and try again.");
        }
    }
}
