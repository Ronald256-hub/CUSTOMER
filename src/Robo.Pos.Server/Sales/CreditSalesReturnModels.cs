namespace Robo.Pos.Server.Sales;

public sealed record CreditSalesReturnLineRequest(
    long SaleItemId,
    long Quantity,
    string Disposition = "restock");

public sealed record CreateCreditSalesReturnRequest(
    IReadOnlyList<CreditSalesReturnLineRequest>? Items = null,
    string Reason = "",
    string? Notes = null);

public sealed record CreditReturnableSaleListItem(
    string SaleId,
    string ReceiptNumber,
    string? InvoiceNumber,
    string CustomerId,
    string CustomerNumber,
    string CustomerName,
    string ReceivableItemId,
    long OriginalTotalMinor,
    long ReturnedAmountMinor,
    long RemainingReturnAmountMinor,
    long ReceivableOriginalAmountMinor,
    long ReceivableSettledAmountMinor,
    long ReceivableOutstandingAmountMinor,
    long RemainingQuantity,
    DateTimeOffset CompletedAtUtc,
    string Status,
    string ShopId,
    string ShopCode,
    string ShopName);

public sealed record CreditReturnableSaleDetails(
    string SaleId,
    string ReceiptNumber,
    string? InvoiceNumber,
    string CustomerId,
    string CustomerNumber,
    string CustomerName,
    string ReceivableItemId,
    long OriginalTotalMinor,
    long ReturnedAmountMinor,
    long RemainingReturnAmountMinor,
    long ReceivableOriginalAmountMinor,
    long ReceivableSettledAmountMinor,
    long ReceivableOutstandingAmountMinor,
    DateTimeOffset CompletedAtUtc,
    string Status,
    string ShopId,
    string ShopCode,
    string ShopName,
    IReadOnlyList<ReturnableSaleLine> Items);

public sealed record CreditSalesReturnRecord(
    string Id,
    string CreditNoteNumber,
    string SaleId,
    string OriginalReceiptNumber,
    string CustomerId,
    string CustomerNumber,
    string CustomerName,
    string ReceivableItemId,
    string Status,
    long ReturnAmountMinor,
    long ReceivableReductionMinor,
    long CustomerCreditMinor,
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

public sealed record CustomerCreditBalanceRecord(
    string Id,
    string CustomerId,
    string CustomerNumber,
    string CustomerName,
    string CreditNoteNumber,
    string SourceCreditReturnId,
    long OriginalAmountMinor,
    long AppliedAmountMinor,
    long AvailableAmountMinor,
    string Status,
    string ShopId,
    string ShopCode,
    DateTimeOffset CreatedAtUtc);

public sealed record ApplyCustomerCreditRequest(
    string CreditId = "",
    string ReceivableItemId = "",
    string ApplicationDate = "",
    long AmountMinor = 0,
    string? Notes = null);

public sealed record CustomerCreditApplicationRecord(
    string Id,
    string ApplicationNumber,
    string ApplicationDate,
    string CreditId,
    string CreditNoteNumber,
    string CustomerId,
    string CustomerNumber,
    string CustomerName,
    string ReceivableItemId,
    string ReceivableDocumentNumber,
    long AmountMinor,
    string Notes,
    string CreatedByDisplayName,
    DateTimeOffset CreatedAtUtc,
    string ShopId,
    string ShopCode);
