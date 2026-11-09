using System.Globalization;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.Allocations;

internal static class AllocationDisputeWorkEndpoints
{
    public static RouteGroupBuilder MapAllocationDisputeWorkEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/allocation-disputes/summary", SummaryAsync)
            .WithName("GetAllocationDisputeWorkSummary")
            .WithSummary("Count allocation disputes waiting for a decision or a possible correction.")
            .WithDescription(
                "Requires allocation-disputes.resolution.read. "
                + "needsDecision counts disputes with no decision. "
                + "needsCorrection counts upheld disputes with no correction whose allocation is still Confirmed. "
                + "An earlier independent reversal is excluded from needsCorrection. "
                + "The two counts are separate statements. "
                + "Any query parameter is rejected. The read does not change rows.")
            .Produces<AllocationDisputeWorkSummaryResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithMetadata(FinancialEndpointClassification.Read("allocation-disputes.resolution.read"));

        group.MapGet("/allocation-disputes/queue", QueueAsync)
            .WithName("ListAllocationDisputeQueue")
            .WithSummary("List one allocation dispute work queue.")
            .WithDescription(
                "Requires allocation-disputes.resolution.read. "
                + "queue is needs-decision or needs-correction and uses the same predicate as the summary. "
                + "totalCount is the full 64-bit count, not the page length. "
                + "Ordering is RecordedAt ascending, then dispute id ascending. "
                + "The count and the page are separate statements.")
            .Produces<AllocationDisputeListResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithMetadata(FinancialEndpointClassification.Read("allocation-disputes.resolution.read"));

        return group;
    }

    private static async Task<Results<Ok<AllocationDisputeWorkSummaryResponse>, ProblemHttpResult>> SummaryAsync(
        HttpRequest httpRequest,
        GetAllocationDisputeWorkSummaryHandler handler,
        CancellationToken cancellationToken)
    {
        if (httpRequest.Query.Count > 0)
        {
            return Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
        }

        Result<AllocationDisputeWorkSummaryResponse> result = await handler.Handle(
            new GetAllocationDisputeWorkSummaryQuery(),
            cancellationToken);
        return result.IsFailure ? Problem(result.Error) : TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<AllocationDisputeListResponse>, ProblemHttpResult>> QueueAsync(
        HttpRequest httpRequest,
        GetAllocationDisputeQueueHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryBind(httpRequest, out AllocationDisputeQueue queue, out int page, out int pageSize, out ProblemHttpResult? problem))
        {
            return problem!;
        }

        Result<AllocationDisputeListResponse> result = await handler.Handle(
            new GetAllocationDisputeQueueQuery(queue, page, pageSize),
            cancellationToken);
        return result.IsFailure ? Problem(result.Error) : TypedResults.Ok(result.Value);
    }

    private static bool TryBind(
        HttpRequest httpRequest,
        out AllocationDisputeQueue queue,
        out int page,
        out int pageSize,
        out ProblemHttpResult? problem)
    {
        queue = default;
        page = AllocationDisputeListPagination.DefaultPage;
        pageSize = AllocationDisputeListPagination.DefaultPageSize;
        problem = null;
        foreach (string key in httpRequest.Query.Keys)
        {
            if (!string.Equals(key, "queue", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "page", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "pageSize", StringComparison.OrdinalIgnoreCase))
            {
                problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
                return false;
            }

            if (httpRequest.Query[key].Count != 1)
            {
                problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
                return false;
            }
        }

        if (!httpRequest.Query.TryGetValue("queue", out StringValues queueValues)
            || !TryQueue(queueValues[0], out queue))
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
            return false;
        }

        if (!TryPage(httpRequest, "page", AllocationDisputeListPagination.DefaultPage, out page)
            || !TryPage(httpRequest, "pageSize", AllocationDisputeListPagination.DefaultPageSize, out pageSize))
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
            return false;
        }

        if (!AllocationDisputeListPagination.IsValid(page, pageSize))
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidPagination);
            return false;
        }

        return true;
    }

    private static bool TryQueue(string? value, out AllocationDisputeQueue queue)
    {
        if (string.Equals(value, "needs-decision", StringComparison.Ordinal))
        {
            queue = AllocationDisputeQueue.NeedsDecision;
            return true;
        }

        if (string.Equals(value, "needs-correction", StringComparison.Ordinal))
        {
            queue = AllocationDisputeQueue.NeedsCorrection;
            return true;
        }

        queue = default;
        return false;
    }

    private static bool TryPage(HttpRequest httpRequest, string name, int fallback, out int page)
    {
        page = fallback;
        if (!httpRequest.Query.TryGetValue(name, out StringValues values))
        {
            return true;
        }

        return int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out page);
    }

    private static ProblemHttpResult Problem(Error error)
    {
        int statusCode = error.Code == ReconciliationApplicationErrors.AllocationDisputeInvalidPagination.Code
            || error.Code == ReconciliationApplicationErrors.AllocationDisputeInvalidRequest.Code
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status500InternalServerError;
        return TypedResults.Problem(
            detail: error.Message,
            statusCode: statusCode,
            title: "Invalid allocation dispute request",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = error.Code,
            });
    }
}