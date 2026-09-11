namespace Wms.Modules.Inbound.Application;

/// <param name="Lines">
/// Empty for a blind receipt, which claims nothing in advance — that is what
/// makes every count on it non-divergent by definition (§6.2).
/// </param>
public sealed record CreateReceiptCommand(
    Guid WarehouseId,
    string ReceiptType,
    Guid OwnerId,
    string? SupplierReference,
    DateTimeOffset? ExpectedAt,
    Guid ActorUserId,
    IReadOnlyList<CreateReceiptLine> Lines);

public sealed record CreateReceiptLine(Guid ItemId, decimal? ExpectedQuantity, string Uom);

public sealed record CreatedReceipt(
    Guid Id, string ReceiptNumber, string Status, IReadOnlyList<Guid> LineIds);

/// <summary>
/// The outcome of a state-changing receipt command.
/// </summary>
/// <remarks>
/// Commands may be refused — nothing physical has happened when a supervisor
/// presses a button, so a refusal asks nobody to undo anything. That is the
/// entire difference between this type and <c>FactResult</c>, and the reason
/// they are deliberately not the same type.
/// </remarks>
public sealed record ReceiptTransition(
    ReceiptOutcome Outcome, string? Status, string? FromStatus, string? ToStatus)
{
    public static ReceiptTransition Succeeded(string status) =>
        new(ReceiptOutcome.Succeeded, status, null, null);

    public static ReceiptTransition NotFound() =>
        new(ReceiptOutcome.NotFound, null, null, null);

    public static ReceiptTransition InvalidTransition(string from, string to) =>
        new(ReceiptOutcome.InvalidTransition, null, from, to);
}

public sealed record ReceiptCompletion(
    ReceiptOutcome Outcome,
    int PutawayTasksCreated,
    string? FromStatus,
    IReadOnlyList<int>? UnconfirmedLineNos)
{
    public static ReceiptCompletion Succeeded(int tasksCreated) =>
        new(ReceiptOutcome.Succeeded, tasksCreated, null, null);

    public static ReceiptCompletion NotFound() =>
        new(ReceiptOutcome.NotFound, 0, null, null);

    public static ReceiptCompletion InvalidTransition(string from) =>
        new(ReceiptOutcome.InvalidTransition, 0, from, null);

    public static ReceiptCompletion LinesUnconfirmed(IReadOnlyList<int> lineNos) =>
        new(ReceiptOutcome.LinesUnconfirmed, 0, null, lineNos);
}

public enum ReceiptOutcome
{
    Succeeded,
    NotFound,
    InvalidTransition,

    /// <summary>
    /// Completion attempted while lines still have no count at all. A line
    /// counted as <em>zero</em> is confirmed and does not appear here — "the
    /// pallet was empty" is a result, not an omission.
    /// </summary>
    LinesUnconfirmed,
}
