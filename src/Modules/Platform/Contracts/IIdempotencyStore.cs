using Npgsql;

namespace Wms.Modules.Platform.Contracts;

/// <summary>
/// Step 1 and step 7 of the fact-processing contract (§4.3).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Claims the key as the first statement of the transaction.
    /// </summary>
    /// <remarks>
    /// Claiming first, rather than checking then claiming, is what makes two
    /// simultaneous replays safe. The loser does <em>not</em> hit a
    /// primary-key violation: <c>ON CONFLICT DO NOTHING</c> blocks on the
    /// winner's speculative insert, returns no row once the winner commits,
    /// and the follow-up read then sees the winner's stored response. That
    /// read depends on READ COMMITTED's per-statement snapshots, so callers
    /// must begin the transaction at that isolation level explicitly rather
    /// than inheriting a server default.
    /// </remarks>
    Task<IdempotencyClaim> TryClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        Guid userId,
        string requestHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the response against the claimed key, so a later replay
    /// returns exactly what the original call returned.
    /// </summary>
    Task CompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        Guid userId,
        int responseStatus,
        string responseBody,
        CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of claiming an idempotency key.
/// </summary>
/// <param name="Outcome">Whether this is new work, a replay, or a conflict.</param>
/// <param name="StoredResponseBody">
/// Present only for <see cref="IdempotencyOutcome.Replay"/>.
/// </param>
public sealed record IdempotencyClaim(IdempotencyOutcome Outcome, string? StoredResponseBody);

public enum IdempotencyOutcome
{
    /// <summary>First time this key has been seen. Proceed with the work.</summary>
    Claimed,

    /// <summary>
    /// Same key, same request. Return the stored response without doing the
    /// work again — this is the retry a handheld makes over bad wifi.
    /// </summary>
    Replay,

    /// <summary>
    /// Same key, different request. A client bug, not a retry: answering it
    /// with either version would silently pick one at random.
    /// </summary>
    Conflict,
}
