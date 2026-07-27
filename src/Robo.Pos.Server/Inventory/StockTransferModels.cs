namespace Robo.Pos.Server.Inventory;

public sealed record StockTransferItemInput(
    string ProductId,
    long QuantityBaseUnits);

public sealed record CreateStockTransferRequest(
    string DestinationShopId,
    string? Notes,
    IReadOnlyList<StockTransferItemInput>? Items);

public sealed record UpdateStockTransferDraftRequest(
    int ExpectedVersion,
    string? Notes,
    IReadOnlyList<StockTransferItemInput>? Items);

public sealed record StockTransferTransitionRequest(
    int ExpectedVersion,
    string? Notes = null);

public sealed record CancelStockTransferRequest(
    int ExpectedVersion,
    string Reason);

public sealed record ReceiveStockTransferItemRequest(
    string ProductId,
    long QuantityReceivedBaseUnits,
    long QuantityDamagedBaseUnits,
    string? DiscrepancyReason);

public sealed record ReceiveStockTransferRequest(
    int ExpectedVersion,
    bool Finalize,
    string? Notes,
    IReadOnlyList<ReceiveStockTransferItemRequest>? Items);

public sealed record StockTransferItemRecord(
    long Id,
    string ProductId,
    string Sku,
    string ProductName,
    long RequestedQuantityBaseUnits,
    long ReservedQuantityBaseUnits,
    long DispatchedQuantityBaseUnits,
    long ReceivedQuantityBaseUnits,
    long DamagedQuantityBaseUnits,
    long OutstandingQuantityBaseUnits,
    long UnitCostMinor,
    string DiscrepancyReason,
    long? SourceBalanceBefore,
    long? SourceBalanceAfter,
    long? DestinationBalanceBefore,
    long? DestinationBalanceAfter);

public sealed record StockTransferEventRecord(
    long Id,
    string EventType,
    string? FromStatus,
    string ToStatus,
    string DetailsJson,
    string PerformedByUserId,
    string PerformedByDisplayName,
    DateTimeOffset OccurredAtUtc);

public sealed record StockTransferRecord(
    string Id,
    string TransferNumber,
    string OrganizationId,
    string SourceShopId,
    string SourceShopCode,
    string SourceShopName,
    string DestinationShopId,
    string DestinationShopCode,
    string DestinationShopName,
    string Status,
    string Notes,
    string CreatedByUserId,
    string CreatedByDisplayName,
    string? SubmittedByUserId,
    string? ApprovedByUserId,
    string? DispatchedByUserId,
    string? ReceivedByUserId,
    string? CancelledByUserId,
    string? CancellationKind,
    string? CancellationReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? ReceivedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int Version,
    long RequestedQuantityBaseUnits,
    long ReservedQuantityBaseUnits,
    long DispatchedQuantityBaseUnits,
    long ReceivedQuantityBaseUnits,
    long DamagedQuantityBaseUnits,
    long OutstandingQuantityBaseUnits,
    IReadOnlyList<StockTransferItemRecord> Items,
    IReadOnlyList<StockTransferEventRecord> Events);

public sealed record StockTransferListItem(
    string Id,
    string TransferNumber,
    string SourceShopId,
    string SourceShopCode,
    string SourceShopName,
    string DestinationShopId,
    string DestinationShopCode,
    string DestinationShopName,
    string Status,
    long RequestedQuantityBaseUnits,
    long DispatchedQuantityBaseUnits,
    long ReceivedQuantityBaseUnits,
    long DamagedQuantityBaseUnits,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int Version);

public sealed record StockTransferStatusSummary(
    string Status,
    long TransferCount,
    long RequestedQuantityBaseUnits,
    long DispatchedQuantityBaseUnits,
    long ReceivedQuantityBaseUnits,
    long DamagedQuantityBaseUnits,
    long OutstandingQuantityBaseUnits);

public sealed record StockTransferReport(
    string Scope,
    string OrganizationId,
    string OrganizationName,
    string? ShopId,
    string? ShopCode,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long TransferCount,
    long RequestedQuantityBaseUnits,
    long DispatchedQuantityBaseUnits,
    long ReceivedQuantityBaseUnits,
    long DamagedQuantityBaseUnits,
    long InTransitQuantityBaseUnits,
    IReadOnlyList<StockTransferStatusSummary> Statuses);

public sealed class StockTransferException : Exception
{
    public StockTransferException(
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