namespace Robo.Pos.Server.Sales;

public sealed record SalesReturnLineRequest(
    long SaleItemId,
    long Quantity,
    string Disposition = "restock");

public sealed record CreateSalesReturnRequest(
    IReadOnlyList<SalesReturnLineRequest>? Items = null,
    string RefundMethod = "cash",
    string Reason = "",
    string? Notes = null);

public sealed record ReturnableSaleListItem(
    string SaleId,
    string ReceiptNumber,
    string? InvoiceNumber,
    string CustomerName,
    string PaymentMethod,
    long OriginalTotalMinor,
    long ReturnedAmountMinor,
    long RemainingAmountMinor,
    long RemainingQuantity,
    DateTimeOffset CompletedAtUtc,
    string Status,
    string ShopId,
    string ShopCode,
    string ShopName);

public sealed record ReturnableSaleLine(
    long SaleItemId,
    string ProductId,
    string ProductName,
    string Sku,
    long SoldQuantity,
    long ReturnedQuantity,
    long RemainingQuantity,
    string SaleUnit,
    int? UnitSizeMl,
    long UnitPriceMinor,
    long RemainingRefundMinor);

public sealed record ReturnableSaleDetails(
    string SaleId,
    string ReceiptNumber,
    string? InvoiceNumber,
    string CustomerName,
    string PaymentMethod,
    long OriginalTotalMinor,
    long ReturnedAmountMinor,
    long RemainingAmountMinor,
    DateTimeOffset CompletedAtUtc,
    string Status,
    string ShopId,
    string ShopCode,
    string ShopName,
    IReadOnlyList<ReturnableSaleLine> Items);

public sealed record SalesReturnLineRecord(
    long Id,
    long SaleItemId,
    string ProductId,
    string ProductName,
    string Sku,
    long Quantity,
    string SaleUnit,
    int? UnitSizeMl,
    long UnitPriceMinor,
    long UnitCostMinor,
    long RefundMinor,
    long BaseUnitsReturned,
    long CostValueMinor,
    string Disposition,
    long BaseUnitsRestocked,
    long RestockedCostMinor);

public sealed record SalesReturnDocumentRecord(
    string Id,
    string DocumentType,
    string DocumentNumber,
    string FileFormat,
    string RelativePath,
    string FileSha256,
    long FileSizeBytes);

public sealed record SalesReturnRecord(
    string Id,
    string ReturnNumber,
    string SaleId,
    string OriginalReceiptNumber,
    string Status,
    string RefundMethod,
    long RefundAmountMinor,
    long ReturnedBaseUnits,
    long RestockedBaseUnits,
    long ReturnedCostMinor,
    long RestockedCostMinor,
    string Reason,
    string Notes,
    string CreatedByDisplayName,
    string ApprovedByDisplayName,
    DateTimeOffset CompletedAtUtc,
    string ShopId,
    string ShopCode,
    string ShopName,
    IReadOnlyList<SalesReturnLineRecord> Items,
    IReadOnlyList<SalesReturnDocumentRecord> Documents);

public sealed record StoredSalesReturnDocument(
    string FullPath,
    string ContentType,
    string DownloadName);
