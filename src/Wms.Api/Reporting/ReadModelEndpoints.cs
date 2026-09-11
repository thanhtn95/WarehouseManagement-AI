using Wms.Modules.Identity.Contracts;
using Wms.Modules.Inbound.Contracts;
using Wms.Modules.Inventory.Contracts;
using Wms.Modules.Tasks.Contracts;

namespace Wms.Api.Reporting;

/// <summary>
/// The admin site's read surfaces (§5.5, §5.12).
/// </summary>
/// <remarks>
/// Every one of these composes module read models rather than querying
/// tables. The dashboard in particular spans receiving, putaway, the
/// exception queue and the ledger — four modules — and writing that as one
/// join here would be the first crack in the boundary the architecture tests
/// exist to hold.
/// </remarks>
public static class ReadModelEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    public static void MapReadModels(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/inventory/balances", GetBalancesAsync)
            .WithTags("Inventory")
            .WithName("GetBalances")
            .WithSummary("Stock positions, keyset paginated.")
            .WithDescription(
                "available is computed, never stored. onHand may be negative — that is " +
                "real information, not an error.");

        routes.MapGet("/api/v1/inventory/exceptions", GetExceptionsAsync)
            .WithTags("Inventory")
            .WithName("GetExceptions")
            .WithSummary("The exception queue — a work surface, not an error log.");

        routes.MapGet("/api/v1/inventory/movements", GetMovementsAsync)
            .WithTags("Inventory")
            .WithName("GetMovements")
            .WithSummary("The ledger, ordered by sequence.")
            .WithDescription(
                "Ordered by sequence, never by timestamp. deviceReportedAt is forensic " +
                "and is never used for ordering.");

        routes.MapGet("/api/v1/inventory/reconciliation/runs", GetReconciliationRunsAsync)
            .WithTags("Inventory")
            .WithName("GetReconciliationRuns")
            .WithSummary("Reconciliation history. varianceCount should always be zero.");

        routes.MapGet("/api/v1/receipts", GetReceiptsAsync)
            .WithTags("Receiving")
            .WithName("GetReceipts")
            .WithSummary("Receipts, keyset paginated.");

        routes.MapGet("/api/v1/receipts/{id:guid}", GetReceiptAsync)
            .WithTags("Receiving")
            .WithName("GetReceipt")
            .WithSummary("One receipt with its lines.");

        routes.MapGet("/api/v1/dashboard", GetDashboardAsync)
            .WithTags("Reporting")
            .WithName("GetDashboard")
            .WithSummary("The admin site's home screen, in one call.");
    }

    private static async Task<IResult> GetBalancesAsync(
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IInventoryReadModel inventory,
        CancellationToken cancellationToken,
        Guid? warehouseId = null,
        Guid? itemId = null,
        Guid? locationId = null,
        Guid? zoneId = null,
        Guid? ownerId = null,
        string? stockStatus = null,
        bool includeZero = false,
        int? limit = null,
        string? afterKey = null)
    {
        IResult? refusal = await AuthorizeAsync(
            http, principals, "inventory.read", cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        BalancePage page = await inventory.GetBalancesAsync(
            new BalanceQuery(
                warehouseId, itemId, locationId, zoneId, ownerId, stockStatus,
                includeZero, Clamp(limit), afterKey),
            cancellationToken);

        return Results.Ok(new
        {
            items = page.Items,
            totals = page.Totals,
            nextCursor = page.NextAfterKey is null ? null : new { afterKey = page.NextAfterKey },
            hasMore = page.HasMore,
        });
    }

    private static async Task<IResult> GetExceptionsAsync(
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IInventoryReadModel inventory,
        CancellationToken cancellationToken,
        Guid? warehouseId = null,
        string? status = "open",
        string? exceptionType = null,
        double? minAgeHours = null,
        int? limit = null)
    {
        IResult? refusal = await AuthorizeAsync(
            http, principals, "exception.read", cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        ExceptionPage page = await inventory.GetExceptionsAsync(
            new ExceptionQuery(warehouseId, status, exceptionType, minAgeHours, Clamp(limit)),
            cancellationToken);

        return Results.Ok(new { items = page.Items, summary = page.Summary });
    }

    private static async Task<IResult> GetMovementsAsync(
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IInventoryReadModel inventory,
        CancellationToken cancellationToken,
        Guid? warehouseId = null,
        Guid? itemId = null,
        Guid? locationId = null,
        string? movementType = null,
        Guid? referenceId = null,
        DateOnly? businessDateFrom = null,
        long? afterSequence = null,
        int? limit = null)
    {
        IResult? refusal = await AuthorizeAsync(
            http, principals, "inventory.read", cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        MovementPage page = await inventory.GetMovementsAsync(
            new MovementQuery(
                warehouseId, itemId, locationId, movementType, referenceId,
                businessDateFrom, afterSequence, Clamp(limit)),
            cancellationToken);

        return Results.Ok(new
        {
            items = page.Items,
            nextCursor = page.HasMore ? new { afterSequence = page.NextAfterSequence } : null,
            hasMore = page.HasMore,
        });
    }

    private static async Task<IResult> GetReconciliationRunsAsync(
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IInventoryReadModel inventory,
        CancellationToken cancellationToken,
        Guid? warehouseId = null,
        int? limit = null)
    {
        IResult? refusal = await AuthorizeAsync(
            http, principals, "inventory.read", cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        return Results.Ok(new
        {
            items = await inventory.GetReconciliationRunsAsync(
                warehouseId, Clamp(limit), cancellationToken),
        });
    }

    private static async Task<IResult> GetReceiptsAsync(
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IInboundReadModel inbound,
        CancellationToken cancellationToken,
        Guid? warehouseId = null,
        string? status = null,
        string? supplierReference = null,
        DateOnly? businessDateFrom = null,
        DateOnly? businessDateTo = null,
        int? limit = null,
        Guid? afterId = null)
    {
        IResult? refusal = await AuthorizeAsync(
            http, principals, "receipt.read", cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        ReceiptPage page = await inbound.GetReceiptsAsync(
            new ReceiptQuery(
                warehouseId, status, supplierReference,
                businessDateFrom, businessDateTo, Clamp(limit), afterId),
            cancellationToken);

        return Results.Ok(new
        {
            items = page.Items,
            nextCursor = page.HasMore ? new { afterId = page.NextAfterId } : null,
            hasMore = page.HasMore,
        });
    }

    private static async Task<IResult> GetReceiptAsync(
        Guid id,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IInboundReadModel inbound,
        CancellationToken cancellationToken)
    {
        IResult? refusal = await AuthorizeAsync(
            http, principals, "receipt.read", cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        ReceiptDetail? detail = await inbound.GetReceiptAsync(id, cancellationToken);

        return detail is null
            ? Results.Problem(
                detail: "Unknown receipt.",
                statusCode: StatusCodes.Status404NotFound,
                type: ProblemTypes.NotFound)
            : Results.Ok(detail);
    }

    private static async Task<IResult> GetDashboardAsync(
        Guid warehouseId,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IInventoryReadModel inventory,
        IInboundReadModel inbound,
        ITaskReadModel tasks,
        CancellationToken cancellationToken)
    {
        IResult? refusal = await AuthorizeAsync(http, principals, "report.read", cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        // Sequential, not Task.WhenAll: each read model opens its own
        // connection from the pool, and firing four at once per dashboard view
        // is exactly the burst that starves scan confirmations under the
        // reserved API pool (C7). These are small, indexed aggregates.
        ReceivingSummary receiving =
            await inbound.GetReceivingSummaryAsync(warehouseId, cancellationToken);
        PutawaySummary putaway =
            await tasks.GetPutawaySummaryAsync(warehouseId, cancellationToken);
        ExceptionPage exceptions = await inventory.GetExceptionsAsync(
            new ExceptionQuery(warehouseId, "open", null, null, 0), cancellationToken);
        StockSummary stock =
            await inventory.GetStockSummaryAsync(warehouseId, cancellationToken);
        ReconciliationSummary? reconciliation =
            await inventory.GetLatestReconciliationAsync(warehouseId, cancellationToken);

        return Results.Ok(new
        {
            warehouse = new { id = warehouseId },
            generatedAt = DateTimeOffset.UtcNow,
            receiving,
            putaway,
            exceptions = exceptions.Summary,
            reconciliation,
            stockOnHand = stock,
        });
    }

    private static int Clamp(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    private static async Task<IResult?> AuthorizeAsync(
        HttpContext http,
        IOperatorPrincipalResolver principals,
        string permission,
        CancellationToken cancellationToken)
    {
        OperatorPrincipal? principal = await principals.ResolveAsync(
            http.Request.Headers.Authorization.ToString(),
            http.Request.Headers["X-Device-Id"].ToString(),
            cancellationToken);

        if (principal is null)
        {
            return Results.Problem(
                detail: "No authenticated operator.",
                statusCode: StatusCodes.Status401Unauthorized,
                type: ProblemTypes.Unauthenticated);
        }

        // Read endpoints are permission-checked exactly like writes. A report
        // is not "just reading" — stock positions and the exception queue are
        // precisely what an unauthorised viewer would want (invariant 8).
        return principal.Can(permission)
            ? null
            : Results.Problem(
                detail: $"Requires '{permission}'.",
                statusCode: StatusCodes.Status403Forbidden,
                type: ProblemTypes.InsufficientPermission);
    }
}
