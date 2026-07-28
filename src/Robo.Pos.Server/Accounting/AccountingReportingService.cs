using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Accounting;

public sealed partial class AccountingService
{
    public async Task<TrialBalanceReport> GetTrialBalanceAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? requestedScope,
        string? requestedFromDate,
        string? requestedToDate,
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
                "Only an administrator can view consolidated financial reports.");
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        string fromDate = string.IsNullOrWhiteSpace(requestedFromDate)
            ? new DateOnly(today.Year, 1, 1).ToString("yyyy-MM-dd")
            : NormalizeDate(requestedFromDate, "invalid_report_from_date");
        string toDate = string.IsNullOrWhiteSpace(requestedToDate)
            ? today.ToString("yyyy-MM-dd")
            : NormalizeDate(requestedToDate, "invalid_report_to_date");
        if (string.CompareOrdinal(fromDate, toDate) > 0)
        {
            throw Validation(
                "invalid_report_period",
                "The report start date must not be after the end date.");
        }

        DateOnly parsedFrom = DateOnly.ParseExact(fromDate, "yyyy-MM-dd");
        DateOnly parsedTo = DateOnly.ParseExact(toDate, "yyyy-MM-dd");
        if (parsedTo.DayNumber - parsedFrom.DayNumber > 3660)
        {
            throw Validation(
                "report_period_too_large",
                "A trial balance cannot cover more than ten years at once.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireAccountingAccessAsync(
            connection,
            transaction: null,
            user,
            context.ShopId,
            cancellationToken);

        string currencyCode = await ResolveReportCurrencyAsync(
            connection,
            context,
            consolidated,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        WITH movements AS
        (
            SELECT
                line.account_id,
                SUM(line.debit_minor) AS debit_movement,
                SUM(line.credit_minor) AS credit_movement
            FROM accounting_journal_lines AS line
            INNER JOIN accounting_journals AS journal
                ON journal.id = line.journal_id
            WHERE journal.organization_id = $organizationId
              AND journal.status IN ('posted', 'reversed')
              AND journal.journal_date BETWEEN $fromDate AND $toDate
              AND ($consolidated = 1 OR journal.shop_id = $shopId)
            GROUP BY line.account_id
        )
        SELECT
            account.id,
            account.code,
            account.name,
            account.account_type,
            account.normal_balance,
            COALESCE(movement.debit_movement, 0),
            COALESCE(movement.credit_movement, 0)
        FROM accounting_accounts AS account
        LEFT JOIN movements AS movement
            ON movement.account_id = account.id
        WHERE account.organization_id = $organizationId
        ORDER BY account.code COLLATE NOCASE, account.name COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);
        command.Parameters.AddWithValue("$fromDate", fromDate);
        command.Parameters.AddWithValue("$toDate", toDate);
        command.Parameters.AddWithValue("$consolidated", consolidated ? 1 : 0);
        command.Parameters.AddWithValue("$shopId", context.ShopId);

        var lines = new List<TrialBalanceLineRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string normalBalance = reader.GetString(4);
            long debitMovement = reader.GetInt64(5);
            long creditMovement = reader.GetInt64(6);
            long debitBalance = 0;
            long creditBalance = 0;

            if (normalBalance == "debit")
            {
                long net = checked(debitMovement - creditMovement);
                if (net >= 0)
                {
                    debitBalance = net;
                }
                else
                {
                    creditBalance = checked(-net);
                }
            }
            else
            {
                long net = checked(creditMovement - debitMovement);
                if (net >= 0)
                {
                    creditBalance = net;
                }
                else
                {
                    debitBalance = checked(-net);
                }
            }

            lines.Add(new TrialBalanceLineRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                normalBalance,
                debitMovement,
                creditMovement,
                debitBalance,
                creditBalance));
        }

        long totalDebitMovement = lines.Sum(line => line.DebitMovementMinor);
        long totalCreditMovement = lines.Sum(line => line.CreditMovementMinor);
        long totalDebitBalance = lines.Sum(line => line.DebitBalanceMinor);
        long totalCreditBalance = lines.Sum(line => line.CreditBalanceMinor);
        if (totalDebitMovement != totalCreditMovement ||
            totalDebitBalance != totalCreditBalance)
        {
            throw new AccountingException(
                StatusCodes.Status500InternalServerError,
                "ledger_out_of_balance",
                "The posted ledger is out of balance. Stop posting and investigate the database immediately.");
        }

        return new TrialBalanceReport(
            scope,
            context.OrganizationId,
            context.OrganizationName,
            consolidated ? null : context.ShopId,
            consolidated ? null : context.ShopCode,
            currencyCode,
            fromDate,
            toDate,
            totalDebitMovement,
            totalCreditMovement,
            totalDebitBalance,
            totalCreditBalance,
            lines);
    }

    private static async Task<string> ResolveReportCurrencyAsync(
        SqliteConnection connection,
        ActiveShopContextRecord context,
        bool consolidated,
        CancellationToken cancellationToken)
    {
        if (!consolidated)
        {
            return context.CurrencyCode;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT DISTINCT currency_code
        FROM shops
        WHERE organization_id = $organizationId
          AND is_active = 1
        ORDER BY currency_code;
        """;
        command.Parameters.AddWithValue(
            "$organizationId",
            context.OrganizationId);

        var currencies = new List<string>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            currencies.Add(reader.GetString(0));
        }

        if (currencies.Count == 0)
        {
            return context.CurrencyCode;
        }
        if (currencies.Count > 1)
        {
            throw Conflict(
                "mixed_currency_consolidation_not_supported",
                "Consolidated accounting reports require every active branch to use the same currency.");
        }

        return currencies[0];
    }
}
