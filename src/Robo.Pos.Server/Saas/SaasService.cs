using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Saas;

public sealed partial class SaasService
{
    private readonly DatabaseBootstrap _database;

    public SaasService(DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task EnsureBootstrapAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT OR IGNORE INTO saas_plans
        (
            id, code, name, description, status, billing_interval,
            price_minor, currency_code, trial_days, enforcement_mode,
            sort_order, version, created_at_utc, updated_at_utc
        )
        VALUES
        (
            'enterprise-unlimited', 'ENTERPRISE', 'Enterprise Unlimited',
            'Default compatibility plan for existing Nexus POS organisations.',
            'active', 'custom', 0, 'UGX', 0, 'report_only', 100, 1,
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        );

        INSERT OR IGNORE INTO saas_plan_entitlements
        (plan_id, entitlement_key, is_enabled, limit_value, configuration_json, updated_at_utc)
        VALUES
        ('enterprise-unlimited', 'accounting', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
        ('enterprise-unlimited', 'procurement', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
        ('enterprise-unlimited', 'crm', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
        ('enterprise-unlimited', 'hrm', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
        ('enterprise-unlimited', 'multi_shop', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
        ('enterprise-unlimited', 'max_active_shops', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
        ('enterprise-unlimited', 'max_active_users', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

        INSERT OR IGNORE INTO saas_subscriptions
        (
            id, organization_id, plan_id, status, started_at_utc,
            current_period_starts_utc, version, created_at_utc, updated_at_utc
        )
        SELECT
            'subscription-' || organization.id,
            organization.id,
            'enterprise-unlimited',
            'active',
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
            1,
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        FROM organizations organization;

        INSERT OR IGNORE INTO saas_platform_operators
        (
            user_id, operator_role, is_active, version,
            assigned_by_user_id, assigned_at_utc, updated_at_utc
        )
        SELECT
            user.id,
            'owner',
            1,
            1,
            NULL,
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        FROM users user
        WHERE user.role = 'admin'
          AND user.is_active = 1
          AND NOT EXISTS
          (
              SELECT 1
              FROM saas_platform_operators operator
              WHERE operator.operator_role = 'owner'
                AND operator.is_active = 1
          )
        ORDER BY user.created_at_utc
        LIMIT 1;
        """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SaasSubscriptionRecord> GetCurrentSubscriptionAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken = default)
    {
        _ = user;
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadSubscriptionAsync(
            connection,
            null,
            context.OrganizationId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<SaasEntitlementRecord>>
        GetCurrentEntitlementsAsync(
            AuthenticatedUser user,
            ActiveShopContextRecord context,
            CancellationToken cancellationToken = default)
    {
        _ = user;
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadEffectiveEntitlementsAsync(
            connection,
            null,
            context.OrganizationId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<SaasPlanRecord>> ListPlansAsync(
        AuthenticatedUser user,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequirePlatformOperatorAsync(connection, null, user, writeRequired: false, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            id, code, name, description, status, billing_interval,
            price_minor, currency_code, trial_days, enforcement_mode,
            sort_order, version, created_at_utc, updated_at_utc
        FROM saas_plans
        ORDER BY sort_order, name COLLATE NOCASE;
        """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<SaasPlanRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(MapPlan(reader));
        }
        return records;
    }

    public async Task<SaasPlanRecord> CreatePlanAsync(
        AuthenticatedUser user,
        CreateSaasPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        string code = Required(request.Code, 50, "plan_code_required", "Enter a plan code.").ToUpperInvariant();
        string name = Required(request.Name, 150, "plan_name_required", "Enter a plan name.");
        string description = Optional(request.Description, 1000, "Plan description");
        string interval = Choice(request.BillingInterval, "billing_interval_invalid", "monthly", "annual", "custom");
        string currency = Required(request.CurrencyCode, 10, "currency_required", "Enter a currency code.").ToUpperInvariant();
        string enforcement = Choice(request.EnforcementMode, "enforcement_mode_invalid", "report_only", "hard");
        if (request.PriceMinor < 0 || request.TrialDays < 0)
        {
            throw Error(400, "invalid_plan_amount", "Plan price and trial days cannot be negative.");
        }

        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequirePlatformOperatorAsync(connection, transaction, user, writeRequired: true, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            INSERT INTO saas_plans
            (
                id, code, name, description, status, billing_interval,
                price_minor, currency_code, trial_days, enforcement_mode,
                sort_order, version, created_by_user_id, updated_by_user_id,
                created_at_utc, updated_at_utc
            )
            VALUES
            (
                $id, $code, $name, $description, 'active', $interval,
                $price, $currency, $trialDays, $enforcement,
                $sortOrder, 1, $userId, $userId, $now, $now
            );
            """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$description", description);
            command.Parameters.AddWithValue("$interval", interval);
            command.Parameters.AddWithValue("$price", request.PriceMinor);
            command.Parameters.AddWithValue("$currency", currency);
            command.Parameters.AddWithValue("$trialDays", request.TrialDays);
            command.Parameters.AddWithValue("$enforcement", enforcement);
            command.Parameters.AddWithValue("$sortOrder", request.SortOrder);
            command.Parameters.AddWithValue("$userId", user.Id);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await WriteAuditAsync(connection, transaction, user, "saas.plan.created", "saas_plan", id,
                new { code, name, interval, request.PriceMinor, currency, enforcement }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Error(409, "plan_code_exists", "A SaaS plan with that code already exists.");
        }
        return await GetPlanAsync(id, cancellationToken);
    }

    public async Task<SaasPlanRecord> UpdatePlanAsync(
        AuthenticatedUser user,
        string planId,
        UpdateSaasPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = Required(planId, 100, "plan_id_required", "Plan ID is required.");
        string name = Required(request.Name, 150, "plan_name_required", "Enter a plan name.");
        string description = Optional(request.Description, 1000, "Plan description");
        string status = Choice(request.Status, "plan_status_invalid", "active", "retired");
        string interval = Choice(request.BillingInterval, "billing_interval_invalid", "monthly", "annual", "custom");
        string currency = Required(request.CurrencyCode, 10, "currency_required", "Enter a currency code.").ToUpperInvariant();
        string enforcement = Choice(request.EnforcementMode, "enforcement_mode_invalid", "report_only", "hard");
        if (request.ExpectedVersion < 1 || request.PriceMinor < 0 || request.TrialDays < 0)
        {
            throw Error(400, "invalid_plan_update", "Plan version, price and trial days are invalid.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequirePlatformOperatorAsync(connection, transaction, user, writeRequired: true, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE saas_plans
        SET name = $name,
            description = $description,
            status = $status,
            billing_interval = $interval,
            price_minor = $price,
            currency_code = $currency,
            trial_days = $trialDays,
            enforcement_mode = $enforcement,
            sort_order = $sortOrder,
            version = version + 1,
            updated_by_user_id = $userId,
            updated_at_utc = $now
        WHERE id = $id
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$interval", interval);
        command.Parameters.AddWithValue("$price", request.PriceMinor);
        command.Parameters.AddWithValue("$currency", currency);
        command.Parameters.AddWithValue("$trialDays", request.TrialDays);
        command.Parameters.AddWithValue("$enforcement", enforcement);
        command.Parameters.AddWithValue("$sortOrder", request.SortOrder);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Error(409, "plan_version_conflict", "The plan changed before this update was saved.");
        }
        await WriteAuditAsync(connection, transaction, user, "saas.plan.updated", "saas_plan", id,
            new { name, status, interval, request.PriceMinor, currency, enforcement }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetPlanAsync(id, cancellationToken);
    }

    public async Task<SaasEntitlementRecord> SetPlanEntitlementAsync(
        AuthenticatedUser user,
        string planId,
        string entitlementKey,
        SetSaasEntitlementRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = Required(planId, 100, "plan_id_required", "Plan ID is required.");
        string key = NormalizedKey(entitlementKey);
        string configuration = JsonObjectOrDefault(request.ConfigurationJson);
        if (request.LimitValue < 0)
        {
            throw Error(400, "entitlement_limit_invalid", "Entitlement limits cannot be negative.");
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequirePlatformOperatorAsync(connection, transaction, user, writeRequired: true, cancellationToken);
        await EnsurePlanExistsAsync(connection, transaction, id, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO saas_plan_entitlements
        (
            plan_id, entitlement_key, is_enabled, limit_value,
            configuration_json, updated_by_user_id, updated_at_utc
        )
        VALUES
        ($planId, $key, $enabled, $limit, $configuration, $userId, $now)
        ON CONFLICT(plan_id, entitlement_key) DO UPDATE SET
            is_enabled = excluded.is_enabled,
            limit_value = excluded.limit_value,
            configuration_json = excluded.configuration_json,
            updated_by_user_id = excluded.updated_by_user_id,
            updated_at_utc = excluded.updated_at_utc;
        """;
        command.Parameters.AddWithValue("$planId", id);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$enabled", request.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$limit", (object?)request.LimitValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$configuration", configuration);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await WriteAuditAsync(connection, transaction, user, "saas.entitlement.updated", "saas_plan", id,
            new { key, request.IsEnabled, request.LimitValue }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SaasEntitlementRecord(key, request.IsEnabled, request.LimitValue, configuration, "plan", null);
    }

    public async Task<IReadOnlyList<SaasTenantSummaryRecord>> ListTenantsAsync(
        AuthenticatedUser user,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequirePlatformOperatorAsync(connection, null, user, writeRequired: false, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            organization.id,
            organization.name,
            organization.legal_name,
            organization.default_currency_code,
            organization.timezone_id,
            subscription.status,
            plan.code,
            plan.name,
            (SELECT COUNT(1) FROM shops shop
             WHERE shop.organization_id = organization.id AND shop.is_active = 1),
            (SELECT COUNT(DISTINCT access.user_id)
             FROM user_shop_access access
             JOIN shops shop ON shop.id = access.shop_id
             JOIN users user ON user.id = access.user_id
             WHERE shop.organization_id = organization.id
               AND access.is_active = 1 AND user.is_active = 1),
            (SELECT COUNT(1) FROM saas_support_cases support_case
             WHERE support_case.organization_id = organization.id
               AND support_case.status IN ('open','in_progress','waiting')),
            subscription.updated_at_utc
        FROM organizations organization
        JOIN saas_subscriptions subscription
          ON subscription.organization_id = organization.id
        JOIN saas_plans plan ON plan.id = subscription.plan_id
        ORDER BY organization.name COLLATE NOCASE;
        """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<SaasTenantSummaryRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new SaasTenantSummaryRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt32(8),
                reader.GetInt32(9), reader.GetInt32(10),
                DateTimeOffset.Parse(reader.GetString(11))));
        }
        return records;
    }

    public async Task<SaasTenantSummaryRecord> OnboardTenantAsync(
        AuthenticatedUser user,
        OnboardSaasTenantRequest request,
        CancellationToken cancellationToken = default)
    {
        string organizationName = Required(request.OrganizationName, 150, "organization_name_required", "Enter the organisation name.");
        string legalName = Optional(request.LegalName, 200, "Legal name");
        if (string.IsNullOrWhiteSpace(legalName)) legalName = organizationName;
        string currency = Required(request.CurrencyCode, 10, "currency_required", "Enter a currency code.").ToUpperInvariant();
        string timezone = Required(request.TimezoneId, 100, "timezone_required", "Enter a timezone.");
        string shopCode = Required(request.ShopCode, 30, "shop_code_required", "Enter a head-office shop code.").ToUpperInvariant();
        string shopName = Required(string.IsNullOrWhiteSpace(request.ShopName) ? organizationName : request.ShopName, 150, "shop_name_required", "Enter a head-office shop name.");
        string ownerUserId = Required(request.OwnerUserId, 100, "owner_user_required", "Select an existing owner user.");
        string planId = Required(request.PlanId, 100, "plan_required", "Select a SaaS plan.");
        string organizationId = Guid.NewGuid().ToString("N");
        string shopId = Guid.NewGuid().ToString("N");
        string subscriptionId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequirePlatformOperatorAsync(connection, transaction, user, writeRequired: true, cancellationToken);
        await EnsurePlanExistsAsync(connection, transaction, planId, cancellationToken);
        await EnsureActiveUserAsync(connection, transaction, ownerUserId, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
            """
            INSERT INTO organizations
            (id, name, legal_name, default_currency_code, timezone_id, created_at_utc, updated_at_utc)
            VALUES
            ($id, $name, $legalName, $currency, $timezone, $now, $now);

            INSERT INTO saas_subscriptions
            (
                id, organization_id, plan_id, status, started_at_utc,
                current_period_starts_utc, version, updated_by_user_id,
                created_at_utc, updated_at_utc
            )
            VALUES
            ($subscriptionId, $id, $planId, 'active', $now, $now, 1, $userId, $now, $now);

            INSERT INTO shops
            (
                id, organization_id, code, name, address, phone, email,
                tax_number, currency_code, timezone_id, is_head_office,
                is_active, version, created_by_user_id, updated_by_user_id,
                created_at_utc, updated_at_utc
            )
            VALUES
            (
                $shopId, $id, $shopCode, $shopName, $address, $phone, $email,
                '', $currency, $timezone, 1, 1, 1, $userId, $userId, $now, $now
            );

            INSERT INTO user_shop_access
            (
                user_id, shop_id, access_level, is_primary, is_active,
                assigned_by_user_id, assigned_at_utc, updated_at_utc
            )
            VALUES
            ($ownerUserId, $shopId, 'manager', 1, 1, $userId, $now, $now);
            """;
            command.Parameters.AddWithValue("$id", organizationId);
            command.Parameters.AddWithValue("$name", organizationName);
            command.Parameters.AddWithValue("$legalName", legalName);
            command.Parameters.AddWithValue("$currency", currency);
            command.Parameters.AddWithValue("$timezone", timezone);
            command.Parameters.AddWithValue("$subscriptionId", subscriptionId);
            command.Parameters.AddWithValue("$planId", planId);
            command.Parameters.AddWithValue("$shopId", shopId);
            command.Parameters.AddWithValue("$shopCode", shopCode);
            command.Parameters.AddWithValue("$shopName", shopName);
            command.Parameters.AddWithValue("$address", Optional(request.ShopAddress, 500, "Shop address"));
            command.Parameters.AddWithValue("$phone", Optional(request.ShopPhone, 100, "Shop phone"));
            command.Parameters.AddWithValue("$email", Optional(request.ShopEmail, 200, "Shop email"));
            command.Parameters.AddWithValue("$ownerUserId", ownerUserId);
            command.Parameters.AddWithValue("$userId", user.Id);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertSubscriptionEventAsync(connection, transaction, subscriptionId, organizationId,
            "tenant_onboarded", null, "active", new { organizationName, shopCode, ownerUserId, planId }, user.Id, cancellationToken);
        await WriteAuditAsync(connection, transaction, user, "saas.tenant.onboarded", "organization", organizationId,
            new { organizationName, shopId, shopCode, ownerUserId, planId }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        IReadOnlyList<SaasTenantSummaryRecord> tenants = await ListTenantsAsync(user, cancellationToken);
        return tenants.Single(item => item.OrganizationId == organizationId);
    }

    public async Task<SaasSubscriptionRecord> UpdateSubscriptionAsync(
        AuthenticatedUser user,
        string organizationId,
        UpdateSaasSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        string tenantId = Required(organizationId, 100, "organization_id_required", "Organisation ID is required.");
        string planId = Required(request.PlanId, 100, "plan_required", "Select a SaaS plan.");
        string status = Choice(request.Status, "subscription_status_invalid", "trialing", "active", "past_due", "suspended", "cancelled");
        if (request.ExpectedVersion < 1)
        {
            throw Error(400, "subscription_version_invalid", "Subscription version is invalid.");
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequirePlatformOperatorAsync(connection, transaction, user, writeRequired: true, cancellationToken);
        await EnsurePlanExistsAsync(connection, transaction, planId, cancellationToken);
        SaasSubscriptionRecord previous = await ReadSubscriptionAsync(connection, transaction, tenantId, cancellationToken);
        ValidateSubscriptionTransition(previous.Status, status);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE saas_subscriptions
        SET plan_id = $planId,
            status = $status,
            trial_ends_at_utc = $trialEnd,
            current_period_starts_utc = $periodStart,
            current_period_ends_utc = $periodEnd,
            grace_ends_at_utc = $graceEnd,
            external_customer_ref = $customerRef,
            external_subscription_ref = $subscriptionRef,
            notes = $notes,
            version = version + 1,
            updated_by_user_id = $userId,
            updated_at_utc = $now
        WHERE organization_id = $organizationId
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$organizationId", tenantId);
        command.Parameters.AddWithValue("$planId", planId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$trialEnd", DbDate(request.TrialEndsAtUtc));
        command.Parameters.AddWithValue("$periodStart", DbDate(request.CurrentPeriodStartsUtc));
        command.Parameters.AddWithValue("$periodEnd", DbDate(request.CurrentPeriodEndsUtc));
        command.Parameters.AddWithValue("$graceEnd", DbDate(request.GraceEndsAtUtc));
        command.Parameters.AddWithValue("$customerRef", Optional(request.ExternalCustomerReference, 200, "External customer reference"));
        command.Parameters.AddWithValue("$subscriptionRef", Optional(request.ExternalSubscriptionReference, 200, "External subscription reference"));
        command.Parameters.AddWithValue("$notes", Optional(request.Notes, 1000, "Subscription notes"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Error(409, "subscription_version_conflict", "The subscription changed before this update was saved.");
        }
        await InsertSubscriptionEventAsync(connection, transaction, previous.Id, tenantId,
            "subscription_updated", previous.Status, status,
            new { previous.PlanId, newPlanId = planId, request.Notes }, user.Id, cancellationToken);
        await WriteAuditAsync(connection, transaction, user, "saas.subscription.updated", "organization", tenantId,
            new { previousStatus = previous.Status, status, previousPlanId = previous.PlanId, planId }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadSubscriptionAsync(connection, null, tenantId, cancellationToken);
    }

    public async Task<SaasUsageSnapshotRecord> CaptureUsageAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken = default)
    {
        RequireTenantAdmin(user, context);
        return await CaptureUsageForOrganizationAsync(user, context.OrganizationId, cancellationToken);
    }

    public async Task<IReadOnlyList<SaasUsageSnapshotRecord>> ListUsageAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        int limit,
        CancellationToken cancellationToken = default)
    {
        RequireTenantAdmin(user, context);
        int bounded = Math.Clamp(limit, 1, 365);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id, organization_id, captured_at_utc, active_shop_count,
               active_user_count, employee_count, customer_count,
               completed_sales_30d, purchase_orders_30d,
               database_size_bytes, limit_violations_json
        FROM saas_usage_snapshots
        WHERE organization_id = $organizationId
        ORDER BY captured_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$limit", bounded);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<SaasUsageSnapshotRecord>();
        while (await reader.ReadAsync(cancellationToken)) records.Add(MapUsage(reader));
        return records;
    }

    public async Task<SaasTenantHealthRecord> CaptureHealthAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken = default)
    {
        RequireTenantAdmin(user, context);
        SaasUsageSnapshotRecord usage = await CaptureUsageForOrganizationAsync(user, context.OrganizationId, cancellationToken);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        int schemaVersion;
        DateTimeOffset? lastBackup;
        int openSupport;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT
                (SELECT COALESCE(MAX(version), 0) FROM schema_versions),
                (SELECT MAX(occurred_at_utc) FROM audit_logs WHERE event_type = 'backup.created'),
                (SELECT COUNT(1) FROM saas_support_cases
                 WHERE organization_id = $organizationId
                   AND status IN ('open','in_progress','waiting'));
            """;
            command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            schemaVersion = reader.GetInt32(0);
            lastBackup = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1));
            openSupport = reader.GetInt32(2);
        }
        var violations = JsonSerializer.Deserialize<List<string>>(usage.LimitViolationsJson) ?? [];
        string health = schemaVersion < 16 || violations.Count > 0
            ? "warning"
            : "healthy";
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string details = JsonSerializer.Serialize(new
        {
            subscription = await GetCurrentSubscriptionAsync(user, context, cancellationToken),
            limitViolations = violations,
            databaseReady = true
        });
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            INSERT INTO saas_tenant_health_snapshots
            (
                id, organization_id, health_status, schema_version,
                database_size_bytes, active_shop_count, active_user_count,
                open_support_count, last_backup_at_utc, details_json,
                captured_by_user_id, captured_at_utc
            )
            VALUES
            (
                $id, $organizationId, $health, $schemaVersion,
                $databaseSize, $shops, $users, $support, $lastBackup,
                $details, $userId, $now
            );
            """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            command.Parameters.AddWithValue("$health", health);
            command.Parameters.AddWithValue("$schemaVersion", schemaVersion);
            command.Parameters.AddWithValue("$databaseSize", usage.DatabaseSizeBytes);
            command.Parameters.AddWithValue("$shops", usage.ActiveShopCount);
            command.Parameters.AddWithValue("$users", usage.ActiveUserCount);
            command.Parameters.AddWithValue("$support", openSupport);
            command.Parameters.AddWithValue("$lastBackup", lastBackup is null ? DBNull.Value : lastBackup.Value.ToString("O"));
            command.Parameters.AddWithValue("$details", details);
            command.Parameters.AddWithValue("$userId", user.Id);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return new SaasTenantHealthRecord(
            id, context.OrganizationId, health, schemaVersion,
            usage.DatabaseSizeBytes, usage.ActiveShopCount, usage.ActiveUserCount,
            openSupport, lastBackup, details, now);
    }

    public async Task<SaasPlatformDashboardRecord> GetPlatformDashboardAsync(
        AuthenticatedUser user,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequirePlatformOperatorAsync(connection, null, user, writeRequired: false, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            (SELECT COUNT(1) FROM organizations),
            (SELECT COUNT(1) FROM saas_subscriptions WHERE status = 'active'),
            (SELECT COUNT(1) FROM saas_subscriptions WHERE status = 'trialing'),
            (SELECT COUNT(1) FROM saas_subscriptions WHERE status = 'past_due'),
            (SELECT COUNT(1) FROM saas_subscriptions WHERE status = 'suspended'),
            (SELECT COUNT(1) FROM saas_support_cases
             WHERE status IN ('open','in_progress','waiting')),
            (SELECT COUNT(1) FROM saas_support_cases
             WHERE priority = 'urgent' AND status IN ('open','in_progress','waiting')),
            (SELECT COUNT(1) FROM saas_support_access_grants
             WHERE revoked_at_utc IS NULL AND expires_at_utc > $now),
            (SELECT COALESCE(SUM(amount_minor), 0) FROM saas_billing_events
             WHERE status = 'pending' AND event_type = 'invoice');
        """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new SaasPlatformDashboardRecord(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
            reader.GetInt32(6), reader.GetInt32(7), reader.GetInt64(8),
            DateTimeOffset.UtcNow);
    }

    private async Task<SaasUsageSnapshotRecord> CaptureUsageForOrganizationAsync(
        AuthenticatedUser user,
        string organizationId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        int shops;
        int users;
        int employees;
        int customers;
        int sales;
        int purchaseOrders;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT
                (SELECT COUNT(1) FROM shops shop
                 WHERE shop.organization_id = $organizationId AND shop.is_active = 1),
                (SELECT COUNT(DISTINCT access.user_id)
                 FROM user_shop_access access
                 JOIN shops shop ON shop.id = access.shop_id
                 JOIN users user ON user.id = access.user_id
                 WHERE shop.organization_id = $organizationId
                   AND access.is_active = 1 AND user.is_active = 1),
                (SELECT COUNT(1) FROM hrm_employees employee
                 WHERE employee.organization_id = $organizationId
                   AND employee.status IN ('active','probation','on_leave')),
                (SELECT COUNT(1) FROM finance_customers customer
                 WHERE customer.organization_id = $organizationId
                   AND customer.is_active = 1),
                (SELECT COUNT(1) FROM sales sale
                 JOIN shops shop ON shop.id = sale.shop_id
                 WHERE shop.organization_id = $organizationId
                   AND sale.status IN ('completed','partially_returned')
                   AND sale.completed_at_utc >= datetime('now', '-30 days')),
                (SELECT COUNT(1) FROM procurement_purchase_orders purchase_order
                 WHERE purchase_order.organization_id = $organizationId
                   AND purchase_order.created_at_utc >= datetime('now', '-30 days'));
            """;
            command.Parameters.AddWithValue("$organizationId", organizationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            shops = reader.GetInt32(0);
            users = reader.GetInt32(1);
            employees = reader.GetInt32(2);
            customers = reader.GetInt32(3);
            sales = reader.GetInt32(4);
            purchaseOrders = reader.GetInt32(5);
        }
        IReadOnlyList<SaasEntitlementRecord> entitlements =
            await ReadEffectiveEntitlementsAsync(connection, null, organizationId, cancellationToken);
        var violations = new List<string>();
        SaasEntitlementRecord? shopLimit = entitlements.FirstOrDefault(item => item.Key == "max_active_shops");
        SaasEntitlementRecord? userLimit = entitlements.FirstOrDefault(item => item.Key == "max_active_users");
        if (shopLimit?.IsEnabled == true && shopLimit.LimitValue is long maximumShops && shops > maximumShops)
            violations.Add($"max_active_shops:{shops}/{maximumShops}");
        if (userLimit?.IsEnabled == true && userLimit.LimitValue is long maximumUsers && users > maximumUsers)
            violations.Add($"max_active_users:{users}/{maximumUsers}");
        long databaseSize = File.Exists(_database.DatabasePath)
            ? new FileInfo(_database.DatabasePath).Length
            : 0;
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string violationsJson = JsonSerializer.Serialize(violations);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            INSERT INTO saas_usage_snapshots
            (
                id, organization_id, captured_at_utc, active_shop_count,
                active_user_count, employee_count, customer_count,
                completed_sales_30d, purchase_orders_30d,
                database_size_bytes, limit_violations_json, captured_by_user_id
            )
            VALUES
            (
                $id, $organizationId, $now, $shops, $users, $employees,
                $customers, $sales, $purchaseOrders, $databaseSize,
                $violations, $userId
            );
            """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$organizationId", organizationId);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$shops", shops);
            command.Parameters.AddWithValue("$users", users);
            command.Parameters.AddWithValue("$employees", employees);
            command.Parameters.AddWithValue("$customers", customers);
            command.Parameters.AddWithValue("$sales", sales);
            command.Parameters.AddWithValue("$purchaseOrders", purchaseOrders);
            command.Parameters.AddWithValue("$databaseSize", databaseSize);
            command.Parameters.AddWithValue("$violations", violationsJson);
            command.Parameters.AddWithValue("$userId", user.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return new SaasUsageSnapshotRecord(
            id, organizationId, now, shops, users, employees, customers,
            sales, purchaseOrders, databaseSize, violationsJson);
    }

    private async Task<SaasPlanRecord> GetPlanAsync(
        string planId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            id, code, name, description, status, billing_interval,
            price_minor, currency_code, trial_days, enforcement_mode,
            sort_order, version, created_at_utc, updated_at_utc
        FROM saas_plans
        WHERE id = $id;
        """;
        command.Parameters.AddWithValue("$id", planId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw Error(404, "plan_not_found", "The SaaS plan was not found.");
        return MapPlan(reader);
    }

    private static SaasPlanRecord MapPlan(SqliteDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetInt64(6), reader.GetString(7), reader.GetInt32(8),
            reader.GetString(9), reader.GetInt32(10), reader.GetInt32(11),
            DateTimeOffset.Parse(reader.GetString(12)),
            DateTimeOffset.Parse(reader.GetString(13)));

    private static SaasUsageSnapshotRecord MapUsage(SqliteDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
            reader.GetInt32(7), reader.GetInt32(8), reader.GetInt64(9), reader.GetString(10));

    private static async Task<SaasSubscriptionRecord> ReadSubscriptionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string organizationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            subscription.id,
            subscription.organization_id,
            organization.name,
            subscription.plan_id,
            plan.code,
            plan.name,
            subscription.status,
            subscription.started_at_utc,
            subscription.trial_ends_at_utc,
            subscription.current_period_starts_utc,
            subscription.current_period_ends_utc,
            subscription.grace_ends_at_utc,
            subscription.external_customer_ref,
            subscription.external_subscription_ref,
            subscription.notes,
            subscription.version,
            subscription.created_at_utc,
            subscription.updated_at_utc
        FROM saas_subscriptions subscription
        JOIN organizations organization ON organization.id = subscription.organization_id
        JOIN saas_plans plan ON plan.id = subscription.plan_id
        WHERE subscription.organization_id = $organizationId;
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw Error(404, "subscription_not_found", "The organisation subscription was not found.");
        return new SaasSubscriptionRecord(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7)),
            ReadDate(reader, 8), ReadDate(reader, 9), ReadDate(reader, 10), ReadDate(reader, 11),
            reader.GetString(12), reader.GetString(13), reader.GetString(14),
            reader.GetInt32(15), DateTimeOffset.Parse(reader.GetString(16)),
            DateTimeOffset.Parse(reader.GetString(17)));
    }

    private static async Task<IReadOnlyList<SaasEntitlementRecord>> ReadEffectiveEntitlementsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string organizationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            entitlement.entitlement_key,
            CASE WHEN override.organization_id IS NOT NULL
                 THEN COALESCE(override.is_enabled, entitlement.is_enabled)
                 ELSE entitlement.is_enabled END,
            CASE WHEN override.organization_id IS NOT NULL AND override.limit_value IS NOT NULL
                 THEN override.limit_value ELSE entitlement.limit_value END,
            entitlement.configuration_json,
            CASE WHEN override.organization_id IS NULL THEN 'plan' ELSE 'override' END,
            override.expires_at_utc
        FROM saas_subscriptions subscription
        JOIN saas_plan_entitlements entitlement
          ON entitlement.plan_id = subscription.plan_id
        LEFT JOIN saas_feature_overrides override
          ON override.organization_id = subscription.organization_id
         AND override.entitlement_key = entitlement.entitlement_key
         AND (override.expires_at_utc IS NULL OR override.expires_at_utc > $now)
        WHERE subscription.organization_id = $organizationId
        ORDER BY entitlement.entitlement_key;
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<SaasEntitlementRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new SaasEntitlementRecord(
                reader.GetString(0), reader.GetInt32(1) == 1,
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4), ReadDate(reader, 5)));
        }
        return records;
    }

    private static async Task<string> RequirePlatformOperatorAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        AuthenticatedUser user,
        bool writeRequired,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT operator_role
        FROM saas_platform_operators
        WHERE user_id = $userId
          AND is_active = 1;
        """;
        command.Parameters.AddWithValue("$userId", user.Id);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null)
            throw Error(403, "platform_operator_required", "Platform operator access is required.");
        string role = Convert.ToString(value) ?? "";
        if (writeRequired && role is not ("owner" or "operator"))
            throw Error(403, "platform_write_access_required", "Platform owner or operator access is required.");
        return role;
    }

    private static void RequireTenantAdmin(
        AuthenticatedUser user,
        ActiveShopContextRecord context)
    {
        if (!string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AccessLevel, "manager", StringComparison.OrdinalIgnoreCase))
        {
            throw Error(403, "tenant_admin_required", "Organisation administrator or branch manager access is required.");
        }
    }

    private static async Task EnsurePlanExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(1) FROM saas_plans WHERE id = $id AND status = 'active';";
        command.Parameters.AddWithValue("$id", planId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            throw Error(404, "active_plan_not_found", "The selected active SaaS plan was not found.");
    }

    private static async Task EnsureActiveUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(1) FROM users WHERE id = $id AND is_active = 1;";
        command.Parameters.AddWithValue("$id", userId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            throw Error(404, "active_owner_user_not_found", "The selected active owner user was not found.");
    }

    private static void ValidateSubscriptionTransition(string previous, string next)
    {
        if (previous == next) return;
        bool allowed = previous switch
        {
            "trialing" => next is "active" or "past_due" or "suspended" or "cancelled",
            "active" => next is "past_due" or "suspended" or "cancelled" or "trialing",
            "past_due" => next is "active" or "suspended" or "cancelled",
            "suspended" => next is "active" or "cancelled",
            "cancelled" => next is "active" or "trialing",
            _ => false
        };
        if (!allowed)
            throw Error(409, "subscription_transition_invalid", $"Subscription cannot move from {previous} to {next}.");
    }

    private static async Task InsertSubscriptionEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string subscriptionId,
        string organizationId,
        string eventType,
        string? previousStatus,
        string? newStatus,
        object details,
        string? actorUserId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO saas_subscription_events
        (
            subscription_id, organization_id, event_type,
            previous_status, new_status, details_json,
            actor_user_id, occurred_at_utc
        )
        VALUES
        ($subscriptionId, $organizationId, $eventType,
         $previousStatus, $newStatus, $details, $actorUserId, $now);
        """;
        command.Parameters.AddWithValue("$subscriptionId", subscriptionId);
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$previousStatus", (object?)previousStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$newStatus", (object?)newStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$details", JsonSerializer.Serialize(details));
        command.Parameters.AddWithValue("$actorUserId", (object?)actorUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

    private static object DbDate(DateTimeOffset? value) =>
        value is null ? DBNull.Value : value.Value.ToString("O");

    private static string Required(string? value, int maximumLength, string errorCode, string message)
    {
        string trimmed = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed)) throw Error(400, errorCode, message);
        if (trimmed.Length > maximumLength) throw Error(400, "value_too_long", $"{message} Maximum length is {maximumLength}.");
        return trimmed;
    }

    private static string Optional(string? value, int maximumLength, string fieldName)
    {
        string trimmed = value?.Trim() ?? "";
        if (trimmed.Length > maximumLength) throw Error(400, "value_too_long", $"{fieldName} is too long.");
        return trimmed;
    }

    private static string Choice(string? value, string errorCode, params string[] choices)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? "";
        if (!choices.Contains(normalized, StringComparer.Ordinal))
            throw Error(400, errorCode, $"Allowed values: {string.Join(", ", choices)}.");
        return normalized;
    }

    private static string NormalizedKey(string? value)
    {
        string key = Required(value, 100, "entitlement_key_required", "Entitlement key is required.")
            .Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (key.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
            throw Error(400, "entitlement_key_invalid", "Entitlement keys may contain letters, numbers and underscores only.");
        return key;
    }

    private static string JsonObjectOrDefault(string? value)
    {
        string json = string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            return json;
        }
        catch (JsonException)
        {
            throw Error(400, "configuration_json_invalid", "Configuration must be a JSON object.");
        }
    }

    private static SaasException Error(int statusCode, string errorCode, string message) =>
        new(statusCode, errorCode, message);
}
