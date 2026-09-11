namespace Wms.Modules.Inventory.Contracts;

/// <summary>
/// Read-only projections over the ledger, balances and the exception queue
/// (§5.5).
/// </summary>
/// <remarks>
/// Exposed through <c>Contracts</c> because the admin dashboard aggregates
/// across four modules and must not reach into any of their tables directly.
/// Each module answers for its own data; the dashboard only composes.
/// </remarks>
public interface IInventoryReadModel
{
    Task<BalancePage> GetBalancesAsync(BalanceQuery query, CancellationToken cancellationToken);

    Task<ExceptionPage> GetExceptionsAsync(ExceptionQuery query, CancellationToken cancellationToken);

    Task<StockSummary> GetStockSummaryAsync(Guid warehouseId, CancellationToken cancellationToken);

    Task<ReconciliationSummary?> GetLatestReconciliationAsync(
        Guid warehouseId, CancellationToken cancellationToken);

    Task<MovementPage> GetMovementsAsync(MovementQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReconciliationRun>> GetReconciliationRunsAsync(
        Guid? warehouseId, int limit, CancellationToken cancellationToken);
}

/// <param name="AfterSequence">
/// The ledger's own cursor. Ordered by <c>sequence</c>, never by timestamp —
/// a device clock is forensic and two movements can share a millisecond, so
/// a time-ordered cursor would skip or repeat rows (C4, invariant 5).
/// </param>
public sealed record MovementQuery(
    Guid? WarehouseId,
    Guid? ItemId,
    Guid? LocationId,
    string? MovementType,
    Guid? ReferenceId,
    DateOnly? BusinessDateFrom,
    long? AfterSequence,
    int Limit);

public sealed record MovementPage(
    IReadOnlyList<MovementRow> Items,
    long? NextAfterSequence,
    bool HasMore);

/// <param name="QuantityBase">
/// Signed for the reader: negative when stock left and nothing received it.
/// The stored value is always positive — direction lives in the location
/// columns — but a ledger view that showed a pick as <c>+12</c> would be read
/// wrong by everyone.
/// </param>
/// <param name="DeviceReportedAt">
/// What the handheld believed the time was. Present for forensics and never
/// used for ordering.
/// </param>
public sealed record MovementRow(
    Guid Id,
    long Sequence,
    DateTimeOffset RecordedAt,
    DateTimeOffset? DeviceReportedAt,
    DateOnly BusinessDate,
    string MovementType,
    Guid ItemId,
    string SkuCode,
    string? FromLocationCode,
    string? ToLocationCode,
    decimal QuantityBase,
    decimal EnteredQuantity,
    string EnteredUom,
    string? ReasonCode,
    string? ReferenceType,
    Guid? ReferenceId,
    Guid ActorUserId,
    string? ActorDisplayName,
    string? DeviceLabel,
    Guid? AuthorizedByUserId);

public sealed record ReconciliationRun(
    Guid Id,
    Guid WarehouseId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long FromSequence,
    long ToSequence,
    int RowsChecked,
    int VarianceCount,
    string Status);

/// <param name="AfterKey">
/// Keyset cursor, never an offset (§3.4). On a table that grows to hundreds
/// of millions of rows, <c>OFFSET</c> re-reads and discards everything it
/// skips, so deep pages get linearly slower and the last page of a report is
/// the one that times out.
/// </param>
/// <param name="IncludeZero">
/// Positions that have netted to zero are hidden by default: an empty bin is
/// noise on a stock report. Negative positions are always shown — a negative
/// balance is true information and hiding it would defeat the ledger (C1, C3).
/// </param>
public sealed record BalanceQuery(
    Guid? WarehouseId,
    Guid? ItemId,
    Guid? LocationId,
    Guid? ZoneId,
    Guid? OwnerId,
    string? StockStatus,
    bool IncludeZero,
    int Limit,
    string? AfterKey);

public sealed record BalancePage(
    IReadOnlyList<BalanceRow> Items,
    BalanceTotals Totals,
    string? NextAfterKey,
    bool HasMore);

/// <param name="Available">
/// Computed, never stored. Collapsing on-hand, allocated and available into
/// fewer numbers is what causes overselling.
/// </param>
public sealed record BalanceRow(
    Guid OwnerId,
    string OwnerCode,
    Guid ItemId,
    string SkuCode,
    string ItemName,
    Guid LocationId,
    string LocationCode,
    string ZoneCode,
    Guid LotId,
    string StockStatus,
    decimal OnHand,
    decimal Allocated,
    decimal Available,
    string Uom,
    long Version,
    DateTimeOffset UpdatedAt);

public sealed record BalanceTotals(decimal OnHand, decimal Allocated, decimal Available);

public sealed record ExceptionQuery(
    Guid? WarehouseId,
    string? Status,
    string? ExceptionType,
    double? MinAgeHours,
    int Limit);

/// <param name="Summary">
/// Drives the operational dashboard. The exception queue is a work surface,
/// not an error log, so the counts matter as much as the rows.
/// </param>
public sealed record ExceptionPage(
    IReadOnlyList<ExceptionRow> Items,
    ExceptionSummary Summary);

public sealed record ExceptionRow(
    Guid Id,
    string ExceptionType,
    string Severity,
    DateTimeOffset RaisedAt,
    double AgeHours,
    string? SkuCode,
    string? ItemName,
    string? LocationCode,
    decimal? ExpectedQuantity,
    decimal? ActualQuantity,
    decimal? Difference,
    Guid? StockMovementId,
    Guid? TaskId,
    string? RaisedByDisplayName,
    string? DeviceLabel,
    string Status);

public sealed record ExceptionSummary(
    int Open,
    double? OldestAgeHours,
    IReadOnlyDictionary<string, int> ByType);

public sealed record StockSummary(int SkuCount, decimal OnHand);

public sealed record ReconciliationSummary(DateTimeOffset? LastRunAt, int VarianceCount);
