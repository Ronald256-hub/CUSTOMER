namespace Robo.Pos.Server.Hrm;

public sealed record CreateDepartmentRequest(
    string Code = "",
    string Name = "",
    string? Description = null);

public sealed record UpdateDepartmentRequest(
    int ExpectedVersion = 1,
    string Name = "",
    string? Description = null,
    bool IsActive = true);

public sealed record DepartmentRecord(
    string Id,
    string OrganizationId,
    string Code,
    string Name,
    string Description,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreatePositionRequest(
    string DepartmentId = "",
    string Code = "",
    string Title = "",
    string? Description = null,
    string? Grade = null);

public sealed record PositionRecord(
    string Id,
    string OrganizationId,
    string DepartmentId,
    string DepartmentName,
    string Code,
    string Title,
    string Description,
    string Grade,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateEmployeeRequest(
    string DepartmentId = "",
    string PositionId = "",
    string? UserId = null,
    string FirstName = "",
    string LastName = "",
    string? OtherNames = null,
    string? PreferredName = null,
    string? Phone = null,
    string? Email = null,
    string? Address = null,
    string? EmergencyContactName = null,
    string? EmergencyContactPhone = null,
    string EmploymentType = "permanent",
    string HireDate = "",
    string? EndDate = null,
    string Status = "active",
    long BaseSalaryMinor = 0,
    string PayFrequency = "monthly",
    double StandardHoursPerWeek = 45,
    string? TaxNumber = null,
    string? NationalId = null,
    string? BankName = null,
    string? BankAccount = null,
    string? MobileMoneyNumber = null,
    string? Notes = null,
    IReadOnlyList<string>? ShopIds = null);

public sealed record UpdateEmployeeRequest(
    int ExpectedVersion = 1,
    string DepartmentId = "",
    string PositionId = "",
    string? UserId = null,
    string FirstName = "",
    string LastName = "",
    string? OtherNames = null,
    string? PreferredName = null,
    string? Phone = null,
    string? Email = null,
    string? Address = null,
    string? EmergencyContactName = null,
    string? EmergencyContactPhone = null,
    string EmploymentType = "permanent",
    string HireDate = "",
    string? EndDate = null,
    string Status = "active",
    long BaseSalaryMinor = 0,
    string PayFrequency = "monthly",
    double StandardHoursPerWeek = 45,
    string? TaxNumber = null,
    string? NationalId = null,
    string? BankName = null,
    string? BankAccount = null,
    string? MobileMoneyNumber = null,
    string? Notes = null,
    IReadOnlyList<string>? ShopIds = null);

public sealed record EmployeeShopAssignmentRecord(
    string ShopId,
    string ShopCode,
    string ShopName,
    string AssignmentType,
    string EffectiveFrom,
    string? EffectiveTo,
    bool IsActive);

public sealed record EmployeeRecord(
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
    string FullName,
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
    IReadOnlyList<EmployeeShopAssignmentRecord> ShopAssignments,
    long AttendanceDayCount,
    long WorkedMinutes,
    long OvertimeMinutes,
    double ApprovedLeaveDays,
    long PendingLeaveRequests);

public sealed record CreateWorkScheduleRequest(
    string EmployeeId = "",
    string WorkDate = "",
    string StartTime = "",
    string EndTime = "",
    int BreakMinutes = 0,
    string? Notes = null);

public sealed record WorkScheduleActionRequest(int ExpectedVersion = 1);

public sealed record WorkScheduleRecord(
    string Id,
    string ShopId,
    string ShopCode,
    string EmployeeId,
    string EmployeeName,
    string WorkDate,
    string StartTime,
    string EndTime,
    int BreakMinutes,
    string Status,
    string Notes,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ClockInRequest(
    string EmployeeId = "",
    string? ClockInUtc = null,
    string Source = "manual",
    string? Notes = null);

public sealed record ClockOutRequest(
    int ExpectedVersion = 1,
    string? ClockOutUtc = null,
    int BreakMinutes = 0,
    string? Notes = null);

public sealed record AttendanceActionRequest(
    int ExpectedVersion = 1,
    string? Notes = null);

public sealed record AttendanceRecord(
    string Id,
    string ShopId,
    string ShopCode,
    string EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    string WorkDate,
    DateTimeOffset ClockInUtc,
    DateTimeOffset? ClockOutUtc,
    int BreakMinutes,
    int? WorkedMinutes,
    int OvertimeMinutes,
    string Status,
    string Source,
    string Notes,
    int Version,
    string CreatedByUserId,
    string? ApprovedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ApprovedAtUtc);

public sealed record CreateLeaveTypeRequest(
    string Code = "",
    string Name = "",
    double AnnualEntitlementDays = 0,
    bool IsPaid = true,
    bool RequiresAttachment = false);

public sealed record LeaveTypeRecord(
    string Id,
    string Code,
    string Name,
    double AnnualEntitlementDays,
    bool IsPaid,
    bool RequiresAttachment,
    bool IsActive,
    int Version);

public sealed record CreateLeaveRequest(
    string EmployeeId = "",
    string LeaveTypeId = "",
    string StartDate = "",
    string EndDate = "",
    double RequestedDays = 0,
    string? Reason = null,
    string? AttachmentReference = null);

public sealed record LeaveActionRequest(
    int ExpectedVersion = 1,
    string? DecisionNotes = null);

public sealed record LeaveRequestRecord(
    string Id,
    string EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    string LeaveTypeId,
    string LeaveTypeCode,
    string LeaveTypeName,
    string StartDate,
    string EndDate,
    double RequestedDays,
    string Reason,
    string AttachmentReference,
    string Status,
    int Version,
    string RequestedByUserId,
    string? DecidedByUserId,
    string DecisionNotes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? DecidedAtUtc);

public sealed record CreatePayrollPeriodRequest(
    string Name = "",
    string StartDate = "",
    string EndDate = "",
    string PayDate = "");

public sealed record CalculatePayrollRequest(
    int ExpectedVersion = 1,
    long DefaultAllowanceMinor = 0,
    long DefaultDeductionMinor = 0,
    long OvertimeRateMinorPerHour = 0);

public sealed record PayrollActionRequest(int ExpectedVersion = 1);

public sealed record PayrollEntryRecord(
    string Id,
    string EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    long BasePayMinor,
    long OvertimePayMinor,
    long AllowanceMinor,
    long DeductionMinor,
    long GrossPayMinor,
    long NetPayMinor,
    int WorkedMinutes,
    int OvertimeMinutes,
    string Notes);

public sealed record PayrollPeriodRecord(
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
    DateTimeOffset? ClosedAtUtc,
    IReadOnlyList<PayrollEntryRecord> Entries);

public sealed record CreatePerformanceReviewRequest(
    string EmployeeId = "",
    string ReviewPeriodStart = "",
    string ReviewPeriodEnd = "",
    string? Goals = null,
    string? Achievements = null,
    string? ImprovementAreas = null);

public sealed record CompletePerformanceReviewRequest(
    int ExpectedVersion = 1,
    int OverallRating = 0,
    string? Achievements = null,
    string? ImprovementAreas = null);

public sealed record PerformanceReviewRecord(
    string Id,
    string EmployeeId,
    string EmployeeName,
    string ReviewerUserId,
    string ReviewerName,
    string ReviewPeriodStart,
    string ReviewPeriodEnd,
    string Goals,
    string Achievements,
    string ImprovementAreas,
    int? OverallRating,
    string Status,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record CreateTrainingRecordRequest(
    string EmployeeId = "",
    string Title = "",
    string? Provider = null,
    string StartDate = "",
    string? EndDate = null,
    string? ExpiryDate = null,
    long CostMinor = 0,
    string? Notes = null);

public sealed record CompleteTrainingRequest(
    int ExpectedVersion = 1,
    string Status = "completed",
    string? CertificateReference = null,
    string? Notes = null);

public sealed record TrainingRecord(
    string Id,
    string EmployeeId,
    string EmployeeName,
    string Title,
    string Provider,
    string StartDate,
    string? EndDate,
    string? ExpiryDate,
    long CostMinor,
    string Status,
    string CertificateReference,
    string Notes,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record CreateDisciplinaryCaseRequest(
    string EmployeeId = "",
    string IncidentDate = "",
    string Category = "",
    string Severity = "minor",
    string Description = "");

public sealed record ResolveDisciplinaryCaseRequest(
    int ExpectedVersion = 1,
    string Status = "resolved",
    string ActionTaken = "");

public sealed record DisciplinaryCaseRecord(
    string Id,
    string CaseNumber,
    string EmployeeId,
    string EmployeeName,
    string IncidentDate,
    string Category,
    string Severity,
    string Description,
    string ActionTaken,
    string Status,
    int Version,
    string OpenedByUserId,
    string? ResolvedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ResolvedAtUtc);

public sealed record HrmDashboardRecord(
    string OrganizationId,
    string ShopId,
    string ShopCode,
    long ActiveEmployeeCount,
    long ProbationEmployeeCount,
    long OnLeaveEmployeeCount,
    long OpenAttendanceCount,
    long TodayAttendanceCount,
    long PendingLeaveRequestCount,
    long ApprovedLeaveTodayCount,
    long PublishedScheduleCountNext7Days,
    long OpenDisciplinaryCaseCount,
    long ExpiringTrainingCount90Days,
    long DraftPayrollPeriodCount,
    long LatestPayrollNetMinor);

public sealed class HrmException : Exception
{
    public HrmException(int statusCode, string errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
}
