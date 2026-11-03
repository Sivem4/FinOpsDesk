using System.Text.Json;
using System.Text.Json.Serialization;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.Matching;

internal static class MatchEndpoints
{
    public static RouteGroupBuilder MapMatchEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/matches", GetMatchesAsync)
            .WithMetadata(FinancialEndpointClassification.Read("reconciliation.read"));
        group.MapGet("/matches/smart", GetSmartMatchesAsync)
            .WithName("GetSmartMatches")
            .WithSummary("Return advisory smart-1 suggestions for the authenticated organization.")
            .WithDescription(
                "Scores are deterministic points, not probabilities. "
                + "suggestedAmount is advisory and is not an authorized allocation. "
                + "contested false does not prove uniqueness when coverageLimited is true. "
                + "truncated true means scored suggestions were omitted. "
                + "An empty suggestions array with coverageLimited true does not prove that no match exists. "
                + "Suggestions can be stale before confirmation. Query parameters are not accepted.")
            .Produces<SmartMatchResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .WithMetadata(FinancialEndpointClassification.Read("reconciliation.read"));
        group.MapPost("/matches/confirm", ConfirmMatchAsync)
            .Accepts<ConfirmMatchRequest>("application/json")
            .WithMetadata(FinancialEndpointClassification.Write("reconciliation.confirm"));

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

    private static async Task<Results<Ok<SmartMatchResponse>, ProblemHttpResult>> GetSmartMatchesAsync(
        HttpRequest httpRequest,
        GetSmartMatchesHandler handler,
        CancellationToken cancellationToken)
    {
        if (httpRequest.Query.Count > 0)
        {
            return Problem(
                ReconciliationApplicationErrors.SmartMatchUnsupportedQuery,
                StatusCodes.Status400BadRequest,
                "Smart matching request is invalid.");
        }

        Result<SmartMatchResponse> result = await handler.Handle(cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        if (result.Error.Code == ReconciliationApplicationErrors.MatchCandidateLimitExceeded.Code)
        {
            return Problem(
                result.Error,
                StatusCodes.Status409Conflict,
                "Open-book matching limit exceeded.",
                "Smart matching was not run because the open book exceeds 5000 invoices or 5000 bank transactions.");
        }

        if (result.Error.Code == ReconciliationApplicationErrors.InconsistentMatchMoney.Code)
        {
            return Problem(
                result.Error,
                StatusCodes.Status500InternalServerError,
                "Stored financial amounts are inconsistent.",
                "Smart matching was not run because stored invoice or bank transaction amounts are inconsistent.");
        }

        throw new InvalidOperationException(
            $"Unexpected smart match failure '{result.Error.Code}'.");
    }

    private static async Task<Results<JsonHttpResult<ConfirmExactMatchResponse>, ProblemHttpResult>> ConfirmMatchAsync(
        HttpRequest httpRequest,
        ConfirmExactMatchHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadIdempotencyKey(httpRequest, out Guid idempotencyKey, out Error? keyError))
        {
            return Problem(keyError!, StatusCodeFor(keyError!), TitleFor(keyError!));
        }

        ConfirmMatchRequest? request;
        try
        {
            request = await httpRequest.ReadFromJsonAsync<ConfirmMatchRequest>(cancellationToken);
        }
        catch (JsonException)
        {
            return InvalidConfirmationRequest();
        }

        if (request is null
            || request.InvoiceId == Guid.Empty
            || request.BankTransactionId == Guid.Empty)
        {
            return InvalidConfirmationRequest();
        }

        Result<ConfirmExactMatchResponse> result = await handler.Handle(
            new ConfirmExactMatchCommand(
                new InvoiceId(request.InvoiceId),
                new BankTransactionId(request.BankTransactionId),
                idempotencyKey),
            cancellationToken);

        if (result.IsFailure)
        {
            return Problem(result.Error, StatusCodeFor(result.Error), TitleFor(result.Error));
        }

        int statusCode = result.Value.Replayed
            ? StatusCodes.Status200OK
            : StatusCodes.Status201Created;

        return TypedResults.Json(result.Value, statusCode: statusCode);
    }

    private static ProblemHttpResult InvalidConfirmationRequest()
    {
        return Problem(
            ReconciliationApplicationErrors.ConfirmationInvalidRequest,
            StatusCodes.Status400BadRequest,
            "Confirmation request is invalid.");
    }

    private static bool TryReadIdempotencyKey(
        HttpRequest httpRequest,
        out Guid idempotencyKey,
        out Error? error)
    {
        if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out StringValues values)
            || values.Count == 0
            || string.IsNullOrWhiteSpace(values[0]))
        {
            idempotencyKey = Guid.Empty;
            error = ReconciliationApplicationErrors.IdempotencyKeyRequired;
            return false;
        }

        if (values.Count != 1
            || !Guid.TryParseExact(values[0], "D", out idempotencyKey)
            || idempotencyKey == Guid.Empty)
        {
            idempotencyKey = Guid.Empty;
            error = ReconciliationApplicationErrors.IdempotencyKeyInvalid;
            return false;
        }

        error = null;
        return true;
    }

    private static int StatusCodeFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ActorRequired.Code)
        {
            return StatusCodes.Status403Forbidden;
        }

        if (error.Code == ReconciliationApplicationErrors.IdempotencyKeyRequired.Code
            || error.Code == ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code
            || error.Code == ReconciliationApplicationErrors.ConfirmationInvalidRequest.Code)
        {
            return StatusCodes.Status400BadRequest;
        }

        if (error.Code == ReconciliationApplicationErrors.InvoiceNotFound.Code
            || error.Code == ReconciliationApplicationErrors.BankTransactionNotFound.Code)
        {
            return StatusCodes.Status404NotFound;
        }

        if (error.Code == ReconciliationErrors.MatchReferenceMismatch.Code
            || error.Code == ReconciliationErrors.MatchCurrencyMismatch.Code
            || error.Code == ReconciliationErrors.MatchStale.Code
            || error.Code == ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code
            || error.Code == ReconciliationApplicationErrors.ConcurrencyConflict.Code)
        {
            return StatusCodes.Status409Conflict;
        }

        throw new InvalidOperationException(
            $"Unexpected confirmation failure '{error.Code}'.");
    }

    private static string TitleFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ActorRequired.Code)
        {
            return "Actor is required.";
        }

        if (error.Code == ReconciliationApplicationErrors.IdempotencyKeyRequired.Code)
        {
            return "Idempotency key is required.";
        }

        if (error.Code == ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code)
        {
            return "Idempotency key is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ConfirmationInvalidRequest.Code)
        {
            return "Confirmation request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.InvoiceNotFound.Code)
        {
            return "Invoice was not found.";
        }

        if (error.Code == ReconciliationApplicationErrors.BankTransactionNotFound.Code)
        {
            return "Bank transaction was not found.";
        }

        if (error.Code == ReconciliationErrors.MatchReferenceMismatch.Code)
        {
            return "Payment references do not match.";
        }

        if (error.Code == ReconciliationErrors.MatchCurrencyMismatch.Code)
        {
            return "Currencies do not match.";
        }

        if (error.Code == ReconciliationErrors.MatchStale.Code)
        {
            return "The match is stale.";
        }

        if (error.Code == ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code)
        {
            return "Idempotency key was already used.";
        }

        if (error.Code == ReconciliationApplicationErrors.ConcurrencyConflict.Code)
        {
            return "The financial records were modified.";
        }

        throw new InvalidOperationException(
            $"Unexpected confirmation failure '{error.Code}'.");
    }

    private static ProblemHttpResult Problem(
        Error error,
        int statusCode,
        string title,
        string? detail = null)
    {
        return TypedResults.Problem(
            detail: detail ?? error.Message,
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = error.Code,
            });
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ConfirmMatchRequest(Guid InvoiceId, Guid BankTransactionId);
}