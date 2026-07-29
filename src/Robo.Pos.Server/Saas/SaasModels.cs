namespace Robo.Pos.Server.Saas;

public sealed record SaasPlanRecord(
    string Id,
    string Code,
    string Name,
    string Description,
    string Status,
    string BillingInterval,
    long PriceMinor,
    string CurrencyCode,
    int TrialDays,
    string EnforcementMode,
    int SortOrder,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaasEntitlementRecord(
    string Key,
    bool IsEnabled,
    long? LimitValue,
    string ConfigurationJson,
    string Source,
    DateTimeOffset? ExpiresAtUtc);

public sealed record SaasSubscriptionRecord(
    string Id,
    string OrganizationId,
    string OrganizationName,
    string PlanId,
    string PlanCode,
    string PlanName,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? TrialEndsAtUtc,
    DateTimeOffset? CurrentPeriodStartsUtc,
    DateTimeOffset? CurrentPeriodEndsUtc,
    DateTimeOffset? GraceEndsAtUtc,
    string ExternalCustomerReference,
    string ExternalSubscriptionReference,
    string Notes,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaasTenantSummaryRecord(
    string OrganizationId,
    string OrganizationName,
    string LegalName,
    string CurrencyCode,
    string TimezoneId,
    string SubscriptionStatus,
    string PlanCode,
    string PlanName,
    int ActiveShopCount,
    int ActiveUserCount,
    int OpenSupportCaseCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaasUsageSnapshotRecord(
    string Id,
    string OrganizationId,
    DateTimeOffset CapturedAtUtc,
    int ActiveShopCount,
    int ActiveUserCount,
    int EmployeeCount,
    int CustomerCount,
    int CompletedSales30Days,
    int PurchaseOrders30Days,
    long DatabaseSizeBytes,
    string LimitViolationsJson);

public sealed record SaasBillingEventRecord(
    string Id,
    string OrganizationId,
    string SubscriptionId,
    string EventType,
    string ExternalReference,
    long AmountMinor,
    string CurrencyCode,
    string Status,
    DateTimeOffset? DueAtUtc,
    DateTimeOffset OccurredAtUtc,
    string DetailsJson,
    DateTimeOffset CreatedAtUtc);

public sealed record SaasSupportCaseRecord(
    string Id,
    string CaseNumber,
    string OrganizationId,
    string? ShopId,
    string OpenedByUserId,
    string? AssignedToUserId,
    string Category,
    string Priority,
    string Status,
    string Subject,
    string Description,
    string Resolution,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    DateTimeOffset? ClosedAtUtc);

public sealed record SaasSupportCaseEventRecord(
    long Id,
    string EventType,
    string? PreviousStatus,
    string? NewStatus,
    string Note,
    string? ActorUserId,
    DateTimeOffset OccurredAtUtc);

public sealed record SaasSupportAccessGrantRecord(
    string Id,
    string OrganizationId,
    string OperatorUserId,
    string OperatorUsername,
    string AccessScope,
    string Reason,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    int Version,
    DateTimeOffset CreatedAtUtc);

public sealed record SaasTenantHealthRecord(
    string Id,
    string OrganizationId,
    string HealthStatus,
    int SchemaVersion,
    long DatabaseSizeBytes,
    int ActiveShopCount,
    int ActiveUserCount,
    int OpenSupportCount,
    DateTimeOffset? LastBackupAtUtc,
    string DetailsJson,
    DateTimeOffset CapturedAtUtc);

public sealed record SaasPlatformDashboardRecord(
    int TenantCount,
    int ActiveSubscriptionCount,
    int TrialSubscriptionCount,
    int PastDueSubscriptionCount,
    int SuspendedSubscriptionCount,
    int OpenSupportCaseCount,
    int UrgentSupportCaseCount,
    int ActiveSupportGrantCount,
    long PendingBillingMinor,
    DateTimeOffset GeneratedAtUtc);

public sealed record CreateSaasPlanRequest(
    string Code = "",
    string Name = "",
    string? Description = null,
    string BillingInterval = "monthly",
    long PriceMinor = 0,
    string CurrencyCode = "UGX",
    int TrialDays = 0,
    string EnforcementMode = "report_only",
    int SortOrder = 0);

public sealed record UpdateSaasPlanRequest(
    string Name = "",
    string? Description = null,
    string Status = "active",
    string BillingInterval = "monthly",
    long PriceMinor = 0,
    string CurrencyCode = "UGX",
    int TrialDays = 0,
    string EnforcementMode = "report_only",
    int SortOrder = 0,
    int ExpectedVersion = 1);

public sealed record SetSaasEntitlementRequest(
    bool IsEnabled = true,
    long? LimitValue = null,
    string? ConfigurationJson = null);

public sealed record UpdateSaasSubscriptionRequest(
    string PlanId = "",
    string Status = "active",
    DateTimeOffset? TrialEndsAtUtc = null,
    DateTimeOffset? CurrentPeriodStartsUtc = null,
    DateTimeOffset? CurrentPeriodEndsUtc = null,
    DateTimeOffset? GraceEndsAtUtc = null,
    string? ExternalCustomerReference = null,
    string? ExternalSubscriptionReference = null,
    string? Notes = null,
    int ExpectedVersion = 1);

public sealed record CreateSaasBillingEventRequest(
    string EventType = "invoice",
    string? ExternalReference = null,
    long AmountMinor = 0,
    string CurrencyCode = "UGX",
    string Status = "pending",
    DateTimeOffset? DueAtUtc = null,
    DateTimeOffset? OccurredAtUtc = null,
    string? DetailsJson = null);

public sealed record CreateSaasSupportCaseRequest(
    string? ShopId = null,
    string Category = "general",
    string Priority = "normal",
    string Subject = "",
    string Description = "");

public sealed record UpdateSaasSupportCaseRequest(
    string Status = "in_progress",
    string? AssignedToUserId = null,
    string? Resolution = null,
    string? Note = null,
    int ExpectedVersion = 1);

public sealed record AddSaasSupportCaseNoteRequest(
    string Note = "");

public sealed record CreateSaasSupportGrantRequest(
    string OperatorUserId = "",
    string AccessScope = "read_only",
    string Reason = "",
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record RevokeSaasSupportGrantRequest(
    int ExpectedVersion = 1);

public sealed record OnboardSaasTenantRequest(
    string OrganizationName = "",
    string? LegalName = null,
    string CurrencyCode = "UGX",
    string TimezoneId = "Africa/Kampala",
    string ShopCode = "MAIN",
    string ShopName = "",
    string? ShopAddress = null,
    string? ShopPhone = null,
    string? ShopEmail = null,
    string OwnerUserId = "",
    string PlanId = "enterprise-unlimited");

public sealed class SaasException : Exception
{
    public SaasException(int statusCode, string errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
}
