namespace Robo.Pos.Server.Sales;

public sealed record OpenShiftRequest(
    long OpeningCashMinor = 0);

public sealed record CloseShiftRequest(
    long CountedCashMinor = 0,
    string? Notes = null);

public sealed record ShiftRecord(
    string Id,
    string TellerUserId,
    string TellerName,
    string Status,
    long OpeningCashMinor,
    long? ExpectedCashMinor,
    long? CountedCashMinor,
    long? CashVarianceMinor,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc);

public sealed record SaleLineRequest(
    string ProductId,
    long Quantity);

public sealed record CompleteSaleRequest(
    IReadOnlyList<SaleLineRequest>? Items = null,
    string PaymentMethod = "cash",
    long AmountReceivedMinor = 0,
    bool IssueInvoice = false,
    string? CustomerName = null,
    string? CustomerPhone = null,
    string? CustomerAddress = null,
    string? CustomerTaxNumber = null,
    string? Notes = null);

public sealed record CompletedSaleLine(
    string ProductId,
    string ProductName,
    string Sku,
    long Quantity,
    string SaleUnit,
    int? UnitSizeMl,
    long UnitPriceMinor,
    long LineTotalMinor);

public sealed record GeneratedSaleDocument(
    string Id,
    string DocumentType,
    string DocumentNumber,
    string FileFormat,
    string RelativePath,
    string FileSha256,
    long FileSizeBytes);

public sealed record CompleteSaleResult(
    string SaleId,
    string ReceiptNumber,
    string? InvoiceNumber,
    string TellerName,
    long SubtotalMinor,
    long TotalMinor,
    long AmountReceivedMinor,
    long ChangeMinor,
    string PaymentMethod,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<CompletedSaleLine> Items,
    IReadOnlyList<GeneratedSaleDocument> Documents);

public sealed record ReceiptListItem(
    string SaleId,
    string ReceiptNumber,
    string? InvoiceNumber,
    string TellerName,
    string Status,
    long TotalMinor,
    string PaymentMethod,
    DateTimeOffset CompletedAtUtc,
    int DocumentCount);

public sealed record ReceiptDetails(
    string SaleId,
    string ReceiptNumber,
    string? InvoiceNumber,
    string TellerName,
    string Status,
    string CustomerName,
    string CustomerPhone,
    string CustomerAddress,
    string CustomerTaxNumber,
    long SubtotalMinor,
    long DiscountMinor,
    long TotalMinor,
    long AmountReceivedMinor,
    long ChangeMinor,
    string PaymentMethod,
    string Notes,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<CompletedSaleLine> Items,
    IReadOnlyList<GeneratedSaleDocument> Documents);

public sealed record StoredDocumentFile(
    string FullPath,
    string ContentType,
    string DownloadName);

public sealed class SalesException : Exception
{
    public SalesException(
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
