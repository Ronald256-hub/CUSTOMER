using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Finance;

public sealed partial class FinanceService
{
    private static readonly HashSet<string> PaymentMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cash",
            "mobile_money",
            "card",
            "bank"
        };

    private readonly DatabaseBootstrap _database;

    public FinanceService(DatabaseBootstrap database)
    {
        _database = database;
    }

    private static bool IsAdministrator(AuthenticatedUser user) =>
        string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);

    private static async Task RequireFinanceAccessAsync(
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

        string? level = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(level, "manager", StringComparison.OrdinalIgnoreCase))
        {
            throw Forbidden(
                "finance_permission_required",
                "A branch manager or administrator is required for finance operations.");
        }
    }

    private static string NormalizeId(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 100)
        {
            throw Validation("invalid_identifier", "The supplied identifier is invalid.");
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
            throw Validation("text_too_long", $"Text cannot exceed {maximumLength} characters.");
        }
        return normalized;
    }

    private static string OptionalText(string? value, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximumLength)
        {
            throw Validation("text_too_long", $"Text cannot exceed {maximumLength} characters.");
        }
        return normalized;
    }

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
            throw Validation(errorCode, "Use a valid date in YYYY-MM-DD format.");
        }
        return normalized;
    }

    private static string NormalizePaymentMethod(string? value)
    {
        string method = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!PaymentMethods.Contains(method))
        {
            throw Validation(
                "invalid_payment_method",
                "Use cash, mobile money, card or bank.");
        }
        return method;
    }

    private static IReadOnlyList<NormalizedAllocation> NormalizeAllocations(
        IReadOnlyList<SettlementAllocationInput>? allocations)
    {
        if (allocations is null || allocations.Count == 0)
        {
            throw Validation(
                "settlement_allocations_required",
                "Allocate the settlement to at least one open item.");
        }
        if (allocations.Count > 100)
        {
            throw Validation(
                "too_many_settlement_allocations",
                "A settlement cannot contain more than 100 allocations.");
        }

        var normalized = allocations
            .GroupBy(item => NormalizeId(item.ItemId), StringComparer.Ordinal)
            .Select(group => new NormalizedAllocation(
                group.Key,
                checked(group.Sum(item => item.AmountMinor))))
            .ToList();

        if (normalized.Any(item => item.AmountMinor <= 0))
        {
            throw Validation(
                "invalid_settlement_allocation",
                "Every settlement allocation must be greater than zero.");
        }
        return normalized;
    }

    private static async Task EnsureOpenPeriodAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string date,
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
          AND $date BETWEEN start_date AND end_date;
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$date", date);

        int count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
        if (count != 1)
        {
            throw Conflict(
                "accounting_period_closed",
                "The settlement date is not inside an open accounting period.");
        }
    }

    private static async Task<string> ResolveSystemAccountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string systemKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT id
        FROM accounting_accounts
        WHERE organization_id = $organizationId
          AND system_key = $systemKey
          AND is_active = 1
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$systemKey", systemKey);

        string? id = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken));
        if (string.IsNullOrWhiteSpace(id))
        {
            throw Conflict(
                "system_account_missing",
                $"The required {systemKey.Replace('_', ' ')} account is missing.");
        }
        return id;
    }

    private static string PaymentAccountKey(string paymentMethod) =>
        paymentMethod switch
        {
            "cash" => "cash_on_hand",
            "mobile_money" => "mobile_money_clearing",
            "card" => "card_clearing",
            "bank" => "bank_account",
            _ => throw new InvalidOperationException("Unsupported payment method.")
        };

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

    public async Task<IReadOnlyList<CustomerRecord>> ListCustomersAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireFinanceAccessAsync(
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
            customer_number,
            name,
            phone,
            email,
            address,
            tax_number,
            credit_limit_minor,
            payment_terms_days,
            is_active,
            version,
            created_at_utc,
            updated_at_utc
        FROM finance_customers
        WHERE organization_id = $organizationId
          AND ($includeInactive = 1 OR is_active = 1)
        ORDER BY name COLLATE NOCASE, customer_number;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$includeInactive", includeInactive ? 1 : 0);

        var records = new List<CustomerRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadCustomer(reader));
        }
        return records;
    }

    public async Task<CustomerRecord> CreateCustomerAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        string name = RequiredText(
            request.Name,
            150,
            "customer_name_required",
            "Enter the customer name.");
        string phone = OptionalText(request.Phone, 50);
        string email = OptionalText(request.Email, 150);
        string address = OptionalText(request.Address, 250);
        string taxNumber = OptionalText(request.TaxNumber, 100);
        ValidateTerms(request.CreditLimitMinor, request.PaymentTermsDays);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireFinanceAccessAsync(
            connection,
            transaction,
            user,
            context.ShopId,
            cancellationToken);

        string id = Guid.NewGuid().ToString("N");
        string customerNumber = $"CUS-{id[..8].ToUpperInvariant()}";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO finance_customers
        (
            id,
            organization_id,
            customer_number,
            name,
            phone,
            email,
            address,
            tax_number,
            credit_limit_minor,
            payment_terms_days,
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
            $customerNumber,
            $name,
            $phone,
            $email,
            $address,
            $taxNumber,
            $creditLimitMinor,
            $paymentTermsDays,
            1,
            1,
            $userId,
            $userId,
            $now,
            $now
        );
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$customerNumber", customerNumber);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$phone", phone);
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$address", address);
        command.Parameters.AddWithValue("$taxNumber", taxNumber);
        command.Parameters.AddWithValue("$creditLimitMinor", request.CreditLimitMinor);
        command.Parameters.AddWithValue("$paymentTermsDays", request.PaymentTermsDays);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "finance.customer.created",
            "customer",
            id,
            new
            {
                context.OrganizationId,
                context.ShopId,
                customerNumber,
                name,
                request.CreditLimitMinor,
                request.PaymentTermsDays
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CustomerRecord(
            id,
            context.OrganizationId,
            customerNumber,
            name,
            phone,
            email,
            address,
            taxNumber,
            request.CreditLimitMinor,
            request.PaymentTermsDays,
            true,
            1,
            now,
            now);
    }

    public async Task<CustomerRecord> UpdateCustomerAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string customerId,
        UpdateCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(customerId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation("invalid_customer_version", "The customer version is invalid.");
        }
        string name = RequiredText(
            request.Name,
            150,
            "customer_name_required",
            "Enter the customer name.");
        string phone = OptionalText(request.Phone, 50);
        string email = OptionalText(request.Email, 150);
        string address = OptionalText(request.Address, 250);
        string taxNumber = OptionalText(request.TaxNumber, 100);
        ValidateTerms(request.CreditLimitMinor, request.PaymentTermsDays);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireFinanceAccessAsync(
            connection,
            transaction,
            user,
            context.ShopId,
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE finance_customers
        SET name = $name,
            phone = $phone,
            email = $email,
            address = $address,
            tax_number = $taxNumber,
            credit_limit_minor = $creditLimitMinor,
            payment_terms_days = $paymentTermsDays,
            is_active = $isActive,
            version = version + 1,
            updated_by_user_id = $userId,
            updated_at_utc = $now
        WHERE id = $id
          AND organization_id = $organizationId
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$phone", phone);
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$address", address);
        command.Parameters.AddWithValue("$taxNumber", taxNumber);
        command.Parameters.AddWithValue("$creditLimitMinor", request.CreditLimitMinor);
        command.Parameters.AddWithValue("$paymentTermsDays", request.PaymentTermsDays);
        command.Parameters.AddWithValue("$isActive", request.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "customer_changed",
                "The customer changed or is unavailable. Reload it and try again.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "finance.customer.updated",
            "customer",
            id,
            new
            {
                name,
                request.IsActive,
                request.CreditLimitMinor,
                request.PaymentTermsDays,
                previousVersion = request.ExpectedVersion
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetCustomerAsync(
            user,
            context,
            id,
            cancellationToken);
    }

    private async Task<CustomerRecord> GetCustomerAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string customerId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireFinanceAccessAsync(
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
            customer_number,
            name,
            phone,
            email,
            address,
            tax_number,
            credit_limit_minor,
            payment_terms_days,
            is_active,
            version,
            created_at_utc,
            updated_at_utc
        FROM finance_customers
        WHERE id = $id
          AND organization_id = $organizationId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", customerId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound("customer_not_found", "The customer could not be found.");
        }
        return ReadCustomer(reader);
    }

    private static CustomerRecord ReadCustomer(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8),
            reader.GetInt32(9),
            reader.GetInt32(10) == 1,
            reader.GetInt32(11),
            DateTimeOffset.Parse(reader.GetString(12)),
            DateTimeOffset.Parse(reader.GetString(13)));

    private static void ValidateTerms(long creditLimitMinor, int paymentTermsDays)
    {
        if (creditLimitMinor < 0)
        {
            throw Validation(
                "invalid_credit_limit",
                "The credit limit cannot be negative.");
        }
        if (paymentTermsDays is < 0 or > 3650)
        {
            throw Validation(
                "invalid_payment_terms",
                "Payment terms must be between 0 and 3650 days.");
        }
    }

    private static FinanceException Validation(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    private static FinanceException Forbidden(string code, string message) =>
        new(StatusCodes.Status403Forbidden, code, message);

    private static FinanceException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);

    private static FinanceException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private sealed record NormalizedAllocation(
        string ItemId,
        long AmountMinor);
}