namespace Wms.Modules.Inbound.Contracts;

/// <summary>
/// Receiving figures for the admin dashboard (§5.12).
/// </summary>
/// <remarks>
/// Inbound answers for its own tables. The dashboard composes across modules
/// and never queries them directly — otherwise the first cross-module report
/// becomes the precedent that dissolves the module boundary.
/// </remarks>
public interface IInboundReadModel
{
    Task<ReceivingSummary> GetReceivingSummaryAsync(
        Guid warehouseId, CancellationToken cancellationToken);

    Task<ReceiptPage> GetReceiptsAsync(ReceiptQuery query, CancellationToken cancellationToken);

    Task<ReceiptDetail?> GetReceiptAsync(Guid receiptId, CancellationToken cancellationToken);
}

public sealed record ReceiptQuery(
    Guid? WarehouseId,
    string? Status,
    string? SupplierReference,
    DateOnly? BusinessDateFrom,
    DateOnly? BusinessDateTo,
    int Limit,
    Guid? AfterId);

public sealed record ReceiptPage(
    IReadOnlyList<ReceiptRow> Items,
    Guid? NextAfterId,
    bool HasMore);

/// <param name="LinesConfirmed">
/// Counted as lines with a <c>received_quantity</c>, so a line counted as zero
/// reads as done. "The pallet was empty" is a result, not an omission.
/// </param>
public sealed record ReceiptRow(
    Guid Id,
    string ReceiptNumber,
    string Status,
    string? SupplierReference,
    int LinesTotal,
    int LinesConfirmed,
    int DiscrepancyCount,
    DateTimeOffset? ExpectedAt,
    DateTimeOffset? StartedAt);

public sealed record ReceiptDetail(
    Guid Id,
    string ReceiptNumber,
    string Status,
    string ReceiptType,
    Guid OwnerId,
    string OwnerCode,
    string? SupplierReference,
    long Version,
    IReadOnlyList<ReceiptLineRow> Lines);

public sealed record ReceiptLineRow(
    Guid Id,
    int LineNo,
    Guid ItemId,
    string SkuCode,
    string ItemName,
    decimal? ExpectedQuantity,
    decimal? ReceivedQuantity,
    string Uom,
    string? DiscrepancyType,
    string? Status);

/// <param name="ReceiptsCompletedToday">
/// Counted against the warehouse's own business day, not a UTC date — a night
/// shift crossing midnight is one working day and must report as one (M4).
/// </param>
public sealed record ReceivingSummary(
    int OpenReceipts,
    int ReceiptsCompletedToday,
    int OpenDiscrepancies);
