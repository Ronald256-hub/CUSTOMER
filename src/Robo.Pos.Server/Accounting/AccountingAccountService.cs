using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Accounting;

public sealed partial class AccountingService
{
    public async Task<IReadOnlyList<AccountingAccountRecord>> ListAccountsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
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
            id,
            organization_id,
            code,
            name,
            account_type,
            normal_balance,
            parent_account_id,
            system_key,
            allow_manual_posting,
            is_active,
            version,
            created_at_utc,
            updated_at_utc
        FROM accounting_accounts
        WHERE organization_id = $organizationId
          AND ($includeInactive = 1 OR is_active = 1)
        ORDER BY code COLLATE NOCASE, name COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue(
            "$includeInactive",
            includeInactive ? 1 : 0);

        var accounts = new List<AccountingAccountRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            accounts.Add(ToAccountRecord(ReadAccount(reader)));
        }

        return accounts;
    }

    public async Task<AccountingAccountRecord> CreateAccountAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateAccountingAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string code = NormalizeAccountCode(request.Code);
        string name = RequiredText(
            request.Name,
            150,
            "account_name_required",
            "Enter the account name.");
        string accountType = NormalizeAccountType(request.AccountType);
        string normalBalance = NormalBalanceFor(accountType);
        string? parentAccountId = string.IsNullOrWhiteSpace(request.ParentAccountId)
            ? null
            : NormalizeId(request.ParentAccountId);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        if (parentAccountId is not null)
        {
            AccountIdentity? parent = await ReadAccountIdentityAsync(
                connection,
                transaction,
                context.OrganizationId,
                parentAccountId,
                cancellationToken);
            if (parent is null)
            {
                throw Validation(
                    "parent_account_not_found",
                    "Select a parent account from the active organization.");
            }
        }

        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            INSERT INTO accounting_accounts
            (
                id,
                organization_id,
                code,
                name,
                account_type,
                normal_balance,
                parent_account_id,
                system_key,
                allow_manual_posting,
                is_active,
                version,
                created_by_user_id,
                updated_by_user_id,
                created_at_utc,
                updated_at_utc
            )
            VALUES
            (
                $id,
                $organizationId,
                $code,
                $name,
                $accountType,
                $normalBalance,
                $parentAccountId,
                NULL,
                $allowManualPosting,
                1,
                1,
                $userId,
                $userId,
                $createdAtUtc,
                $updatedAtUtc
            );
            """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue(
                "$organizationId",
                context.OrganizationId);
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$accountType", accountType);
            command.Parameters.AddWithValue("$normalBalance", normalBalance);
            command.Parameters.AddWithValue(
                "$parentAccountId",
                parentAccountId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue(
                "$allowManualPosting",
                request.AllowManualPosting ? 1 : 0);
            command.Parameters.AddWithValue("$userId", user.Id);
            command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "account_code_conflict",
                "An account with this code already exists in the organization.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "accounting.account.created",
            "accounting_account",
            id,
            new
            {
                context.OrganizationId,
                code,
                name,
                accountType,
                normalBalance,
                parentAccountId,
                request.AllowManualPosting
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AccountingAccountRecord(
            id,
            context.OrganizationId,
            code,
            name,
            accountType,
            normalBalance,
            parentAccountId,
            null,
            request.AllowManualPosting,
            true,
            1,
            now,
            now);
    }

    public async Task<AccountingAccountRecord> UpdateAccountAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string accountId,
        UpdateAccountingAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string id = NormalizeId(accountId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation(
                "invalid_account_version",
                "The expected account version is invalid.");
        }

        string name = RequiredText(
            request.Name,
            150,
            "account_name_required",
            "Enter the account name.");
        string? parentAccountId = string.IsNullOrWhiteSpace(request.ParentAccountId)
            ? null
            : NormalizeId(request.ParentAccountId);
        if (string.Equals(parentAccountId, id, StringComparison.Ordinal))
        {
            throw Validation(
                "invalid_parent_account",
                "An account cannot be its own parent.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        AccountIdentity existing = await ReadAccountIdentityAsync(
                connection,
                transaction,
                context.OrganizationId,
                id,
                cancellationToken)
            ?? throw NotFound(
                "account_not_found",
                "The accounting account could not be found.");
        if (existing.Version != request.ExpectedVersion)
        {
            throw Conflict(
                "account_changed",
                "The accounting account changed. Reload it and try again.");
        }

        if (parentAccountId is not null)
        {
            AccountIdentity? parent = await ReadAccountIdentityAsync(
                connection,
                transaction,
                context.OrganizationId,
                parentAccountId,
                cancellationToken);
            if (parent is null)
            {
                throw Validation(
                    "parent_account_not_found",
                    "Select a parent account from the active organization.");
            }
        }

        if (existing.SystemKey is not null &&
            (existing.ParentAccountId != parentAccountId ||
             existing.AllowManualPosting != request.AllowManualPosting ||
             !request.IsActive))
        {
            throw Forbidden(
                "system_account_structure_locked",
                "System account structure, posting controls and active status cannot be changed.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE accounting_accounts
        SET name = $name,
            parent_account_id = $parentAccountId,
            allow_manual_posting = $allowManualPosting,
            is_active = $isActive,
            version = version + 1,
            updated_by_user_id = $userId,
            updated_at_utc = $updatedAtUtc
        WHERE id = $id
          AND organization_id = $organizationId
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue(
            "$parentAccountId",
            parentAccountId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$allowManualPosting",
            request.AllowManualPosting ? 1 : 0);
        command.Parameters.AddWithValue("$isActive", request.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue(
            "$expectedVersion",
            request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "account_changed",
                "The accounting account changed. Reload it and try again.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "accounting.account.updated",
            "accounting_account",
            id,
            new
            {
                name,
                parentAccountId,
                request.AllowManualPosting,
                request.IsActive,
                previousVersion = existing.Version
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AccountingAccountRecord(
            id,
            existing.OrganizationId,
            existing.Code,
            name,
            existing.AccountType,
            existing.NormalBalance,
            parentAccountId,
            existing.SystemKey,
            request.AllowManualPosting,
            request.IsActive,
            existing.Version + 1,
            existing.CreatedAtUtc,
            now);
    }

    public async Task<IReadOnlyList<AccountingPeriodRecord>> ListPeriodsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken = default)
    {
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
            id,
            organization_id,
            name,
            start_date,
            end_date,
            status,
            version,
            closed_by_user_id,
            created_at_utc,
            updated_at_utc,
            closed_at_utc
        FROM accounting_periods
        WHERE organization_id = $organizationId
        ORDER BY start_date DESC, end_date DESC;
        """;
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);

        var periods = new List<AccountingPeriodRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            periods.Add(ReadPeriod(reader));
        }

        return periods;
    }

    public async Task<AccountingPeriodRecord> CreatePeriodAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateAccountingPeriodRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string name = RequiredText(
            request.Name,
            100,
            "period_name_required",
            "Enter the accounting period name.");
        string startDate = NormalizeDate(
            request.StartDate,
            "invalid_period_start_date");
        string endDate = NormalizeDate(
            request.EndDate,
            "invalid_period_end_date");
        if (string.CompareOrdinal(startDate, endDate) > 0)
        {
            throw Validation(
                "invalid_accounting_period",
                "The accounting period start date must not be after the end date.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            INSERT INTO accounting_periods
            (
                id,
                organization_id,
                name,
                start_date,
                end_date,
                status,
                version,
                created_by_user_id,
                created_at_utc,
                updated_at_utc
            )
            VALUES
            (
                $id,
                $organizationId,
                $name,
                $startDate,
                $endDate,
                'open',
                1,
                $userId,
                $createdAtUtc,
                $updatedAtUtc
            );
            """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue(
                "$organizationId",
                context.OrganizationId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$startDate", startDate);
            command.Parameters.AddWithValue("$endDate", endDate);
            command.Parameters.AddWithValue("$userId", user.Id);
            command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "accounting_period_overlap",
                "Accounting periods cannot overlap.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "accounting.period.created",
            "accounting_period",
            id,
            new
            {
                context.OrganizationId,
                name,
                startDate,
                endDate
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AccountingPeriodRecord(
            id,
            context.OrganizationId,
            name,
            startDate,
            endDate,
            "open",
            1,
            null,
            now,
            now,
            null);
    }

    public async Task<AccountingPeriodRecord> ClosePeriodAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string periodId,
        CloseAccountingPeriodRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string id = NormalizeId(periodId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation(
                "invalid_period_version",
                "The expected accounting period version is invalid.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        AccountingPeriodRecord existing = await ReadPeriodAsync(
                connection,
                transaction,
                context.OrganizationId,
                id,
                cancellationToken)
            ?? throw NotFound(
                "accounting_period_not_found",
                "The accounting period could not be found.");
        if (existing.Version != request.ExpectedVersion)
        {
            throw Conflict(
                "accounting_period_changed",
                "The accounting period changed. Reload it and try again.");
        }
        if (existing.Status != "open")
        {
            throw Conflict(
                "accounting_period_already_closed",
                "This accounting period is already closed.");
        }

        await using (var drafts = connection.CreateCommand())
        {
            drafts.Transaction = transaction;
            drafts.CommandText =
            """
            SELECT COUNT(1)
            FROM accounting_journals
            WHERE organization_id = $organizationId
              AND status = 'draft'
              AND journal_date BETWEEN $startDate AND $endDate;
            """;
            drafts.Parameters.AddWithValue(
                "$organizationId",
                context.OrganizationId);
            drafts.Parameters.AddWithValue("$startDate", existing.StartDate);
            drafts.Parameters.AddWithValue("$endDate", existing.EndDate);
            int draftCount = Convert.ToInt32(
                await drafts.ExecuteScalarAsync(cancellationToken));
            if (draftCount > 0)
            {
                throw Conflict(
                    "draft_journals_in_period",
                    "Post or correct every draft journal before closing the period.");
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            UPDATE accounting_periods
            SET status = 'closed',
                closed_by_user_id = $userId,
                closed_at_utc = $closedAtUtc,
                updated_at_utc = $updatedAtUtc,
                version = version + 1
            WHERE id = $id
              AND organization_id = $organizationId
              AND status = 'open'
              AND version = $expectedVersion;
            """;
            command.Parameters.AddWithValue("$userId", user.Id);
            command.Parameters.AddWithValue("$closedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue(
                "$organizationId",
                context.OrganizationId);
            command.Parameters.AddWithValue(
                "$expectedVersion",
                request.ExpectedVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "accounting_period_changed",
                    "The accounting period changed. Reload it and try again.");
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "accounting_period_close_blocked",
                "The accounting period failed a closing control.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "accounting.period.closed",
            "accounting_period",
            id,
            new
            {
                existing.Name,
                existing.StartDate,
                existing.EndDate,
                previousVersion = existing.Version
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return existing with
        {
            Status = "closed",
            Version = existing.Version + 1,
            ClosedByUserId = user.Id,
            UpdatedAtUtc = now,
            ClosedAtUtc = now
        };
    }

    private static async Task<AccountIdentity?> ReadAccountIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            id,
            organization_id,
            code,
            name,
            account_type,
            normal_balance,
            parent_account_id,
            system_key,
            allow_manual_posting,
            is_active,
            version,
            created_at_utc,
            updated_at_utc
        FROM accounting_accounts
        WHERE id = $id
          AND organization_id = $organizationId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", accountId);
        command.Parameters.AddWithValue("$organizationId", organizationId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadAccount(reader)
            : null;
    }

    private static AccountIdentity ReadAccount(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            GetNullableString(reader, 6),
            GetNullableString(reader, 7),
            reader.GetInt32(8) == 1,
            reader.GetInt32(9) == 1,
            reader.GetInt32(10),
            ParseDateTime(reader.GetString(11)),
            ParseDateTime(reader.GetString(12)));

    private static AccountingAccountRecord ToAccountRecord(
        AccountIdentity account) =>
        new(
            account.Id,
            account.OrganizationId,
            account.Code,
            account.Name,
            account.AccountType,
            account.NormalBalance,
            account.ParentAccountId,
            account.SystemKey,
            account.AllowManualPosting,
            account.IsActive,
            account.Version,
            account.CreatedAtUtc,
            account.UpdatedAtUtc);

    private static async Task<AccountingPeriodRecord?> ReadPeriodAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string periodId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            id,
            organization_id,
            name,
            start_date,
            end_date,
            status,
            version,
            closed_by_user_id,
            created_at_utc,
            updated_at_utc,
            closed_at_utc
        FROM accounting_periods
        WHERE id = $id
          AND organization_id = $organizationId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", periodId);
        command.Parameters.AddWithValue("$organizationId", organizationId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPeriod(reader)
            : null;
    }

    private static AccountingPeriodRecord ReadPeriod(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            GetNullableString(reader, 7),
            ParseDateTime(reader.GetString(8)),
            ParseDateTime(reader.GetString(9)),
            GetNullableDateTime(reader, 10));
}
