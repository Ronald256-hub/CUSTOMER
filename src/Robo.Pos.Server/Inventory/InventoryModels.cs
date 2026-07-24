namespace Robo.Pos.Server.Inventory;

public sealed record CreateCategoryRequest(
    string Name,
    string? Description,
    int DisplayOrder = 0);

public sealed record CategoryRecord(
    string Id,
    string Name,
    string Description,
    int DisplayOrder,
    bool IsActive);

public sealed record CreateProductRequest(
    string? CategoryId,
    string Sku,
    string? Barcode,
    string Name,
    string? Description,
    string ProductType,
    string StockUnit,
    string SaleUnit,
    int? BottleVolumeMl,
    int? GlassSizeMl,
    int? UnitsPerCrate,
    long CostPriceMinor,
    long SellingPriceMinor,
    long LowStockThreshold,
    long OpeningStockBaseUnits,
    bool AllowNegativeStock = false,
    bool TrackExpiry = false);

public sealed record UpdateProductPriceRequest(
    long CostPriceMinor,
    long SellingPriceMinor,
    string Reason,
    int ExpectedVersion);

public sealed record StockAdjustmentRequest(
    string MovementType,
    long? QuantityDeltaBaseUnits,
    long? NewQuantityBaseUnits,
    string Reason,
    int ExpectedStockVersion);

public sealed record ProductCatalogItem(
    string Id,
    string? CategoryId,
    string? CategoryName,
    string Sku,
    string? Barcode,
    string Name,
    string Description,
    string ProductType,
    string StockUnit,
    string SaleUnit,
    int? BottleVolumeMl,
    int? GlassSizeMl,
    int? UnitsPerCrate,
    long? CostPriceMinor,
    long SellingPriceMinor,
    long LowStockThreshold,
    long QuantityBaseUnits,
    long ReservedBaseUnits,
    long AvailableBaseUnits,
    bool IsLowStock,
    bool AllowNegativeStock,
    bool TrackExpiry,
    bool IsActive,
    int Version,
    int StockVersion);

public sealed record PriceChangeRecord(
    string ProductId,
    string ProductName,
    long PreviousCostPriceMinor,
    long NewCostPriceMinor,
    long PreviousSellingPriceMinor,
    long NewSellingPriceMinor,
    int Version,
    DateTimeOffset ChangedAtUtc);

public sealed record StockAdjustmentRecord(
    string ProductId,
    string ProductName,
    string MovementType,
    long QuantityDeltaBaseUnits,
    long BalanceAfterBaseUnits,
    int StockVersion,
    DateTimeOffset AdjustedAtUtc);

public sealed class InventoryException : Exception
{
    public InventoryException(
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
