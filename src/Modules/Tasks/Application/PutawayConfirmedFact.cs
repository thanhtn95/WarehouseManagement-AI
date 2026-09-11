namespace Wms.Modules.Tasks.Application;

/// <summary>
/// A report that stock was physically carried from one location to another
/// and put down (§4.2, §6.3).
/// </summary>
/// <remarks>
/// The operator scanned the source, the item, and the destination, and the
/// stock is now in the destination. Every part of that already happened
/// before the server hears about it — usually while the device was offline —
/// so it may not be refused. Where the operator used a different bin than the
/// one directed, the movement records where the stock <em>actually</em> went
/// and raises <c>location_mismatch</c>. Rejecting it would leave the system
/// believing stock is somewhere it is not, which is strictly worse than an
/// exception.
/// </remarks>
/// <param name="ClientFactId">
/// The envelope's per-fact id, which doubles as the idempotency key
/// (invariant 7).
/// </param>
/// <param name="FromLocationId">
/// Where the operator actually took the stock from, not necessarily where
/// the task directed them.
/// </param>
/// <param name="ToLocationId">
/// Where the operator actually put it. A directed bin that turned out to be
/// full or blocked is the normal reason this differs.
/// </param>
public sealed record PutawayConfirmedFact(
    string ClientFactId,
    Guid TaskLineId,
    Guid FromLocationId,
    Guid ToLocationId,
    decimal Quantity,
    string Uom,
    string? ReasonCode,
    Guid ActorUserId,
    Guid? DeviceId,
    DateTimeOffset? OccurredAtDevice = null);
