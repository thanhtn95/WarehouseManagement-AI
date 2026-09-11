using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Wms.Modules.Identity.Contracts;
using Wms.Modules.Inbound.Application;
using Wms.Modules.Tasks.Application;
using Wms.SharedKernel;

namespace Wms.Api.Sync;

/// <summary>
/// <c>POST /api/v1/sync/facts</c> — the only way stock quantities change
/// through operator action (§4.1, §5.3).
/// </summary>
/// <remarks>
/// The contract this implements, and the reason each part of it exists:
/// the response is <strong>always <c>202</c></strong> once the batch itself
/// parses and the caller is authenticated, because a device that has already
/// moved goods cannot act on a refusal; a fact that cannot be applied is one
/// <c>rejected</c> entry beside its accepted neighbours, never a failed
/// batch; and every result is keyed by <c>clientFactId</c> so the device can
/// clear exactly what landed.
/// </remarks>
public static class SyncFactsEndpoint
{
    public static void MapSyncFacts(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/sync/facts", HandleAsync)
            .WithName("SubmitFacts")
            .WithSummary("Submit a batch of facts recorded on a device.")
            .WithDescription(
                "Physical events that already happened. Accepted even when they " +
                "disagree with server expectation — the divergence becomes an " +
                "inventory_exception, never an error.");
    }

    private static async Task<IResult> HandleAsync(
        FactBatch? batch,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        NpgsqlDataSource dataSource,
        FactHandlers handlers,
        CancellationToken cancellationToken)
    {
        // A batch that does not parse is the one thing this endpoint may
        // refuse outright: there is nothing to record and no per-fact result
        // to report against (§4.1).
        if (batch?.Facts is not { Count: > 0 })
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                ProblemTypes.MalformedRequest,
                "A batch must contain at least one fact.");
        }

        OperatorPrincipal? principal = await principals.ResolveAsync(
            http.Request.Headers.Authorization.ToString(),
            http.Request.Headers["X-Device-Id"].ToString(),
            cancellationToken);

        if (principal is null)
        {
            return Problem(
                StatusCodes.Status401Unauthorized,
                ProblemTypes.Unauthenticated,
                "No authenticated operator.");
        }

        // One connection for the whole batch, and the facts are applied
        // SEQUENTIALLY on it. An NpgsqlConnection is single-threaded, so
        // dispatching the batch with Task.WhenAll would interleave
        // transactions on one connection; opening one connection per fact
        // would instead exhaust the reserved wms_api pool under fifty
        // operators, which is the stall invariant 10 reserves it to prevent.
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        List<FactOutcome> results = new(batch.Facts.Count);
        foreach (FactEnvelope fact in batch.Facts)
        {
            results.Add(await ApplyAsync(
                fact, principal, connection, handlers, cancellationToken));
        }

        return Results.Accepted(value: new FactBatchResponse(results, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Applies one fact, converting every outcome — including failure — into
    /// a result. Nothing here throws for bad input.
    /// </summary>
    private static async Task<FactOutcome> ApplyAsync(
        FactEnvelope fact,
        OperatorPrincipal principal,
        NpgsqlConnection connection,
        FactHandlers handlers,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fact.ClientFactId))
        {
            return FactOutcome.Rejected(
                fact.ClientFactId, ProblemTypes.MalformedRequest,
                "clientFactId is required; it is the idempotency key.");
        }

        // Permission is checked per fact type, not per batch: a batch may
        // legitimately mix types, and §5.3 states the required permission
        // "varies by fact type".
        if (!FactTypes.TryRequiredPermission(fact.Type, out string? permission))
        {
            return FactOutcome.Rejected(
                fact.ClientFactId, ProblemTypes.MalformedRequest,
                $"Unknown fact type '{fact.Type}'.");
        }

        if (!principal.Can(permission))
        {
            // A permission failure is about who is asking, not about what
            // physically happened, so it is safe to refuse and the device can
            // act on it — by elevating (§6.5). It is still reported per fact
            // rather than failing the batch.
            return FactOutcome.Rejected(
                fact.ClientFactId, ProblemTypes.InsufficientPermission,
                $"Requires '{permission}'.");
        }

        return fact.Type switch
        {
            FactTypes.ReceiptConfirmed => await ApplyReceiptAsync(
                fact, principal, connection, handlers.Receipt, cancellationToken),
            FactTypes.PutawayConfirmed => await ApplyPutawayAsync(
                fact, principal, connection, handlers.Putaway, cancellationToken),
            _ => FactOutcome.Rejected(
                fact.ClientFactId, ProblemTypes.MalformedRequest,
                $"Fact type '{fact.Type}' is not implemented in this phase."),
        };
    }

    private static async Task<FactOutcome> ApplyReceiptAsync(
        FactEnvelope fact,
        OperatorPrincipal principal,
        NpgsqlConnection connection,
        ConfirmReceiptFactHandler receiptHandler,
        CancellationToken cancellationToken)
    {
        if (!TryReadPayload(fact, out ReceiptConfirmedPayload? payload, out FactOutcome? failure))
        {
            return failure!;
        }

        if (payload!.ReceiptLineId == Guid.Empty
            || payload.ToLocationId == Guid.Empty
            || string.IsNullOrWhiteSpace(payload.Uom))
        {
            return FactOutcome.Rejected(
                fact.ClientFactId, ProblemTypes.MalformedRequest,
                "receiptLineId, toLocationId and uom are required.");
        }

        // A quantity that disagrees with expectation is emphatically NOT
        // malformed — that is the whole design (§4.3, exit criterion 5). Only
        // a quantity that cannot describe a physical count is.
        if (payload.Quantity <= 0)
        {
            return FactOutcome.Rejected(
                fact.ClientFactId, ProblemTypes.MalformedRequest,
                "quantity must be a positive number.");
        }

        FactResult result = await receiptHandler.HandleAsync(
            connection,
            new ReceiptConfirmedFact(
                ClientFactId: fact.ClientFactId,
                ReceiptLineId: payload.ReceiptLineId,
                Quantity: payload.Quantity,
                Uom: payload.Uom,
                ToLocationId: payload.ToLocationId,
                ReasonCode: payload.ReasonCode,
                // Both come from the credential, never from the payload. A
                // device able to name its own actor could attribute a
                // movement to anyone (invariant 8).
                ActorUserId: principal.UserId,
                DeviceId: principal.DeviceId,
                OccurredAtDevice: fact.OccurredAtDevice),
            cancellationToken);

        return FactOutcome.From(fact.ClientFactId, result);
    }

    private static async Task<FactOutcome> ApplyPutawayAsync(
        FactEnvelope fact,
        OperatorPrincipal principal,
        NpgsqlConnection connection,
        ConfirmPutawayFactHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadPayload(fact, out PutawayConfirmedPayload? payload, out FactOutcome? failure))
        {
            return failure!;
        }

        if (payload!.TaskLineId == Guid.Empty
            || payload.FromLocationId == Guid.Empty
            || payload.ToLocationId == Guid.Empty
            || string.IsNullOrWhiteSpace(payload.Uom))
        {
            return FactOutcome.Rejected(
                fact.ClientFactId, ProblemTypes.MalformedRequest,
                "taskLineId, fromLocationId, toLocationId and uom are required.");
        }

        if (payload.Quantity <= 0)
        {
            return FactOutcome.Rejected(
                fact.ClientFactId, ProblemTypes.MalformedRequest,
                "quantity must be a positive number.");
        }

        FactResult result = await handler.HandleAsync(
            connection,
            new PutawayConfirmedFact(
                ClientFactId: fact.ClientFactId,
                TaskLineId: payload.TaskLineId,
                FromLocationId: payload.FromLocationId,
                ToLocationId: payload.ToLocationId,
                Quantity: payload.Quantity,
                Uom: payload.Uom,
                ReasonCode: payload.ReasonCode,
                ActorUserId: principal.UserId,
                DeviceId: principal.DeviceId,
                OccurredAtDevice: fact.OccurredAtDevice),
            cancellationToken);

        return FactOutcome.From(fact.ClientFactId, result);
    }

    /// <summary>
    /// Binds a fact's payload, turning every failure into a rejection.
    /// </summary>
    /// <remarks>
    /// An ABSENT payload binds as default(JsonElement) — ValueKind Undefined —
    /// and deserializing that throws InvalidOperationException rather than
    /// JsonException, so it slips past a catch written for parse errors and
    /// takes the whole batch down. Checked before deserializing rather than
    /// caught, because "there is no payload" is not a parse failure.
    /// </remarks>
    private static bool TryReadPayload<T>(
        FactEnvelope fact, out T? payload, out FactOutcome? failure) where T : class
    {
        payload = null;

        if (fact.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            failure = FactOutcome.Rejected(
                fact.ClientFactId, ProblemTypes.MalformedRequest, "payload is required.");
            return false;
        }

        try
        {
            payload = fact.Payload.Deserialize<T>(JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // The serializer's message carries the JSON path, line number,
            // byte offset and sometimes the offending value. The field is
            // useful to a client; the rest is internals it should not see.
            failure = FactOutcome.Rejected(
                fact.ClientFactId, ProblemTypes.MalformedRequest,
                $"payload could not be read{((ex as JsonException)?.Path is string path
                    ? $" at '{path}'" : string.Empty)}.");
            return false;
        }

        if (payload is null)
        {
            failure = FactOutcome.Rejected(
                fact.ClientFactId, ProblemTypes.MalformedRequest, "payload is required.");
            return false;
        }

        failure = null;
        return true;
    }

    private static IResult Problem(int status, string type, string detail) =>
        Results.Problem(detail: detail, statusCode: status, type: type);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
}

/// <summary>
/// §4.1's envelope.
/// </summary>
public sealed record FactBatch(
    Guid? DeviceId,
    Guid? ClientBatchId,
    IReadOnlyList<FactEnvelope> Facts);

/// <summary>
/// One fact in the batch. The payload stays as raw JSON until the fact type
/// is known, so an unrecognised type is rejected rather than failing the
/// whole batch's model binding.
/// </summary>
public sealed record FactEnvelope(
    string ClientFactId,
    string Type,
    DateTimeOffset? OccurredAtDevice,
    JsonElement Payload);

/// <summary>
/// The <c>receipt_confirmed</c> payload (§4.2).
/// </summary>
/// <remarks>
/// <c>itemId</c> is deliberately absent. The item is read from the receipt
/// line inside the transaction, because the line is the authoritative record
/// of what was expected; taking it from the device would let a mis-scanned
/// payload post a movement against a different item than the line it
/// confirms. Lot and expiry are Phase 2 (`lotCode`, `expiryDate` in §4.2)
/// and are not bound until lot tracking ships.
/// </remarks>
public sealed record ReceiptConfirmedPayload(
    Guid ReceiptLineId,
    decimal Quantity,
    string Uom,
    Guid ToLocationId,
    string? ReasonCode);

/// <summary>
/// The <c>putaway_confirmed</c> payload (§4.2).
/// </summary>
/// <remarks>
/// <c>itemId</c> and <c>handlingUnitId</c> are not bound. The item comes from
/// the task line, which is the authoritative record of what was directed;
/// handling units are Phase 2. Both locations are taken from the device
/// because the whole point is to record where the stock <em>actually</em>
/// went, which may not be where it was sent.
/// </remarks>
public sealed record PutawayConfirmedPayload(
    Guid TaskLineId,
    Guid FromLocationId,
    Guid ToLocationId,
    decimal Quantity,
    string Uom,
    string? ReasonCode);

/// <summary>
/// The fact handlers the endpoint dispatches to, bundled so adding a fact
/// type does not change the endpoint's signature.
/// </summary>
public sealed record FactHandlers(
    ConfirmReceiptFactHandler Receipt,
    ConfirmPutawayFactHandler Putaway);

/// <summary>
/// One entry in the <c>results</c> array (§5.3).
/// </summary>
public sealed record FactOutcome(
    string ClientFactId,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<Guid>? MovementIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ServerSequence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<Guid>? ExceptionIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    FactError? Error)
{
    public static FactOutcome Rejected(string? clientFactId, string type, string detail) =>
        new(clientFactId ?? string.Empty, "rejected", null, null, null,
            new FactError(type, detail));

    public static FactOutcome From(string clientFactId, FactResult result) => result.Status switch
    {
        FactStatus.Rejected => Rejected(
            clientFactId,
            result.Rejection switch
            {
                FactRejection.IdempotencyKeyReused => ProblemTypes.IdempotencyConflict,
                FactRejection.UnknownReceiptLine => ProblemTypes.NotFound,
                FactRejection.UnknownTaskLine => ProblemTypes.NotFound,
                FactRejection.UnknownLocation => ProblemTypes.NotFound,
                // Deliberately not /not-found: the location exists, the
                // payload is internally inconsistent. §7.1 has no closer
                // type; whether it deserves its own is an api-designer
                // question, not one to settle by inventing a URN here.
                FactRejection.LocationInAnotherWarehouse => ProblemTypes.MalformedRequest,
                _ => ProblemTypes.MalformedRequest,
            },
            result.Rejection switch
            {
                FactRejection.IdempotencyKeyReused =>
                    "This clientFactId was already used with a different payload.",
                FactRejection.UnknownReceiptLine => "Unknown receipt line.",
                FactRejection.UnknownTaskLine => "Unknown task line.",
                FactRejection.UnknownLocation => "Unknown location.",
                FactRejection.LocationInAnotherWarehouse =>
                    "The destination location belongs to a different warehouse.",
                _ => "Rejected.",
            }),

        // `duplicate` returns the ORIGINAL result, so a handheld retrying
        // over bad wifi sees the same movement id it would have seen first
        // time and can clear its queue entry with confidence.
        _ => new FactOutcome(
            clientFactId,
            result.Status == FactStatus.Duplicate ? "duplicate" : "accepted",
            result.MovementId is Guid id ? [id] : [],
            result.Sequence,
            result.ExceptionId is Guid exception ? [exception] : [],
            null),
    };
}

public sealed record FactError(string Type, string Detail);

public sealed record FactBatchResponse(
    IReadOnlyList<FactOutcome> Results,
    DateTimeOffset ServerTime);

/// <summary>
/// The fact registry (§4.2), and the permission each type requires.
/// </summary>
/// <remarks>
/// A type absent from here is rejected, which is what keeps the registry
/// honest: adding a fact type to the design without deciding its permission
/// and divergence behaviour cannot silently half-work.
/// </remarks>
public static class FactTypes
{
    public const string ReceiptConfirmed = "receipt_confirmed";
    public const string PutawayConfirmed = "putaway_confirmed";

    private static readonly Dictionary<string, string> RequiredPermissions =
        new(StringComparer.Ordinal)
        {
            [ReceiptConfirmed] = "receipt.confirm",
            [PutawayConfirmed] = "putaway.execute",
        };

    public static bool TryRequiredPermission(string? type, out string permission)
    {
        if (type is not null && RequiredPermissions.TryGetValue(type, out string? found))
        {
            permission = found;
            return true;
        }

        permission = string.Empty;
        return false;
    }
}
