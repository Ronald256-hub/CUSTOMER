using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Hrm;

public sealed partial class HrmService
{
    private static readonly HashSet<string> EmploymentTypes = new(StringComparer.Ordinal)
    {
        "permanent", "contract", "temporary", "casual", "intern", "consultant"
    };

    private static readonly HashSet<string> EmployeeStatuses = new(StringComparer.Ordinal)
    {
        "active", "probation", "suspended", "on_leave", "terminated", "resigned", "retired"
    };

    private static readonly HashSet<string> PayFrequencies = new(StringComparer.Ordinal)
    {
        "monthly", "weekly", "daily", "hourly"
    };

    private readonly DatabaseBootstrap _database;

    public HrmService(DatabaseBootstrap database)
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
        int count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
        if (count != 1)
        {
            throw Forbidden(
                "hrm_access_required",
                "You do not have access to workforce information for this branch.");
        }
    }

    private static async Task RequireManagerAccessAsync(
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
                "hrm_manager_required",
                "A branch manager or administrator is required for this workforce operation.");
        }
    }

    private static void RequireAdministrator(AuthenticatedUser user)
    {
        if (!IsAdministrator(user))
        {
            throw Forbidden(
                "hrm_administrator_required",
                "Only an administrator can perform this workforce operation.");
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

    private static string NormalizeCode(string? value, string errorCode)
    {
        string code = RequiredText(value, 30, errorCode, "Enter a valid code.")
            .ToUpperInvariant();
        if (code.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_')))
        {
            throw Validation(errorCode, "Codes may contain only letters, numbers, hyphens and underscores.");
        }
        return code;
    }

    private static string NormalizeDate(string? value, string errorCode, bool optional = false)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (optional && normalized.Length == 0)
        {
            return string.Empty;
        }
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

    private static DateTimeOffset NormalizeTimestamp(
        string? value,
        DateTimeOffset fallback,
        string errorCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset timestamp))
        {
            throw Validation(errorCode, "Use a valid ISO 8601 timestamp.");
        }
        return timestamp.ToUniversalTime();
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
            occurred_at_utc, user_id, username, event_type, entity_type,
            entity_id, success, details_json, client_ip_hash
        )
        VALUES
        (
            $occurredAtUtc, $userId, $username, $eventType, $entityType,
            $entityId, 1, $detailsJson, NULL
        );
        """;
        command.Parameters.AddWithValue("$occurredAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$detailsJson", JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DepartmentRecord>> ListDepartmentsAsync(
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
        SELECT id, organization_id, code, name, description, is_active,
               version, created_at_utc, updated_at_utc
        FROM hrm_departments
        WHERE organization_id = $organizationId
          AND ($includeInactive = 1 OR is_active = 1)
        ORDER BY name COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$includeInactive", includeInactive ? 1 : 0);
        var records = new List<DepartmentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new DepartmentRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5) == 1,
                reader.GetInt32(6), DateTimeOffset.Parse(reader.GetString(7)),
                DateTimeOffset.Parse(reader.GetString(8))));
        }
        return records;
    }

    public async Task<DepartmentRecord> CreateDepartmentAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateDepartmentRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string code = NormalizeCode(request.Code, "invalid_department_code");
        string name = RequiredText(request.Name, 120, "department_name_required", "Enter the department name.");
        string description = OptionalText(request.Description, 1000);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string id = Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO hrm_departments
        (id, organization_id, code, name, description, is_active, version,
         created_by_user_id, updated_by_user_id, created_at_utc, updated_at_utc)
        VALUES
        ($id, $organizationId, $code, $name, $description, 1, 1,
         $userId, $userId, $now, $now);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$code", code);
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
            throw Conflict("department_exists", "A department with this code or name already exists.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.department.created", "department", id,
            new { context.OrganizationId, code, name }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DepartmentRecord(
            id, context.OrganizationId, code, name, description, true, 1, now, now);
    }

    public async Task<DepartmentRecord> UpdateDepartmentAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string departmentId,
        UpdateDepartmentRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string id = NormalizeId(departmentId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation("invalid_department_version", "The department version is invalid.");
        }
        string name = RequiredText(request.Name, 120, "department_name_required", "Enter the department name.");
        string description = OptionalText(request.Description, 1000);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE hrm_departments
        SET name = $name,
            description = $description,
            is_active = $isActive,
            version = version + 1,
            updated_by_user_id = $userId,
            updated_at_utc = $now
        WHERE id = $id
          AND organization_id = $organizationId
          AND version = $expectedVersion;
        """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$isActive", request.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("department_changed", "The department changed or is unavailable.");
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("department_exists", "A department with this name already exists.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.department.updated", "department", id,
            new { name, request.IsActive, previousVersion = request.ExpectedVersion }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListDepartmentsAsync(user, context, true, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<IReadOnlyList<PositionRecord>> ListPositionsAsync(
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
        SELECT position.id, position.organization_id, position.department_id,
               department.name, position.code, position.title, position.description,
               position.grade, position.is_active, position.version,
               position.created_at_utc, position.updated_at_utc
        FROM hrm_positions AS position
        INNER JOIN hrm_departments AS department ON department.id = position.department_id
        WHERE position.organization_id = $organizationId
          AND ($includeInactive = 1 OR position.is_active = 1)
        ORDER BY department.name COLLATE NOCASE, position.title COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$includeInactive", includeInactive ? 1 : 0);
        var records = new List<PositionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new PositionRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt32(8) == 1,
                reader.GetInt32(9), DateTimeOffset.Parse(reader.GetString(10)),
                DateTimeOffset.Parse(reader.GetString(11))));
        }
        return records;
    }

    public async Task<PositionRecord> CreatePositionAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreatePositionRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string departmentId = NormalizeId(request.DepartmentId);
        string code = NormalizeCode(request.Code, "invalid_position_code");
        string title = RequiredText(request.Title, 120, "position_title_required", "Enter the job title.");
        string description = OptionalText(request.Description, 1000);
        string grade = OptionalText(request.Grade, 50);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string id = Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO hrm_positions
        (id, organization_id, department_id, code, title, description, grade,
         is_active, version, created_by_user_id, updated_by_user_id,
         created_at_utc, updated_at_utc)
        VALUES
        ($id, $organizationId, $departmentId, $code, $title, $description, $grade,
         1, 1, $userId, $userId, $now, $now);
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$departmentId", departmentId);
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$grade", grade);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("position_invalid", "The position code exists or its department is invalid.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "hrm.position.created", "position", id,
            new { context.OrganizationId, departmentId, code, title }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListPositionsAsync(user, context, true, cancellationToken))
            .Single(record => record.Id == id);
    }

    public async Task<IReadOnlyList<EmployeeRecord>> ListEmployeesAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? search,
        string? status,
        bool includeAllShops,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        string normalizedSearch = OptionalText(search, 150);
        string normalizedStatus = status?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedStatus.Length > 0 && !EmployeeStatuses.Contains(normalizedStatus))
        {
            throw Validation("invalid_employee_status", "The employee status is invalid.");
        }
        int limit = Math.Clamp(requestedLimit, 1, 2000);
        await using var command = connection.CreateCommand();
        command.CommandText = EmployeeSelectSql +
        """
        WHERE employee.organization_id = $organizationId
          AND ($allShops = 1 OR employee.home_shop_id = $shopId OR EXISTS
              (SELECT 1 FROM hrm_employee_shop_assignments AS assignment
               WHERE assignment.employee_id = employee.id
                 AND assignment.shop_id = $shopId
                 AND assignment.is_active = 1))
          AND ($status = '' OR employee.status = $status)
          AND ($search = '' OR employee.employee_number LIKE '%' || $search || '%' COLLATE NOCASE
               OR employee.first_name LIKE '%' || $search || '%' COLLATE NOCASE
               OR employee.last_name LIKE '%' || $search || '%' COLLATE NOCASE
               OR employee.phone LIKE '%' || $search || '%' COLLATE NOCASE
               OR employee.email LIKE '%' || $search || '%' COLLATE NOCASE)
        ORDER BY employee.last_name COLLATE NOCASE, employee.first_name COLLATE NOCASE
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$allShops", includeAllShops && IsAdministrator(user) ? 1 : 0);
        command.Parameters.AddWithValue("$status", normalizedStatus);
        command.Parameters.AddWithValue("$search", normalizedSearch);
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadEmployeesAsync(connection, command, cancellationToken);
    }

    public async Task<EmployeeRecord> GetEmployeeAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string employeeId,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(employeeId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = EmployeeSelectSql +
            " WHERE employee.id = $id AND employee.organization_id = $organizationId LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        IReadOnlyList<EmployeeRecord> records =
            await ReadEmployeesAsync(connection, command, cancellationToken);
        if (records.Count != 1)
        {
            throw NotFound("employee_not_found", "The employee could not be found.");
        }
        return records[0];
    }

    public async Task<EmployeeRecord> CreateEmployeeAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateEmployeeRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        EmployeeInput input = NormalizeEmployeeInput(request);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ValidateEmployeeReferencesAsync(
            connection, transaction, context, input.DepartmentId, input.PositionId,
            input.UserId, input.ShopIds, cancellationToken);
        string id = Guid.NewGuid().ToString("N");
        string employeeNumber = $"EMP-{id[..8].ToUpperInvariant()}";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await InsertEmployeeAsync(
            connection, transaction, context, user, id, employeeNumber, input, now,
            cancellationToken);
        await ReplaceEmployeeAssignmentsAsync(
            connection, transaction, context, user, id, input.ShopIds, input.HireDate,
            now, cancellationToken);
        await WriteAuditAsync(
            connection, transaction, user, "hrm.employee.created", "employee", id,
            new
            {
                context.OrganizationId,
                context.ShopId,
                employeeNumber,
                input.FirstName,
                input.LastName,
                input.DepartmentId,
                input.PositionId,
                input.EmploymentType,
                input.Status,
                input.BaseSalaryMinor
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetEmployeeAsync(user, context, id, cancellationToken);
    }

    public async Task<EmployeeRecord> UpdateEmployeeAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string employeeId,
        UpdateEmployeeRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user);
        string id = NormalizeId(employeeId);
        if (request.ExpectedVersion < 1)
        {
            throw Validation("invalid_employee_version", "The employee version is invalid.");
        }
        EmployeeInput input = NormalizeEmployeeInput(request);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ValidateEmployeeReferencesAsync(
            connection, transaction, context, input.DepartmentId, input.PositionId,
            input.UserId, input.ShopIds, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE hrm_employees
        SET home_shop_id = $homeShopId,
            user_id = $userId,
            department_id = $departmentId,
            position_id = $positionId,
            first_name = $firstName,
            last_name = $lastName,
            other_names = $otherNames,
            preferred_name = $preferredName,
            phone = $phone,
            email = $email,
            address = $address,
            emergency_contact_name = $emergencyContactName,
            emergency_contact_phone = $emergencyContactPhone,
            employment_type = $employmentType,
            hire_date = $hireDate,
            end_date = $endDate,
            status = $status,
            base_salary_minor = $baseSalaryMinor,
            pay_frequency = $payFrequency,
            standard_hours_per_week = $standardHoursPerWeek,
            tax_number = $taxNumber,
            national_id = $nationalId,
            bank_name = $bankName,
            bank_account = $bankAccount,
            mobile_money_number = $mobileMoneyNumber,
            notes = $notes,
            version = version + 1,
            updated_by_user_id = $updatedByUserId,
            updated_at_utc = $updatedAtUtc
        WHERE id = $id
          AND organization_id = $organizationId
          AND version = $expectedVersion;
        """;
        AddEmployeeParameters(command, context, user, id, input, now);
        command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("employee_changed", "The employee changed or is unavailable.");
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("employee_invalid", "The linked user, department, position or shop assignment is invalid or already used.");
        }
        await ReplaceEmployeeAssignmentsAsync(
            connection, transaction, context, user, id, input.ShopIds, input.HireDate,
            now, cancellationToken);
        await WriteAuditAsync(
            connection, transaction, user, "hrm.employee.updated", "employee", id,
            new { input.Status, input.DepartmentId, input.PositionId, input.BaseSalaryMinor, previousVersion = request.ExpectedVersion },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetEmployeeAsync(user, context, id, cancellationToken);
    }

    private static EmployeeInput NormalizeEmployeeInput(CreateEmployeeRequest request) =>
        NormalizeEmployeeInput(
            request.DepartmentId, request.PositionId, request.UserId,
            request.FirstName, request.LastName, request.OtherNames, request.PreferredName,
            request.Phone, request.Email, request.Address, request.EmergencyContactName,
            request.EmergencyContactPhone, request.EmploymentType, request.HireDate,
            request.EndDate, request.Status, request.BaseSalaryMinor, request.PayFrequency,
            request.StandardHoursPerWeek, request.TaxNumber, request.NationalId,
            request.BankName, request.BankAccount, request.MobileMoneyNumber, request.Notes,
            request.ShopIds);

    private static EmployeeInput NormalizeEmployeeInput(UpdateEmployeeRequest request) =>
        NormalizeEmployeeInput(
            request.DepartmentId, request.PositionId, request.UserId,
            request.FirstName, request.LastName, request.OtherNames, request.PreferredName,
            request.Phone, request.Email, request.Address, request.EmergencyContactName,
            request.EmergencyContactPhone, request.EmploymentType, request.HireDate,
            request.EndDate, request.Status, request.BaseSalaryMinor, request.PayFrequency,
            request.StandardHoursPerWeek, request.TaxNumber, request.NationalId,
            request.BankName, request.BankAccount, request.MobileMoneyNumber, request.Notes,
            request.ShopIds);

    private static EmployeeInput NormalizeEmployeeInput(
        string departmentId,
        string positionId,
        string? userId,
        string firstName,
        string lastName,
        string? otherNames,
        string? preferredName,
        string? phone,
        string? email,
        string? address,
        string? emergencyContactName,
        string? emergencyContactPhone,
        string employmentType,
        string hireDate,
        string? endDate,
        string status,
        long baseSalaryMinor,
        string payFrequency,
        double standardHoursPerWeek,
        string? taxNumber,
        string? nationalId,
        string? bankName,
        string? bankAccount,
        string? mobileMoneyNumber,
        string? notes,
        IReadOnlyList<string>? shopIds)
    {
        string normalizedEmploymentType = employmentType.Trim().ToLowerInvariant();
        string normalizedStatus = status.Trim().ToLowerInvariant();
        string normalizedPayFrequency = payFrequency.Trim().ToLowerInvariant();
        if (!EmploymentTypes.Contains(normalizedEmploymentType))
        {
            throw Validation("invalid_employment_type", "The employment type is invalid.");
        }
        if (!EmployeeStatuses.Contains(normalizedStatus))
        {
            throw Validation("invalid_employee_status", "The employee status is invalid.");
        }
        if (!PayFrequencies.Contains(normalizedPayFrequency))
        {
            throw Validation("invalid_pay_frequency", "The pay frequency is invalid.");
        }
        if (baseSalaryMinor < 0 || standardHoursPerWeek <= 0 || standardHoursPerWeek > 168)
        {
            throw Validation("invalid_employee_pay", "Salary and standard hours are invalid.");
        }
        string normalizedHireDate = NormalizeDate(hireDate, "invalid_hire_date");
        string normalizedEndDate = NormalizeDate(endDate, "invalid_end_date", optional: true);
        if (normalizedEndDate.Length > 0 && string.CompareOrdinal(normalizedEndDate, normalizedHireDate) < 0)
        {
            throw Validation("invalid_end_date", "The end date cannot precede the hire date.");
        }
        IReadOnlyList<string> normalizedShops = (shopIds ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new EmployeeInput(
            NormalizeId(departmentId), NormalizeId(positionId),
            string.IsNullOrWhiteSpace(userId) ? null : NormalizeId(userId),
            RequiredText(firstName, 80, "employee_first_name_required", "Enter the first name."),
            RequiredText(lastName, 80, "employee_last_name_required", "Enter the last name."),
            OptionalText(otherNames, 120), OptionalText(preferredName, 80),
            OptionalText(phone, 50), OptionalText(email, 150), OptionalText(address, 300),
            OptionalText(emergencyContactName, 150), OptionalText(emergencyContactPhone, 50),
            normalizedEmploymentType, normalizedHireDate, normalizedEndDate,
            normalizedStatus, baseSalaryMinor, normalizedPayFrequency,
            standardHoursPerWeek, OptionalText(taxNumber, 100), OptionalText(nationalId, 100),
            OptionalText(bankName, 100), OptionalText(bankAccount, 100),
            OptionalText(mobileMoneyNumber, 50), OptionalText(notes, 2000), normalizedShops);
    }

    private static async Task ValidateEmployeeReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string departmentId,
        string positionId,
        string? userId,
        IReadOnlyList<string> shopIds,
        CancellationToken cancellationToken)
    {
        await using (var structure = connection.CreateCommand())
        {
            structure.Transaction = transaction;
            structure.CommandText =
            """
            SELECT COUNT(1)
            FROM hrm_departments AS department
            INNER JOIN hrm_positions AS position
                ON position.department_id = department.id
            WHERE department.id = $departmentId
              AND position.id = $positionId
              AND department.organization_id = $organizationId
              AND position.organization_id = $organizationId
              AND department.is_active = 1
              AND position.is_active = 1;
            """;
            structure.Parameters.AddWithValue("$departmentId", departmentId);
            structure.Parameters.AddWithValue("$positionId", positionId);
            structure.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            int count = Convert.ToInt32(
                await structure.ExecuteScalarAsync(cancellationToken));
            if (count != 1)
            {
                throw Conflict("employee_structure_invalid", "The department and position combination is invalid.");
            }
        }

        if (userId is not null)
        {
            await using var user = connection.CreateCommand();
            user.Transaction = transaction;
            user.CommandText = "SELECT COUNT(1) FROM users WHERE id = $userId AND is_active = 1;";
            user.Parameters.AddWithValue("$userId", userId);
            int count = Convert.ToInt32(
                await user.ExecuteScalarAsync(cancellationToken));
            if (count != 1)
            {
                throw Conflict("employee_user_invalid", "The linked login account is invalid.");
            }
        }

        var allShops = new HashSet<string>(shopIds, StringComparer.Ordinal)
        {
            context.ShopId
        };
        foreach (string shopId in allShops)
        {
            await using var shop = connection.CreateCommand();
            shop.Transaction = transaction;
            shop.CommandText =
            """
            SELECT COUNT(1)
            FROM shops
            WHERE id = $shopId
              AND organization_id = $organizationId
              AND is_active = 1;
            """;
            shop.Parameters.AddWithValue("$shopId", shopId);
            shop.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            int count = Convert.ToInt32(
                await shop.ExecuteScalarAsync(cancellationToken));
            if (count != 1)
            {
                throw Conflict("employee_shop_invalid", "An employee branch assignment is invalid.");
            }
        }
    }

    private static async Task InsertEmployeeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        AuthenticatedUser user,
        string id,
        string employeeNumber,
        EmployeeInput input,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO hrm_employees
        (
            id, organization_id, home_shop_id, user_id, department_id, position_id,
            employee_number, first_name, last_name, other_names, preferred_name,
            phone, email, address, emergency_contact_name, emergency_contact_phone,
            employment_type, hire_date, end_date, status, base_salary_minor,
            pay_frequency, standard_hours_per_week, tax_number, national_id,
            bank_name, bank_account, mobile_money_number, notes, version,
            created_by_user_id, updated_by_user_id, created_at_utc, updated_at_utc
        )
        VALUES
        (
            $id, $organizationId, $homeShopId, $userId, $departmentId, $positionId,
            $employeeNumber, $firstName, $lastName, $otherNames, $preferredName,
            $phone, $email, $address, $emergencyContactName, $emergencyContactPhone,
            $employmentType, $hireDate, $endDate, $status, $baseSalaryMinor,
            $payFrequency, $standardHoursPerWeek, $taxNumber, $nationalId,
            $bankName, $bankAccount, $mobileMoneyNumber, $notes, 1,
            $createdByUserId, $updatedByUserId, $createdAtUtc, $updatedAtUtc
        );
        """;
        AddEmployeeParameters(command, context, user, id, input, now);
        command.Parameters.AddWithValue("$employeeNumber", employeeNumber);
        command.Parameters.AddWithValue("$createdByUserId", user.Id);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("employee_invalid", "The linked user, department, position or shop assignment is invalid or already used.");
        }
    }

    private static void AddEmployeeParameters(
        SqliteCommand command,
        ActiveShopContextRecord context,
        AuthenticatedUser user,
        string id,
        EmployeeInput input,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$homeShopId", context.ShopId);
        command.Parameters.AddWithValue("$userId", input.UserId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$departmentId", input.DepartmentId);
        command.Parameters.AddWithValue("$positionId", input.PositionId);
        command.Parameters.AddWithValue("$firstName", input.FirstName);
        command.Parameters.AddWithValue("$lastName", input.LastName);
        command.Parameters.AddWithValue("$otherNames", input.OtherNames);
        command.Parameters.AddWithValue("$preferredName", input.PreferredName);
        command.Parameters.AddWithValue("$phone", input.Phone);
        command.Parameters.AddWithValue("$email", input.Email);
        command.Parameters.AddWithValue("$address", input.Address);
        command.Parameters.AddWithValue("$emergencyContactName", input.EmergencyContactName);
        command.Parameters.AddWithValue("$emergencyContactPhone", input.EmergencyContactPhone);
        command.Parameters.AddWithValue("$employmentType", input.EmploymentType);
        command.Parameters.AddWithValue("$hireDate", input.HireDate);
        command.Parameters.AddWithValue("$endDate", input.EndDate.Length == 0 ? DBNull.Value : input.EndDate);
        command.Parameters.AddWithValue("$status", input.Status);
        command.Parameters.AddWithValue("$baseSalaryMinor", input.BaseSalaryMinor);
        command.Parameters.AddWithValue("$payFrequency", input.PayFrequency);
        command.Parameters.AddWithValue("$standardHoursPerWeek", input.StandardHoursPerWeek);
        command.Parameters.AddWithValue("$taxNumber", input.TaxNumber);
        command.Parameters.AddWithValue("$nationalId", input.NationalId);
        command.Parameters.AddWithValue("$bankName", input.BankName);
        command.Parameters.AddWithValue("$bankAccount", input.BankAccount);
        command.Parameters.AddWithValue("$mobileMoneyNumber", input.MobileMoneyNumber);
        command.Parameters.AddWithValue("$notes", input.Notes);
        command.Parameters.AddWithValue("$updatedByUserId", user.Id);
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
    }

    private static async Task ReplaceEmployeeAssignmentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        AuthenticatedUser user,
        string employeeId,
        IReadOnlyList<string> requestedShopIds,
        string effectiveFrom,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText =
            """
            UPDATE hrm_employee_shop_assignments
            SET is_active = 0,
                effective_to = COALESCE(effective_to, $today)
            WHERE employee_id = $employeeId;
            """;
            deactivate.Parameters.AddWithValue("$today", now.ToString("yyyy-MM-dd"));
            deactivate.Parameters.AddWithValue("$employeeId", employeeId);
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
        }

        var shopIds = new HashSet<string>(requestedShopIds, StringComparer.Ordinal)
        {
            context.ShopId
        };
        foreach (string shopId in shopIds)
        {
            await using var assignment = connection.CreateCommand();
            assignment.Transaction = transaction;
            assignment.CommandText =
            """
            INSERT INTO hrm_employee_shop_assignments
            (employee_id, shop_id, assignment_type, effective_from, effective_to,
             is_active, assigned_by_user_id, created_at_utc)
            VALUES
            ($employeeId, $shopId, $assignmentType, $effectiveFrom, NULL,
             1, $userId, $now)
            ON CONFLICT(employee_id, shop_id) DO UPDATE SET
                assignment_type = excluded.assignment_type,
                effective_from = excluded.effective_from,
                effective_to = NULL,
                is_active = 1,
                assigned_by_user_id = excluded.assigned_by_user_id;
            """;
            assignment.Parameters.AddWithValue("$employeeId", employeeId);
            assignment.Parameters.AddWithValue("$shopId", shopId);
            assignment.Parameters.AddWithValue(
                "$assignmentType",
                shopId == context.ShopId ? "home" : "secondary");
            assignment.Parameters.AddWithValue("$effectiveFrom", effectiveFrom);
            assignment.Parameters.AddWithValue("$userId", user.Id);
            assignment.Parameters.AddWithValue("$now", now.ToString("O"));
            await assignment.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<EmployeeRecord>> ReadEmployeesAsync(
        SqliteConnection connection,
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<EmployeeSnapshot>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                snapshots.Add(new EmployeeSnapshot(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7),
                    reader.GetString(8), reader.GetString(9), reader.GetString(10),
                    reader.GetString(11), reader.GetString(12), reader.GetString(13),
                    reader.GetString(14), reader.GetString(15), reader.GetString(16),
                    reader.GetString(17), reader.GetString(18), reader.GetString(19),
                    reader.GetString(20), reader.GetString(21), reader.GetString(22),
                    reader.GetString(23), reader.GetString(24),
                    reader.IsDBNull(25) ? null : reader.GetString(25), reader.GetString(26),
                    reader.GetInt64(27), reader.GetString(28), reader.GetDouble(29),
                    reader.GetString(30), reader.GetString(31), reader.GetString(32),
                    reader.GetString(33), reader.GetString(34), reader.GetString(35),
                    reader.GetInt32(36), DateTimeOffset.Parse(reader.GetString(37)),
                    DateTimeOffset.Parse(reader.GetString(38)), reader.GetInt64(39),
                    reader.GetInt64(40), reader.GetInt64(41), reader.GetDouble(42),
                    reader.GetInt64(43)));
            }
        }

        var records = new List<EmployeeRecord>();
        foreach (EmployeeSnapshot snapshot in snapshots)
        {
            IReadOnlyList<EmployeeShopAssignmentRecord> assignments =
                await ReadAssignmentsAsync(connection, snapshot.Id, cancellationToken);
            string fullName = string.Join(
                " ",
                new[] { snapshot.FirstName, snapshot.OtherNames, snapshot.LastName }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            records.Add(new EmployeeRecord(
                snapshot.Id, snapshot.OrganizationId, snapshot.EmployeeNumber,
                snapshot.HomeShopId, snapshot.HomeShopCode, snapshot.HomeShopName,
                snapshot.UserId, snapshot.UserDisplayName, snapshot.DepartmentId,
                snapshot.DepartmentCode, snapshot.DepartmentName, snapshot.PositionId,
                snapshot.PositionCode, snapshot.PositionTitle, snapshot.FirstName,
                snapshot.LastName, snapshot.OtherNames, snapshot.PreferredName, fullName,
                snapshot.Phone, snapshot.Email, snapshot.Address,
                snapshot.EmergencyContactName, snapshot.EmergencyContactPhone,
                snapshot.EmploymentType, snapshot.HireDate, snapshot.EndDate,
                snapshot.Status, snapshot.BaseSalaryMinor, snapshot.PayFrequency,
                snapshot.StandardHoursPerWeek, snapshot.TaxNumber, snapshot.NationalId,
                snapshot.BankName, snapshot.BankAccount, snapshot.MobileMoneyNumber,
                snapshot.Notes, snapshot.Version, snapshot.CreatedAtUtc,
                snapshot.UpdatedAtUtc, assignments, snapshot.AttendanceDayCount,
                snapshot.WorkedMinutes, snapshot.OvertimeMinutes,
                snapshot.ApprovedLeaveDays, snapshot.PendingLeaveRequests));
        }
        return records;
    }

    private static async Task<IReadOnlyList<EmployeeShopAssignmentRecord>> ReadAssignmentsAsync(
        SqliteConnection connection,
        string employeeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT assignment.shop_id, shop.code, shop.name, assignment.assignment_type,
               assignment.effective_from, assignment.effective_to, assignment.is_active
        FROM hrm_employee_shop_assignments AS assignment
        INNER JOIN shops AS shop ON shop.id = assignment.shop_id
        WHERE assignment.employee_id = $employeeId
        ORDER BY assignment.is_active DESC, shop.name COLLATE NOCASE;
        """;
        command.Parameters.AddWithValue("$employeeId", employeeId);
        var records = new List<EmployeeShopAssignmentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new EmployeeShopAssignmentRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6) == 1));
        }
        return records;
    }

    private const string EmployeeSelectSql =
    """
    SELECT
        employee.id, employee.organization_id, employee.employee_number,
        employee.home_shop_id, shop.code, shop.name, employee.user_id,
        COALESCE(user.display_name, ''), employee.department_id, department.code,
        department.name, employee.position_id, position.code, position.title,
        employee.first_name, employee.last_name, employee.other_names,
        employee.preferred_name, employee.phone, employee.email, employee.address,
        employee.emergency_contact_name, employee.emergency_contact_phone,
        employee.employment_type, employee.hire_date, employee.end_date,
        employee.status, employee.base_salary_minor, employee.pay_frequency,
        employee.standard_hours_per_week, employee.tax_number, employee.national_id,
        employee.bank_name, employee.bank_account, employee.mobile_money_number,
        employee.notes, employee.version, employee.created_at_utc,
        employee.updated_at_utc, attendance.attendance_day_count,
        attendance.worked_minutes, attendance.overtime_minutes,
        leave.approved_leave_days, leave.pending_leave_requests
    FROM hrm_employees AS employee
    INNER JOIN shops AS shop ON shop.id = employee.home_shop_id
    LEFT JOIN users AS user ON user.id = employee.user_id
    INNER JOIN hrm_departments AS department ON department.id = employee.department_id
    INNER JOIN hrm_positions AS position ON position.id = employee.position_id
    INNER JOIN hrm_employee_attendance_summary AS attendance ON attendance.employee_id = employee.id
    INNER JOIN hrm_employee_leave_summary AS leave ON leave.employee_id = employee.id
    """;

    private sealed record EmployeeInput(
        string DepartmentId,
        string PositionId,
        string? UserId,
        string FirstName,
        string LastName,
        string OtherNames,
        string PreferredName,
        string Phone,
        string Email,
        string Address,
        string EmergencyContactName,
        string EmergencyContactPhone,
        string EmploymentType,
        string HireDate,
        string EndDate,
        string Status,
        long BaseSalaryMinor,
        string PayFrequency,
        double StandardHoursPerWeek,
        string TaxNumber,
        string NationalId,
        string BankName,
        string BankAccount,
        string MobileMoneyNumber,
        string Notes,
        IReadOnlyList<string> ShopIds);

    private sealed record EmployeeSnapshot(
        string Id,
        string OrganizationId,
        string EmployeeNumber,
        string HomeShopId,
        string HomeShopCode,
        string HomeShopName,
        string? UserId,
        string UserDisplayName,
        string DepartmentId,
        string DepartmentCode,
        string DepartmentName,
        string PositionId,
        string PositionCode,
        string PositionTitle,
        string FirstName,
        string LastName,
        string OtherNames,
        string PreferredName,
        string Phone,
        string Email,
        string Address,
        string EmergencyContactName,
        string EmergencyContactPhone,
        string EmploymentType,
        string HireDate,
        string? EndDate,
        string Status,
        long BaseSalaryMinor,
        string PayFrequency,
        double StandardHoursPerWeek,
        string TaxNumber,
        string NationalId,
        string BankName,
        string BankAccount,
        string MobileMoneyNumber,
        string Notes,
        int Version,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        long AttendanceDayCount,
        long WorkedMinutes,
        long OvertimeMinutes,
        double ApprovedLeaveDays,
        long PendingLeaveRequests);

    private static HrmException Validation(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);
    private static HrmException Forbidden(string code, string message) =>
        new(StatusCodes.Status403Forbidden, code, message);
    private static HrmException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);
    private static HrmException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);
}
