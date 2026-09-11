using Npgsql;

namespace Wms.Modules.Inventory.Contracts;

/// <summary>
/// Resolves a warehouse's current business date from its own day boundary
/// (M4).
/// </summary>
/// <remarks>
/// Shifts cross midnight, so "what did we ship on the 3rd" is not the same
/// question as "what has a UTC timestamp on the 3rd". The rule lives here,
/// once, because the alternative is every caller re-deriving it — and they
/// would not all do it the same way. Two callers already need it: the ledger
/// stamps <c>stock_movement.business_date</c>, and document numbering keys
/// <c>document_sequence</c> by it, which is what makes
/// <c>RCV-TKY-260909-0042</c> restart at 0001 on the warehouse's own next
/// day rather than at a UTC midnight in the middle of a night shift.
/// </remarks>
public interface IBusinessCalendar
{
    Task<DateOnly> ResolveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid warehouseId,
        CancellationToken cancellationToken);
}
