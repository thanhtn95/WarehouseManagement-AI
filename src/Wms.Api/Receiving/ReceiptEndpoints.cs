using Npgsql;
using Wms.Modules.Identity.Contracts;
using Wms.Modules.Inbound.Application;

namespace Wms.Api.Receiving;

/// <summary>
/// The receipt lifecycle commands (§5.4).
/// </summary>
/// <remarks>
/// These are commands, and unlike <c>/sync/facts</c> they may be refused.
/// Nothing physical has happened when a supervisor opens or completes a
/// receipt, so a <c>409</c> asks nobody to undo anything — which is precisely
/// the test §3.3 gives for the split, applied in the opposite direction.
/// </remarks>
public static class ReceiptEndpoints
{
    public static void MapReceipts(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder receipts = routes.MapGroup("/api/v1/receipts")
            .WithTags("Receiving");

        receipts.MapPost("/", CreateAsync)
            .WithName("CreateReceipt")
            .WithSummary("Open a receipt.");

        receipts.MapPost("/{id:guid}/start", StartAsync)
            .WithName("StartReceipt")
            .WithSummary("Move a receipt from draft to in progress.");

        receipts.MapPost("/{id:guid}/complete", CompleteAsync)
            .WithName("CompleteReceipt")
            .WithSummary("Close a receipt and generate directed putaway work.");
    }

    private static async Task<IResult> CreateAsync(
        CreateReceiptRequest? request,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        NpgsqlDataSource dataSource,
        ReceiptService receipts,
        CancellationToken cancellationToken)
    {
        if (request is null
            || request.WarehouseId == Guid.Empty
            || request.OwnerId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.ReceiptType))
        {
            return Problem(StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest,
                "warehouseId, ownerId and receiptType are required.");
        }

        if (request.ReceiptType is not ("blind" or "against_asn" or "return"))
        {
            return Problem(StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest,
                "receiptType must be blind, against_asn or return.");
        }

        (OperatorPrincipal? principal, IResult? refusal) =
            await ResolvePrincipalAsync(http, principals, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        refusal = AuthorizeIn(principal!, "receipt.create", request.WarehouseId);
        if (refusal is not null)
        {
            return refusal;
        }

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        CreatedReceipt created = await receipts.CreateAsync(
            connection,
            new CreateReceiptCommand(
                WarehouseId: request.WarehouseId,
                ReceiptType: request.ReceiptType,
                OwnerId: request.OwnerId,
                SupplierReference: request.SupplierReference,
                ExpectedAt: request.ExpectedAt,
                ActorUserId: principal!.UserId,
                Lines: request.Lines ?? []),
            cancellationToken);

        return Results.Created($"/api/v1/receipts/{created.Id}", created);
    }

    private static async Task<IResult> StartAsync(
        Guid id,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        NpgsqlDataSource dataSource,
        ReceiptService receipts,
        CancellationToken cancellationToken)
    {
        (OperatorPrincipal? principal, IResult? refusal) =
            await ResolvePrincipalAsync(http, principals, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        refusal = await AuthorizeForReceiptAsync(
            principal!, "receipt.confirm", dataSource, id, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        ReceiptTransition result = await receipts.StartAsync(connection, id, cancellationToken);

        return result.Outcome switch
        {
            ReceiptOutcome.Succeeded => Results.Ok(new { id, status = result.Status }),
            ReceiptOutcome.NotFound => Problem(
                StatusCodes.Status404NotFound, ProblemTypes.NotFound, "Unknown receipt."),
            _ => Problem(
                StatusCodes.Status409Conflict, ProblemTypes.InvalidTransition,
                $"Cannot move a receipt from '{result.FromStatus}' to '{result.ToStatus}'."),
        };
    }

    private static async Task<IResult> CompleteAsync(
        Guid id,
        CompleteReceiptRequest? request,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        NpgsqlDataSource dataSource,
        ReceiptService receipts,
        CancellationToken cancellationToken)
    {
        (OperatorPrincipal? principal, IResult? refusal) =
            await ResolvePrincipalAsync(http, principals, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        refusal = await AuthorizeForReceiptAsync(
            principal!, "receipt.complete", dataSource, id, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        ReceiptCompletion result = await receipts.CompleteAsync(
            connection, id, request?.GeneratePutaway ?? true, cancellationToken);

        return result.Outcome switch
        {
            ReceiptOutcome.Succeeded => Results.Ok(new
            {
                id,
                status = "received",
                putawayTasksCreated = result.PutawayTasksCreated,
            }),

            ReceiptOutcome.NotFound => Problem(
                StatusCodes.Status404NotFound, ProblemTypes.NotFound, "Unknown receipt."),

            // The unconfirmed line numbers travel in the body, not just the
            // message: §5.4 specifies them, and a supervisor needs to know
            // WHICH lines to go and count.
            ReceiptOutcome.LinesUnconfirmed => Results.Json(
                new
                {
                    type = ProblemTypes.LinesUnconfirmed,
                    title = "Lines are still awaiting a count.",
                    status = StatusCodes.Status409Conflict,
                    unconfirmedLineNos = result.UnconfirmedLineNos,
                },
                statusCode: StatusCodes.Status409Conflict,
                contentType: "application/problem+json"),

            _ => Problem(
                StatusCodes.Status409Conflict, ProblemTypes.InvalidTransition,
                $"Cannot complete a receipt in state '{result.FromStatus}'."),
        };
    }

    /// <summary>
    /// Resolves the caller only — no permission check. Shared by every
    /// endpoint below so there is exactly one place that reads the
    /// <c>Authorization</c>/<c>X-Device-Id</c> headers.
    /// </summary>
    private static async Task<(OperatorPrincipal?, IResult?)> ResolvePrincipalAsync(
        HttpContext http,
        IOperatorPrincipalResolver principals,
        CancellationToken cancellationToken)
    {
        OperatorPrincipal? principal = await principals.ResolveAsync(
            http.Request.Headers.Authorization.ToString(),
            http.Request.Headers["X-Device-Id"].ToString(),
            cancellationToken);

        return principal is null
            ? (null, Problem(
                StatusCodes.Status401Unauthorized, ProblemTypes.Unauthenticated,
                "No authenticated operator."))
            : (principal, null);
    }

    private static IResult? Authorize(OperatorPrincipal principal, string permission) =>
        principal.Can(permission)
            ? null
            : Problem(StatusCodes.Status403Forbidden, ProblemTypes.InsufficientPermission,
                $"Requires '{permission}'.");

    /// <summary>
    /// The scoped form of <see cref="Authorize"/> (invariant 8: permission
    /// <em>plus</em> scope, not permission alone).
    /// </summary>
    /// <remarks>
    /// Without this, a receiver scoped to warehouse A holding <c>receipt.create</c>
    /// could open, start or complete a receipt in warehouse B — the permission
    /// check alone cannot see that the receipt names a different site.
    /// </remarks>
    private static IResult? AuthorizeIn(
        OperatorPrincipal principal, string permission, Guid warehouseId) =>
        principal.CanIn(permission, warehouseId)
            ? null
            : Problem(StatusCodes.Status403Forbidden, ProblemTypes.InsufficientPermission,
                $"Requires '{permission}' in this warehouse.");

    /// <summary>
    /// <c>Start</c>/<c>Complete</c> only carry the receipt id, so the
    /// warehouse has to be looked up before it can be checked — resolving the
    /// caller first, so an unauthenticated request never reaches the
    /// database. A receipt that does not exist falls back to the unscoped
    /// check and lets the service call answer <c>404</c>, exactly as it did
    /// before scope was enforced.
    /// </summary>
    private static async Task<IResult?> AuthorizeForReceiptAsync(
        OperatorPrincipal principal,
        string permission,
        NpgsqlDataSource dataSource,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        Guid? warehouseId = await ReceiptService.WarehouseIdAsync(
            dataSource, receiptId, cancellationToken);

        return warehouseId is Guid known
            ? AuthorizeIn(principal, permission, known)
            : Authorize(principal, permission);
    }

    private static IResult Problem(int status, string type, string detail) =>
        Results.Problem(detail: detail, statusCode: status, type: type);
}

public sealed record CreateReceiptRequest(
    Guid WarehouseId,
    string ReceiptType,
    Guid OwnerId,
    string? SupplierReference,
    DateTimeOffset? ExpectedAt,
    IReadOnlyList<CreateReceiptLine>? Lines);

public sealed record CompleteReceiptRequest(bool GeneratePutaway = true);
