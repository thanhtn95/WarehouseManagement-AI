namespace Wms.Modules.Inbound.Application;

/// <summary>
/// A report that stock physically arrived and was counted.
/// </summary>
/// <remarks>
/// This is a <em>fact</em>, not a command: the goods are on the dock and the
/// count already happened. The server may not refuse it. Where the counted
/// quantity disagrees with what was expected, the movement is still written
/// and an exception is raised for a supervisor — the discrepancy is a
/// supervisor's problem surfaced through the exception queue, not a
/// receiver's problem surfaced as a modal on a handheld.
/// </remarks>
/// <param name="ClientFactId">
/// The envelope's per-fact <c>clientFactId</c> (§4.1), which doubles as the
/// idempotency key. Named for its origin deliberately: a batch carries one
/// <c>Idempotency-Key</c> header for twenty facts, and wiring that header
/// here instead would record only the first fact and replay-suppress the
/// other nineteen — losing nineteen movements while reporting success.
/// Invariant 7 also requires it to have been generated at the moment of the
/// operator's action, not at send time.
/// </param>
public sealed record ReceiptConfirmedFact(
    string ClientFactId,
    Guid ReceiptLineId,
    decimal Quantity,
    string Uom,
    Guid ToLocationId,
    string? ReasonCode,
    Guid ActorUserId,
    Guid? DeviceId,
    DateTimeOffset? OccurredAtDevice = null);
