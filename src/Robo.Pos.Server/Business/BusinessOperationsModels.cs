namespace Robo.Pos.Server.Business;

public sealed record CreateSupplierRequest(
    string Name,
    string? Phone = null,
    string? Email = null,
    string? Address = null,
    string? Notes = null);

public sealed record UpdateSupplierRequest(
    string Name,
    string? Phone = null,
    string? Email = null,
    string? Address = null,
    string? Notes = null,
    bool IsActive = true);

public sealed record SupplierResult(
    string Id,
    string Name,
    string Phone,
    string Email,
    string Address,
    string Notes,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PurchaseItemRequest(
    string ProductId,
    long QuantityBaseUnits,
    long UnitCostMinor,
    string? BatchNumber = null,
    string? ExpiryDate = null);

public sealed record ReceivePurchaseRequest(
    string? SupplierId,
    string? SupplierInvoiceNumber,
    IReadOnlyList<PurchaseItemRequest>? Items,
    string? Notes = null);

public sealed record PurchaseItemResult(
    string ProductId,
    string ProductName,
    string Sku,
    long QuantityBaseUnits,
    long UnitCostMinor,
    long LineTotalMinor,
    string BatchNumber,
    string? ExpiryDate);

public sealed record PurchaseResult(
    string Id,
    string PurchaseNumber,
    string? SupplierId,
    string SupplierName,
    string SupplierInvoiceNumber,
    string Status,
    long SubtotalMinor,
    long TotalMinor,
    string Notes,
    string ReceivedBy,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyList<PurchaseItemResult> Items);

public sealed record CreateExpenseRequest(
    string Category,
    string Description,
    long AmountMinor,
    string PaymentMethod,
    string ExpenseDate);

public sealed record VoidExpenseRequest(
    string Reason);

public sealed record ExpenseResult(
    string Id,
    string ExpenseNumber,
    string Category,
    string Description,
    long AmountMinor,
    string PaymentMethod,
    string ExpenseDate,
    string RecordedBy,
    DateTimeOffset CreatedAtUtc,
    bool IsVoided,
    DateTimeOffset? VoidedAtUtc,
    string? VoidReason);

public sealed record ProductReportResult(
    string ProductName,
    string Sku,
    long QuantitySold,
    long RevenueMinor,
    long CostMinor,
    long GrossProfitMinor);

public sealed record TellerReportResult(
    string TellerName,
    long SalesCount,
    long RevenueMinor);

public sealed record BusinessReportResult(
    string From,
    string To,
    long SalesCount,
    long RevenueMinor,
    long CostOfGoodsMinor,
    long GrossProfitMinor,
    long ExpenseTotalMinor,
    long NetProfitMinor,
    long PurchaseTotalMinor,
    IReadOnlyList<ProductReportResult> TopProducts,
    IReadOnlyList<TellerReportResult> TellerPerformance);

public sealed class BusinessOperationsException : Exception
{
    public BusinessOperationsException(
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
