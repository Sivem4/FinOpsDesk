using System.Buffers;
using System.Text.Json;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.Allocations;

internal static class AllocationDisputeResolutionEndpoints
{
    private const int MaxJsonBodyBytes = 4096;

    public static RouteGroupBuilder MapAllocationDisputeResolutionEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/allocation-disputes/{disputeId}/resolution", ResolveAsync)
            .WithName("ResolveAllocationDispute")
            .WithSummary("Record one terminal resolution of a stored allocation dispute.")
            .WithDescription(
                "Requires the TenantMember policy. The effective OrganizationId comes from the host organization context. "
                + "A client-supplied organization id is not the tenant. "
                + "The actor source and actor id come from the authenticated caller, not from the request. "
                + "The body accepts only outcome and reason. outcome is upheld or not_upheld. "
                + "string.Trim() normalizes reason, and the stored value must then contain 1 to 500 UTF-16 code units. "
                + "U+FEFF is preserved. Idempotency-Key is one non-empty UUID whose raw text is the canonical D format, ignoring case, with no surrounding whitespace. "
                + "A new resolution returns HTTP 201 with no Location header. A matching replay returns HTTP 200 and the original decision, including after reversal. "
                + "The decision does not change the dispute registration, the allocation, its rowversion, invoice or bank balances, or financial audit rows.")
            .Produces<AllocationDisputeResolutionResponse>(StatusCodes.Status201Created, "application/json")
            .Produces<AllocationDisputeResolutionResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(DocumentResolveOperation)
            .WithMetadata(FinancialEndpointClassification.Write(
                "allocation-disputes.resolve",
                FinancialWriteOperation.ResolveAllocationDispute));

        group.MapGet("/allocation-disputes/{disputeId}/resolution", GetAsync)
            .WithName("GetAllocationDisputeResolution")
            .WithSummary("Read the stored resolution of one allocation dispute.")
            .WithDescription(
                "Requires the TenantMember policy. The effective OrganizationId comes from the host organization context. "
                + "A client-supplied organization id is not the tenant. A missing actor id is allowed on this read. "
                + "Any query parameter is rejected. A malformed dispute id is rejected. "
                + "An unknown dispute and a dispute owned by another organization both return the same not-found result. "
                + "An existing dispute with no resolution returns decision null. A resolved dispute returns the stored decision. "
                + "The response does not include the organization id or the idempotency key. "
                + "The read does not open a transaction and does not change rows.")
            .Produces<AllocationDisputeResolutionStateResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AddOpenApiOperationTransformer(DocumentGetOperation)
            .WithMetadata(FinancialEndpointClassification.Read("allocation-disputes.resolution.read"));

        return group;
    }

    private static async Task<Results<JsonHttpResult<AllocationDisputeResolutionResponse>, ProblemHttpResult>> ResolveAsync(
        string disputeId,
        HttpRequest httpRequest,
        ResolveAllocationDisputeHandler handler,
        CancellationToken cancellationToken)
    {
        if (httpRequest.Query.Count > 0)
        {
            return ResolutionProblem(ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest);
        }

        if (!Guid.TryParse(disputeId, out Guid parsedDisputeId) || parsedDisputeId == Guid.Empty)
        {
            return ResolutionProblem(ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest);
        }

        if (!IsJsonContentType(httpRequest.ContentType))
        {
            return ResolutionProblem(ReconciliationApplicationErrors.AllocationUnsupportedMediaType);
        }

        if (!TryReadIdempotencyKey(httpRequest, out Guid idempotencyKey, out Error? keyError))
        {
            return ResolutionProblem(keyError!);
        }

        byte[]? body = await ReadBoundedBodyAsync(httpRequest.Body, cancellationToken);
        if (body is null)
        {
            return ResolutionProblem(ReconciliationApplicationErrors.AllocationRequestTooLarge);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception) when (exception.InnerException is not OperationCanceledException)
        {
            return ResolutionProblem(ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest);
        }

        using (document)
        {
            if (!TryReadBody(
                    document.RootElement,
                    out AllocationDisputeResolutionOutcome outcome,
                    out string? reason,
                    out Error? requestError))
            {
                return ResolutionProblem(requestError!);
            }

            Result<ResolveAllocationDisputeResult> result = await handler.Handle(
                new ResolveAllocationDisputeCommand(
                    new AllocationDisputeId(parsedDisputeId),
                    outcome,
                    reason,
                    idempotencyKey),
                cancellationToken);
            if (result.IsFailure)
            {
                return ResolutionProblem(result.Error);
            }

            return TypedResults.Json(
                result.Value.Decision,
                statusCode: result.Value.Created
                    ? StatusCodes.Status201Created
                    : StatusCodes.Status200OK);
        }
    }

    private static async Task<Results<Ok<AllocationDisputeResolutionStateResponse>, ProblemHttpResult>> GetAsync(
        string disputeId,
        HttpRequest httpRequest,
        GetAllocationDisputeResolutionHandler handler,
        CancellationToken cancellationToken)
    {
        if (httpRequest.Query.Count > 0)
        {
            return Problem(
                ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest,
                StatusCodes.Status400BadRequest,
                "Invalid allocation dispute resolution request.");
        }

        if (!Guid.TryParse(disputeId, out Guid parsedDisputeId) || parsedDisputeId == Guid.Empty)
        {
            return Problem(
                ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest,
                StatusCodes.Status400BadRequest,
                "Invalid allocation dispute resolution request.");
        }

        Result<AllocationDisputeResolutionStateResponse> result = await handler.Handle(
            new GetAllocationDisputeResolutionQuery(parsedDisputeId),
            cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error, StatusCodes.Status404NotFound, "Allocation dispute not found.");
        }

        return TypedResults.Ok(result.Value);
    }

    private static bool TryReadIdempotencyKey(
        HttpRequest httpRequest,
        out Guid idempotencyKey,
        out Error? error)
    {
        idempotencyKey = Guid.Empty;
        if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out StringValues values))
        {
            error = ReconciliationApplicationErrors.AllocationDisputeResolutionIdempotencyKeyRequired;
            return false;
        }

        if (values.Count != 1 || !IsExactIdempotencyKey(values[0], out idempotencyKey))
        {
            error = ReconciliationApplicationErrors.AllocationDisputeResolutionIdempotencyKeyInvalid;
            return false;
        }

        error = null;
        return true;
    }

    // Guid.TryParseExact trims Char.IsWhiteSpace and accepts + or 0x prefixes in .NET 10.
    // The raw header is rejected unless it is the canonical D text of the parsed value.
    private static bool IsExactIdempotencyKey(string? value, out Guid key)
    {
        key = Guid.Empty;
        if (string.IsNullOrEmpty(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            return false;
        }

        return Guid.TryParseExact(value, "D", out key)
            && key != Guid.Empty
            && string.Equals(value, key.ToString("D"), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadBody(
        JsonElement root,
        out AllocationDisputeResolutionOutcome outcome,
        out string? reason,
        out Error? error)
    {
        outcome = default;
        reason = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest;
            return false;
        }

        int outcomeCount = 0;
        int reasonCount = 0;
        bool outcomePresent = false;
        JsonElement outcomeValue = default;
        JsonElement reasonValue = default;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            string name;
            try
            {
                name = property.Name;
            }
            catch (InvalidOperationException)
            {
                error = ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest;
                return false;
            }

            if (string.Equals(name, "outcome", StringComparison.Ordinal))
            {
                outcomeCount++;
                outcomePresent = true;
                outcomeValue = property.Value;
                continue;
            }

            if (string.Equals(name, "reason", StringComparison.Ordinal))
            {
                reasonCount++;
                reasonValue = property.Value;
                continue;
            }

            error = ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest;
            return false;
        }

        if (!outcomePresent)
        {
            error = ReconciliationApplicationErrors.AllocationDisputeResolutionOutcomeRequired;
            return false;
        }

        if (outcomeCount != 1 || reasonCount > 1 || outcomeValue.ValueKind != JsonValueKind.String)
        {
            error = ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest;
            return false;
        }

        string? outcomeText;
        try
        {
            outcomeText = outcomeValue.GetString();
        }
        catch (InvalidOperationException)
        {
            error = ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest;
            return false;
        }

        if (!AllocationDisputeResolution.TryParseOutcome(outcomeText, out outcome))
        {
            error = ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest;
            return false;
        }

        if (reasonCount == 0)
        {
            error = ReconciliationApplicationErrors.AllocationDisputeResolutionReasonRequired;
            return false;
        }

        if (reasonValue.ValueKind != JsonValueKind.String)
        {
            error = ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest;
            return false;
        }

        try
        {
            reason = reasonValue.GetString();
        }
        catch (InvalidOperationException)
        {
            error = ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest;
            return false;
        }

        error = null;
        return true;
    }

    private static async Task<byte[]?> ReadBoundedBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] rented = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            int total = 0;
            while (true)
            {
                int read = await body.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaxJsonBodyBytes)
                {
                    return null;
                }

                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return buffer.ToArray();
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        string mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static ProblemHttpResult ResolutionProblem(Error error)
    {
        return Problem(error, StatusCodeFor(error), TitleFor(error));
    }

    private static int StatusCodeFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ActorRequired.Code)
        {
            return StatusCodes.Status403Forbidden;
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationDisputeResolutionInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.AllocationDisputeResolutionOutcomeRequired.Code
            || error.Code == ReconciliationApplicationErrors.AllocationDisputeResolutionReasonRequired.Code
            || error.Code == ReconciliationApplicationErrors.AllocationDisputeResolutionReasonTooLong.Code
            || error.Code == ReconciliationApplicationErrors.AllocationDisputeResolutionIdempotencyKeyRequired.Code
            || error.Code == ReconciliationApplicationErrors.AllocationDisputeResolutionIdempotencyKeyInvalid.Code)
        {
            return StatusCodes.Status400BadRequest;
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationRequestTooLarge.Code)
        {
            return StatusCodes.Status413PayloadTooLarge;
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationUnsupportedMediaType.Code)
        {
            return StatusCodes.Status415UnsupportedMediaType;
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationDisputeNotFound.Code)
        {
            return StatusCodes.Status404NotFound;
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationDisputeResolutionIdempotencyPayloadConflict.Code
            || error.Code == ReconciliationApplicationErrors.AllocationDisputeResolutionAlreadyResolved.Code
            || error.Code == ReconciliationApplicationErrors.AllocationDisputeResolutionConcurrencyConflict.Code)
        {
            return StatusCodes.Status409Conflict;
        }

        throw new InvalidOperationException("Unexpected allocation dispute resolution error '" + error.Code + "'.");
    }

    private static string TitleFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ActorRequired.Code)
        {
            return "Actor is required.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationDisputeNotFound.Code)
        {
            return "Allocation dispute was not found.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationDisputeResolutionIdempotencyPayloadConflict.Code
            || error.Code == ReconciliationApplicationErrors.AllocationDisputeResolutionAlreadyResolved.Code
            || error.Code == ReconciliationApplicationErrors.AllocationDisputeResolutionConcurrencyConflict.Code)
        {
            return "Allocation dispute resolution conflict.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationRequestTooLarge.Code)
        {
            return "Allocation dispute resolution request is too large.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationUnsupportedMediaType.Code)
        {
            return "Unsupported media type.";
        }

        return "Allocation dispute resolution request is invalid.";
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

    private static Task DocumentResolveOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Description = "outcome and reason are required. Blank reason text is rejected. The stored reason is the .NET trim of the submitted text.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        AdditionalPropertiesAllowed = false,
                        Required = new HashSet<string> { "outcome", "reason" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["outcome"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Description = "Required. upheld means the challenge stands. not_upheld means it does not.",
                            },
                            ["reason"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Description = "Required plain text. The server applies string.Trim() and then requires 1 to 500 UTF-16 code units. Surrounding whitespace is not part of that limit. U+FEFF is preserved. The raw value has no maxLength.",
                            },
                        },
                    },
                },
            },
        };
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Required. One non-empty UUID whose raw text is the canonical D format, ignoring case, with no surrounding whitespace. A parsed + or 0x alias is not a valid key.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            },
        });
        return Task.CompletedTask;
    }

    private static Task DocumentGetOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        operation.Parameters ??= [];
        foreach (IOpenApiParameter parameter in operation.Parameters)
        {
            if (string.Equals(parameter.Name, "disputeId", StringComparison.OrdinalIgnoreCase)
                && parameter is OpenApiParameter target)
            {
                target.Description =
                    "Dispute id. An unknown id and an id owned by another organization return the same not-found result. Any query parameter is rejected.";
                if (target.Schema is OpenApiSchema schema)
                {
                    schema.Format = "uuid";
                }
            }
        }

        return Task.CompletedTask;
    }
}