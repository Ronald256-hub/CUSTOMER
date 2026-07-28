namespace Robo.Pos.Server.Accounting;

public sealed record CreateAccountingAccountRequest(
    string Code = "",
    string Name = "",
    string AccountType = "",
    string? ParentAccountId = null,
    bool AllowManualPosting = true);

public sealed record UpdateAccountingAccountRequest(
    int ExpectedVersion = 1,
    string Name = "",
    string? ParentAccountId = null,
    bool AllowManualPosting = true,
    bool IsActive = true);

public sealed record AccountingAccountRecord(
    string Id,
    string OrganizationId,
    string Code,
    string Name,
    string AccountType,
    string NormalBalance,
    string? ParentAccountId,
    string? SystemKey,
    bool AllowManualPosting,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateAccountingPeriodRequest(
    string Name = "",
    string StartDate = "",
    string EndDate = "");

public sealed record CloseAccountingPeriodRequest(
    int ExpectedVersion = 1);

public sealed record AccountingPeriodRecord(
    string Id,
    string OrganizationId,
    string Name,
    string StartDate,
    string EndDate,
    string Status,
    int Version,
    string? ClosedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ClosedAtUtc);

public sealed record AccountingJournalLineInput(
    string AccountId = "",
    long DebitMinor = 0,
    long CreditMinor = 0,
    string? Description = null,
    string? CounterpartyType = null,
    string? CounterpartyId = null);

public sealed record CreateAccountingJournalRequest(
    string JournalDate = "",
    string Description = "",
    IReadOnlyList<AccountingJournalLineInput>? Lines = null);

public sealed record UpdateAccountingJournalRequest(
    int ExpectedVersion = 1,
    string JournalDate = "",
    string Description = "",
    IReadOnlyList<AccountingJournalLineInput>? Lines = null);

public sealed record PostAccountingJournalRequest(
    int ExpectedVersion = 1);

public sealed record ReverseAccountingJournalRequest(
    int ExpectedVersion = 1,
    string ReversalDate = "",
    string Reason = "");

public sealed record AccountingJournalLineRecord(
    long Id,
    int LineNumber,
    string AccountId,
    string AccountCode,
    string AccountName,
    string AccountType,
    string NormalBalance,
    long DebitMinor,
    long CreditMinor,
    string Description,
    string? CounterpartyType,
    string? CounterpartyId);

public sealed record AccountingJournalRecord(
    string Id,
    string OrganizationId,
    string ShopId,
    string ShopCode,
    string ShopName,
    string JournalNumber,
    string JournalDate,
    string CurrencyCode,
    string Description,
    string SourceType,
    string? SourceId,
    string Status,
    string? ReversalOfJournalId,
    string? ReversedByJournalId,
    long TotalDebitMinor,
    long TotalCreditMinor,
    int Version,
    string CreatedByUserId,
    string CreatedByDisplayName,
    string? PostedByUserId,
    string PostedByDisplayName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? PostedAtUtc,
    IReadOnlyList<AccountingJournalLineRecord> Lines);

public sealed record AccountingJournalListItem(
    string Id,
    string ShopId,
    string ShopCode,
    string JournalNumber,
    string JournalDate,
    string Description,
    string SourceType,
    string? SourceId,
    string Status,
    long TotalDebitMinor,
    long TotalCreditMinor,
    int Version,
    DateTimeOffset UpdatedAtUtc);

public sealed record AccountingJournalReversalResult(
    AccountingJournalRecord Original,
    AccountingJournalRecord Reversal);

public sealed record TrialBalanceLineRecord(
    string AccountId,
    string AccountCode,
    string AccountName,
    string AccountType,
    string NormalBalance,
    long DebitMovementMinor,
    long CreditMovementMinor,
    long DebitBalanceMinor,
    long CreditBalanceMinor);

public sealed record TrialBalanceReport(
    string Scope,
    string OrganizationId,
    string OrganizationName,
    string? ShopId,
    string? ShopCode,
    string CurrencyCode,
    string FromDate,
    string ToDate,
    long TotalDebitMovementMinor,
    long TotalCreditMovementMinor,
    long TotalDebitBalanceMinor,
    long TotalCreditBalanceMinor,
    IReadOnlyList<TrialBalanceLineRecord> Lines);

public sealed class AccountingException : Exception
{
    public AccountingException(
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
