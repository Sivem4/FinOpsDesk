using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.Matching;

internal static class BatchMatchEndpoints
{
    public const int MaxJsonBodyBytes = 8192;

    private const string TenantDescription =
        "Requires the TenantMember policy at runtime. OrganizationId comes from the authenticated access token. "
        + "A client organization id in the body, query, or header is not the tenant. "
        + "Generated OpenAPI Bearer security metadata is incomplete and remains a Phase 7 follow-up. "
        + "TenantMember may start this financial batch. Finer financial authorization is also Phase 7.";

    private static readonly string[] RootPropertyNames = ["pairs"];

    private static readonly string[] PairPropertyNames = ["invoiceId", "bankTransactionId"];

    private static readonly string[] PreviewQueryNames = ["page", "pageSize"];

    private static readonly string[] OutcomeValues =
    [
        "confirmed",
        "skippedAmbiguous",
        "skippedCoverage",
        "skippedReview",
        "stale",
        "conflict",
        "notFound",
        "interrupted",
    ];

    public static RouteGroupBuilder MapBatchMatchEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/matches/batch/preview", PreviewAsync)
            .WithName("PreviewBatchExactMatches")
            .WithSummary("Preview operator-approved exact-1 batch pairs.")
            .WithDescription(
                TenantDescription
                + " This read classifies the complete open book before it pages eligible pairs. "
                + "It does not persist a plan, create a receipt, create an allocation, or change a review case. "
                + "The preview does not authorize execution. Page defaults to 1 and page size defaults to 50, with a maximum of 100. "
                + "Currency totals cover the full eligible set, not only the current page. "
                + "An incomplete book is not reported as a complete classification.")
            .Produces<PreviewBatchExactMatchesResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .AddOpenApiOperationTransformer(DocumentPreviewOperation);

        group.MapPost("/matches/batch/execute", ExecuteAsync)
            .WithName("ExecuteBatchExactMatches")
            .WithSummary("Execute an operator-approved batch of exact-1 pairs.")
            .WithDescription(
                TenantDescription
                + " Submitting concrete pairs is the operator's approval to attempt confirmation. "
                + "The server revalidates every pair and does not treat an earlier preview as permission to allocate. "
                + "Only a unique exact-1 full remaining balance can be confirmed. Smart-1 scores and match suggestions cannot allocate. "
                + "The body accepts 1 to 25 distinct pairs and only invoiceId and bankTransactionId. "
                + "The body is limited to 8192 bytes. A completed attempt returns 200 even when some or all items are rejected. "
                + "allConfirmed is false unless every item is confirmed. Replay returns the stored result and does not allocate again.")
            .Produces<ExecuteBatchExactMatchesResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .AddOpenApiOperationTransformer(DocumentExecuteOperation);

        return group;
    }

    private static async Task<Results<Ok<PreviewBatchExactMatchesResponse>, ProblemHttpResult>> PreviewAsync(
        HttpRequest httpRequest,
        PreviewBatchExactMatchesHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadPreviewQuery(httpRequest, out PreviewBatchExactMatchesQuery query, out Error? error))
        {
            return Problem(error!, StatusCodeFor(error!), TitleFor(error!));
        }

        Result<PreviewBatchExactMatchesResponse> result = await handler.Handle(query, cancellationToken);
        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        return Problem(result.Error, StatusCodeFor(result.Error), TitleFor(result.Error));
    }

    private static async Task<Results<Ok<ExecuteBatchExactMatchesResponse>, ProblemHttpResult>> ExecuteAsync(
        HttpRequest httpRequest,
        ExecuteBatchExactMatchesHandler handler,
        CancellationToken cancellationToken)
    {
        if (!IsJsonContentType(httpRequest.ContentType))
        {
            return Problem(
                ReconciliationApplicationErrors.BatchUnsupportedMediaType,
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported media type.");
        }

        byte[]? body = await ReadBoundedBodyAsync(httpRequest.Body, cancellationToken);
        if (body is null)
        {
            return Problem(
                ReconciliationApplicationErrors.BatchRequestTooLarge,
                StatusCodes.Status413PayloadTooLarge,
                "Batch request is too large.");
        }

        if (!TryReadIdempotencyKey(httpRequest, out Guid idempotencyKey, out Error? keyError))
        {
            return Problem(keyError!, StatusCodeFor(keyError!), TitleFor(keyError!));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception) when (exception.InnerException is not OperationCanceledException)
        {
            return InvalidBatchRequest();
        }

        using (document)
        {
            if (!TryReadPairs(document.RootElement, out BatchExactMatchPair[]? pairs)
                || pairs is null)
            {
                return InvalidBatchRequest();
            }

            Result<ExecuteBatchExactMatchesResponse> result = await handler.Handle(
                new ExecuteBatchExactMatchesCommand(idempotencyKey, pairs),
                cancellationToken);

            if (result.IsFailure)
            {
                return Problem(result.Error, StatusCodeFor(result.Error), TitleFor(result.Error));
            }

            return TypedResults.Ok(result.Value);
        }
    }

    private static bool TryReadPreviewQuery(
        HttpRequest httpRequest,
        out PreviewBatchExactMatchesQuery query,
        out Error? error)
    {
        query = new PreviewBatchExactMatchesQuery();
        foreach (string key in httpRequest.Query.Keys)
        {
            if (!PreviewQueryNames.Contains(key, StringComparer.OrdinalIgnoreCase)
                || httpRequest.Query[key].Count != 1)
            {
                error = ReconciliationApplicationErrors.BatchInvalidQuery;
                return false;
            }
        }

        if (!TryReadPositiveInt(
                httpRequest,
                "page",
                defaultValue: 1,
                BatchExecutionLimits.MaxPreviewPageSize,
                enforceMaximum: false,
                out int page)
            || !TryReadPositiveInt(
                httpRequest,
                "pageSize",
                BatchExecutionLimits.DefaultPreviewPageSize,
                BatchExecutionLimits.MaxPreviewPageSize,
                enforceMaximum: true,
                out int pageSize))
        {
            error = ReconciliationApplicationErrors.BatchInvalidQuery;
            return false;
        }

        try
        {
            _ = checked((page - 1) * pageSize);
        }
        catch (OverflowException)
        {
            error = ReconciliationApplicationErrors.BatchInvalidQuery;
            return false;
        }

        query = new PreviewBatchExactMatchesQuery(page, pageSize);
        error = null;
        return true;
    }

    private static bool TryReadPositiveInt(
        HttpRequest httpRequest,
        string name,
        int defaultValue,
        int maximum,
        bool enforceMaximum,
        out int value)
    {
        value = defaultValue;
        if (!httpRequest.Query.TryGetValue(name, out StringValues values))
        {
            return true;
        }

        if (values.Count != 1
            || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out value)
            || value < 1
            || (enforceMaximum && value > maximum))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadPairs(JsonElement root, out BatchExactMatchPair[]? pairs)
    {
        pairs = null;
        if (root.ValueKind != JsonValueKind.Object
            || !TryReadObject(root, RootPropertyNames, out Dictionary<string, JsonElement>? properties)
            || !properties!.TryGetValue("pairs", out JsonElement pairsElement)
            || pairsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        int count = pairsElement.GetArrayLength();
        if (count < 1 || count > BatchExactMatchIdentity.MaximumPairCount)
        {
            return false;
        }

        var parsed = new BatchExactMatchPair[count];
        var seen = new HashSet<(Guid InvoiceId, Guid BankTransactionId)>();
        int index = 0;
        foreach (JsonElement item in pairsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryReadObject(item, PairPropertyNames, out Dictionary<string, JsonElement>? itemProperties)
                || !itemProperties!.TryGetValue("invoiceId", out JsonElement invoiceElement)
                || !itemProperties.TryGetValue("bankTransactionId", out JsonElement bankElement)
                || !TryReadGuid(invoiceElement, out Guid invoiceId)
                || !TryReadGuid(bankElement, out Guid bankTransactionId)
                || !seen.Add((invoiceId, bankTransactionId)))
            {
                return false;
            }

            parsed[index] = new BatchExactMatchPair(new InvoiceId(invoiceId), new BankTransactionId(bankTransactionId));
            index++;
        }

        pairs = parsed;
        return true;
    }

    private static bool TryReadObject(
        JsonElement element,
        IReadOnlyList<string> allowedNames,
        out Dictionary<string, JsonElement>? properties)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowedNames.Any(name => name.Equals(property.Name, StringComparison.OrdinalIgnoreCase))
                || !properties.TryAdd(property.Name, property.Value))
            {
                properties = null;
                return false;
            }
        }

        return true;
    }

    private static bool TryReadGuid(JsonElement element, out Guid value)
    {
        value = Guid.Empty;
        return element.ValueKind == JsonValueKind.String
            && Guid.TryParseExact(element.GetString(), "D", out value)
            && value != Guid.Empty;
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
        if (string.IsNullOrWhiteSpace(contentType)
            || !MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed)
            || !parsed.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The body reader decodes UTF-8. A missing charset means that default.
        // More than one charset is either a duplicate or a conflict.
        int charsetCount = 0;
        string? charset = null;
        foreach (NameValueHeaderValue parameter in parsed.Parameters)
        {
            if (!parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            charsetCount++;
            charset = Unquote(parameter.Value.Value);
        }

        if (charsetCount > 1)
        {
            return false;
        }

        return charsetCount == 0
            || string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Unquote(string? value)
    {
        if (value is null || value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        return value[1..^1];
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

    private static ProblemHttpResult InvalidBatchRequest()
    {
        return Problem(
            ReconciliationApplicationErrors.BatchInvalidRequest,
            StatusCodes.Status400BadRequest,
            "Batch request is invalid.");
    }

    private static int StatusCodeFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ActorRequired.Code)
        {
            return StatusCodes.Status403Forbidden;
        }

        if (error.Code == ReconciliationApplicationErrors.BatchInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.BatchInvalidQuery.Code
            || error.Code == ReconciliationApplicationErrors.IdempotencyKeyRequired.Code
            || error.Code == ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code)
        {
            return StatusCodes.Status400BadRequest;
        }

        if (error.Code == ReconciliationApplicationErrors.BatchRequestTooLarge.Code)
        {
            return StatusCodes.Status413PayloadTooLarge;
        }

        if (error.Code == ReconciliationApplicationErrors.BatchUnsupportedMediaType.Code)
        {
            return StatusCodes.Status415UnsupportedMediaType;
        }

        if (error.Code == ReconciliationApplicationErrors.BatchIdempotencyPayloadConflict.Code
            || error.Code == ReconciliationApplicationErrors.BatchExecutionInProgress.Code
            || error.Code == ReconciliationApplicationErrors.BatchAllocationIntegrityConflict.Code
            || error.Code == ReconciliationApplicationErrors.BatchCandidateLimitExceeded.Code)
        {
            return StatusCodes.Status409Conflict;
        }

        if (error.Code == ReconciliationApplicationErrors.InconsistentMatchMoney.Code
            || error.Code == ReconciliationApplicationErrors.BatchInfrastructureFailure.Code)
        {
            return StatusCodes.Status500InternalServerError;
        }

        throw new InvalidOperationException(
            $"Unexpected batch failure '{error.Code}'.");
    }

    private static string TitleFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ActorRequired.Code)
        {
            return "Actor is required.";
        }

        if (error.Code == ReconciliationApplicationErrors.BatchInvalidQuery.Code)
        {
            return "Batch preview query is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.BatchInvalidRequest.Code)
        {
            return "Batch request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.IdempotencyKeyRequired.Code)
        {
            return "Idempotency key is required.";
        }

        if (error.Code == ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code)
        {
            return "Idempotency key is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.BatchRequestTooLarge.Code)
        {
            return "Batch request is too large.";
        }

        if (error.Code == ReconciliationApplicationErrors.BatchUnsupportedMediaType.Code)
        {
            return "Unsupported media type.";
        }

        if (error.Code == ReconciliationApplicationErrors.BatchIdempotencyPayloadConflict.Code)
        {
            return "Idempotency key was reused for a different batch.";
        }

        if (error.Code == ReconciliationApplicationErrors.BatchExecutionInProgress.Code)
        {
            return "Batch execution is already in progress.";
        }

        if (error.Code == ReconciliationApplicationErrors.BatchAllocationIntegrityConflict.Code)
        {
            return "Stored batch allocation identity conflicts.";
        }

        if (error.Code == ReconciliationApplicationErrors.BatchCandidateLimitExceeded.Code)
        {
            return "Open-book matching limit exceeded.";
        }

        if (error.Code == ReconciliationApplicationErrors.InconsistentMatchMoney.Code)
        {
            return "Stored financial amounts are inconsistent.";
        }

        if (error.Code == ReconciliationApplicationErrors.BatchInfrastructureFailure.Code)
        {
            return "Batch execution failed.";
        }

        throw new InvalidOperationException(
            $"Unexpected batch failure '{error.Code}'.");
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

    private static Task DocumentPreviewOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        operation.Parameters ??= [];
        operation.Parameters.Add(QueryParameter(
            "page",
            "Page number. Defaults to 1. The zero-based offset must fit in a 32-bit integer.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(1),
                Minimum = "1",
            }));
        operation.Parameters.Add(QueryParameter(
            "pageSize",
            "Page size. Defaults to 50. Maximum is 100. Pagination happens after exact-1 classification.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(BatchExecutionLimits.DefaultPreviewPageSize),
                Minimum = "1",
                Maximum = "100",
            }));
        return Task.CompletedTask;
    }

    private static Task DocumentExecuteOperation(
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
            Description =
                "One to 25 distinct invoice and bank-transaction pairs. "
                + "Amount, currency, organization, score, and allocation id are not accepted.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = CreateExecuteRequestSchema(),
                },
            },
        };
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Exactly one UUID in D format. An empty UUID is rejected. The same key and pair set replays the stored result.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            },
        });
        AnnotateOutcome(context);
        return Task.CompletedTask;
    }

    private static void AnnotateOutcome(OpenApiOperationTransformerContext context)
    {
        if (context.Document?.Components?.Schemas is null
            || !context.Document.Components.Schemas.TryGetValue(
                nameof(ExecuteBatchExactMatchItemResponse),
                out IOpenApiSchema? schema)
            || schema is not OpenApiSchema itemSchema
            || itemSchema.Properties is null
            || !itemSchema.Properties.TryGetValue("outcome", out IOpenApiSchema? outcome)
            || outcome is not OpenApiSchema outcomeSchema)
        {
            return;
        }

        outcomeSchema.Description =
            "confirmed, skippedAmbiguous, skippedCoverage, skippedReview, stale, conflict, notFound, or interrupted.";
        outcomeSchema.Type = JsonSchemaType.String;
        outcomeSchema.Enum = OutcomeValues.Select(static value => (JsonNode)JsonValue.Create(value)!).ToList();
    }

    private static OpenApiSchema CreateExecuteRequestSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalPropertiesAllowed = false,
            Required = new HashSet<string> { "pairs" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["pairs"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    MinItems = 1,
                    MaxItems = BatchExactMatchIdentity.MaximumPairCount,
                    Items = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        AdditionalPropertiesAllowed = false,
                        Required = new HashSet<string> { "invoiceId", "bankTransactionId" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["invoiceId"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Format = "uuid",
                            },
                            ["bankTransactionId"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Format = "uuid",
                            },
                        },
                    },
                },
            },
        };
    }

    private static OpenApiParameter QueryParameter(string name, string description, OpenApiSchema schema)
    {
        return new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = false,
            Description = description,
            Schema = schema,
        };
    }
}