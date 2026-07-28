using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Crm;

public sealed partial class CrmService
{
    private static readonly HashSet<string> CommunicationTypes =
        new(StringComparer.Ordinal)
        {
            "call", "email", "sms", "whatsapp", "meeting", "note", "complaint"
        };

    private static readonly HashSet<string> CommunicationDirections =
        new(StringComparer.Ordinal) { "inbound", "outbound", "internal" };

    private static readonly HashSet<string> TaskPriorities =
        new(StringComparer.Ordinal) { "low", "normal", "high", "urgent" };

    public async Task<IReadOnlyList<CrmCommunicationRecord>> ListCommunicationsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? customerId,
        string? requestedType,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        string customer = string.IsNullOrWhiteSpace(customerId)
            ? string.Empty
            : NormalizeId(customerId);
        string type = requestedType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (type.Length > 0 && !CommunicationTypes.Contains(type))
        {
            throw Validation("invalid_communication_type", "The communication type is invalid.");
        }
        int limit = Math.Clamp(requestedLimit, 1, 2000);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            communication.id,
            communication.shop_id,
            shop.code,
            communication.customer_id,
            customer.name,
            communication.communication_type,
            communication.direction,
            communication.subject,
            communication.details,
            communication.outcome,
            communication.occurred_at_utc,
            communication.follow_up_at_utc,
            communication.created_by_user_id,
            creator.display_name,
            communication.created_at_utc
        FROM crm_communications AS communication
        INNER JOIN shops AS shop ON shop.id = communication.shop_id
        INNER JOIN finance_customers AS customer ON customer.id = communication.customer_id
        INNER JOIN users AS creator ON creator.id = communication.created_by_user_id
        WHERE communication.organization_id = $organizationId
          AND communication.shop_id = $shopId
          AND ($customerId = '' OR communication.customer_id = $customerId)
          AND ($type = '' OR communication.communication_type = $type)
        ORDER BY communication.occurred_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$customerId", customer);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<CrmCommunicationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadCommunication(reader));
        }
        return records;
    }

    public async Task<CrmCommunicationRecord> CreateCommunicationAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateCommunicationRequest request,
        CancellationToken cancellationToken = default)
    {
        string customerId = NormalizeId(request.CustomerId);
        string type = NormalizeChoice(
            request.CommunicationType,
            CommunicationTypes,
            "invalid_communication_type",
            "The communication type is invalid.");
        string direction = NormalizeChoice(
            request.Direction,
            CommunicationDirections,
            "invalid_communication_direction",
            "The communication direction is invalid.");
        string subject = OptionalText(request.Subject, 200);
        string details = RequiredText(
            request.Details,
            4000,
            "communication_details_required",
            "Enter the communication details.");
        string outcome = OptionalText(request.Outcome, 500);
        DateTimeOffset occurredAt = NormalizeUtcDateTime(
            request.OccurredAtUtc,
            "invalid_communication_time",
            defaultNow: true);
        DateTimeOffset? followUpAt = NormalizeOptionalUtcDateTime(
            request.FollowUpAtUtc,
            "invalid_follow_up_time");
        if (followUpAt is not null && followUpAt <= occurredAt)
        {
            throw Validation(
                "follow_up_not_after_communication",
                "The follow-up time must be after the communication time.");
        }

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        await RequireCustomerAsync(
            connection,
            transaction,
            context.OrganizationId,
            customerId,
            includeInactive: true,
            cancellationToken);

        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO crm_communications
        (
            id, organization_id, shop_id, customer_id,
            communication_type, direction, subject, details, outcome,
            occurred_at_utc, follow_up_at_utc,
            created_by_user_id, created_at_utc
        )
        VALUES
        (
            $id, $organizationId, $shopId, $customerId,
            $type, $direction, $subject, $details, $outcome,
            $occurredAt, $followUpAt,
            $userId, $now
        );
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$customerId", customerId);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$direction", direction);
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$details", details);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$occurredAt", occurredAt.ToString("O"));
        command.Parameters.AddWithValue("$followUpAt", followUpAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "crm.communication.logged",
            "crm_communication",
            id,
            new
            {
                context.ShopId,
                customerId,
                communicationType = type,
                direction,
                occurredAtUtc = occurredAt,
                followUpAtUtc = followUpAt
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetCommunicationAsync(user, context, id, cancellationToken);
    }

    public async Task<IReadOnlyList<CrmTaskRecord>> ListTasksAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? customerId,
        string? requestedStatus,
        bool assignedToMe,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        string customer = string.IsNullOrWhiteSpace(customerId)
            ? string.Empty
            : NormalizeId(customerId);
        string status = requestedStatus?.Trim().ToLowerInvariant() ?? string.Empty;
        if (status.Length > 0 &&
            !new[] { "open", "completed", "cancelled" }.Contains(status, StringComparer.Ordinal))
        {
            throw Validation("invalid_task_status", "The CRM task status is invalid.");
        }
        int limit = Math.Clamp(requestedLimit, 1, 2000);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            task.id,
            task.shop_id,
            shop.code,
            task.customer_id,
            COALESCE(customer.name, ''),
            task.title,
            task.details,
            task.priority,
            task.status,
            task.due_at_utc,
            task.assigned_to_user_id,
            assigned.display_name,
            task.created_by_user_id,
            creator.display_name,
            task.completed_by_user_id,
            COALESCE(completer.display_name, ''),
            COALESCE(task.completion_notes, ''),
            task.version,
            task.created_at_utc,
            task.updated_at_utc,
            task.completed_at_utc,
            task.cancelled_at_utc
        FROM crm_tasks AS task
        INNER JOIN shops AS shop ON shop.id = task.shop_id
        LEFT JOIN finance_customers AS customer ON customer.id = task.customer_id
        INNER JOIN users AS assigned ON assigned.id = task.assigned_to_user_id
        INNER JOIN users AS creator ON creator.id = task.created_by_user_id
        LEFT JOIN users AS completer ON completer.id = task.completed_by_user_id
        WHERE task.organization_id = $organizationId
          AND task.shop_id = $shopId
          AND ($customerId = '' OR task.customer_id = $customerId)
          AND ($status = '' OR task.status = $status)
          AND ($assignedToMe = 0 OR task.assigned_to_user_id = $userId)
        ORDER BY
            CASE task.status WHEN 'open' THEN 0 ELSE 1 END,
            task.due_at_utc,
            task.created_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$customerId", customer);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$assignedToMe", assignedToMe ? 1 : 0);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<CrmTaskRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadTask(reader));
        }
        return records;
    }

    public async Task<CrmTaskRecord> CreateTaskAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateCrmTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        string? customerId = string.IsNullOrWhiteSpace(request.CustomerId)
            ? null
            : NormalizeId(request.CustomerId);
        string title = RequiredText(request.Title, 200, "task_title_required", "Enter the task title.");
        string details = OptionalText(request.Details, 2000);
        string priority = NormalizeChoice(
            request.Priority,
            TaskPriorities,
            "invalid_task_priority",
            "The CRM task priority is invalid.");
        DateTimeOffset dueAt = NormalizeUtcDateTime(request.DueAtUtc, "invalid_task_due_time");
        string assignedUserId = string.IsNullOrWhiteSpace(request.AssignedToUserId)
            ? user.Id
            : NormalizeId(request.AssignedToUserId);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        if (customerId is not null)
        {
            await RequireCustomerAsync(
                connection,
                transaction,
                context.OrganizationId,
                customerId,
                includeInactive: true,
                cancellationToken);
        }
        await RequireAssignableUserAsync(
            connection,
            transaction,
            context.OrganizationId,
            assignedUserId,
            cancellationToken);

        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
            """
            INSERT INTO crm_tasks
            (
                id, organization_id, shop_id, customer_id,
                title, details, priority, status, due_at_utc,
                assigned_to_user_id, created_by_user_id,
                version, created_at_utc, updated_at_utc
            )
            VALUES
            (
                $id, $organizationId, $shopId, $customerId,
                $title, $details, $priority, 'open', $dueAt,
                $assignedUserId, $createdByUserId,
                1, $now, $now
            );
            """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            command.Parameters.AddWithValue("$shopId", context.ShopId);
            command.Parameters.AddWithValue("$customerId", customerId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$details", details);
            command.Parameters.AddWithValue("$priority", priority);
            command.Parameters.AddWithValue("$dueAt", dueAt.ToString("O"));
            command.Parameters.AddWithValue("$assignedUserId", assignedUserId);
            command.Parameters.AddWithValue("$createdByUserId", user.Id);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (customerId is not null)
        {
            await using var profile = connection.CreateCommand();
            profile.Transaction = transaction;
            profile.CommandText =
            """
            UPDATE crm_customer_profiles
            SET next_follow_up_at_utc = CASE
                    WHEN next_follow_up_at_utc IS NULL OR $dueAt < next_follow_up_at_utc
                    THEN $dueAt ELSE next_follow_up_at_utc END,
                updated_at_utc = $now,
                version = version + 1
            WHERE customer_id = $customerId;
            """;
            profile.Parameters.AddWithValue("$dueAt", dueAt.ToString("O"));
            profile.Parameters.AddWithValue("$now", now.ToString("O"));
            profile.Parameters.AddWithValue("$customerId", customerId);
            await profile.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "crm.task.created",
            "crm_task",
            id,
            new
            {
                context.ShopId,
                customerId,
                title,
                priority,
                dueAtUtc = dueAt,
                assignedUserId
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetTaskAsync(user, context, id, cancellationToken);
    }

    public Task<CrmTaskRecord> CompleteTaskAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string taskId,
        CompleteCrmTaskRequest request,
        CancellationToken cancellationToken = default) =>
        CloseTaskAsync(
            user,
            context,
            taskId,
            request.ExpectedVersion,
            "completed",
            OptionalText(request.CompletionNotes, 1000),
            cancellationToken);

    public Task<CrmTaskRecord> CancelTaskAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string taskId,
        CancelCrmTaskRequest request,
        CancellationToken cancellationToken = default) =>
        CloseTaskAsync(
            user,
            context,
            taskId,
            request.ExpectedVersion,
            "cancelled",
            OptionalText(request.Reason, 1000),
            cancellationToken);

    private async Task<CrmTaskRecord> CloseTaskAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string taskId,
        int expectedVersion,
        string newStatus,
        string notes,
        CancellationToken cancellationToken)
    {
        string id = NormalizeId(taskId);
        if (expectedVersion < 1)
        {
            throw Validation("invalid_task_version", "The expected task version is invalid.");
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
        UPDATE crm_tasks
        SET status = $status,
            completed_by_user_id = CASE WHEN $status = 'completed' THEN $userId ELSE completed_by_user_id END,
            completion_notes = $notes,
            completed_at_utc = CASE WHEN $status = 'completed' THEN $now ELSE completed_at_utc END,
            cancelled_at_utc = CASE WHEN $status = 'cancelled' THEN $now ELSE cancelled_at_utc END,
            version = version + 1,
            updated_at_utc = $now
        WHERE id = $id
          AND organization_id = $organizationId
          AND shop_id = $shopId
          AND status = 'open'
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$status", newStatus);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("task_changed", "The task changed or is no longer open. Reload and try again.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            $"crm.task.{newStatus}",
            "crm_task",
            id,
            new { notes, previousVersion = expectedVersion },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetTaskAsync(user, context, id, cancellationToken);
    }

    private async Task<CrmCommunicationRecord> GetCommunicationAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string communicationId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            communication.id, communication.shop_id, shop.code,
            communication.customer_id, customer.name,
            communication.communication_type, communication.direction,
            communication.subject, communication.details, communication.outcome,
            communication.occurred_at_utc, communication.follow_up_at_utc,
            communication.created_by_user_id, creator.display_name,
            communication.created_at_utc
        FROM crm_communications AS communication
        INNER JOIN shops AS shop ON shop.id = communication.shop_id
        INNER JOIN finance_customers AS customer ON customer.id = communication.customer_id
        INNER JOIN users AS creator ON creator.id = communication.created_by_user_id
        WHERE communication.id = $id
          AND communication.organization_id = $organizationId
          AND communication.shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", communicationId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound("communication_not_found", "The CRM communication could not be found.");
        }
        return ReadCommunication(reader);
    }

    private async Task<CrmTaskRecord> GetTaskAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string taskId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CrmTaskRecord> tasks = await ListTasksAsync(
            user,
            context,
            customerId: null,
            requestedStatus: null,
            assignedToMe: false,
            requestedLimit: 2000,
            cancellationToken);
        CrmTaskRecord? task = tasks.SingleOrDefault(item => item.Id == taskId);
        return task ?? throw NotFound("task_not_found", "The CRM task could not be found.");
    }

    private static CrmCommunicationRecord ReadCommunication(SqliteDataReader reader) =>
        new(
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
            DateTimeOffset.Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
            reader.GetString(12),
            reader.GetString(13),
            DateTimeOffset.Parse(reader.GetString(14)));

    private static CrmTaskRecord ReadTask(SqliteDataReader reader)
    {
        DateTimeOffset dueAt = DateTimeOffset.Parse(reader.GetString(9));
        string status = reader.GetString(8);
        return new CrmTaskRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            status,
            dueAt,
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetInt32(17),
            DateTimeOffset.Parse(reader.GetString(18)),
            DateTimeOffset.Parse(reader.GetString(19)),
            reader.IsDBNull(20) ? null : DateTimeOffset.Parse(reader.GetString(20)),
            reader.IsDBNull(21) ? null : DateTimeOffset.Parse(reader.GetString(21)),
            status == "open" && dueAt < DateTimeOffset.UtcNow);
    }
}
