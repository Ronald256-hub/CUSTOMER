namespace Robo.Pos.Server.Sales;

public sealed record CreateCashDrawerMovementRequest(
    string MovementType = "safe_drop",
    long AmountMinor = 0,
    string Reason = "",
    string? Reference = null);

public sealed record CashDenominationLine(
    long DenominationMinor,
    long Quantity);

public sealed record RecordCashCountRequest(
    string CountType = "interim",
    IReadOnlyList<CashDenominationLine>? Denominations = null,
    string? Notes = null);

public sealed record ReviewShiftReconciliationRequest(
    string Decision = "approved",
    string? Notes = null);

public sealed record CashDrawerMovementRecord(
    string Id,
    string MovementNumber,
    string MovementType,
    long AmountMinor,
    string Reason,
    string Reference,
    string CreatedByDisplayName,
    string ApprovedByDisplayName,
    DateTimeOffset CreatedAtUtc);

public sealed record CashCountRecord(
    string Id,
    string CountType,
    long TotalMinor,
    IReadOnlyList<CashDenominationLine> Denominations,
    string Notes,
    string CountedByDisplayName,
    DateTimeOffset CreatedAtUtc);

public sealed record CashDrawerSnapshot(
    string ShiftId,
    string ShopId,
    string ShopCode,
    string ShopName,
    long OpeningCashMinor,
    long CashSalesMinor,
    long CashRefundsMinor,
    long FloatInMinor,
    long SafeDropMinor,
    long ExpectedDrawerCashMinor,
    IReadOnlyList<CashDrawerMovementRecord> Movements,
    IReadOnlyList<CashCountRecord> Counts);

public sealed record ShiftReconciliationReviewRecord(
    string ShiftId,
    string ShopId,
    string ShopCode,
    string ShopName,
    string TellerDisplayName,
    string ReviewStatus,
    long ExpectedCashMinor,
    long CountedCashMinor,
    long VarianceMinor,
    string ReviewNotes,
    string? ReviewedByDisplayName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReviewedAtUtc);

public sealed class CashDrawerException : Exception
{
    public CashDrawerException(int statusCode, string errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
}