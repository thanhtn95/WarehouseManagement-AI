namespace Wms.SharedKernel;

/// <summary>
/// The per-fact result that becomes one entry in the <c>202</c> envelope's
/// <c>results</c> array (§4.1).
/// </summary>
/// <remarks>
/// There is deliberately no failure case here for a business-rule
/// disagreement — that is what <see cref="ExceptionId"/> is for. A handler
/// returns this for every outcome rather than throwing, because one bad fact
/// in a batch of twenty must not fail the other nineteen, and must never
/// reach a handheld as a 5xx.
///
/// It lives in the shared kernel rather than in one module because every
/// fact type in the registry (§4.2) answers in this shape, and a module
/// reaching into another module's <c>Application</c> namespace to borrow it
/// would breach the boundary rule the architecture tests enforce.
/// </remarks>
public sealed record FactResult(
    FactStatus Status,
    Guid? MovementId,
    long? Sequence,
    Guid? ExceptionId,
    FactRejection? Rejection = null)
{
    public static FactResult Accepted(Guid movementId, long sequence, Guid? exceptionId) =>
        new(FactStatus.Accepted, movementId, sequence, exceptionId);

    public static FactResult Rejected(FactRejection rejection) =>
        new(FactStatus.Rejected, null, null, null, rejection);
}

/// <summary>
/// §4.1's <c>status</c> values, and the complete set of them.
/// </summary>
public enum FactStatus
{
    /// <summary>The fact was applied. A divergence may still have raised an exception.</summary>
    Accepted,

    /// <summary>
    /// Already applied under this <c>clientFactId</c>. The stored result is
    /// returned verbatim — this is the retry a handheld makes over bad wifi.
    /// </summary>
    Duplicate,

    /// <summary>
    /// The fact could not be applied at all. Reserved for payloads that are
    /// wrong in themselves, <strong>never</strong> for a business-rule
    /// refusal — a quantity that disagrees with expectation is
    /// <see cref="Accepted"/> with an exception, not this.
    /// </summary>
    Rejected,
}

/// <summary>
/// Why a fact could not be applied, so the device can distinguish "retrying
/// will never help" from "try again later" without parsing prose.
/// </summary>
public enum FactRejection
{
    /// <summary>
    /// The referenced receipt line does not exist. The device is holding a
    /// fact against something deleted or never synced; retrying is futile and
    /// it should be surfaced for support rather than requeued.
    /// </summary>
    UnknownReceiptLine,

    /// <summary>
    /// The referenced task line does not exist — the same situation, for work
    /// confirmed against a task rather than a receipt.
    /// </summary>
    UnknownTaskLine,

    /// <summary>
    /// A location named by the payload does not exist. Left to the database
    /// this would surface as a foreign-key violation escaping the per-fact
    /// boundary and failing the whole batch.
    /// </summary>
    UnknownLocation,

    /// <summary>
    /// A location named by the payload is a real bin belonging to a
    /// <em>different</em> warehouse than the work it confirms against.
    /// Accepting it would put stock in site B while dating the movement by
    /// site A's day boundary and raising any discrepancy into site A's
    /// exception queue — a divergence that reaches nobody who can see the
    /// stock.
    /// </summary>
    LocationInAnotherWarehouse,

    /// <summary>
    /// The same <c>clientFactId</c> arrived carrying a different payload — a
    /// client bug, not a retry, since answering with either version would
    /// silently pick one at random. §4.3 step 1 calls this a <c>409</c>; in a
    /// batch it is this per-fact rejection, because failing the whole request
    /// would punish the nineteen well-formed facts travelling with it.
    /// </summary>
    IdempotencyKeyReused,
}
