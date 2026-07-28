namespace Robo.Pos.Server.Crm;

public sealed record CreateCrmCustomerRequest(
    string Name = "",
    string? Phone = null,
    string? Email = null,
    string? Address = null,
    string? TaxNumber = null,
    long CreditLimitMinor = 0,
    int PaymentTermsDays = 30,
    string CustomerType = "individual",
    string? CompanyName = null,
    string? ContactPerson = null,
    string LifecycleStage = "prospect",
    string? Source = null,
    string PreferredChannel = "phone",
    bool MarketingOptIn = false,
    bool LoyaltyEnrolled = true,
    string? AssignedUserId = null,
    string? Notes = null,
    IReadOnlyList<string>? TagIds = null);

public sealed record UpdateCrmCustomerRequest(
    int ExpectedCustomerVersion = 1,
    int ExpectedProfileVersion = 1,
    string Name = "",
    string? Phone = null,
    string? Email = null,
    string? Address = null,
    string? TaxNumber = null,
    long CreditLimitMinor = 0,
    int PaymentTermsDays = 30,
    bool IsActive = true,
    string CustomerType = "individual",
    string? CompanyName = null,
    string? ContactPerson = null,
    string LifecycleStage = "customer",
    string? Source = null,
    string PreferredChannel = "phone",
    bool MarketingOptIn = false,
    bool LoyaltyEnrolled = true,
    string? AssignedUserId = null,
    string? Notes = null,
    IReadOnlyList<string>? TagIds = null);

public sealed record CrmTagRecord(
    string Id,
    string Name,
    string Description,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateCrmTagRequest(
    string Name = "",
    string? Description = null);

public sealed record UpdateCrmTagRequest(
    int ExpectedVersion = 1,
    string Name = "",
    string? Description = null,
    bool IsActive = true);

public sealed record CrmCustomerMetrics(
    long CompletedSaleCount,
    long LifetimeSpendMinor,
    long AverageSaleMinor,
    DateTimeOffset? FirstSaleAtUtc,
    DateTimeOffset? LastSaleAtUtc,
    long OutstandingMinor,
    long OpenTaskCount,
    long CommunicationCount,
    long QuotationCount,
    long AcceptedQuotationCount,
    long ConvertedQuotationCount,
    string Segment);

public sealed record CrmCustomerRecord(
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
    IReadOnlyList<CrmTagRecord> Tags,
    CrmCustomerMetrics Metrics);

public sealed record DuplicateCustomerCandidate(
    string Id,
    string CustomerNumber,
    string Name,
    string Phone,
    string Email,
    string MatchReason,
    bool IsActive);

public sealed record CreateCommunicationRequest(
    string CustomerId = "",
    string CommunicationType = "note",
    string Direction = "internal",
    string? Subject = null,
    string Details = "",
    string? Outcome = null,
    string? OccurredAtUtc = null,
    string? FollowUpAtUtc = null);

public sealed record CrmCommunicationRecord(
    string Id,
    string ShopId,
    string ShopCode,
    string CustomerId,
    string CustomerName,
    string CommunicationType,
    string Direction,
    string Subject,
    string Details,
    string Outcome,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset? FollowUpAtUtc,
    string CreatedByUserId,
    string CreatedByName,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateCrmTaskRequest(
    string? CustomerId = null,
    string Title = "",
    string? Details = null,
    string Priority = "normal",
    string DueAtUtc = "",
    string? AssignedToUserId = null);

public sealed record CompleteCrmTaskRequest(
    int ExpectedVersion = 1,
    string? CompletionNotes = null);

public sealed record CancelCrmTaskRequest(
    int ExpectedVersion = 1,
    string? Reason = null);

public sealed record CrmTaskRecord(
    string Id,
    string ShopId,
    string ShopCode,
    string? CustomerId,
    string CustomerName,
    string Title,
    string Details,
    string Priority,
    string Status,
    DateTimeOffset DueAtUtc,
    string AssignedToUserId,
    string AssignedToName,
    string CreatedByUserId,
    string CreatedByName,
    string? CompletedByUserId,
    string CompletedByName,
    string CompletionNotes,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    bool IsOverdue);

public sealed record UpdateLoyaltySettingsRequest(
    int ExpectedVersion = 1,
    bool IsEnabled = false,
    long SpendMinorPerPoint = 1000,
    long MinimumRedeemPoints = 1,
    long SilverThresholdPoints = 100,
    long GoldThresholdPoints = 500,
    long PlatinumThresholdPoints = 1000);

public sealed record LoyaltySettingsRecord(
    string OrganizationId,
    bool IsEnabled,
    long SpendMinorPerPoint,
    long MinimumRedeemPoints,
    long SilverThresholdPoints,
    long GoldThresholdPoints,
    long PlatinumThresholdPoints,
    int Version,
    DateTimeOffset UpdatedAtUtc);

public sealed record LoyaltyAdjustmentRequest(
    long PointsDelta = 0,
    string Reason = "");

public sealed record LoyaltyRedemptionRequest(
    long Points = 0,
    string Reason = "",
    string? Reference = null);

public sealed record LoyaltyLedgerRecord(
    string Id,
    string CustomerId,
    string CustomerName,
    string? ShopId,
    string ShopCode,
    string? SaleId,
    string EntryType,
    long PointsDelta,
    long BalanceAfter,
    string ReferenceType,
    string ReferenceId,
    string Reason,
    string CreatedByName,
    DateTimeOffset CreatedAtUtc);

public sealed record QuotationLineRequest(
    string ProductId = "",
    long Quantity = 0,
    long? UnitPriceMinor = null);

public sealed record CreateQuotationRequest(
    string CustomerId = "",
    string? QuotationDate = null,
    string ValidUntil = "",
    long DiscountMinor = 0,
    string? Notes = null,
    string? Terms = null,
    IReadOnlyList<QuotationLineRequest>? Lines = null);

public sealed record UpdateQuotationRequest(
    int ExpectedVersion = 1,
    string CustomerId = "",
    string QuotationDate = "",
    string ValidUntil = "",
    long DiscountMinor = 0,
    string? Notes = null,
    string? Terms = null,
    IReadOnlyList<QuotationLineRequest>? Lines = null);

public sealed record QuotationActionRequest(
    int ExpectedVersion = 1);

public sealed record ConvertQuotationRequest(
    int ExpectedVersion = 1,
    string SaleId = "");

public sealed record QuotationLineRecord(
    string Id,
    int LineNumber,
    string ProductId,
    string ProductName,
    string Sku,
    long Quantity,
    long UnitPriceMinor,
    long LineTotalMinor);

public sealed record QuotationRecord(
    string Id,
    string QuotationNumber,
    string OrganizationId,
    string ShopId,
    string ShopCode,
    string CustomerId,
    string CustomerNumber,
    string CustomerName,
    string Status,
    string QuotationDate,
    string ValidUntil,
    string CurrencyCode,
    long SubtotalMinor,
    long DiscountMinor,
    long TotalMinor,
    string Notes,
    string Terms,
    string? SaleId,
    int Version,
    string CreatedByName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? SentAtUtc,
    DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? ConvertedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    bool IsPastValidity,
    IReadOnlyList<QuotationLineRecord> Lines);

public sealed record CrmTimelineEntry(
    DateTimeOffset OccurredAtUtc,
    string EntryType,
    string Title,
    string Description,
    string Status,
    long AmountMinor,
    long PointsDelta,
    string ShopCode,
    string SourceId);

public sealed record CrmDashboardRecord(
    string OrganizationId,
    string ShopId,
    string ShopCode,
    long ActiveCustomerCount,
    long ProspectCount,
    long NewCustomerCount30Days,
    long RepeatCustomerCount,
    long DormantCustomerCount,
    long DebtorCustomerCount,
    long TotalOutstandingMinor,
    long OpenTaskCount,
    long OverdueTaskCount,
    long FollowUpsDueCount,
    long OpenQuotationCount,
    long OpenQuotationValueMinor,
    long LoyaltyMemberCount,
    long OutstandingLoyaltyPoints);

public sealed record CrmSegmentRecord(
    string Segment,
    long CustomerCount,
    long LifetimeSpendMinor,
    long OutstandingMinor);

public sealed class CrmException : Exception
{
    public CrmException(int statusCode, string errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
}
