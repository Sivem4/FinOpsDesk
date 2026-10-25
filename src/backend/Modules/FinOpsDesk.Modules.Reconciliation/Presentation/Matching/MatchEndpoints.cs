using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.Matching;

internal static class MatchEndpoints
{
    public static RouteGroupBuilder MapMatchEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/matches", GetMatchesAsync);

        return group;
    }

    private static async Task<Results<Ok<ExactMatchResponse>, ProblemHttpResult>> GetMatchesAsync(
        GetExactMatchesHandler handler,
        CancellationToken cancellationToken)
    {
        Result<ExactMatchResponse> result = await handler.Handle(cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        if (result.Error.Code == ReconciliationApplicationErrors.MatchCandidateLimitExceeded.Code)
        {
            return Problem(
                result.Error,
                StatusCodes.Status409Conflict,
                "Open-book matching limit exceeded.");
        }

        if (result.Error.Code == ReconciliationApplicationErrors.InconsistentMatchMoney.Code)
        {
            return Problem(
                result.Error,
                StatusCodes.Status500InternalServerError,
                "Stored financial amounts are inconsistent.");
        }

        throw new InvalidOperationException(
            $"Unexpected match failure '{result.Error.Code}'.");
    }

    private static ProblemHttpResult Problem(Error error, int statusCode, string title)
    {
        return TypedResults.Problem(
            detail: error.Message,
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = error.Code,
            });
    }
}