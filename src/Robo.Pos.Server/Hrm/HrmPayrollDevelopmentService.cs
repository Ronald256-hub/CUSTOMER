using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Hrm;

public sealed partial class HrmService
{
    public async Task<IReadOnlyList<PayrollPeriodRecord>> ListPayrollPeriodsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        int limit = Math.Clamp(requestedLimit, 1, 200);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT period.id, period.organization_id, period.name, period.start_date,
               period.end_date, period.pay_date, period.status, period.version,
               COUNT(entry.id), COALESCE(SUM(entry.gross_pay_minor), 0),
               COALESCE(SUM(entry.deduction_minor), 0),
               COALESCE(SUM(entry.net_pay_minor), 0),
               period.created_at_utc, period.updated_at_utc,
               period.approved_at_utc, period.closed_at_utc
        FROM hrm_payroll_periods AS period
        LEFT JOIN hrm_payroll_entries AS entry ON entry.payroll_period_id = period.id
        WHERE period.organization_id = $organizationId
        GROUP BY period.id
        ORDER BY period.start_date DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$limit", limit);
        var periods = new List<PayrollPeriodRecord>();
        var headers = new List<PayrollHeader>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                headers.Add(new PayrollHeader(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetString(6), reader.GetInt32(7), reader.GetInt64(8),
                    reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11),
                    DateTimeOffset.Parse(reader.GetString(12)),
                    DateTimeOffset.Parse(reader.GetString(13)),
                    reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)),
                    reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15))));
            }
        }
        foreach (PayrollHeader header in headers)
        {
            IReadOnlyList<PayrollEntryRecord> entries =
                await ReadPayrollEntriesAsync(connection, header.Id, cancellationToken);
            periods.Add(ToPayrollRecord(header, entries));
        }
        return periods;
    }

    public async Task<PayrollPeriodRecord> CreatePayrollPeriodAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreatePayrollPeriodRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string name = RequiredText(request.Name, 120, "payroll_period_name_required", "Enter the payroll period name.");
        string startDate = NormalizeDate(request.StartDate, "invalid_payroll_start_date");
        string endDate = NormalizeDate(request.EndDate, "invalid_payroll_end_date");
        string payDate = NormalizeDate(request.PayDate, "invalid_pay_date");
        if (string.CompareOrdinal(endDate, startDate) < 0 || string.CompareOrdinal(payDate, endDate) < 0)
        {
            throw Validation("invalid_payroll_dates", "The payroll period dates are invalid.");
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
        INSERT INTO hrm_payroll_periods
        (id, organization_id, name, start_date, end_date, pay_date, status,
         version, created_by_user_id, approved_by_user_id, created_at_utc,
         updated_at_utc, approved_at_utc, closed_at_utc)
        VALUES
        ($id, $organizationId, $name, $startDate, $endDate, $payDate, 'draft',
         1, $userId, NULL, $now, $now, NULL, NULL);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$startDate", startDate);
        command.Parameters.AddWithValue("$endDate", endDate);
        command.Parameters.AddWithValue("$payDate", payDate);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("payroll_period_exists", "A payroll period already exists for these dates.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.payroll.created", "payroll_period", id,
            new { name, startDate, endDate, payDate }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListPayrollPeriodsAsync(user, context, 200, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<PayrollPeriodRecord> CalculatePayrollAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string payrollPeriodId,
        CalculatePayrollRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string id = NormalizeId(payrollPeriodId);
        if (request.ExpectedVersion < 1 || request.DefaultAllowanceMinor < 0 ||
            request.DefaultDeductionMinor < 0 || request.OvertimeRateMinorPerHour < 0)
        {
            throw Validation("invalid_payroll_calculation", "The payroll calculation inputs are invalid.");
        }
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        PayrollPeriodState period = await RequirePayrollPeriodAsync(
            connection, transaction, context.OrganizationId, id, "draft",
            request.ExpectedVersion, cancellationToken);
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM hrm_payroll_entries WHERE payroll_period_id = $id;";
            clear.Parameters.AddWithValue("$id", id);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        IReadOnlyList<PayrollEmployeeState> employees = await ReadPayrollEmployeesAsync(
            connection, transaction, context.OrganizationId, period.StartDate,
            period.EndDate, cancellationToken);
        if (employees.Count == 0)
        {
            throw Conflict("payroll_has_no_employees", "No eligible employees exist for this payroll period.");
        }
        DateOnly start = DateOnly.Parse(period.StartDate);
        DateOnly end = DateOnly.Parse(period.EndDate);
        int calendarDays = end.DayNumber - start.DayNumber + 1;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (PayrollEmployeeState employee in employees)
        {
            long basePay = employee.PayFrequency switch
            {
                "monthly" => employee.BaseSalaryMinor,
                "weekly" => checked(employee.BaseSalaryMinor * Math.Max(1, (calendarDays + 6) / 7)),
                "daily" => checked(employee.BaseSalaryMinor * employee.AttendanceDays),
                "hourly" => checked(employee.BaseSalaryMinor * employee.WorkedMinutes / 60),
                _ => throw new InvalidOperationException("Unsupported pay frequency.")
            };
            long overtimePay = checked(
                request.OvertimeRateMinorPerHour * employee.OvertimeMinutes / 60);
            long grossPay = checked(basePay + overtimePay + request.DefaultAllowanceMinor);
            if (request.DefaultDeductionMinor > grossPay)
            {
                throw Conflict(
                    "payroll_negative_net_pay",
                    $"Default deductions exceed gross pay for {employee.EmployeeName}.");
            }
            long netPay = grossPay - request.DefaultDeductionMinor;
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
            """
            INSERT INTO hrm_payroll_entries
            (id, payroll_period_id, employee_id, base_pay_minor,
             overtime_pay_minor, allowance_minor, deduction_minor,
             gross_pay_minor, net_pay_minor, worked_minutes,
             overtime_minutes, notes, created_at_utc)
            VALUES
            ($id, $payrollPeriodId, $employeeId, $basePay,
             $overtimePay, $allowance, $deduction, $grossPay, $netPay,
             $workedMinutes, $overtimeMinutes, $notes, $now);
            """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$payrollPeriodId", id);
            insert.Parameters.AddWithValue("$employeeId", employee.EmployeeId);
            insert.Parameters.AddWithValue("$basePay", basePay);
            insert.Parameters.AddWithValue("$overtimePay", overtimePay);
            insert.Parameters.AddWithValue("$allowance", request.DefaultAllowanceMinor);
            insert.Parameters.AddWithValue("$deduction", request.DefaultDeductionMinor);
            insert.Parameters.AddWithValue("$grossPay", grossPay);
            insert.Parameters.AddWithValue("$netPay", netPay);
            insert.Parameters.AddWithValue("$workedMinutes", employee.WorkedMinutes);
            insert.Parameters.AddWithValue("$overtimeMinutes", employee.OvertimeMinutes);
            insert.Parameters.AddWithValue("$notes", "System-calculated payroll foundation entry");
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE hrm_payroll_periods
            SET status = 'calculated',
                version = version + 1,
                updated_at_utc = $now
            WHERE id = $id
              AND status = 'draft'
              AND version = $expectedVersion;
            """;
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("payroll_changed", "The payroll period changed during calculation.");
            }
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.payroll.calculated", "payroll_period", id,
            new
            {
                employeeCount = employees.Count,
                request.DefaultAllowanceMinor,
                request.DefaultDeductionMinor,
                request.OvertimeRateMinorPerHour
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListPayrollPeriodsAsync(user, context, 200, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<PayrollPeriodRecord> ApprovePayrollAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string payrollPeriodId,
        PayrollActionRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string id = NormalizeId(payrollPeriodId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation("invalid_payroll_version", "The payroll version is invalid.");
        }
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE hrm_payroll_periods
        SET status = 'approved',
            approved_by_user_id = $userId,
            approved_at_utc = $now,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $id
          AND organization_id = $organizationId
          AND status = 'calculated'
          AND version = $expectedVersion
          AND EXISTS
          (SELECT 1 FROM hrm_payroll_entries WHERE payroll_period_id = $id);
        """;
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("payroll_changed", "Only the current calculated payroll can be approved.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.payroll.approved", "payroll_period", id,
            new { previousVersion = request.ExpectedVersion }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListPayrollPeriodsAsync(user, context, 200, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<IReadOnlyList<PerformanceReviewRecord>> ListPerformanceReviewsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? employeeId,
        CancellationToken cancellationToken = default)
    {
        string employee = string.IsNullOrWhiteSpace(employeeId) ? string.Empty : NormalizeId(employeeId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT review.id, review.employee_id,
               employee.first_name || ' ' || employee.last_name,
               review.reviewer_user_id, reviewer.display_name,
               review.review_period_start, review.review_period_end,
               review.goals, review.achievements, review.improvement_areas,
               review.overall_rating, review.status, review.version,
               review.created_at_utc, review.updated_at_utc, review.completed_at_utc
        FROM hrm_performance_reviews AS review
        INNER JOIN hrm_employees AS employee ON employee.id = review.employee_id
        INNER JOIN users AS reviewer ON reviewer.id = review.reviewer_user_id
        WHERE review.organization_id = $organizationId
          AND ($employeeId = '' OR review.employee_id = $employeeId)
        ORDER BY review.review_period_end DESC;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$employeeId", employee);
        var records = new List<PerformanceReviewRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadPerformanceReview(reader));
        }
        return records;
    }

    public async Task<PerformanceReviewRecord> CreatePerformanceReviewAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreatePerformanceReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        string employeeId = NormalizeId(request.EmployeeId);
        string startDate = NormalizeDate(request.ReviewPeriodStart, "invalid_review_start_date");
        string endDate = NormalizeDate(request.ReviewPeriodEnd, "invalid_review_end_date");
        if (string.CompareOrdinal(endDate, startDate) < 0)
        {
            throw Validation("invalid_review_period", "The performance review period is invalid.");
        }
        string goals = OptionalText(request.Goals, 4000);
        string achievements = OptionalText(request.Achievements, 4000);
        string improvementAreas = OptionalText(request.ImprovementAreas, 4000);
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
        INSERT INTO hrm_performance_reviews
        (id, organization_id, employee_id, reviewer_user_id,
         review_period_start, review_period_end, goals, achievements,
         improvement_areas, overall_rating, status, version,
         created_at_utc, updated_at_utc, completed_at_utc)
        VALUES
        ($id, $organizationId, $employeeId, $reviewerUserId,
         $startDate, $endDate, $goals, $achievements,
         $improvementAreas, NULL, 'draft', 1, $now, $now, NULL);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$employeeId", employeeId);
        command.Parameters.AddWithValue("$reviewerUserId", user.Id);
        command.Parameters.AddWithValue("$startDate", startDate);
        command.Parameters.AddWithValue("$endDate", endDate);
        command.Parameters.AddWithValue("$goals", goals);
        command.Parameters.AddWithValue("$achievements", achievements);
        command.Parameters.AddWithValue("$improvementAreas", improvementAreas);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await WriteAuditAsync(
            connection, transaction, user, "hrm.performance.created", "performance_review", id,
            new { employeeId, startDate, endDate }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListPerformanceReviewsAsync(user, context, employeeId, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<PerformanceReviewRecord> CompletePerformanceReviewAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string reviewId,
        CompletePerformanceReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(reviewId);
        if (request.ExpectedVersion < 1 || request.OverallRating is < 1 or > 5)
        {
            throw Validation("invalid_review_completion", "The review version or rating is invalid.");
        }
        string achievements = RequiredText(
            request.Achievements, 4000, "review_achievements_required", "Enter the review achievements.");
        string improvementAreas = OptionalText(request.ImprovementAreas, 4000);
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
        UPDATE hrm_performance_reviews
        SET achievements = $achievements,
            improvement_areas = $improvementAreas,
            overall_rating = $rating,
            status = 'completed',
            completed_at_utc = $now,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $id
          AND organization_id = $organizationId
          AND reviewer_user_id = $userId
          AND status = 'draft'
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$achievements", achievements);
        command.Parameters.AddWithValue("$improvementAreas", improvementAreas);
        command.Parameters.AddWithValue("$rating", request.OverallRating);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("review_changed", "Only the assigned reviewer can complete the current draft review.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.performance.completed", "performance_review", id,
            new { request.OverallRating, previousVersion = request.ExpectedVersion }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListPerformanceReviewsAsync(user, context, null, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<IReadOnlyList<TrainingRecord>> ListTrainingRecordsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? employeeId,
        CancellationToken cancellationToken = default)
    {
        string employee = string.IsNullOrWhiteSpace(employeeId) ? string.Empty : NormalizeId(employeeId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT training.id, training.employee_id,
               employee.first_name || ' ' || employee.last_name,
               training.title, training.provider, training.start_date,
               training.end_date, training.expiry_date, training.cost_minor,
               training.status, training.certificate_reference, training.notes,
               training.version, training.created_at_utc, training.updated_at_utc,
               training.completed_at_utc
        FROM hrm_training_records AS training
        INNER JOIN hrm_employees AS employee ON employee.id = training.employee_id
        WHERE training.organization_id = $organizationId
          AND ($employeeId = '' OR training.employee_id = $employeeId)
        ORDER BY training.start_date DESC;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$employeeId", employee);
        var records = new List<TrainingRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadTrainingRecord(reader));
        }
        return records;
    }

    public async Task<TrainingRecord> CreateTrainingRecordAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateTrainingRecordRequest request,
        CancellationToken cancellationToken = default)
    {
        string employeeId = NormalizeId(request.EmployeeId);
        string title = RequiredText(request.Title, 200, "training_title_required", "Enter the training title.");
        string provider = OptionalText(request.Provider, 200);
        string startDate = NormalizeDate(request.StartDate, "invalid_training_start_date");
        string endDate = NormalizeDate(request.EndDate, "invalid_training_end_date", optional: true);
        string expiryDate = NormalizeDate(request.ExpiryDate, "invalid_training_expiry_date", optional: true);
        if ((endDate.Length > 0 && string.CompareOrdinal(endDate, startDate) < 0) ||
            (expiryDate.Length > 0 && string.CompareOrdinal(expiryDate, startDate) < 0) ||
            request.CostMinor < 0)
        {
            throw Validation("invalid_training_record", "The training dates or cost are invalid.");
        }
        string notes = OptionalText(request.Notes, 2000);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireManagerAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        await RequireEmployeeInShopAsync(
            connection, transaction, context, employeeId, activeOnly: false, cancellationToken);
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO hrm_training_records
        (id, organization_id, employee_id, title, provider, start_date,
         end_date, expiry_date, cost_minor, status, certificate_reference,
         notes, version, created_by_user_id, created_at_utc,
         updated_at_utc, completed_at_utc)
        VALUES
        ($id, $organizationId, $employeeId, $title, $provider, $startDate,
         $endDate, $expiryDate, $costMinor, 'planned', '',
         $notes, 1, $userId, $now, $now, NULL);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$employeeId", employeeId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$startDate", startDate);
        command.Parameters.AddWithValue("$endDate", endDate.Length == 0 ? DBNull.Value : endDate);
        command.Parameters.AddWithValue("$expiryDate", expiryDate.Length == 0 ? DBNull.Value : expiryDate);
        command.Parameters.AddWithValue("$costMinor", request.CostMinor);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await WriteAuditAsync(
            connection, transaction, user, "hrm.training.created", "training", id,
            new { employeeId, title, startDate, request.CostMinor }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListTrainingRecordsAsync(user, context, employeeId, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<TrainingRecord> CompleteTrainingRecordAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string trainingId,
        CompleteTrainingRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(trainingId);
        string status = request.Status.Trim().ToLowerInvariant();
        if (request.ExpectedVersion < 1 || status is not ("completed" or "failed"))
        {
            throw Validation("invalid_training_completion", "The training completion status or version is invalid.");
        }
        string certificate = OptionalText(request.CertificateReference, 500);
        string notes = OptionalText(request.Notes, 2000);
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
        UPDATE hrm_training_records
        SET status = $status,
            certificate_reference = $certificate,
            notes = CASE WHEN $notes = '' THEN notes ELSE $notes END,
            completed_at_utc = $now,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $id
          AND organization_id = $organizationId
          AND status IN ('planned','in_progress')
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$certificate", certificate);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("training_changed", "The training record changed or cannot be completed.");
        }
        await WriteAuditAsync(
            connection, transaction, user, $"hrm.training.{status}", "training", id,
            new { certificate, previousVersion = request.ExpectedVersion }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListTrainingRecordsAsync(user, context, null, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<IReadOnlyList<DisciplinaryCaseRecord>> ListDisciplinaryCasesAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? employeeId,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string employee = string.IsNullOrWhiteSpace(employeeId) ? string.Empty : NormalizeId(employeeId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT disciplinary.id, disciplinary.case_number, disciplinary.employee_id,
               employee.first_name || ' ' || employee.last_name,
               disciplinary.incident_date, disciplinary.category,
               disciplinary.severity, disciplinary.description,
               disciplinary.action_taken, disciplinary.status,
               disciplinary.version, disciplinary.opened_by_user_id,
               disciplinary.resolved_by_user_id, disciplinary.created_at_utc,
               disciplinary.updated_at_utc, disciplinary.resolved_at_utc
        FROM hrm_disciplinary_cases AS disciplinary
        INNER JOIN hrm_employees AS employee ON employee.id = disciplinary.employee_id
        WHERE disciplinary.organization_id = $organizationId
          AND ($employeeId = '' OR disciplinary.employee_id = $employeeId)
        ORDER BY disciplinary.incident_date DESC, disciplinary.case_number;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$employeeId", employee);
        var records = new List<DisciplinaryCaseRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadDisciplinaryCase(reader));
        }
        return records;
    }

    public async Task<DisciplinaryCaseRecord> CreateDisciplinaryCaseAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateDisciplinaryCaseRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string employeeId = NormalizeId(request.EmployeeId);
        string incidentDate = NormalizeDate(request.IncidentDate, "invalid_incident_date");
        string category = RequiredText(request.Category, 120, "disciplinary_category_required", "Enter the disciplinary category.");
        string severity = request.Severity.Trim().ToLowerInvariant();
        if (severity is not ("minor" or "moderate" or "major" or "critical"))
        {
            throw Validation("invalid_disciplinary_severity", "The disciplinary severity is invalid.");
        }
        string description = RequiredText(
            request.Description, 4000, "disciplinary_description_required", "Enter the incident description.");
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireEmployeeInShopAsync(
            connection, transaction, context, employeeId, activeOnly: false, cancellationToken);
        string id = Guid.NewGuid().ToString("N");
        string caseNumber = $"HR-{DateTime.UtcNow:yyyyMMdd}-{id[..6].ToUpperInvariant()}";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO hrm_disciplinary_cases
        (id, organization_id, employee_id, case_number, incident_date,
         category, severity, description, action_taken, status, version,
         opened_by_user_id, resolved_by_user_id, created_at_utc,
         updated_at_utc, resolved_at_utc)
        VALUES
        ($id, $organizationId, $employeeId, $caseNumber, $incidentDate,
         $category, $severity, $description, '', 'open', 1,
         $userId, NULL, $now, $now, NULL);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$employeeId", employeeId);
        command.Parameters.AddWithValue("$caseNumber", caseNumber);
        command.Parameters.AddWithValue("$incidentDate", incidentDate);
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await WriteAuditAsync(
            connection, transaction, user, "hrm.disciplinary.created", "disciplinary_case", id,
            new { caseNumber, employeeId, incidentDate, category, severity }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListDisciplinaryCasesAsync(user, context, employeeId, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<DisciplinaryCaseRecord> ResolveDisciplinaryCaseAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string caseId,
        ResolveDisciplinaryCaseRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string id = NormalizeId(caseId);
        string status = request.Status.Trim().ToLowerInvariant();
        if (request.ExpectedVersion < 1 || status is not ("resolved" or "dismissed"))
        {
            throw Validation("invalid_disciplinary_resolution", "The disciplinary resolution is invalid.");
        }
        string actionTaken = RequiredText(
            request.ActionTaken, 4000, "disciplinary_action_required", "Enter the action or resolution.");
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE hrm_disciplinary_cases
        SET status = $status,
            action_taken = $actionTaken,
            resolved_by_user_id = $userId,
            resolved_at_utc = $now,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $id
          AND organization_id = $organizationId
          AND status IN ('open','under_review','appealed')
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$actionTaken", actionTaken);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("disciplinary_changed", "The disciplinary case changed or cannot be resolved.");
        }
        await WriteAuditAsync(
            connection, transaction, user, $"hrm.disciplinary.{status}", "disciplinary_case", id,
            new { actionTaken, previousVersion = request.ExpectedVersion }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListDisciplinaryCasesAsync(user, context, null, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<HrmDashboardRecord> GetDashboardAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            (SELECT COUNT(1) FROM hrm_employees AS employee
             WHERE employee.organization_id = $organizationId
               AND employee.status = 'active'
               AND EXISTS (SELECT 1 FROM hrm_employee_shop_assignments AS assignment
                           WHERE assignment.employee_id = employee.id
                             AND assignment.shop_id = $shopId AND assignment.is_active = 1)),
            (SELECT COUNT(1) FROM hrm_employees AS employee
             WHERE employee.organization_id = $organizationId AND employee.status = 'probation'),
            (SELECT COUNT(1) FROM hrm_employees AS employee
             WHERE employee.organization_id = $organizationId AND employee.status = 'on_leave'),
            (SELECT COUNT(1) FROM hrm_attendance_entries
             WHERE organization_id = $organizationId AND shop_id = $shopId AND status = 'open'),
            (SELECT COUNT(1) FROM hrm_attendance_entries
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND work_date = date('now')),
            (SELECT COUNT(1) FROM hrm_leave_requests AS request
             WHERE request.organization_id = $organizationId AND request.status = 'submitted'
               AND EXISTS (SELECT 1 FROM hrm_employee_shop_assignments AS assignment
                           WHERE assignment.employee_id = request.employee_id
                             AND assignment.shop_id = $shopId AND assignment.is_active = 1)),
            (SELECT COUNT(1) FROM hrm_leave_requests AS request
             WHERE request.organization_id = $organizationId AND request.status = 'approved'
               AND date('now') BETWEEN request.start_date AND request.end_date),
            (SELECT COUNT(1) FROM hrm_work_schedules
             WHERE organization_id = $organizationId AND shop_id = $shopId
               AND status = 'published' AND work_date BETWEEN date('now') AND date('now','+7 days')),
            (SELECT COUNT(1) FROM hrm_disciplinary_cases
             WHERE organization_id = $organizationId AND status IN ('open','under_review','appealed')),
            (SELECT COUNT(1) FROM hrm_training_records
             WHERE organization_id = $organizationId AND status = 'completed'
               AND expiry_date BETWEEN date('now') AND date('now','+90 days')),
            (SELECT COUNT(1) FROM hrm_payroll_periods
             WHERE organization_id = $organizationId AND status IN ('draft','calculated')),
            COALESCE((SELECT SUM(entry.net_pay_minor)
                      FROM hrm_payroll_entries AS entry
                      INNER JOIN hrm_payroll_periods AS period
                          ON period.id = entry.payroll_period_id
                      WHERE period.organization_id = $organizationId
                        AND period.status = 'approved'
                      ORDER BY period.end_date DESC LIMIT 1), 0);
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The HRM dashboard could not be calculated.");
        }
        return new HrmDashboardRecord(
            context.OrganizationId, context.ShopId, context.ShopCode,
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
            reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11));
    }

    private static async Task<PayrollPeriodState> RequirePayrollPeriodAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string id,
        string status,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT start_date, end_date
        FROM hrm_payroll_periods
        WHERE id = $id
          AND organization_id = $organizationId
          AND status = $status
          AND version = $version
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Conflict("payroll_changed", "The payroll period changed or is unavailable.");
        }
        return new PayrollPeriodState(reader.GetString(0), reader.GetString(1));
    }

    private static async Task<IReadOnlyList<PayrollEmployeeState>> ReadPayrollEmployeesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string startDate,
        string endDate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT employee.id, employee.employee_number,
               employee.first_name || ' ' || employee.last_name,
               employee.base_salary_minor, employee.pay_frequency,
               COUNT(DISTINCT CASE WHEN attendance.status IN ('completed','approved','corrected')
                              THEN attendance.work_date END),
               COALESCE(SUM(CASE WHEN attendance.status IN ('completed','approved','corrected')
                                 THEN attendance.worked_minutes ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN attendance.status IN ('completed','approved','corrected')
                                 THEN attendance.overtime_minutes ELSE 0 END), 0)
        FROM hrm_employees AS employee
        LEFT JOIN hrm_attendance_entries AS attendance
            ON attendance.employee_id = employee.id
           AND attendance.work_date BETWEEN $startDate AND $endDate
        WHERE employee.organization_id = $organizationId
          AND employee.status IN ('active','probation','on_leave')
          AND employee.hire_date <= $endDate
          AND (employee.end_date IS NULL OR employee.end_date >= $startDate)
        GROUP BY employee.id
        ORDER BY employee.employee_number;
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$startDate", startDate);
        command.Parameters.AddWithValue("$endDate", endDate);
        var records = new List<PayrollEmployeeState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new PayrollEmployeeState(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5),
                reader.GetInt32(6), reader.GetInt32(7)));
        }
        return records;
    }

    private static async Task<IReadOnlyList<PayrollEntryRecord>> ReadPayrollEntriesAsync(
        SqliteConnection connection,
        string payrollPeriodId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT entry.id, entry.employee_id, employee.employee_number,
               employee.first_name || ' ' || employee.last_name,
               entry.base_pay_minor, entry.overtime_pay_minor,
               entry.allowance_minor, entry.deduction_minor,
               entry.gross_pay_minor, entry.net_pay_minor,
               entry.worked_minutes, entry.overtime_minutes, entry.notes
        FROM hrm_payroll_entries AS entry
        INNER JOIN hrm_employees AS employee ON employee.id = entry.employee_id
        WHERE entry.payroll_period_id = $payrollPeriodId
        ORDER BY employee.employee_number;
        """;
        command.Parameters.AddWithValue("$payrollPeriodId", payrollPeriodId);
        var entries = new List<PayrollEntryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new PayrollEntryRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
                reader.GetInt64(9), reader.GetInt32(10), reader.GetInt32(11),
                reader.GetString(12)));
        }
        return entries;
    }

    private static PayrollPeriodRecord ToPayrollRecord(
        PayrollHeader header,
        IReadOnlyList<PayrollEntryRecord> entries) =>
        new(
            header.Id, header.OrganizationId, header.Name, header.StartDate,
            header.EndDate, header.PayDate, header.Status, header.Version,
            header.EmployeeCount, header.GrossPayMinor, header.DeductionMinor,
            header.NetPayMinor, header.CreatedAtUtc, header.UpdatedAtUtc,
            header.ApprovedAtUtc, header.ClosedAtUtc, entries);

    private static PerformanceReviewRecord ReadPerformanceReview(SqliteDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8),
            reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetInt32(10),
            reader.GetString(11), reader.GetInt32(12),
            DateTimeOffset.Parse(reader.GetString(13)),
            DateTimeOffset.Parse(reader.GetString(14)),
            reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15)));

    private static TrainingRecord ReadTrainingRecord(SqliteDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt64(8),
            reader.GetString(9), reader.GetString(10), reader.GetString(11),
            reader.GetInt32(12), DateTimeOffset.Parse(reader.GetString(13)),
            DateTimeOffset.Parse(reader.GetString(14)),
            reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15)));

    private static DisciplinaryCaseRecord ReadDisciplinaryCase(SqliteDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8),
            reader.GetString(9), reader.GetInt32(10), reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            DateTimeOffset.Parse(reader.GetString(13)),
            DateTimeOffset.Parse(reader.GetString(14)),
            reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15)));

    private sealed record PayrollPeriodState(string StartDate, string EndDate);

    private sealed record PayrollEmployeeState(
        string EmployeeId,
        string EmployeeNumber,
        string EmployeeName,
        long BaseSalaryMinor,
        string PayFrequency,
        long AttendanceDays,
        int WorkedMinutes,
        int OvertimeMinutes);

    private sealed record PayrollHeader(
        string Id,
        string OrganizationId,
        string Name,
        string StartDate,
        string EndDate,
        string PayDate,
        string Status,
        int Version,
        long EmployeeCount,
        long GrossPayMinor,
        long DeductionMinor,
        long NetPayMinor,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? ApprovedAtUtc,
        DateTimeOffset? ClosedAtUtc);
}
