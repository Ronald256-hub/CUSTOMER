namespace Robo.Pos.Server.Sales;

public sealed record VoidSaleRequest(
    string? Reason = null);

public sealed record VoidSaleResult(
    string SaleId,
    string ReceiptNumber,
    string? InvoiceNumber,
    string Status,
    string VoidReason,
    DateTimeOffset VoidedAtUtc,
    string VoidedByUserId,
    string VoidedByDisplayName,
    int RestoredProductCount,
    long RestoredBaseUnits);

public sealed record SaleVoidMetadata(
    string? VoidReason,
    DateTimeOffset? VoidedAtUtc,
    string? VoidedByDisplayName);
