namespace Robo.Pos.Server.Finance;

public sealed record CreateCustomerRequest(
    string Name = "",
    string? Phone = null,
    string? Email = null,
    string? Address = null,
    string? TaxNumber = null,
    long CreditLimitMinor = 0,
    int PaymentTermsDays = 30);

public sealed record UpdateCustomerRequest(
    int ExpectedVersion = 1,
    string Name = "",
    string? Phone = null,
    string? Email = null,
    string? Address = null,
    string? TaxNumber = null,
    long CreditLimitMinor = 0,
    int PaymentTermsDays = 30,
    bool IsActive = true);

public sealed record CustomerRecord(
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
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SettlementAllocationInput(
    string ItemId = "",
    long AmountMinor = 0);

public sealed record CreateCustomerReceiptRequest(
    string CustomerId = "",
    string ReceiptDate = "",
    string PaymentMethod = "cash",
    string? Reference = null,
    string? Notes = null,
    IReadOnlyList<SettlementAllocationInput>? Allocations = null);

public sealed record CreateSupplierPaymentRequest(
    string SupplierId = "",
    string PaymentDate = "",
    string PaymentMethod = "cash",
    string? Reference = null,
    string? Notes = null,
    IReadOnlyList<SettlementAllocationInput>? Allocations = null);

public sealed record ReverseSettlementRequest(
    string ReversalDate = "",
    string Reason = "");

public sealed record ReceivableItemRecord(
    string Id,
    string ShopId,
    string ShopCode,
    string CustomerId,
    string CustomerNumber,
    string CustomerName,
    string SaleId,
    string DocumentNumber,
    string DocumentDate,
    string DueDate,
    long OriginalAmountMinor,
    long SettledAmountMinor,
    long OutstandingAmountMinor,
    string Status);

public sealed record PayableItemRecord(
    string Id,
    string ShopId,
    string ShopCode,
    string? SupplierId,
    string SupplierName,
    string PurchaseId,
    string DocumentNumber,
    string SupplierInvoiceNumber,
    string DocumentDate,
    string DueDate,
    long OriginalAmountMinor,
    long SettledAmountMinor,
    long OutstandingAmountMinor,
    string Status);

public sealed record SettlementAllocationRecord(
    string ItemId,
    string DocumentNumber,
    long AmountMinor);

public sealed record FinanceSettlementRecord(
    string Id,
    string SettlementType,
    string Number,
    string Date,
    string ShopId,
    string ShopCode,
    string CounterpartyId,
    string CounterpartyName,
    string PaymentMethod,
    long AmountMinor,
    string Reference,
    string Notes,
    string Status,
    string? PostingJournalId,
    string? ReversalJournalId,
    string CreatedByDisplayName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PostedAtUtc,
    DateTimeOffset? ReversedAtUtc,
    string? ReversalReason,
    IReadOnlyList<SettlementAllocationRecord> Allocations);

public sealed record StatementLineRecord(
    string Date,
    string EntryType,
    string Reference,
    string Description,
    long DebitMinor,
    long CreditMinor,
    long RunningBalanceMinor,
    string ShopCode,
    string SourceId);

public sealed record CounterpartyStatementReport(
    string CounterpartyType,
    string CounterpartyId,
    string CounterpartyNumber,
    string CounterpartyName,
    string CurrencyCode,
    string FromDate,
    string ToDate,
    long OpeningBalanceMinor,
    long ClosingBalanceMinor,
    IReadOnlyList<StatementLineRecord> Lines);

public sealed record AgeingBucketRecord(
    string Bucket,
    long AmountMinor,
    int ItemCount);

public sealed record AgeingCounterpartyRecord(
    string? CounterpartyId,
    string CounterpartyName,
    long CurrentMinor,
    long Days1To30Minor,
    long Days31To60Minor,
    long Days61To90Minor,
    long Over90DaysMinor,
    long TotalMinor,
    int ItemCount);

public sealed record AgeingReport(
    string LedgerType,
    string Scope,
    string OrganizationId,
    string? ShopId,
    string? ShopCode,
    string CurrencyCode,
    string AsOfDate,
    long TotalOutstandingMinor,
    IReadOnlyList<AgeingBucketRecord> Buckets,
    IReadOnlyList<AgeingCounterpartyRecord> Counterparties);

public sealed record CashbookEntryRecord(
    string JournalId,
    string JournalNumber,
    string JournalDate,
    string ShopId,
    string ShopCode,
    string AccountId,
    string AccountCode,
    string AccountName,
    string? SystemKey,
    string Direction,
    long DebitMinor,
    long CreditMinor,
    long SignedAmountMinor,
    string JournalDescription,
    string LineDescription,
    string SourceType,
    string? SourceId,
    string? CounterpartyType,
    string? CounterpartyId,
    DateTimeOffset? PostedAtUtc);

public sealed class FinanceException : Exception
{
    public FinanceException(
        int statusCode,
        string errorCode,
        string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
}