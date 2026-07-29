using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Saas;

public sealed partial class SaasService
{
    public async Task<SaasBillingEventRecord> CreateBillingEventAsync(
        AuthenticatedUser user,
        string organizationId,
        CreateSaasBillingEventRequest request,
        CancellationToken cancellationToken = default)
    {
        string tenantId = Required(organizationId, 100, "organization_id_required", "Organisation ID is required.");
        string eventType = Choice(request.EventType, "billing_event_type_invalid", "invoice", "payment", "credit", "refund", "adjustment");
        string status = Choice(request.Status, "billing_status_invalid", "pending", "paid", "failed", "voided");
        string currency = Required(request.CurrencyCode, 10, "currency_required", "Enter a currency code.").ToUpperInvariant();
        string externalReference = Optional(request.ExternalReference, 200, "External billing reference");
        string details = JsonObjectOrDefault(request.DetailsJson);
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset occurredAt = request.OccurredAtUtc ?? DateTimeOffset.UtcNow;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequirePlatformOperatorAsync(connection, transaction, user, writeRequired: true, cancellationToken);
        SaasSubscriptionRecord subscription = await ReadSubscriptionAsync(connection, transaction, tenantId, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            INSERT INTO saas_billing_events
            (
                id, organization_id, subscription_id, event_type,
                external_reference, amount_minor, currency_code, status,
                due_at_utc, occurred_at_utc, details_json,
                created_by_user_id, created_at_utc
            )
            VALUES
            (
                $id, $organizationId, $subscriptionId, $eventType,
                $externalReference, $amount, $currency, $status,
                $dueAt, $occurredAt, $details, $userId, $now
            );
            """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$organizationId", tenantId);
            command.Parameters.AddWithValue("$subscriptionId", subscription.Id);
            command.Parameters.AddWithValue("$eventType", eventType);
            command.Parameters.AddWithValue("$externalReference", externalReference);
            command.Parameters.AddWithValue("$amount", request.AmountMinor);
            command.Parameters.AddWithValue("$currency", currency);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$dueAt", DbDate(request.DueAtUtc));
            command.Parameters.AddWithValue("$occurredAt", occurredAt.ToString("O"));
            command.Parameters.AddWithValue("$details", details);
            command.Parameters.AddWithValue("$userId", user.Id);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await WriteAuditAsync(connection, transaction, user, "saas.billing_event.created", "organization", tenantId,
                new { id, eventType, request.AmountMinor, currency, status, externalReference }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19 && externalReference.Length > 0)
        {
            throw Error(409, "billing_reference_exists", "That external billing reference already exists for this organisation.");
        }
        return new SaasBillingEventRecord(
            id, tenantId, subscription.Id, eventType, externalReference,
            request.AmountMinor, currency, status, request.DueAtUtc,
            occurredAt, details, now);
    }

    public async Task<IReadOnlyList<SaasBillingEventRecord>> ListCurrentBillingEventsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        int limit,
        CancellationToken cancellationToken = default)
    {
        RequireTenantAdmin(user, context);
        return await ListBillingEventsAsync(context.OrganizationId, Math.Clamp(limit, 1, 500), cancellationToken);
    }

    public async Task<SaasSupportCaseRecord> CreateSupportCaseAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateSaasSupportCaseRequest request,
        CancellationToken cancellationToken = default)
    {
        string subject = Required(request.Subject, 200, "support_subject_required", "Enter a support-case subject.");
        string description = Required(request.Description, 5000, "support_description_required", "Describe the support issue.");
        string category = Required(request.Category, 100, "support_category_required", "Enter a support category.").ToLowerInvariant();
        string priority = Choice(request.Priority, "support_priority_invalid", "low", "normal", "high", "urgent");
        string? shopId = string.IsNullOrWhiteSpace(request.ShopId) ? context.ShopId : request.ShopId.Trim();
        if (!string.Equals(shopId, context.ShopId, StringComparison.Ordinal))
        {
            throw Error(403, "support_shop_scope_invalid", "Support cases may only be opened for the active shop.");
        }
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        string caseNumber = await NextCaseNumberAsync(connection, transaction, cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
            """
            INSERT INTO saas_support_cases
            (
                id, case_number, organization_id, shop_id,
                opened_by_user_id, category, priority, status,
                subject, description, resolution, version,
                created_at_utc, updated_at_utc
            )
            VALUES
            (
                $id, $caseNumber, $organizationId, $shopId,
                $userId, $category, $priority, 'open',
                $subject, $description, '', 1, $now, $now
            );
            """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$caseNumber", caseNumber);
            command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            command.Parameters.AddWithValue("$shopId", shopId);
            command.Parameters.AddWithValue("$userId", user.Id);
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$priority", priority);
            command.Parameters.AddWithValue("$subject", subject);
            command.Parameters.AddWithValue("$description", description);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertSupportEventAsync(connection, transaction, id, "opened", null, "open", description, user.Id, cancellationToken);
        await WriteAuditAsync(connection, transaction, user, "saas.support_case.opened", "saas_support_case", id,
            new { caseNumber, context.OrganizationId, shopId, category, priority, subject }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadSupportCaseAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<SaasSupportCaseRecord>> ListCurrentSupportCasesAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? status,
        int limit,
        CancellationToken cancellationToken = default)
    {
        _ = user;
        string normalizedStatus = string.IsNullOrWhiteSpace(status)
            ? ""
            : Choice(status, "support_status_invalid", "open", "in_progress", "waiting", "resolved", "closed");
        return await ListSupportCasesAsync(context.OrganizationId, normalizedStatus, Math.Clamp(limit, 1, 500), cancellationToken);
    }

    public async Task<IReadOnlyList<SaasSupportCaseRecord>> ListPlatformSupportCasesAsync(
        AuthenticatedUser user,
        string? organizationId,
        string? status,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequirePlatformOperatorAsync(connection, null, user, writeRequired: false, cancellationToken);
        string tenantId = Optional(organizationId, 100, "Organisation ID");
        string normalizedStatus = string.IsNullOrWhiteSpace(status)
            ? ""
            : Choice(status, "support_status_invalid", "open", "in_progress", "waiting", "resolved", "closed");
        return await ListSupportCasesAsync(tenantId, normalizedStatus, Math.Clamp(limit, 1, 1000), cancellationToken);
    }

    public async Task<SaasSupportCaseRecord> UpdateSupportCaseAsync(
        AuthenticatedUser user,
        string caseId,
        UpdateSaasSupportCaseRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = Required(caseId, 100, "support_case_id_required", "Support-case ID is required.");
        string status = Choice(request.Status, "support_status_invalid", "open", "in_progress", "waiting", "resolved", "closed");
        if (request.ExpectedVersion < 1)
            throw Error(400, "support_version_invalid", "Support-case version is invalid.");
        string assignedTo = Optional(request.AssignedToUserId, 100, "Assigned operator");
        string resolution = Optional(request.Resolution, 5000, "Support resolution");
        string note = Optional(request.Note, 5000, "Support note");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        string operatorRole = await RequirePlatformOperatorAsync(connection, transaction, user, writeRequired: false, cancellationToken);
        if (operatorRole == "read_only")
            throw Error(403, "platform_support_write_required", "Platform support write access is required.");
        SaasSupportCaseRecord previous = await ReadSupportCaseAsync(connection, transaction, id, cancellationToken);
        ValidateSupportTransition(previous.Status, status);
        if (assignedTo.Length > 0)
            await EnsurePlatformOperatorUserAsync(connection, transaction, assignedTo, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE saas_support_cases
        SET assigned_to_user_id = $assignedTo,
            status = $status,
            resolution = $resolution,
            resolved_at_utc = CASE WHEN $status = 'resolved' THEN $now ELSE resolved_at_utc END,
            closed_at_utc = CASE WHEN $status = 'closed' THEN $now ELSE closed_at_utc END,
            version = version + 1,
            updated_at_utc = $now
        WHERE id = $id
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$assignedTo", assignedTo.Length == 0 ? DBNull.Value : assignedTo);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$resolution", resolution);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw Error(409, "support_version_conflict", "The support case changed before this update was saved.");
        await InsertSupportEventAsync(connection, transaction, id, "status_updated", previous.Status, status, note, user.Id, cancellationToken);
        await WriteAuditAsync(connection, transaction, user, "saas.support_case.updated", "saas_support_case", id,
            new { previousStatus = previous.Status, status, assignedTo, resolution }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadSupportCaseAsync(id, cancellationToken);
    }

    public async Task<SaasSupportCaseEventRecord> AddSupportCaseNoteAsync(
        AuthenticatedUser user,
        string caseId,
        AddSaasSupportCaseNoteRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = Required(caseId, 100, "support_case_id_required", "Support-case ID is required.");
        string note = Required(request.Note, 5000, "support_note_required", "Enter a support note.");
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        string role = await RequirePlatformOperatorAsync(connection, transaction, user, writeRequired: false, cancellationToken);
        if (role == "read_only")
            throw Error(403, "platform_support_write_required", "Platform support write access is required.");
        SaasSupportCaseRecord supportCase = await ReadSupportCaseAsync(connection, transaction, id, cancellationToken);
        long eventId = await InsertSupportEventAsync(connection, transaction, id, "note_added", supportCase.Status, supportCase.Status, note, user.Id, cancellationToken);
        await WriteAuditAsync(connection, transaction, user, "saas.support_case.note_added", "saas_support_case", id,
            new { eventId }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SaasSupportCaseEventRecord(eventId, "note_added", supportCase.Status, supportCase.Status, note, user.Id, DateTimeOffset.UtcNow);
    }

    public async Task<SaasSupportAccessGrantRecord> CreateSupportGrantAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateSaasSupportGrantRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireTenantAdmin(user, context);
        string operatorId = Required(request.OperatorUserId, 100, "operator_user_required", "Select a platform operator.");
        string scope = Choice(request.AccessScope, "support_scope_invalid", "read_only", "diagnostics", "support");
        string reason = Required(request.Reason, 1000, "support_grant_reason_required", "Explain why support access is required.");
        DateTimeOffset expiresAt = request.ExpiresAtUtc ?? DateTimeOffset.UtcNow.AddHours(4);
        if (expiresAt <= DateTimeOffset.UtcNow || expiresAt > DateTimeOffset.UtcNow.AddDays(30))
            throw Error(400, "support_grant_expiry_invalid", "Support access must expire within the next 30 days.");
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsurePlatformOperatorUserAsync(connection, transaction, operatorId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO saas_support_access_grants
        (
            id, organization_id, operator_user_id, access_scope,
            reason, expires_at_utc, version, created_by_user_id, created_at_utc
        )
        VALUES
        ($id, $organizationId, $operatorId, $scope, $reason, $expiresAt, 1, $userId, $now);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$operatorId", operatorId);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$expiresAt", expiresAt.ToString("O"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await WriteAuditAsync(connection, transaction, user, "saas.support_access.granted", "organization", context.OrganizationId,
            new { id, operatorId, scope, expiresAt }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadSupportGrantAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<SaasSupportAccessGrantRecord>> ListSupportGrantsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken = default)
    {
        RequireTenantAdmin(user, context);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT grant_record.id, grant_record.organization_id,
               grant_record.operator_user_id, user.username,
               grant_record.access_scope, grant_record.reason,
               grant_record.expires_at_utc, grant_record.revoked_at_utc,
               grant_record.version, grant_record.created_at_utc
        FROM saas_support_access_grants grant_record
        JOIN users user ON user.id = grant_record.operator_user_id
        WHERE grant_record.organization_id = $organizationId
        ORDER BY grant_record.created_at_utc DESC;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<SaasSupportAccessGrantRecord>();
        while (await reader.ReadAsync(cancellationToken)) records.Add(MapGrant(reader));
        return records;
    }

    public async Task<SaasSupportAccessGrantRecord> RevokeSupportGrantAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string grantId,
        RevokeSaasSupportGrantRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireTenantAdmin(user, context);
        string id = Required(grantId, 100, "support_grant_id_required", "Support grant ID is required.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE saas_support_access_grants
        SET revoked_at_utc = $now,
            revoked_by_user_id = $userId,
            version = version + 1
        WHERE id = $id
          AND organization_id = $organizationId
          AND revoked_at_utc IS NULL
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw Error(409, "support_grant_version_conflict", "The support grant changed, was revoked, or is outside this organisation.");
        await WriteAuditAsync(connection, transaction, user, "saas.support_access.revoked", "organization", context.OrganizationId,
            new { id }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadSupportGrantAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<SaasSupportCaseEventRecord>> ListSupportCaseEventsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string caseId,
        CancellationToken cancellationToken = default)
    {
        string id = Required(caseId, 100, "support_case_id_required", "Support-case ID is required.");
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        SaasSupportCaseRecord supportCase = await ReadSupportCaseAsync(connection, null, id, cancellationToken);
        if (supportCase.OrganizationId != context.OrganizationId)
            throw Error(404, "support_case_not_found", "The support case was not found.");
        _ = user;
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id, event_type, previous_status, new_status,
               note, actor_user_id, occurred_at_utc
        FROM saas_support_case_events
        WHERE support_case_id = $caseId
        ORDER BY id;
        """;
        command.Parameters.AddWithValue("$caseId", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<SaasSupportCaseEventRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new SaasSupportCaseEventRecord(
                reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }
        return records;
    }

    private async Task<IReadOnlyList<SaasBillingEventRecord>> ListBillingEventsAsync(
        string organizationId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id, organization_id, subscription_id, event_type,
               external_reference, amount_minor, currency_code, status,
               due_at_utc, occurred_at_utc, details_json, created_at_utc
        FROM saas_billing_events
        WHERE organization_id = $organizationId
        ORDER BY occurred_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<SaasBillingEventRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new SaasBillingEventRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt64(5),
                reader.GetString(6), reader.GetString(7), ReadDate(reader, 8),
                DateTimeOffset.Parse(reader.GetString(9)), reader.GetString(10),
                DateTimeOffset.Parse(reader.GetString(11))));
        }
        return records;
    }

    private async Task<IReadOnlyList<SaasSupportCaseRecord>> ListSupportCasesAsync(
        string organizationId,
        string status,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id, case_number, organization_id, shop_id,
               opened_by_user_id, assigned_to_user_id, category,
               priority, status, subject, description, resolution,
               version, created_at_utc, updated_at_utc,
               resolved_at_utc, closed_at_utc
        FROM saas_support_cases
        WHERE ($organizationId = '' OR organization_id = $organizationId)
          AND ($status = '' OR status = $status)
        ORDER BY
          CASE priority WHEN 'urgent' THEN 0 WHEN 'high' THEN 1 WHEN 'normal' THEN 2 ELSE 3 END,
          updated_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<SaasSupportCaseRecord>();
        while (await reader.ReadAsync(cancellationToken)) records.Add(MapSupportCase(reader));
        return records;
    }

    private async Task<SaasSupportCaseRecord> ReadSupportCaseAsync(
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadSupportCaseAsync(connection, null, caseId, cancellationToken);
    }

    private static async Task<SaasSupportCaseRecord> ReadSupportCaseAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT id, case_number, organization_id, shop_id,
               opened_by_user_id, assigned_to_user_id, category,
               priority, status, subject, description, resolution,
               version, created_at_utc, updated_at_utc,
               resolved_at_utc, closed_at_utc
        FROM saas_support_cases
        WHERE id = $id;
        """;
        command.Parameters.AddWithValue("$id", caseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw Error(404, "support_case_not_found", "The support case was not found.");
        return MapSupportCase(reader);
    }

    private static SaasSupportCaseRecord MapSupportCase(SqliteDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8),
            reader.GetString(9), reader.GetString(10), reader.GetString(11),
            reader.GetInt32(12), DateTimeOffset.Parse(reader.GetString(13)),
            DateTimeOffset.Parse(reader.GetString(14)), ReadDate(reader, 15), ReadDate(reader, 16));

    private async Task<SaasSupportAccessGrantRecord> ReadSupportGrantAsync(
        string grantId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT grant_record.id, grant_record.organization_id,
               grant_record.operator_user_id, user.username,
               grant_record.access_scope, grant_record.reason,
               grant_record.expires_at_utc, grant_record.revoked_at_utc,
               grant_record.version, grant_record.created_at_utc
        FROM saas_support_access_grants grant_record
        JOIN users user ON user.id = grant_record.operator_user_id
        WHERE grant_record.id = $id;
        """;
        command.Parameters.AddWithValue("$id", grantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw Error(404, "support_grant_not_found", "The support access grant was not found.");
        return MapGrant(reader);
    }

    private static SaasSupportAccessGrantRecord MapGrant(SqliteDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)),
            ReadDate(reader, 7), reader.GetInt32(8), DateTimeOffset.Parse(reader.GetString(9)));

    private static async Task<long> InsertSupportEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        string eventType,
        string? previousStatus,
        string? newStatus,
        string note,
        string? actorUserId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO saas_support_case_events
        (support_case_id, event_type, previous_status, new_status, note, actor_user_id, occurred_at_utc)
        VALUES
        ($caseId, $eventType, $previousStatus, $newStatus, $note, $actorUserId, $now);
        SELECT last_insert_rowid();
        """;
        command.Parameters.AddWithValue("$caseId", caseId);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$previousStatus", (object?)previousStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$newStatus", (object?)newStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", note);
        command.Parameters.AddWithValue("$actorUserId", (object?)actorUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task EnsurePlatformOperatorUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COUNT(1)
        FROM saas_platform_operators operator
        JOIN users user ON user.id = operator.user_id
        WHERE operator.user_id = $userId
          AND operator.is_active = 1
          AND user.is_active = 1;
        """;
        command.Parameters.AddWithValue("$userId", userId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            throw Error(404, "platform_operator_not_found", "The selected active platform operator was not found.");
    }

    private static async Task<string> NextCaseNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COALESCE(MAX(id), 0) + 1
        FROM saas_support_case_events;
        """;
        long sequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return $"SUP-{DateTimeOffset.UtcNow:yyyyMMdd}-{sequence:000000}";
    }

    private static void ValidateSupportTransition(string previous, string next)
    {
        if (previous == next) return;
        bool allowed = previous switch
        {
            "open" => next is "in_progress" or "waiting" or "resolved" or "closed",
            "in_progress" => next is "waiting" or "resolved" or "closed" or "open",
            "waiting" => next is "in_progress" or "resolved" or "closed" or "open",
            "resolved" => next is "closed" or "in_progress",
            "closed" => next is "in_progress",
            _ => false
        };
        if (!allowed)
            throw Error(409, "support_transition_invalid", $"Support case cannot move from {previous} to {next}.");
    }
}
