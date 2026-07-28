namespace Robo.Pos.Server.Procurement;

public sealed record PurchaseOrderLineRequest(
    string ProductId,
    long QuantityBaseUnits,
    long UnitCostMinor);

public sealed record CreatePurchaseOrderRequest(
    string SupplierId,
    string? OrderDate,
    string? ExpectedDate,
    IReadOnlyList<PurchaseOrderLineRequest>? Items,
    string? Notes = null);

public sealed record UpdatePurchaseOrderRequest(
    string SupplierId,
    string? OrderDate,
    string? ExpectedDate,
    IReadOnlyList<PurchaseOrderLineRequest>? Items,
    string? Notes,
    int ExpectedVersion);

public sealed record VersionedActionRequest(int ExpectedVersion);

public sealed record CancelPurchaseOrderRequest(
    int ExpectedVersion,
    string Reason);

public sealed record PurchaseOrderLineRecord(
    string Id,
    int LineNumber,
    string ProductId,
    string ProductName,
    string Sku,
    long OrderedQuantityBaseUnits,
    long ReceivedQuantityBaseUnits,
    long ReturnedQuantityBaseUnits,
    long OutstandingQuantityBaseUnits,
    long UnitCostMinor,
    long LineTotalMinor);

public sealed record PurchaseOrderRecord(
    string Id,
    string PurchaseOrderNumber,
    string OrganizationId,
    string ShopId,
    string ShopCode,
    string SupplierId,
    string SupplierName,
    string Status,
    string OrderDate,
    string? ExpectedDate,
    long SubtotalMinor,
    long LandedCostMinor,
    long TotalMinor,
    string Notes,
    int Version,
    string CreatedBy,
    string? ApprovedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<PurchaseOrderLineRecord> Lines);

public sealed record GoodsReceiptLineRequest(
    string PurchaseOrderLineId,
    long QuantityBaseUnits,
    long LandedCostMinor = 0,
    string? BatchNumber = null,
    string? ExpiryDate = null);

public sealed record ReceiveGoodsRequest(
    string? SupplierInvoiceNumber,
    IReadOnlyList<GoodsReceiptLineRequest>? Items,
    string? Notes = null);

public sealed record GoodsReceiptLineRecord(
    string Id,
    string PurchaseOrderLineId,
    string ProductId,
    string ProductName,
    string Sku,
    long QuantityBaseUnits,
    long UnitCostMinor,
    long LandedCostMinor,
    long EffectiveUnitCostMinor,
    long LineTotalMinor,
    string BatchNumber,
    string? ExpiryDate,
    string? BatchId);

public sealed record GoodsReceiptRecord(
    string Id,
    string GoodsReceiptNumber,
    string PurchaseOrderId,
    string PurchaseOrderNumber,
    string PurchaseId,
    string PurchaseNumber,
    string SupplierId,
    string SupplierName,
    string SupplierInvoiceNumber,
    string Status,
    long SubtotalMinor,
    long LandedCostMinor,
    long TotalMinor,
    string Notes,
    string ReceivedBy,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyList<GoodsReceiptLineRecord> Lines);

public sealed record SupplierReturnLineRequest(
    string GoodsReceiptLineId,
    long QuantityBaseUnits);

public sealed record CreateSupplierReturnRequest(
    IReadOnlyList<SupplierReturnLineRequest>? Items,
    string Reason);

public sealed record SupplierReturnLineRecord(
    string Id,
    string GoodsReceiptLineId,
    string ProductId,
    string ProductName,
    string Sku,
    long QuantityBaseUnits,
    long UnitCostMinor,
    long LineTotalMinor,
    string? BatchId);

public sealed record SupplierReturnRecord(
    string Id,
    string SupplierReturnNumber,
    string PurchaseOrderId,
    string PurchaseOrderNumber,
    string GoodsReceiptId,
    string GoodsReceiptNumber,
    string SupplierId,
    string SupplierName,
    string Status,
    long TotalMinor,
    string Reason,
    string ReturnedBy,
    DateTimeOffset ReturnedAtUtc,
    string CreditJournalId,
    IReadOnlyList<SupplierReturnLineRecord> Lines);

public sealed record ReorderPolicyRequest(
    string ProductId,
    long ReorderPointBaseUnits,
    long TargetStockBaseUnits,
    int LeadTimeDays,
    string? PreferredSupplierId,
    bool IsActive,
    int? ExpectedVersion = null);

public sealed record ReorderPolicyRecord(
    string Id,
    string ShopId,
    string ProductId,
    string ProductName,
    string Sku,
    long ReorderPointBaseUnits,
    long TargetStockBaseUnits,
    int LeadTimeDays,
    string? PreferredSupplierId,
    string PreferredSupplierName,
    bool IsActive,
    int Version,
    DateTimeOffset UpdatedAtUtc);

public sealed record ReorderRecommendationRecord(
    string ShopId,
    string ProductId,
    string ProductName,
    string Sku,
    long AvailableBaseUnits,
    long OnOrderBaseUnits,
    long ReorderPointBaseUnits,
    long TargetStockBaseUnits,
    long SuggestedOrderBaseUnits,
    int LeadTimeDays,
    string? PreferredSupplierId,
    string PreferredSupplierName);

public sealed record CreateStockCountRequest(string? Notes = null);

public sealed record StockCountLineRequest(
    string StockCountLineId,
    long CountedQuantityBaseUnits);

public sealed record SubmitStockCountRequest(
    IReadOnlyList<StockCountLineRequest>? Lines,
    int ExpectedVersion);

public sealed record ApproveStockCountRequest(
    int ExpectedVersion,
    string Reason);

public sealed record StockCountLineRecord(
    string Id,
    string ProductId,
    string ProductName,
    string Sku,
    long SystemQuantityBaseUnits,
    long? CountedQuantityBaseUnits,
    long? VarianceBaseUnits,
    long UnitCostMinor,
    long VarianceValueMinor);

public sealed record StockCountRecord(
    string Id,
    string StockCountNumber,
    string OrganizationId,
    string ShopId,
    string ShopCode,
    string Status,
    string Notes,
    int Version,
    string CreatedBy,
    string? SubmittedBy,
    string? ApprovedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    IReadOnlyList<StockCountLineRecord> Lines);

public sealed record InventoryBatchRecord(
    string Id,
    string ShopId,
    string ProductId,
    string ProductName,
    string Sku,
    string BatchNumber,
    string? ExpiryDate,
    long ReceivedQuantityBaseUnits,
    long AvailableQuantityBaseUnits,
    long UnitCostMinor,
    long LandedCostMinor,
    string Status,
    DateTimeOffset ReceivedAtUtc);

public sealed record ProcurementSummaryRecord(
    string FromDate,
    string ToDate,
    long PurchaseOrderCount,
    long ApprovedOrderValueMinor,
    long GoodsReceiptCount,
    long GoodsReceivedValueMinor,
    long LandedCostMinor,
    long SupplierReturnCount,
    long SupplierReturnValueMinor,
    long OpenOrderCount,
    long OverdueOrderCount,
    long ReorderRecommendationCount,
    long ExpiringBatchCount);

public sealed class ProcurementException : Exception
{
    public ProcurementException(int statusCode, string errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
}
