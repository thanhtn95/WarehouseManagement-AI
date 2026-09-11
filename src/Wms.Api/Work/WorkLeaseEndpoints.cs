using Npgsql;
using Wms.Modules.Identity.Contracts;
using Wms.Modules.Tasks.Application;

namespace Wms.Api.Work;

/// <summary>
/// Task dispatch (§5.3): <c>POST /work/leases</c> and its release.
/// </summary>
public static class WorkLeaseEndpoints
{
    /// <summary>
    /// Bounded so one device cannot drain a warehouse's ready queue.
    /// </summary>
    /// <remarks>
    /// A handheld that leased everything would leave every other operator
    /// polling an empty queue while its own batch sat un-worked for the ten
    /// hours a lease lasts.
    /// </remarks>
    private const int MaxBatchSize = 50;

    private const int DefaultBatchSize = 20;

    public static void MapWorkLeases(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder work = routes.MapGroup("/api/v1/work/leases").WithTags("Work");

        work.MapPost("/", LeaseAsync)
            .WithName("LeaseTasks")
            .WithSummary("Lease a batch of ready tasks.")
            .WithDescription(
                "Self-contained: item names in the operator's locale, barcodes and the " +
                "applicable reason codes travel with the batch, so the device can " +
                "complete every task offline. No work available is 200 with an empty " +
                "list, never 404.");

        work.MapDelete("/{leaseId:guid}", ReleaseAsync)
            .WithName("ReleaseLease")
            .WithSummary("Return unworked tasks to the ready queue.");
    }

    private static async Task<IResult> LeaseAsync(
        LeaseTasksRequest? request,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        NpgsqlDataSource dataSource,
        LeaseService leases,
        CancellationToken cancellationToken)
    {
        if (request is null
            || request.WarehouseId == Guid.Empty
            || request.TaskTypes is not { Count: > 0 })
        {
            return Problem(StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest,
                "warehouseId and at least one taskType are required.");
        }

        if (request.TaskTypes.Any(type =>
            type is not ("putaway" or "pick" or "move" or "count" or "replenish")))
        {
            return Problem(StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest,
                "taskTypes must be drawn from putaway, pick, move, count, replenish.");
        }

        OperatorPrincipal? principal = await principals.ResolveAsync(
            http.Request.Headers.Authorization.ToString(),
            http.Request.Headers["X-Device-Id"].ToString(),
            cancellationToken);

        if (principal is null)
        {
            return Problem(StatusCodes.Status401Unauthorized, ProblemTypes.Unauthenticated,
                "No authenticated operator.");
        }

        // Scoped, not just permission: a device pinned to zone A must not be
        // able to lease from another warehouse entirely just because it holds
        // task.lease somewhere (invariant 8).
        if (!principal.CanIn("task.lease", request.WarehouseId))
        {
            return Problem(StatusCodes.Status403Forbidden, ProblemTypes.InsufficientPermission,
                "Requires 'task.lease' in this warehouse.");
        }

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        LeasedWork work = await leases.LeaseAsync(
            connection,
            new LeaseRequest(
                WarehouseId: request.WarehouseId,
                TaskTypes: request.TaskTypes,
                ZoneIds: request.ZoneIds ?? [],
                BatchSize: Math.Clamp(request.BatchSize ?? DefaultBatchSize, 1, MaxBatchSize),
                UserId: principal.UserId,
                DeviceId: principal.DeviceId),
            cancellationToken);

        return Results.Ok(work);
    }

    private static async Task<IResult> ReleaseAsync(
        Guid leaseId,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        NpgsqlDataSource dataSource,
        LeaseService leases,
        CancellationToken cancellationToken)
    {
        OperatorPrincipal? principal = await principals.ResolveAsync(
            http.Request.Headers.Authorization.ToString(),
            http.Request.Headers["X-Device-Id"].ToString(),
            cancellationToken);

        if (principal is null)
        {
            return Problem(StatusCodes.Status401Unauthorized, ProblemTypes.Unauthenticated,
                "No authenticated operator.");
        }

        if (!principal.Can("task.lease"))
        {
            return Problem(StatusCodes.Status403Forbidden, ProblemTypes.InsufficientPermission,
                "Requires 'task.lease'.");
        }

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        // 204 whether or not anything was released: releasing an already-empty
        // lease is the state the caller wanted, and a device retrying after a
        // dropped response must not get an error for succeeding twice. Scoping
        // the release to the caller's own userId (rather than leaseId alone)
        // is what stops any other holder of task.lease from releasing a lease
        // that is not theirs — reclaiming someone ELSE's (expired) lease is a
        // distinct, supervisor-only act (task.reassign, §5.3), not this one.
        await leases.ReleaseAsync(connection, leaseId, principal.UserId, cancellationToken);
        return Results.NoContent();
    }

    private static IResult Problem(int status, string type, string detail) =>
        Results.Problem(detail: detail, statusCode: status, type: type);
}

public sealed record LeaseTasksRequest(
    Guid WarehouseId,
    IReadOnlyList<string> TaskTypes,
    IReadOnlyList<Guid>? ZoneIds,
    int? BatchSize);
