using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Crm;

public sealed partial class CrmService
{
    private readonly DatabaseBootstrap _database;

    public CrmService(DatabaseBootstrap database)
    {
        _database = database;
    }

    private static bool IsAdministrator(AuthenticatedUser user) =>
        string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);

    private static async Task RequireReadAccessAsync(
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
        SELECT COUNT(1)
        FROM user_shop_access
        WHERE user_id = $userId
          AND shop_id = $shopId
          AND is_active = 1;
        """;
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$shopId", shopId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw Forbidden(
                "crm_access_required",
                "Active access to the selected branch is required for CRM records.");
        }
    }

    private static async Task RequireWriteAccessAsync(
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
        if (!string.Equals(accessLevel, "manager", StringComparison.OrdinalIgnoreCase))
        {
            throw Forbidden(
                "crm_write_permission_required",
                "A branch manager or administrator is required to change CRM records.");
        }
    }

    private static void RequireAdministrator(AuthenticatedUser user, string action)
    {
        if (!IsAdministrator(user))
        {
            throw Forbidden(
                "administrator_required",
                $"Only an administrator can {action}.");
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

    private static DateTimeOffset NormalizeUtcDateTime(
        string? value,
        string errorCode,
        bool defaultNow = false)
    {
        if (string.IsNullOrWhiteSpace(value) && defaultNow)
        {
            return DateTimeOffset.UtcNow;
        }
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            throw Validation(errorCode, "Use a valid ISO-8601 date and time.");
        }
        return parsed.ToUniversalTime();
    }

    private static DateTimeOffset? NormalizeOptionalUtcDateTime(
        string? value,
        string errorCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return NormalizeUtcDateTime(value, errorCode);
    }

    private static string NormalizeChoice(
        string? value,
        IReadOnlySet<string> allowed,
        string errorCode,
        string message)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!allowed.Contains(normalized))
        {
            throw Validation(errorCode, message);
        }
        return normalized;
    }

    private static readonly HashSet<string> CustomerTypes =
        new(StringComparer.Ordinal) { "individual", "business" };
    private static readonly HashSet<string> LifecycleStages =
        new(StringComparer.Ordinal) { "lead", "prospect", "customer", "vip", "dormant", "blocked" };
    private static readonly HashSet<string> PreferredChannels =
        new(StringComparer.Ordinal) { "phone", "email", "sms", "whatsapp", "in_person", "none" };

    private static void ValidateCreditTerms(long creditLimitMinor, int paymentTermsDays)
    {
        if (creditLimitMinor < 0)
        {
            throw Validation("invalid_credit_limit", "The credit limit cannot be negative.");
        }
        if (paymentTermsDays is < 0 or > 3650)
        {
            throw Validation(
                "invalid_payment_terms",
                "Payment terms must be between 0 and 3650 days.");
        }
    }

    private static IReadOnlyList<string> NormalizeTagIds(IReadOnlyList<string>? tagIds)
    {
        if (tagIds is null)
        {
            return Array.Empty<string>();
        }
        if (tagIds.Count > 50)
        {
            throw Validation("too_many_customer_tags", "A customer cannot have more than 50 tags.");
        }
        return tagIds
            .Select(NormalizeId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<CrmCustomerRecord>> ListCustomersAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? search,
        string? requestedSegment,
        bool includeInactive,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        string term = OptionalText(search, 150);
        string segment = requestedSegment?.Trim().ToLowerInvariant() ?? string.Empty;
        string[] segments = ["active", "new", "loyal", "dormant", "debtor", "prospect", "blocked"];
        if (segment.Length > 0 && !segments.Contains(segment, StringComparer.Ordinal))
        {
            throw Validation("invalid_customer_segment", "The customer segment filter is invalid.");
        }
        int limit = Math.Clamp(requestedLimit, 1, 2000);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);

        IReadOnlyList<CustomerSnapshot> snapshots = await ReadCustomerSnapshotsAsync(
            connection,
            null,
            context.OrganizationId,
            customerId: null,
            term,
            segment,
            includeInactive,
            limit,
            cancellationToken);

        var records = new List<CrmCustomerRecord>(snapshots.Count);
        foreach (CustomerSnapshot snapshot in snapshots)
        {
            IReadOnlyList<CrmTagRecord> tags = await ReadCustomerTagsAsync(
                connection,
                null,
                snapshot.Id,
                cancellationToken);
            records.Add(ToCustomerRecord(snapshot, tags));
        }
        return records;
    }

    public async Task<CrmCustomerRecord> GetCustomerAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string customerId,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(customerId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        return await ReadCustomerRecordAsync(
            connection,
            null,
            context.OrganizationId,
            id,
            cancellationToken);
    }

    public async Task<CrmCustomerRecord> CreateCustomerAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateCrmCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        string name = RequiredText(request.Name, 150, "customer_name_required", "Enter the customer name.");
        string phone = OptionalText(request.Phone, 50);
        string email = OptionalText(request.Email, 150);
        string address = OptionalText(request.Address, 250);
        string taxNumber = OptionalText(request.TaxNumber, 100);
        ValidateCreditTerms(request.CreditLimitMinor, request.PaymentTermsDays);
        string customerType = NormalizeChoice(
            request.CustomerType,
            CustomerTypes,
            "invalid_customer_type",
            "Customer type must be individual or business.");
        string companyName = OptionalText(request.CompanyName, 150);
        string contactPerson = OptionalText(request.ContactPerson, 150);
        if (customerType == "business" && companyName.Length == 0)
        {
            throw Validation("company_name_required", "Enter the company name for a business customer.");
        }
        string lifecycleStage = NormalizeChoice(
            request.LifecycleStage,
            LifecycleStages,
            "invalid_lifecycle_stage",
            "The customer lifecycle stage is invalid.");
        string source = OptionalText(request.Source, 100);
        if (source.Length == 0)
        {
            source = "manual";
        }
        string preferredChannel = NormalizeChoice(
            request.PreferredChannel,
            PreferredChannels,
            "invalid_preferred_channel",
            "The preferred communication channel is invalid.");
        string? assignedUserId = string.IsNullOrWhiteSpace(request.AssignedUserId)
            ? null
            : NormalizeId(request.AssignedUserId);
        string notes = OptionalText(request.Notes, 2000);
        IReadOnlyList<string> tagIds = NormalizeTagIds(request.TagIds);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        if (assignedUserId is not null)
        {
            await RequireAssignableUserAsync(
                connection,
                transaction,
                context.OrganizationId,
                assignedUserId,
                cancellationToken);
        }
        await RequireTagsAsync(
            connection,
            transaction,
            context.OrganizationId,
            tagIds,
            cancellationToken);

        string id = Guid.NewGuid().ToString("N");
        string customerNumber = $"CUS-{id[..8].ToUpperInvariant()}";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var customer = connection.CreateCommand())
        {
            customer.Transaction = transaction;
            customer.CommandText =
            """
            INSERT INTO finance_customers
            (
                id, organization_id, customer_number, name, phone, email,
                address, tax_number, credit_limit_minor, payment_terms_days,
                is_active, version, created_by_user_id, updated_by_user_id,
                created_at_utc, updated_at_utc
            )
            VALUES
            (
                $id, $organizationId, $customerNumber, $name, $phone, $email,
                $address, $taxNumber, $creditLimit, $paymentTerms,
                1, 1, $userId, $userId, $now, $now
            );
            """;
            customer.Parameters.AddWithValue("$id", id);
            customer.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            customer.Parameters.AddWithValue("$customerNumber", customerNumber);
            customer.Parameters.AddWithValue("$name", name);
            customer.Parameters.AddWithValue("$phone", phone);
            customer.Parameters.AddWithValue("$email", email);
            customer.Parameters.AddWithValue("$address", address);
            customer.Parameters.AddWithValue("$taxNumber", taxNumber);
            customer.Parameters.AddWithValue("$creditLimit", request.CreditLimitMinor);
            customer.Parameters.AddWithValue("$paymentTerms", request.PaymentTermsDays);
            customer.Parameters.AddWithValue("$userId", user.Id);
            customer.Parameters.AddWithValue("$now", now.ToString("O"));
            await customer.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var profile = connection.CreateCommand())
        {
            profile.Transaction = transaction;
            profile.CommandText =
            """
            UPDATE crm_customer_profiles
            SET customer_type = $customerType,
                company_name = $companyName,
                contact_person = $contactPerson,
                lifecycle_stage = $lifecycleStage,
                source = $source,
                preferred_channel = $preferredChannel,
                marketing_opt_in = $marketingOptIn,
                loyalty_enrolled = $loyaltyEnrolled,
                assigned_user_id = $assignedUserId,
                notes = $notes,
                updated_at_utc = $now,
                version = version + 1
            WHERE customer_id = $customerId
              AND organization_id = $organizationId;
            """;
            profile.Parameters.AddWithValue("$customerType", customerType);
            profile.Parameters.AddWithValue("$companyName", companyName);
            profile.Parameters.AddWithValue("$contactPerson", contactPerson);
            profile.Parameters.AddWithValue("$lifecycleStage", lifecycleStage);
            profile.Parameters.AddWithValue("$source", source);
            profile.Parameters.AddWithValue("$preferredChannel", preferredChannel);
            profile.Parameters.AddWithValue("$marketingOptIn", request.MarketingOptIn ? 1 : 0);
            profile.Parameters.AddWithValue("$loyaltyEnrolled", request.LoyaltyEnrolled ? 1 : 0);
            profile.Parameters.AddWithValue("$assignedUserId", assignedUserId ?? (object)DBNull.Value);
            profile.Parameters.AddWithValue("$notes", notes);
            profile.Parameters.AddWithValue("$now", now.ToString("O"));
            profile.Parameters.AddWithValue("$customerId", id);
            profile.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            if (await profile.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("crm_profile_missing", "The customer CRM profile could not be initialized.");
            }
        }

        await ReplaceCustomerTagsAsync(
            connection,
            transaction,
            id,
            tagIds,
            user.Id,
            now,
            cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "crm.customer.created",
            "customer",
            id,
            new
            {
                customerNumber,
                context.OrganizationId,
                context.ShopId,
                name,
                customerType,
                lifecycleStage,
                assignedUserId,
                tagCount = tagIds.Count
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetCustomerAsync(user, context, id, cancellationToken);
    }

    public async Task<CrmCustomerRecord> UpdateCustomerAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string customerId,
        UpdateCrmCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(customerId);
        if (request.ExpectedCustomerVersion < 1 || request.ExpectedProfileVersion < 1)
        {
            throw Validation("invalid_customer_version", "The expected customer versions are invalid.");
        }
        string name = RequiredText(request.Name, 150, "customer_name_required", "Enter the customer name.");
        string phone = OptionalText(request.Phone, 50);
        string email = OptionalText(request.Email, 150);
        string address = OptionalText(request.Address, 250);
        string taxNumber = OptionalText(request.TaxNumber, 100);
        ValidateCreditTerms(request.CreditLimitMinor, request.PaymentTermsDays);
        string customerType = NormalizeChoice(
            request.CustomerType,
            CustomerTypes,
            "invalid_customer_type",
            "Customer type must be individual or business.");
        string companyName = OptionalText(request.CompanyName, 150);
        string contactPerson = OptionalText(request.ContactPerson, 150);
        if (customerType == "business" && companyName.Length == 0)
        {
            throw Validation("company_name_required", "Enter the company name for a business customer.");
        }
        string lifecycleStage = request.IsActive
            ? NormalizeChoice(
                request.LifecycleStage,
                LifecycleStages,
                "invalid_lifecycle_stage",
                "The customer lifecycle stage is invalid.")
            : "blocked";
        string source = OptionalText(request.Source, 100);
        if (source.Length == 0)
        {
            source = "manual";
        }
        string preferredChannel = NormalizeChoice(
            request.PreferredChannel,
            PreferredChannels,
            "invalid_preferred_channel",
            "The preferred communication channel is invalid.");
        string? assignedUserId = string.IsNullOrWhiteSpace(request.AssignedUserId)
            ? null
            : NormalizeId(request.AssignedUserId);
        string notes = OptionalText(request.Notes, 2000);
        IReadOnlyList<string> tagIds = NormalizeTagIds(request.TagIds);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        await RequireCustomerAsync(
            connection,
            transaction,
            context.OrganizationId,
            id,
            includeInactive: true,
            cancellationToken);
        if (assignedUserId is not null)
        {
            await RequireAssignableUserAsync(
                connection,
                transaction,
                context.OrganizationId,
                assignedUserId,
                cancellationToken);
        }
        await RequireTagsAsync(
            connection,
            transaction,
            context.OrganizationId,
            tagIds,
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var customer = connection.CreateCommand())
        {
            customer.Transaction = transaction;
            customer.CommandText =
            """
            UPDATE finance_customers
            SET name = $name,
                phone = $phone,
                email = $email,
                address = $address,
                tax_number = $taxNumber,
                credit_limit_minor = $creditLimit,
                payment_terms_days = $paymentTerms,
                is_active = $isActive,
                version = version + 1,
                updated_by_user_id = $userId,
                updated_at_utc = $now
            WHERE id = $id
              AND organization_id = $organizationId
              AND version = $expectedVersion;
            """;
            customer.Parameters.AddWithValue("$name", name);
            customer.Parameters.AddWithValue("$phone", phone);
            customer.Parameters.AddWithValue("$email", email);
            customer.Parameters.AddWithValue("$address", address);
            customer.Parameters.AddWithValue("$taxNumber", taxNumber);
            customer.Parameters.AddWithValue("$creditLimit", request.CreditLimitMinor);
            customer.Parameters.AddWithValue("$paymentTerms", request.PaymentTermsDays);
            customer.Parameters.AddWithValue("$isActive", request.IsActive ? 1 : 0);
            customer.Parameters.AddWithValue("$userId", user.Id);
            customer.Parameters.AddWithValue("$now", now.ToString("O"));
            customer.Parameters.AddWithValue("$id", id);
            customer.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            customer.Parameters.AddWithValue("$expectedVersion", request.ExpectedCustomerVersion);
            if (await customer.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("customer_changed", "The customer changed. Reload and try again.");
            }
        }

        await using (var profile = connection.CreateCommand())
        {
            profile.Transaction = transaction;
            profile.CommandText =
            """
            UPDATE crm_customer_profiles
            SET customer_type = $customerType,
                company_name = $companyName,
                contact_person = $contactPerson,
                lifecycle_stage = $lifecycleStage,
                source = $source,
                preferred_channel = $preferredChannel,
                marketing_opt_in = $marketingOptIn,
                loyalty_enrolled = $loyaltyEnrolled,
                assigned_user_id = $assignedUserId,
                notes = $notes,
                version = version + 1,
                updated_at_utc = $now
            WHERE customer_id = $id
              AND organization_id = $organizationId
              AND version = $expectedVersion;
            """;
            profile.Parameters.AddWithValue("$customerType", customerType);
            profile.Parameters.AddWithValue("$companyName", companyName);
            profile.Parameters.AddWithValue("$contactPerson", contactPerson);
            profile.Parameters.AddWithValue("$lifecycleStage", lifecycleStage);
            profile.Parameters.AddWithValue("$source", source);
            profile.Parameters.AddWithValue("$preferredChannel", preferredChannel);
            profile.Parameters.AddWithValue("$marketingOptIn", request.MarketingOptIn ? 1 : 0);
            profile.Parameters.AddWithValue("$loyaltyEnrolled", request.LoyaltyEnrolled ? 1 : 0);
            profile.Parameters.AddWithValue("$assignedUserId", assignedUserId ?? (object)DBNull.Value);
            profile.Parameters.AddWithValue("$notes", notes);
            profile.Parameters.AddWithValue("$now", now.ToString("O"));
            profile.Parameters.AddWithValue("$id", id);
            profile.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            profile.Parameters.AddWithValue("$expectedVersion", request.ExpectedProfileVersion);
            if (await profile.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("crm_profile_changed", "The CRM profile changed. Reload and try again.");
            }
        }

        await ReplaceCustomerTagsAsync(
            connection,
            transaction,
            id,
            tagIds,
            user.Id,
            now,
            cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "crm.customer.updated",
            "customer",
            id,
            new
            {
                name,
                request.IsActive,
                lifecycleStage,
                assignedUserId,
                tagCount = tagIds.Count,
                previousCustomerVersion = request.ExpectedCustomerVersion,
                previousProfileVersion = request.ExpectedProfileVersion
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetCustomerAsync(user, context, id, cancellationToken);
    }

    public async Task<IReadOnlyList<DuplicateCustomerCandidate>> FindDuplicatesAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? phone,
        string? email,
        CancellationToken cancellationToken = default)
    {
        string normalizedPhone = OptionalText(phone, 50);
        string normalizedEmail = OptionalText(email, 150);
        if (normalizedPhone.Length == 0 && normalizedEmail.Length == 0)
        {
            throw Validation("duplicate_lookup_required", "Enter a phone number or email address.");
        }

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            id, customer_number, name, phone, email,
            CASE
                WHEN $phone <> '' AND lower(trim(phone)) = lower(trim($phone))
                 AND $email <> '' AND lower(trim(email)) = lower(trim($email))
                THEN 'phone_and_email'
                WHEN $phone <> '' AND lower(trim(phone)) = lower(trim($phone))
                THEN 'phone'
                ELSE 'email'
            END,
            is_active
        FROM finance_customers
        WHERE organization_id = $organizationId
          AND
          (
              ($phone <> '' AND lower(trim(phone)) = lower(trim($phone)))
              OR
              ($email <> '' AND lower(trim(email)) = lower(trim($email)))
          )
        ORDER BY is_active DESC, name COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$phone", normalizedPhone);
        command.Parameters.AddWithValue("$email", normalizedEmail);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);

        var candidates = new List<DuplicateCustomerCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new DuplicateCustomerCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6) == 1));
        }
        return candidates;
    }

    public async Task<IReadOnlyList<CrmTagRecord>> ListTagsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id, name, description, is_active, version, created_at_utc, updated_at_utc
        FROM crm_tags
        WHERE organization_id = $organizationId
          AND ($includeInactive = 1 OR is_active = 1)
        ORDER BY name COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$includeInactive", includeInactive ? 1 : 0);
        return await ReadTagsAsync(command, cancellationToken);
    }

    public async Task<CrmTagRecord> CreateTagAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateCrmTagRequest request,
        CancellationToken cancellationToken = default)
    {
        string name = RequiredText(request.Name, 80, "tag_name_required", "Enter the tag name.");
        string description = OptionalText(request.Description, 500);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO crm_tags
        (id, organization_id, name, description, is_active, version, created_by_user_id, created_at_utc, updated_at_utc)
        VALUES
        ($id, $organizationId, $name, $description, 1, 1, $userId, $now, $now);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("tag_name_exists", "A CRM tag with this name already exists.");
        }
        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "crm.tag.created",
            "crm_tag",
            id,
            new { name },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CrmTagRecord(id, name, description, true, 1, now, now);
    }

    public async Task<CrmTagRecord> UpdateTagAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string tagId,
        UpdateCrmTagRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(tagId);
        string name = RequiredText(request.Name, 80, "tag_name_required", "Enter the tag name.");
        string description = OptionalText(request.Description, 500);
        if (request.ExpectedVersion < 1)
        {
            throw Validation("invalid_tag_version", "The expected tag version is invalid.");
        }
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE crm_tags
        SET name = $name,
            description = $description,
            is_active = $isActive,
            version = version + 1,
            updated_at_utc = $now
        WHERE id = $id
          AND organization_id = $organizationId
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$isActive", request.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("tag_changed", "The CRM tag changed. Reload and try again.");
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("tag_name_exists", "A CRM tag with this name already exists.");
        }
        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "crm.tag.updated",
            "crm_tag",
            id,
            new { name, request.IsActive, previousVersion = request.ExpectedVersion },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CrmTagRecord(
            id,
            name,
            description,
            request.IsActive,
            request.ExpectedVersion + 1,
            now,
            now);
    }

    private static async Task<IReadOnlyList<CustomerSnapshot>> ReadCustomerSnapshotsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string organizationId,
        string? customerId,
        string search,
        string segment,
        bool includeInactive,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            customer.id,
            customer.organization_id,
            customer.customer_number,
            customer.name,
            customer.phone,
            customer.email,
            customer.address,
            customer.tax_number,
            customer.credit_limit_minor,
            customer.payment_terms_days,
            customer.is_active,
            customer.version,
            profile.customer_type,
            profile.company_name,
            profile.contact_person,
            profile.lifecycle_stage,
            profile.source,
            profile.preferred_channel,
            profile.marketing_opt_in,
            profile.loyalty_enrolled,
            profile.loyalty_tier,
            profile.current_points,
            profile.lifetime_points,
            profile.assigned_user_id,
            COALESCE(assigned.display_name, ''),
            profile.notes,
            COALESCE(profile.first_sale_at_utc, metrics.first_sale_at_utc),
            COALESCE(profile.last_sale_at_utc, metrics.last_sale_at_utc),
            profile.last_contact_at_utc,
            profile.next_follow_up_at_utc,
            profile.version,
            customer.created_at_utc,
            CASE WHEN profile.updated_at_utc > customer.updated_at_utc
                 THEN profile.updated_at_utc ELSE customer.updated_at_utc END,
            metrics.completed_sale_count,
            metrics.lifetime_spend_minor,
            CAST(metrics.average_sale_minor AS INTEGER),
            outstanding.outstanding_minor,
            (SELECT COUNT(1) FROM crm_tasks AS task
             WHERE task.customer_id = customer.id AND task.status = 'open'),
            (SELECT COUNT(1) FROM crm_communications AS communication
             WHERE communication.customer_id = customer.id),
            (SELECT COUNT(1) FROM crm_quotations AS quotation
             WHERE quotation.customer_id = customer.id),
            (SELECT COUNT(1) FROM crm_quotations AS quotation
             WHERE quotation.customer_id = customer.id AND quotation.status = 'accepted'),
            (SELECT COUNT(1) FROM crm_quotations AS quotation
             WHERE quotation.customer_id = customer.id AND quotation.status = 'converted'),
            segment_view.segment
        FROM finance_customers AS customer
        INNER JOIN crm_customer_profiles AS profile ON profile.customer_id = customer.id
        LEFT JOIN users AS assigned ON assigned.id = profile.assigned_user_id
        INNER JOIN crm_customer_sales_metrics AS metrics ON metrics.customer_id = customer.id
        INNER JOIN crm_customer_outstanding_balances AS outstanding ON outstanding.customer_id = customer.id
        INNER JOIN crm_customer_segments AS segment_view ON segment_view.customer_id = customer.id
        WHERE customer.organization_id = $organizationId
          AND ($customerId IS NULL OR customer.id = $customerId)
          AND ($includeInactive = 1 OR customer.is_active = 1)
          AND ($segment = '' OR segment_view.segment = $segment)
          AND
          (
              $search = ''
              OR customer.customer_number LIKE '%' || $search || '%' COLLATE NOCASE
              OR customer.name LIKE '%' || $search || '%' COLLATE NOCASE
              OR customer.phone LIKE '%' || $search || '%' COLLATE NOCASE
              OR customer.email LIKE '%' || $search || '%' COLLATE NOCASE
              OR profile.company_name LIKE '%' || $search || '%' COLLATE NOCASE
              OR profile.contact_person LIKE '%' || $search || '%' COLLATE NOCASE
          )
        ORDER BY customer.name COLLATE NOCASE, customer.customer_number
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$customerId", customerId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$includeInactive", includeInactive ? 1 : 0);
        command.Parameters.AddWithValue("$segment", segment);
        command.Parameters.AddWithValue("$search", search);
        command.Parameters.AddWithValue("$limit", limit);

        var snapshots = new List<CustomerSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(new CustomerSnapshot(
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
                reader.GetString(12),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetString(15),
                reader.GetString(16),
                reader.GetString(17),
                reader.GetInt32(18) == 1,
                reader.GetInt32(19) == 1,
                reader.GetString(20),
                reader.GetInt64(21),
                reader.GetInt64(22),
                reader.IsDBNull(23) ? null : reader.GetString(23),
                reader.GetString(24),
                reader.GetString(25),
                reader.IsDBNull(26) ? null : DateTimeOffset.Parse(reader.GetString(26)),
                reader.IsDBNull(27) ? null : DateTimeOffset.Parse(reader.GetString(27)),
                reader.IsDBNull(28) ? null : DateTimeOffset.Parse(reader.GetString(28)),
                reader.IsDBNull(29) ? null : DateTimeOffset.Parse(reader.GetString(29)),
                reader.GetInt32(30),
                DateTimeOffset.Parse(reader.GetString(31)),
                DateTimeOffset.Parse(reader.GetString(32)),
                reader.GetInt64(33),
                reader.GetInt64(34),
                reader.GetInt64(35),
                reader.GetInt64(36),
                reader.GetInt64(37),
                reader.GetInt64(38),
                reader.GetInt64(39),
                reader.GetInt64(40),
                reader.GetInt64(41),
                reader.GetString(42)));
        }
        return snapshots;
    }

    private static async Task<CrmCustomerRecord> ReadCustomerRecordAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string organizationId,
        string customerId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CustomerSnapshot> snapshots = await ReadCustomerSnapshotsAsync(
            connection,
            transaction,
            organizationId,
            customerId,
            string.Empty,
            string.Empty,
            includeInactive: true,
            1,
            cancellationToken);
        if (snapshots.Count != 1)
        {
            throw NotFound("customer_not_found", "The customer could not be found.");
        }
        IReadOnlyList<CrmTagRecord> tags = await ReadCustomerTagsAsync(
            connection,
            transaction,
            customerId,
            cancellationToken);
        return ToCustomerRecord(snapshots[0], tags);
    }

    private static CrmCustomerRecord ToCustomerRecord(
        CustomerSnapshot snapshot,
        IReadOnlyList<CrmTagRecord> tags) =>
        new(
            snapshot.Id,
            snapshot.OrganizationId,
            snapshot.CustomerNumber,
            snapshot.Name,
            snapshot.Phone,
            snapshot.Email,
            snapshot.Address,
            snapshot.TaxNumber,
            snapshot.CreditLimitMinor,
            snapshot.PaymentTermsDays,
            snapshot.IsActive,
            snapshot.CustomerVersion,
            snapshot.CustomerType,
            snapshot.CompanyName,
            snapshot.ContactPerson,
            snapshot.LifecycleStage,
            snapshot.Source,
            snapshot.PreferredChannel,
            snapshot.MarketingOptIn,
            snapshot.LoyaltyEnrolled,
            snapshot.LoyaltyTier,
            snapshot.CurrentPoints,
            snapshot.LifetimePoints,
            snapshot.AssignedUserId,
            snapshot.AssignedUserName,
            snapshot.Notes,
            snapshot.FirstSaleAtUtc,
            snapshot.LastSaleAtUtc,
            snapshot.LastContactAtUtc,
            snapshot.NextFollowUpAtUtc,
            snapshot.ProfileVersion,
            snapshot.CreatedAtUtc,
            snapshot.UpdatedAtUtc,
            tags,
            new CrmCustomerMetrics(
                snapshot.CompletedSaleCount,
                snapshot.LifetimeSpendMinor,
                snapshot.AverageSaleMinor,
                snapshot.FirstSaleAtUtc,
                snapshot.LastSaleAtUtc,
                snapshot.OutstandingMinor,
                snapshot.OpenTaskCount,
                snapshot.CommunicationCount,
                snapshot.QuotationCount,
                snapshot.AcceptedQuotationCount,
                snapshot.ConvertedQuotationCount,
                snapshot.Segment));

    private static async Task<IReadOnlyList<CrmTagRecord>> ReadCustomerTagsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string customerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT tag.id, tag.name, tag.description, tag.is_active,
               tag.version, tag.created_at_utc, tag.updated_at_utc
        FROM crm_customer_tags AS assignment
        INNER JOIN crm_tags AS tag ON tag.id = assignment.tag_id
        WHERE assignment.customer_id = $customerId
        ORDER BY tag.name COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$customerId", customerId);
        return await ReadTagsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<CrmTagRecord>> ReadTagsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var tags = new List<CrmTagRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tags.Add(new CrmTagRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) == 1,
                reader.GetInt32(4),
                DateTimeOffset.Parse(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6))));
        }
        return tags;
    }

    private static async Task ReplaceCustomerTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string customerId,
        IReadOnlyList<string> tagIds,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM crm_customer_tags WHERE customer_id = $customerId;";
            delete.Parameters.AddWithValue("$customerId", customerId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (string tagId in tagIds)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
            """
            INSERT INTO crm_customer_tags
            (customer_id, tag_id, assigned_by_user_id, assigned_at_utc)
            VALUES ($customerId, $tagId, $userId, $now);
            """;
            insert.Parameters.AddWithValue("$customerId", customerId);
            insert.Parameters.AddWithValue("$tagId", tagId);
            insert.Parameters.AddWithValue("$userId", userId);
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task RequireTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        IReadOnlyList<string> tagIds,
        CancellationToken cancellationToken)
    {
        foreach (string tagId in tagIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            SELECT COUNT(1)
            FROM crm_tags
            WHERE id = $id
              AND organization_id = $organizationId
              AND is_active = 1;
            """;
            command.Parameters.AddWithValue("$id", tagId);
            command.Parameters.AddWithValue("$organizationId", organizationId);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                throw NotFound("crm_tag_not_found", "A selected active CRM tag could not be found.");
            }
        }
    }

    private static async Task RequireAssignableUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COUNT(1)
        FROM users AS user
        WHERE user.id = $userId
          AND user.is_active = 1
          AND
          (
              user.role = 'admin'
              OR EXISTS
              (
                  SELECT 1
                  FROM user_shop_access AS access
                  INNER JOIN shops AS shop ON shop.id = access.shop_id
                  WHERE access.user_id = user.id
                    AND access.is_active = 1
                    AND shop.organization_id = $organizationId
              )
          );
        """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$organizationId", organizationId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw NotFound("assignable_user_not_found", "The assigned active user could not be found.");
        }
    }

    private static async Task RequireCustomerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string customerId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COUNT(1)
        FROM finance_customers
        WHERE id = $customerId
          AND organization_id = $organizationId
          AND ($includeInactive = 1 OR is_active = 1);
        """;
        command.Parameters.AddWithValue("$customerId", customerId);
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$includeInactive", includeInactive ? 1 : 0);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw NotFound("customer_not_found", "The active customer could not be found.");
        }
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
            occurred_at_utc, user_id, username, event_type,
            entity_type, entity_id, success, details_json, client_ip_hash
        )
        VALUES
        ($now, $userId, $username, $eventType,
         $entityType, $entityId, 1, $details, NULL);
        """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$details", JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CrmException Validation(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);
    private static CrmException Forbidden(string code, string message) =>
        new(StatusCodes.Status403Forbidden, code, message);
    private static CrmException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);
    private static CrmException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private sealed record CustomerSnapshot(
        string Id,
        string OrganizationId,
        string CustomerNumber,
        string Name,
        string Phone,
        string Email,
        string Address,
        string TaxNumber,
        long CreditLimitMinor,
        int PaymentTermsDays,
        bool IsActive,
        int CustomerVersion,
        string CustomerType,
        string CompanyName,
        string ContactPerson,
        string LifecycleStage,
        string Source,
        string PreferredChannel,
        bool MarketingOptIn,
        bool LoyaltyEnrolled,
        string LoyaltyTier,
        long CurrentPoints,
        long LifetimePoints,
        string? AssignedUserId,
        string AssignedUserName,
        string Notes,
        DateTimeOffset? FirstSaleAtUtc,
        DateTimeOffset? LastSaleAtUtc,
        DateTimeOffset? LastContactAtUtc,
        DateTimeOffset? NextFollowUpAtUtc,
        int ProfileVersion,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        long CompletedSaleCount,
        long LifetimeSpendMinor,
        long AverageSaleMinor,
        long OutstandingMinor,
        long OpenTaskCount,
        long CommunicationCount,
        long QuotationCount,
        long AcceptedQuotationCount,
        long ConvertedQuotationCount,
        string Segment);
}
