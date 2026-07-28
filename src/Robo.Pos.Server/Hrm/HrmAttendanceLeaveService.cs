using System.Globalization;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Hrm;

public sealed partial class HrmService
{
    private static readonly HashSet<string> AttendanceSources = new(StringComparer.Ordinal)
    {
        "manual", "device", "import", "schedule"
    };

    public async Task<IReadOnlyList<WorkScheduleRecord>> ListWorkSchedulesAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? fromDate,
        string? toDate,
        CancellationToken cancellationToken = default)
    {
        string from = string.IsNullOrWhiteSpace(fromDate)
            ? DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")
            : NormalizeDate(fromDate, "invalid_schedule_from_date");
        string to = string.IsNullOrWhiteSpace(toDate)
            ? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)).ToString("yyyy-MM-dd")
            : NormalizeDate(toDate, "invalid_schedule_to_date");
        if (string.CompareOrdinal(to, from) < 0)
        {
            throw Validation("invalid_schedule_range", "The schedule end date precedes the start date.");
        }
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT schedule.id, schedule.shop_id, shop.code, schedule.employee_id,
               employee.first_name || ' ' || employee.last_name,
               schedule.work_date, schedule.start_time, schedule.end_time,
               schedule.break_minutes, schedule.status, schedule.notes,
               schedule.version, schedule.created_at_utc, schedule.updated_at_utc
        FROM hrm_work_schedules AS schedule
        INNER JOIN shops AS shop ON shop.id = schedule.shop_id
        INNER JOIN hrm_employees AS employee ON employee.id = schedule.employee_id
        WHERE schedule.organization_id = $organizationId
          AND schedule.shop_id = $shopId
          AND schedule.work_date BETWEEN $fromDate AND $toDate
        ORDER BY schedule.work_date, schedule.start_time, employee.last_name;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$fromDate", from);
        command.Parameters.AddWithValue("$toDate", to);
        var records = new List<WorkScheduleRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadWorkSchedule(reader));
        }
        return records;
    }

    public async Task<WorkScheduleRecord> CreateWorkScheduleAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateWorkScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        string employeeId = NormalizeId(request.EmployeeId);
        string workDate = NormalizeDate(request.WorkDate, "invalid_work_date");
        string startTime = NormalizeTime(request.StartTime, "invalid_schedule_start_time");
        string endTime = NormalizeTime(request.EndTime, "invalid_schedule_end_time");
        if (string.CompareOrdinal(endTime, startTime) <= 0 || request.BreakMinutes is < 0 or > 720)
        {
            throw Validation("invalid_schedule_time", "The schedule end time and break are invalid.");
        }
        string notes = OptionalText(request.Notes, 1000);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireManagerAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        await RequireEmployeeInShopAsync(
            connection, transaction, context, employeeId, activeOnly: true, cancellationToken);
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO hrm_work_schedules
        (id, organization_id, shop_id, employee_id, work_date, start_time,
         end_time, break_minutes, status, notes, version, created_by_user_id,
         updated_by_user_id, created_at_utc, updated_at_utc)
        VALUES
        ($id, $organizationId, $shopId, $employeeId, $workDate, $startTime,
         $endTime, $breakMinutes, 'draft', $notes, 1, $userId,
         $userId, $now, $now);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$employeeId", employeeId);
        command.Parameters.AddWithValue("$workDate", workDate);
        command.Parameters.AddWithValue("$startTime", startTime);
        command.Parameters.AddWithValue("$endTime", endTime);
        command.Parameters.AddWithValue("$breakMinutes", request.BreakMinutes);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("schedule_exists", "This employee already has a schedule for the selected date.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.schedule.created", "work_schedule", id,
            new { context.ShopId, employeeId, workDate, startTime, endTime }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListWorkSchedulesAsync(user, context, workDate, workDate, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<WorkScheduleRecord> PublishWorkScheduleAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string scheduleId,
        WorkScheduleActionRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(scheduleId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation("invalid_schedule_version", "The schedule version is invalid.");
        }
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireManagerAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE hrm_work_schedules
        SET status = 'published',
            version = version + 1,
            updated_by_user_id = $userId,
            updated_at_utc = $now
        WHERE id = $id
          AND organization_id = $organizationId
          AND shop_id = $shopId
          AND status = 'draft'
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("schedule_changed", "Only the current draft schedule can be published.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.schedule.published", "work_schedule", id,
            new { previousVersion = request.ExpectedVersion }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListWorkSchedulesAsync(
                user, context, DateTime.UtcNow.AddYears(-2).ToString("yyyy-MM-dd"),
                DateTime.UtcNow.AddYears(2).ToString("yyyy-MM-dd"), cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<IReadOnlyList<AttendanceRecord>> ListAttendanceAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? fromDate,
        string? toDate,
        string? employeeId,
        CancellationToken cancellationToken = default)
    {
        string from = string.IsNullOrWhiteSpace(fromDate)
            ? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd")
            : NormalizeDate(fromDate, "invalid_attendance_from_date");
        string to = string.IsNullOrWhiteSpace(toDate)
            ? DateTime.UtcNow.ToString("yyyy-MM-dd")
            : NormalizeDate(toDate, "invalid_attendance_to_date");
        string employee = string.IsNullOrWhiteSpace(employeeId) ? string.Empty : NormalizeId(employeeId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = AttendanceSelectSql +
        """
        WHERE attendance.organization_id = $organizationId
          AND attendance.shop_id = $shopId
          AND attendance.work_date BETWEEN $fromDate AND $toDate
          AND ($employeeId = '' OR attendance.employee_id = $employeeId)
        ORDER BY attendance.clock_in_utc DESC;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$fromDate", from);
        command.Parameters.AddWithValue("$toDate", to);
        command.Parameters.AddWithValue("$employeeId", employee);
        return await ReadAttendanceAsync(command, cancellationToken);
    }

    public async Task<AttendanceRecord> ClockInAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        ClockInRequest request,
        CancellationToken cancellationToken = default)
    {
        string employeeId = NormalizeId(request.EmployeeId);
        string source = request.Source.Trim().ToLowerInvariant();
        if (!AttendanceSources.Contains(source))
        {
            throw Validation("invalid_attendance_source", "The attendance source is invalid.");
        }
        string notes = OptionalText(request.Notes, 1000);
        DateTimeOffset clockIn = NormalizeTimestamp(
            request.ClockInUtc, DateTimeOffset.UtcNow, "invalid_clock_in_time");
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireManagerAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        await RequireEmployeeInShopAsync(
            connection, transaction, context, employeeId, activeOnly: true, cancellationToken);
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO hrm_attendance_entries
        (id, organization_id, shop_id, employee_id, work_date, clock_in_utc,
         clock_out_utc, break_minutes, worked_minutes, overtime_minutes, status,
         source, notes, version, created_by_user_id, approved_by_user_id,
         created_at_utc, updated_at_utc, approved_at_utc)
        VALUES
        ($id, $organizationId, $shopId, $employeeId, $workDate, $clockInUtc,
         NULL, 0, NULL, 0, 'open', $source, $notes, 1, $userId, NULL,
         $now, $now, NULL);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$employeeId", employeeId);
        command.Parameters.AddWithValue("$workDate", clockIn.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$clockInUtc", clockIn.ToString("O"));
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("attendance_already_open", "This employee already has an open attendance entry.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.attendance.clock_in", "attendance", id,
            new { context.ShopId, employeeId, clockInUtc = clockIn }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAttendanceAsync(user, context, id, cancellationToken);
    }

    public async Task<AttendanceRecord> ClockOutAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string attendanceId,
        ClockOutRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(attendanceId);
        if (request.ExpectedVersion < 1 || request.BreakMinutes is < 0 or > 720)
        {
            throw Validation("invalid_attendance_update", "The attendance version or break is invalid.");
        }
        string notes = OptionalText(request.Notes, 1000);
        DateTimeOffset clockOut = NormalizeTimestamp(
            request.ClockOutUtc, DateTimeOffset.UtcNow, "invalid_clock_out_time");
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireManagerAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        DateTimeOffset clockIn;
        string employeeId;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
            """
            SELECT clock_in_utc, employee_id
            FROM hrm_attendance_entries
            WHERE id = $id
              AND organization_id = $organizationId
              AND shop_id = $shopId
              AND status = 'open'
              AND version = $expectedVersion
            LIMIT 1;
            """;
            read.Parameters.AddWithValue("$id", id);
            read.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            read.Parameters.AddWithValue("$shopId", context.ShopId);
            read.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw Conflict("attendance_changed", "Only the current open attendance entry can be closed.");
            }
            clockIn = DateTimeOffset.Parse(reader.GetString(0));
            employeeId = reader.GetString(1);
        }
        double totalMinutes = (clockOut - clockIn).TotalMinutes;
        if (totalMinutes <= request.BreakMinutes || totalMinutes > 1440)
        {
            throw Validation("invalid_attendance_duration", "The attendance duration or break is invalid.");
        }
        int workedMinutes = checked((int)Math.Round(totalMinutes) - request.BreakMinutes);
        int standardDailyMinutes = await GetStandardDailyMinutesAsync(
            connection, transaction, employeeId, cancellationToken);
        int overtimeMinutes = Math.Max(0, workedMinutes - standardDailyMinutes);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE hrm_attendance_entries
        SET clock_out_utc = $clockOutUtc,
            break_minutes = $breakMinutes,
            worked_minutes = $workedMinutes,
            overtime_minutes = $overtimeMinutes,
            status = 'completed',
            notes = CASE WHEN $notes = '' THEN notes ELSE $notes END,
            version = version + 1,
            updated_at_utc = $now
        WHERE id = $id
          AND status = 'open'
          AND version = $expectedVersion;
        """;
        update.Parameters.AddWithValue("$clockOutUtc", clockOut.ToString("O"));
        update.Parameters.AddWithValue("$breakMinutes", request.BreakMinutes);
        update.Parameters.AddWithValue("$workedMinutes", workedMinutes);
        update.Parameters.AddWithValue("$overtimeMinutes", overtimeMinutes);
        update.Parameters.AddWithValue("$notes", notes);
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("attendance_changed", "The attendance entry changed while being closed.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.attendance.clock_out", "attendance", id,
            new { clockOutUtc = clockOut, workedMinutes, overtimeMinutes }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAttendanceAsync(user, context, id, cancellationToken);
    }

    public async Task<AttendanceRecord> ApproveAttendanceAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string attendanceId,
        AttendanceActionRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(attendanceId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation("invalid_attendance_version", "The attendance version is invalid.");
        }
        string notes = OptionalText(request.Notes, 1000);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireManagerAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE hrm_attendance_entries
        SET status = 'approved',
            approved_by_user_id = $userId,
            approved_at_utc = $now,
            notes = CASE WHEN $notes = '' THEN notes ELSE $notes END,
            version = version + 1,
            updated_at_utc = $now
        WHERE id = $id
          AND organization_id = $organizationId
          AND shop_id = $shopId
          AND status = 'completed'
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("attendance_changed", "Only the current completed attendance entry can be approved.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.attendance.approved", "attendance", id,
            new { previousVersion = request.ExpectedVersion }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAttendanceAsync(user, context, id, cancellationToken);
    }

    public async Task<IReadOnlyList<LeaveTypeRecord>> ListLeaveTypesAsync(
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
        SELECT id, code, name, annual_entitlement_days, is_paid,
               requires_attachment, is_active, version
        FROM hrm_leave_types
        WHERE organization_id = $organizationId
          AND ($includeInactive = 1 OR is_active = 1)
        ORDER BY name COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$includeInactive", includeInactive ? 1 : 0);
        var records = new List<LeaveTypeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new LeaveTypeRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetDouble(3), reader.GetInt32(4) == 1,
                reader.GetInt32(5) == 1, reader.GetInt32(6) == 1,
                reader.GetInt32(7)));
        }
        return records;
    }

    public async Task<LeaveTypeRecord> CreateLeaveTypeAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateLeaveTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string code = NormalizeCode(request.Code, "invalid_leave_type_code");
        string name = RequiredText(request.Name, 120, "leave_type_name_required", "Enter the leave type name.");
        if (request.AnnualEntitlementDays is < 0 or > 366)
        {
            throw Validation("invalid_leave_entitlement", "The annual leave entitlement is invalid.");
        }
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO hrm_leave_types
        (id, organization_id, code, name, annual_entitlement_days, is_paid,
         requires_attachment, is_active, version, created_by_user_id,
         updated_by_user_id, created_at_utc, updated_at_utc)
        VALUES
        ($id, $organizationId, $code, $name, $entitlement, $isPaid,
         $requiresAttachment, 1, 1, $userId, $userId, $now, $now);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$entitlement", request.AnnualEntitlementDays);
        command.Parameters.AddWithValue("$isPaid", request.IsPaid ? 1 : 0);
        command.Parameters.AddWithValue("$requiresAttachment", request.RequiresAttachment ? 1 : 0);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("leave_type_exists", "A leave type with this code or name already exists.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.leave_type.created", "leave_type", id,
            new { code, name, request.AnnualEntitlementDays, request.IsPaid }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListLeaveTypesAsync(user, context, true, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<IReadOnlyList<LeaveRequestRecord>> ListLeaveRequestsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? status,
        string? employeeId,
        CancellationToken cancellationToken = default)
    {
        string normalizedStatus = status?.Trim().ToLowerInvariant() ?? string.Empty;
        string employee = string.IsNullOrWhiteSpace(employeeId) ? string.Empty : NormalizeId(employeeId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = LeaveSelectSql +
        """
        WHERE request.organization_id = $organizationId
          AND ($status = '' OR request.status = $status)
          AND ($employeeId = '' OR request.employee_id = $employeeId)
          AND EXISTS
          (
              SELECT 1 FROM hrm_employee_shop_assignments AS assignment
              WHERE assignment.employee_id = request.employee_id
                AND assignment.shop_id = $shopId
                AND assignment.is_active = 1
          )
        ORDER BY request.created_at_utc DESC;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$status", normalizedStatus);
        command.Parameters.AddWithValue("$employeeId", employee);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        return await ReadLeaveRequestsAsync(command, cancellationToken);
    }

    public async Task<LeaveRequestRecord> CreateLeaveRequestAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateLeaveRequest request,
        CancellationToken cancellationToken = default)
    {
        string employeeId = NormalizeId(request.EmployeeId);
        string leaveTypeId = NormalizeId(request.LeaveTypeId);
        string startDate = NormalizeDate(request.StartDate, "invalid_leave_start_date");
        string endDate = NormalizeDate(request.EndDate, "invalid_leave_end_date");
        if (string.CompareOrdinal(endDate, startDate) < 0 || request.RequestedDays is <= 0 or > 366)
        {
            throw Validation("invalid_leave_duration", "The leave dates or requested days are invalid.");
        }
        string reason = OptionalText(request.Reason, 1000);
        string attachment = OptionalText(request.AttachmentReference, 500);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireReadAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        await RequireEmployeeInShopAsync(
            connection, transaction, context, employeeId, activeOnly: true, cancellationToken);
        await ValidateLeaveTypeAsync(
            connection, transaction, context.OrganizationId, leaveTypeId, attachment, cancellationToken);
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO hrm_leave_requests
        (id, organization_id, employee_id, leave_type_id, start_date, end_date,
         requested_days, reason, attachment_reference, status, version,
         requested_by_user_id, decided_by_user_id, decision_notes,
         created_at_utc, updated_at_utc, submitted_at_utc, decided_at_utc)
        VALUES
        ($id, $organizationId, $employeeId, $leaveTypeId, $startDate, $endDate,
         $requestedDays, $reason, $attachment, 'draft', 1,
         $userId, NULL, '', $now, $now, NULL, NULL);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$employeeId", employeeId);
        command.Parameters.AddWithValue("$leaveTypeId", leaveTypeId);
        command.Parameters.AddWithValue("$startDate", startDate);
        command.Parameters.AddWithValue("$endDate", endDate);
        command.Parameters.AddWithValue("$requestedDays", request.RequestedDays);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$attachment", attachment);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("leave_request_invalid", "The leave request is invalid for this employee or leave type.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.leave.created", "leave_request", id,
            new { employeeId, leaveTypeId, startDate, endDate, request.RequestedDays }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetLeaveRequestAsync(user, context, id, cancellationToken);
    }

    public Task<LeaveRequestRecord> SubmitLeaveRequestAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string requestId,
        LeaveActionRequest request,
        CancellationToken cancellationToken = default) =>
        TransitionLeaveAsync(
            user, context, requestId, request, "draft", "submitted",
            requiresManager: false, cancellationToken);

    public Task<LeaveRequestRecord> ApproveLeaveRequestAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string requestId,
        LeaveActionRequest request,
        CancellationToken cancellationToken = default) =>
        TransitionLeaveAsync(
            user, context, requestId, request, "submitted", "approved",
            requiresManager: true, cancellationToken);

    public Task<LeaveRequestRecord> RejectLeaveRequestAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string requestId,
        LeaveActionRequest request,
        CancellationToken cancellationToken = default) =>
        TransitionLeaveAsync(
            user, context, requestId, request, "submitted", "rejected",
            requiresManager: true, cancellationToken);

    private async Task<LeaveRequestRecord> TransitionLeaveAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string requestId,
        LeaveActionRequest request,
        string expectedStatus,
        string newStatus,
        bool requiresManager,
        CancellationToken cancellationToken)
    {
        string id = NormalizeId(requestId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation("invalid_leave_version", "The leave request version is invalid.");
        }
        string decisionNotes = OptionalText(request.DecisionNotes, 1000);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (requiresManager)
        {
            await RequireManagerAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        }
        else
        {
            await RequireReadAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE hrm_leave_requests
        SET status = $newStatus,
            version = version + 1,
            updated_at_utc = $now,
            submitted_at_utc = CASE WHEN $newStatus = 'submitted' THEN $now ELSE submitted_at_utc END,
            decided_by_user_id = CASE WHEN $isDecision = 1 THEN $userId ELSE decided_by_user_id END,
            decision_notes = CASE WHEN $isDecision = 1 THEN $decisionNotes ELSE decision_notes END,
            decided_at_utc = CASE WHEN $isDecision = 1 THEN $now ELSE decided_at_utc END
        WHERE id = $id
          AND organization_id = $organizationId
          AND status = $expectedStatus
          AND version = $expectedVersion
          AND EXISTS
          (
              SELECT 1 FROM hrm_employee_shop_assignments AS assignment
              WHERE assignment.employee_id = hrm_leave_requests.employee_id
                AND assignment.shop_id = $shopId
                AND assignment.is_active = 1
          );
        """;
        command.Parameters.AddWithValue("$newStatus", newStatus);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$isDecision", requiresManager ? 1 : 0);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$decisionNotes", decisionNotes);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$expectedStatus", expectedStatus);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("leave_request_changed", "The leave request changed or cannot enter the requested state.");
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("leave_overlap", "The approved leave overlaps another approved leave request.");
        }
        await WriteAuditAsync(
            connection, transaction, user, $"hrm.leave.{newStatus}", "leave_request", id,
            new { previousStatus = expectedStatus, newStatus, previousVersion = request.ExpectedVersion },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetLeaveRequestAsync(user, context, id, cancellationToken);
    }

    private async Task<AttendanceRecord> GetAttendanceAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string attendanceId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = AttendanceSelectSql +
            " WHERE attendance.id = $id AND attendance.organization_id = $organizationId LIMIT 1;";
        command.Parameters.AddWithValue("$id", attendanceId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        IReadOnlyList<AttendanceRecord> records = await ReadAttendanceAsync(command, cancellationToken);
        if (records.Count != 1)
        {
            throw NotFound("attendance_not_found", "The attendance entry could not be found.");
        }
        return records[0];
    }

    private async Task<LeaveRequestRecord> GetLeaveRequestAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string leaveRequestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = LeaveSelectSql +
            " WHERE request.id = $id AND request.organization_id = $organizationId LIMIT 1;";
        command.Parameters.AddWithValue("$id", leaveRequestId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        IReadOnlyList<LeaveRequestRecord> records =
            await ReadLeaveRequestsAsync(command, cancellationToken);
        if (records.Count != 1)
        {
            throw NotFound("leave_request_not_found", "The leave request could not be found.");
        }
        return records[0];
    }

    private static async Task RequireEmployeeInShopAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string employeeId,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COUNT(1)
        FROM hrm_employees AS employee
        WHERE employee.id = $employeeId
          AND employee.organization_id = $organizationId
          AND ($activeOnly = 0 OR employee.status IN ('active','probation','on_leave'))
          AND
          (
              employee.home_shop_id = $shopId
              OR EXISTS
              (
                  SELECT 1 FROM hrm_employee_shop_assignments AS assignment
                  WHERE assignment.employee_id = employee.id
                    AND assignment.shop_id = $shopId
                    AND assignment.is_active = 1
              )
          );
        """;
        command.Parameters.AddWithValue("$employeeId", employeeId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$activeOnly", activeOnly ? 1 : 0);
        int count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
        if (count != 1)
        {
            throw Conflict("employee_shop_unavailable", "The employee is unavailable for this branch.");
        }
    }

    private static async Task ValidateLeaveTypeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string leaveTypeId,
        string attachmentReference,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT requires_attachment
        FROM hrm_leave_types
        WHERE id = $id
          AND organization_id = $organizationId
          AND is_active = 1
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", leaveTypeId);
        command.Parameters.AddWithValue("$organizationId", organizationId);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            throw Conflict("leave_type_unavailable", "The leave type is unavailable.");
        }
        if (Convert.ToInt32(result) == 1 && attachmentReference.Length == 0)
        {
            throw Validation("leave_attachment_required", "This leave type requires an attachment reference.");
        }
    }

    private static async Task<int> GetStandardDailyMinutesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string employeeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT standard_hours_per_week FROM hrm_employees WHERE id = $employeeId;";
        command.Parameters.AddWithValue("$employeeId", employeeId);
        double hours = Convert.ToDouble(
            await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return checked((int)Math.Round(hours * 60 / 5));
    }

    private static string NormalizeTime(string? value, string errorCode)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (!TimeOnly.TryParseExact(
                normalized,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw Validation(errorCode, "Use a valid time in HH:mm format.");
        }
        return normalized;
    }

    private static WorkScheduleRecord ReadWorkSchedule(SqliteDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetInt32(8),
            reader.GetString(9), reader.GetString(10), reader.GetInt32(11),
            DateTimeOffset.Parse(reader.GetString(12)),
            DateTimeOffset.Parse(reader.GetString(13)));

    private static async Task<IReadOnlyList<AttendanceRecord>> ReadAttendanceAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var records = new List<AttendanceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new AttendanceRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
                reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.GetInt32(11), reader.GetString(12), reader.GetString(13),
                reader.GetString(14), reader.GetInt32(15), reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                DateTimeOffset.Parse(reader.GetString(18)),
                DateTimeOffset.Parse(reader.GetString(19)),
                reader.IsDBNull(20) ? null : DateTimeOffset.Parse(reader.GetString(20))));
        }
        return records;
    }

    private static async Task<IReadOnlyList<LeaveRequestRecord>> ReadLeaveRequestsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var records = new List<LeaveRequestRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new LeaveRequestRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetDouble(9), reader.GetString(10), reader.GetString(11),
                reader.GetString(12), reader.GetInt32(13), reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetString(16),
                DateTimeOffset.Parse(reader.GetString(17)),
                DateTimeOffset.Parse(reader.GetString(18)),
                reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19)),
                reader.IsDBNull(20) ? null : DateTimeOffset.Parse(reader.GetString(20))));
        }
        return records;
    }

    private const string AttendanceSelectSql =
    """
    SELECT attendance.id, attendance.shop_id, shop.code, attendance.employee_id,
           employee.employee_number, employee.first_name || ' ' || employee.last_name,
           attendance.work_date, attendance.clock_in_utc, attendance.clock_out_utc,
           attendance.break_minutes, attendance.worked_minutes,
           attendance.overtime_minutes, attendance.status, attendance.source,
           attendance.notes, attendance.version, attendance.created_by_user_id,
           attendance.approved_by_user_id, attendance.created_at_utc,
           attendance.updated_at_utc, attendance.approved_at_utc
    FROM hrm_attendance_entries AS attendance
    INNER JOIN shops AS shop ON shop.id = attendance.shop_id
    INNER JOIN hrm_employees AS employee ON employee.id = attendance.employee_id
    """;

    private const string LeaveSelectSql =
    """
    SELECT request.id, request.employee_id, employee.employee_number,
           employee.first_name || ' ' || employee.last_name,
           request.leave_type_id, leave_type.code, leave_type.name,
           request.start_date, request.end_date, request.requested_days,
           request.reason, request.attachment_reference, request.status,
           request.version, request.requested_by_user_id, request.decided_by_user_id,
           request.decision_notes, request.created_at_utc, request.updated_at_utc,
           request.submitted_at_utc, request.decided_at_utc
    FROM hrm_leave_requests AS request
    INNER JOIN hrm_employees AS employee ON employee.id = request.employee_id
    INNER JOIN hrm_leave_types AS leave_type ON leave_type.id = request.leave_type_id
    """;
}
