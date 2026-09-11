namespace Wms.Modules.Tasks.Application;

/// <param name="ZoneIds">
/// Empty means any zone in the warehouse. Once scope reaches the principal
/// (see <c>docs/shortcuts.md</c>) this is narrowed by what the operator is
/// actually granted, rather than trusted from the request — Phase 1A exit
/// criterion 5 for picking depends on it.
/// </param>
/// <param name="BatchSize">
/// How much work to hand over at once. Large enough that an operator can go
/// offline for a stretch, small enough that reclaiming an abandoned device
/// does not strand a shift's worth of tasks.
/// </param>
public sealed record LeaseRequest(
    Guid WarehouseId,
    IReadOnlyList<string> TaskTypes,
    IReadOnlyList<Guid> ZoneIds,
    int BatchSize,
    Guid UserId,
    Guid? DeviceId);

/// <param name="LeaseId">
/// Null when nothing was available. §5.3 returns that as <c>200</c>, not
/// <c>404</c> — an idle poller is a normal state.
/// </param>
public sealed record LeasedWork(
    Guid? LeaseId,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<LeasedTask> Tasks,
    IReadOnlyList<LeasedReasonCode> ReasonCodes);

public sealed record LeasedTask(
    Guid Id,
    string TaskType,
    int Priority,
    IReadOnlyList<LeasedLine> Lines);

/// <param name="IsDiscrete">
/// Whether the UoM rejects fractional quantities, so the handheld can refuse
/// "2.5 cases" at the keypad rather than letting the server find it later.
/// </param>
public sealed record LeasedLine(
    Guid Id,
    int LineNo,
    LeasedLocation? From,
    LeasedLocation? To,
    LeasedItem Item,
    decimal RequestedQuantity,
    string Uom,
    bool IsDiscrete);

/// <param name="PickSequence">
/// Physical walk order — what makes route optimisation possible at all. Null
/// on a destination, where it carries no meaning.
/// </param>
public sealed record LeasedLocation(Guid Id, string Code, int? PickSequence);

/// <param name="Name">
/// Already resolved to the operator's locale, falling back to English. The
/// device holds no translation table.
/// </param>
/// <param name="Barcodes">
/// Every barcode across the item's UoM levels, so a scan can be validated
/// offline against the cached batch.
/// </param>
public sealed record LeasedItem(
    Guid Id,
    string SkuCode,
    string Name,
    IReadOnlyList<string> Barcodes);

/// <summary>
/// A reason code the operator may choose while offline, with the flags that
/// drive validation on the device.
/// </summary>
public sealed record LeasedReasonCode(
    string Code,
    string Label,
    bool RequiresNote,
    bool RequiresPhoto,
    bool RequiresApproval);
