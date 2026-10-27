using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.ReviewCases;

internal static class ReviewCaseEndpoints
{
    private const int MaxJsonBodyBytes = 4096;

    private const string ConcurrencyTokenDescription =
        "Opaque Base64 encoding of the 8-byte SQL Server rowversion. "
        + "Submit the token read with the case. Do not replace it with a newer token after a conflict.";

    private const string TenantDescription =
        "Requires the TenantMember policy. OrganizationId is taken from the authenticated access token. "
        + "A client organization id in the body, query, or header is not accepted as the tenant.";

    private static readonly string[] OpenPropertyNames =
        ["reason", "invoiceId", "bankTransactionId", "sourceLabel", "note"];

    private static readonly string[] ResolvePropertyNames =
        ["outcome", "allocationId", "resolutionNote", "concurrencyToken"];

    private static readonly string[] DismissPropertyNames =
        ["resolutionNote", "concurrencyToken"];

    private static readonly string[] ListQueryNames =
        ["status", "invoiceId", "bankTransactionId", "page", "pageSize"];

    public static RouteGroupBuilder MapReviewCaseEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/review-cases", OpenAsync)
            .WithName("OpenReviewCase")
            .WithSummary("Open a review case for the authenticated organization.")
            .WithDescription(
                TenantDescription
                + " A new case returns 201 and a Location header for GET /api/review-cases/{id}. "
                + "An existing open case for the same subject returns 200 and does not change the stored note, source, or concurrency token. "
                + "This operation does not allocate money and does not run exact-1 or smart-1.")
            .Produces<ReviewCaseResponse>(StatusCodes.Status201Created, "application/json")
            .Produces<ReviewCaseResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(DocumentOpenOperation);

        group.MapGet("/review-cases", ListAsync)
            .WithName("ListReviewCases")
            .WithSummary("List review cases for the authenticated organization.")
            .WithDescription(
                TenantDescription
                + " Results are ordered by opened time descending, then review case id descending. "
                + "The list does not run exact-1 or smart-1 and does not load the open reconciliation book.")
            .Produces<ReviewCaseListResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .AddOpenApiOperationTransformer(DocumentListOperation);

        group.MapGet("/review-cases/{id}", GetAsync)
            .WithName("GetReviewCase")
            .WithSummary("Get one review case and its current linked balances.")
            .WithDescription(
                TenantDescription
                + " Linked balances are current observations, not stored snapshots and not an authorization to allocate. "
                + "Inconsistent stored money returns 500 and does not include the corrupted amounts.")
            .Produces<ReviewCaseDetailResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .AddOpenApiOperationTransformer(DocumentGetOperation);

        group.MapPost("/review-cases/{id}/resolve", ResolveAsync)
            .WithName("ResolveReviewCase")
            .WithSummary("Resolve an open review case.")
            .WithDescription(
                TenantDescription
                + " AllocationRecorded requires an existing confirmed allocation. "
                + "NoActionRequired and HandledOutside reject an allocation id. "
                + "An identical terminal replay returns 200 and preserves ClosedAt and the concurrency token. "
                + "This operation does not confirm an allocation.")
            .Produces<ReviewCaseResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(DocumentResolveOperation);

        group.MapPost("/review-cases/{id}/dismiss", DismissAsync)
            .WithName("DismissReviewCase")
            .WithSummary("Dismiss an open review case.")
            .WithDescription(
                TenantDescription
                + " Outcome and allocationId are not accepted. "
                + "An identical dismissal replay returns 200 and preserves ClosedAt and the concurrency token. "
                + "Dismissal does not change invoice, bank transaction, or allocation rows.")
            .Produces<ReviewCaseResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(DocumentDismissOperation);

        return group;
    }

    private static async Task<Results<Created<ReviewCaseResponse>, Ok<ReviewCaseResponse>, ProblemHttpResult>> OpenAsync(
        HttpRequest httpRequest,
        OpenReviewCaseHandler handler,
        CancellationToken cancellationToken)
    {
        if (RejectUnexpectedQuery(httpRequest, []) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        JsonRead read = await TryReadJsonObjectAsync(httpRequest, OpenPropertyNames, cancellationToken);
        if (read.Problem is not null)
        {
            return read.Problem;
        }

        using (read.Document)
        {
            if (!TryReadOpen(read.Properties!, out OpenReviewCaseCommand? command, out ProblemHttpResult? requestProblem))
            {
                return requestProblem!;
            }

            Result<OpenReviewCaseResponse> result = await handler.Handle(command!, cancellationToken);
            if (result.IsFailure)
            {
                return Problem(result.Error);
            }

            ReviewCaseResponse reviewCase = result.Value.ReviewCase;
            if (result.Value.Created)
            {
                return TypedResults.Created($"/api/review-cases/{reviewCase.ReviewCaseId:D}", reviewCase);
            }

            return TypedResults.Ok(reviewCase);
        }
    }

    private static async Task<Results<Ok<ReviewCaseListResponse>, ProblemHttpResult>> ListAsync(
        HttpRequest httpRequest,
        ListReviewCasesHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryBindList(httpRequest, out ListReviewCasesQuery? query, out ProblemHttpResult? problem))
        {
            return problem!;
        }

        Result<ReviewCaseListResponse> result = await handler.Handle(query!, cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<ReviewCaseDetailResponse>, ProblemHttpResult>> GetAsync(
        string id,
        HttpRequest httpRequest,
        GetReviewCaseHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadRouteId(id, out Guid reviewCaseId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (RejectUnexpectedQuery(httpRequest, []) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        Result<ReviewCaseDetailResponse> result = await handler.Handle(
            new GetReviewCaseQuery(new ReviewCaseId(reviewCaseId)),
            cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<ReviewCaseResponse>, ProblemHttpResult>> ResolveAsync(
        string id,
        HttpRequest httpRequest,
        ResolveReviewCaseHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadRouteId(id, out Guid reviewCaseId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (RejectUnexpectedQuery(httpRequest, []) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        JsonRead read = await TryReadJsonObjectAsync(httpRequest, ResolvePropertyNames, cancellationToken);
        if (read.Problem is not null)
        {
            return read.Problem;
        }

        using (read.Document)
        {
            if (!TryReadResolve(
                    read.Properties!,
                    reviewCaseId,
                    out ResolveReviewCaseCommand? command,
                    out ProblemHttpResult? requestProblem))
            {
                return requestProblem!;
            }

            Result<ReviewCaseResponse> result = await handler.Handle(command!, cancellationToken);
            return result.IsFailure ? Problem(result.Error) : TypedResults.Ok(result.Value);
        }
    }

    private static async Task<Results<Ok<ReviewCaseResponse>, ProblemHttpResult>> DismissAsync(
        string id,
        HttpRequest httpRequest,
        DismissReviewCaseHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadRouteId(id, out Guid reviewCaseId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (RejectUnexpectedQuery(httpRequest, []) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        JsonRead read = await TryReadJsonObjectAsync(httpRequest, DismissPropertyNames, cancellationToken);
        if (read.Problem is not null)
        {
            return read.Problem;
        }

        using (read.Document)
        {
            if (!TryReadDismiss(
                    read.Properties!,
                    reviewCaseId,
                    out DismissReviewCaseCommand? command,
                    out ProblemHttpResult? requestProblem))
            {
                return requestProblem!;
            }

            Result<ReviewCaseResponse> result = await handler.Handle(command!, cancellationToken);
            return result.IsFailure ? Problem(result.Error) : TypedResults.Ok(result.Value);
        }
    }

    private static async Task<JsonRead> TryReadJsonObjectAsync(
        HttpRequest httpRequest,
        string[] allowedNames,
        CancellationToken cancellationToken)
    {
        if (!IsJsonContentType(httpRequest.ContentType))
        {
            return JsonRead.Failed(Problem(ReconciliationApplicationErrors.ReviewCaseUnsupportedMediaType));
        }

        byte[]? body = await ReadBoundedBodyAsync(httpRequest.Body, cancellationToken);
        if (body is null)
        {
            return JsonRead.Failed(Problem(ReconciliationApplicationErrors.ReviewCaseRequestTooLarge));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception) when (exception.InnerException is not OperationCanceledException)
        {
            return JsonRead.Failed(Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest));
        }

        if (!TryReadProperties(document.RootElement, allowedNames, out Dictionary<string, JsonElement>? properties))
        {
            document.Dispose();
            return JsonRead.Failed(Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest));
        }

        return JsonRead.Succeeded(document, properties!);
    }

    private static bool TryReadOpen(
        Dictionary<string, JsonElement> properties,
        out OpenReviewCaseCommand? command,
        out ProblemHttpResult? problem)
    {
        command = null;
        problem = null;
        if (!TryReadRequiredString(properties, "reason", out string? reasonText)
            || !TryParseReason(reasonText, out ReviewCaseReason reason)
            || !TryReadOptionalGuid(properties, "invoiceId", out Guid? invoiceId)
            || !TryReadOptionalGuid(properties, "bankTransactionId", out Guid? bankTransactionId)
            || !TryReadOptionalString(properties, "sourceLabel", out string? sourceLabel)
            || !TryReadOptionalString(properties, "note", out string? note))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            return false;
        }

        command = new OpenReviewCaseCommand(
            reason,
            invoiceId is null ? null : new InvoiceId(invoiceId.Value),
            bankTransactionId is null ? null : new BankTransactionId(bankTransactionId.Value),
            sourceLabel,
            note);
        return true;
    }

    private static bool TryReadResolve(
        Dictionary<string, JsonElement> properties,
        Guid reviewCaseId,
        out ResolveReviewCaseCommand? command,
        out ProblemHttpResult? problem)
    {
        command = null;
        problem = null;
        if (!TryReadConcurrencyToken(properties, out string? concurrencyToken, out ProblemHttpResult? tokenProblem))
        {
            problem = tokenProblem;
            return false;
        }

        if (!TryReadRequiredString(properties, "outcome", out string? outcomeText)
            || !TryParseOutcome(outcomeText, out ReviewCaseOutcome outcome)
            || !TryReadOptionalGuid(properties, "allocationId", out Guid? allocationId)
            || !TryReadOptionalString(properties, "resolutionNote", out string? resolutionNote))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            return false;
        }

        command = new ResolveReviewCaseCommand(
            new ReviewCaseId(reviewCaseId),
            outcome,
            allocationId is null ? null : new AllocationId(allocationId.Value),
            resolutionNote,
            concurrencyToken!);
        return true;
    }

    private static bool TryReadDismiss(
        Dictionary<string, JsonElement> properties,
        Guid reviewCaseId,
        out DismissReviewCaseCommand? command,
        out ProblemHttpResult? problem)
    {
        command = null;
        problem = null;
        if (!TryReadConcurrencyToken(properties, out string? concurrencyToken, out ProblemHttpResult? tokenProblem))
        {
            problem = tokenProblem;
            return false;
        }

        if (!TryReadOptionalString(properties, "resolutionNote", out string? resolutionNote))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            return false;
        }

        command = new DismissReviewCaseCommand(
            new ReviewCaseId(reviewCaseId),
            resolutionNote,
            concurrencyToken!);
        return true;
    }

    private static bool TryBindList(
        HttpRequest httpRequest,
        out ListReviewCasesQuery? query,
        out ProblemHttpResult? problem)
    {
        query = null;
        problem = RejectUnexpectedQuery(httpRequest, ListQueryNames);
        if (problem is not null)
        {
            return false;
        }

        if (!TryReadStatus(httpRequest, out ReviewCaseStatus? status, out problem)
            || !TryReadQueryGuid(httpRequest, "invoiceId", out Guid? invoiceId, out problem)
            || !TryReadQueryGuid(httpRequest, "bankTransactionId", out Guid? bankTransactionId, out problem)
            || !TryReadPage(httpRequest, "page", ReviewCaseListPagination.DefaultPage, out int page, out problem)
            || !TryReadPage(httpRequest, "pageSize", ReviewCaseListPagination.DefaultPageSize, out int pageSize, out problem))
        {
            return false;
        }

        query = new ListReviewCasesQuery(
            status,
            invoiceId is null ? null : new InvoiceId(invoiceId.Value),
            bankTransactionId is null ? null : new BankTransactionId(bankTransactionId.Value),
            page,
            pageSize);
        return true;
    }

    private static bool TryReadRouteId(string id, out Guid reviewCaseId, out ProblemHttpResult? problem)
    {
        if (!Guid.TryParse(id, out reviewCaseId) || reviewCaseId == Guid.Empty)
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            return false;
        }

        problem = null;
        return true;
    }

    private static ProblemHttpResult? RejectUnexpectedQuery(HttpRequest httpRequest, string[] allowedNames)
    {
        foreach (string key in httpRequest.Query.Keys)
        {
            if (!allowedNames.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            }

            if (httpRequest.Query[key].Count != 1)
            {
                return key.Equals("page", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("pageSize", StringComparison.OrdinalIgnoreCase)
                    ? Problem(ReconciliationApplicationErrors.ReviewCaseInvalidPagination)
                    : Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            }
        }

        return null;
    }

    private static bool TryReadStatus(
        HttpRequest httpRequest,
        out ReviewCaseStatus? status,
        out ProblemHttpResult? problem)
    {
        status = null;
        problem = null;
        if (!httpRequest.Query.TryGetValue("status", out StringValues values))
        {
            return true;
        }

        if (values.Count != 1 || !TryParseStatus(values[0], out ReviewCaseStatus parsed))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            return false;
        }

        status = parsed;
        return true;
    }

    private static bool TryReadQueryGuid(
        HttpRequest httpRequest,
        string name,
        out Guid? identifier,
        out ProblemHttpResult? problem)
    {
        identifier = null;
        problem = null;
        if (!httpRequest.Query.TryGetValue(name, out StringValues values))
        {
            return true;
        }

        if (values.Count != 1
            || !Guid.TryParse(values[0], out Guid parsed)
            || parsed == Guid.Empty)
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            return false;
        }

        identifier = parsed;
        return true;
    }

    private static bool TryReadPage(
        HttpRequest httpRequest,
        string name,
        int defaultValue,
        out int page,
        out ProblemHttpResult? problem)
    {
        page = defaultValue;
        problem = null;
        if (!httpRequest.Query.TryGetValue(name, out StringValues values))
        {
            return true;
        }

        if (values.Count != 1
            || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out page))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidPagination);
            return false;
        }

        return true;
    }

    private static bool TryReadProperties(
        JsonElement root,
        string[] allowedNames,
        out Dictionary<string, JsonElement>? properties)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object)
        {
            properties = null;
            return false;
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!allowedNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                || !properties.TryAdd(property.Name, property.Value))
            {
                properties = null;
                return false;
            }
        }

        return true;
    }

    private static bool TryReadRequiredString(
        Dictionary<string, JsonElement> properties,
        string name,
        out string? value)
    {
        value = null;
        if (!properties.TryGetValue(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryReadOptionalString(
        Dictionary<string, JsonElement> properties,
        string name,
        out string? value)
    {
        value = null;
        if (!properties.TryGetValue(name, out JsonElement element))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryReadOptionalGuid(
        Dictionary<string, JsonElement> properties,
        string name,
        out Guid? value)
    {
        value = null;
        if (!properties.TryGetValue(name, out JsonElement element))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String
            || !Guid.TryParse(element.GetString(), out Guid parsed)
            || parsed == Guid.Empty)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadConcurrencyToken(
        Dictionary<string, JsonElement> properties,
        out string? token,
        out ProblemHttpResult? problem)
    {
        token = null;
        problem = null;
        if (!properties.TryGetValue("concurrencyToken", out JsonElement element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidConcurrencyToken);
            return false;
        }

        token = element.GetString();
        return true;
    }

    private static bool TryParseReason(string? value, out ReviewCaseReason reason)
    {
        if (value == nameof(ReviewCaseReason.CompetingMatch))
        {
            reason = ReviewCaseReason.CompetingMatch;
            return true;
        }

        if (value == nameof(ReviewCaseReason.NeedsInvestigation))
        {
            reason = ReviewCaseReason.NeedsInvestigation;
            return true;
        }

        reason = default;
        return false;
    }

    private static bool TryParseOutcome(string? value, out ReviewCaseOutcome outcome)
    {
        if (value == nameof(ReviewCaseOutcome.AllocationRecorded))
        {
            outcome = ReviewCaseOutcome.AllocationRecorded;
            return true;
        }

        if (value == nameof(ReviewCaseOutcome.NoActionRequired))
        {
            outcome = ReviewCaseOutcome.NoActionRequired;
            return true;
        }

        if (value == nameof(ReviewCaseOutcome.HandledOutside))
        {
            outcome = ReviewCaseOutcome.HandledOutside;
            return true;
        }

        outcome = default;
        return false;
    }

    private static bool TryParseStatus(string? value, out ReviewCaseStatus status)
    {
        if (value == nameof(ReviewCaseStatus.Open))
        {
            status = ReviewCaseStatus.Open;
            return true;
        }

        if (value == nameof(ReviewCaseStatus.Resolved))
        {
            status = ReviewCaseStatus.Resolved;
            return true;
        }

        if (value == nameof(ReviewCaseStatus.Dismissed))
        {
            status = ReviewCaseStatus.Dismissed;
            return true;
        }

        status = default;
        return false;
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

    private static Task DocumentOpenOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        operation.RequestBody = JsonBody(
            "Reason is CompetingMatch or NeedsInvestigation. CompetingMatch requires invoiceId and bankTransactionId. NeedsInvestigation requires exactly one of them. sourceLabel is manual, exact-1, or smart-1.",
            CreateOpenRequestSchema());
        DescribeCreatedLocation(operation);
        AnnotateReviewCaseSchemas(context);
        return Task.CompletedTask;
    }

    private static Task DocumentListOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        operation.Parameters ??= [];
        operation.Parameters.Add(QueryParameter(
            "status",
            "Open, Resolved, or Dismissed. Omitted returns every status.",
            StringEnum(null, "Open", "Resolved", "Dismissed")));
        operation.Parameters.Add(QueryParameter(
            "invoiceId",
            "Optional invoice filter. An empty GUID is rejected.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            }));
        operation.Parameters.Add(QueryParameter(
            "bankTransactionId",
            "Optional bank transaction filter. An empty GUID is rejected.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            }));
        operation.Parameters.Add(QueryParameter(
            "page",
            "Page number. Defaults to 1. The zero-based offset must fit in a 32-bit integer.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(ReviewCaseListPagination.DefaultPage),
                Minimum = "1",
            }));
        operation.Parameters.Add(QueryParameter(
            "pageSize",
            "Page size. Defaults to 50. Maximum is 100.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(ReviewCaseListPagination.DefaultPageSize),
                Minimum = "1",
                Maximum = "100",
            }));
        AnnotateReviewCaseSchemas(context);
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
        DescribeRouteId(operation);
        AnnotateReviewCaseSchemas(context);
        return Task.CompletedTask;
    }

    private static Task DocumentResolveOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        DescribeRouteId(operation);
        operation.RequestBody = JsonBody(
            "AllocationRecorded requires allocationId. NoActionRequired and HandledOutside forbid allocationId.",
            CreateResolveRequestSchema());
        AnnotateReviewCaseSchemas(context);
        return Task.CompletedTask;
    }

    private static Task DocumentDismissOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        DescribeRouteId(operation);
        operation.RequestBody = JsonBody(
            "Dismissal accepts an optional resolution note and the concurrency token. outcome and allocationId are rejected.",
            CreateDismissRequestSchema());
        AnnotateReviewCaseSchemas(context);
        return Task.CompletedTask;
    }

    private static OpenApiRequestBody JsonBody(string description, OpenApiSchema schema)
    {
        return new OpenApiRequestBody
        {
            Required = true,
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = schema,
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

    private static void DescribeRouteId(OpenApiOperation operation)
    {
        operation.Parameters ??= [];
        foreach (IOpenApiParameter parameter in operation.Parameters)
        {
            if (string.Equals(parameter.Name, "id", StringComparison.OrdinalIgnoreCase)
                && parameter.In == ParameterLocation.Path
                && parameter is OpenApiParameter pathParameter)
            {
                pathParameter.Description = "Review case id. An empty or malformed GUID is rejected.";
                pathParameter.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "uuid",
                };
                return;
            }
        }
    }

    private static void DescribeCreatedLocation(OpenApiOperation operation)
    {
        if (operation.Responses?.TryGetValue("201", out IOpenApiResponse? created) != true
            || created is not OpenApiResponse createdResponse)
        {
            return;
        }

        createdResponse.Headers ??= new Dictionary<string, IOpenApiHeader>();
        createdResponse.Headers["Location"] = new OpenApiHeader
        {
            Description = "URL of GET /api/review-cases/{id} for the created case.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
            },
        };
    }

    private static void AnnotateReviewCaseSchemas(OpenApiOperationTransformerContext context)
    {
        if (context.Document?.Components?.Schemas is null)
        {
            return;
        }

        if (context.Document.Components.Schemas.TryGetValue(nameof(ReviewCaseResponse), out IOpenApiSchema? schema))
        {
            AnnotateCaseSchema(schema);
        }
    }

    private static void AnnotateCaseSchema(IOpenApiSchema schema)
    {
        if (schema is not OpenApiSchema target)
        {
            return;
        }

        SetStringEnum(
            target,
            "reason",
            "Why the case was opened.",
            nameof(ReviewCaseReason.CompetingMatch),
            nameof(ReviewCaseReason.NeedsInvestigation));
        SetStringEnum(
            target,
            "status",
            "Open, Resolved, or Dismissed.",
            nameof(ReviewCaseStatus.Open),
            nameof(ReviewCaseStatus.Resolved),
            nameof(ReviewCaseStatus.Dismissed));
        SetStringEnum(
            target,
            "outcome",
            "Present after resolution. A dismissed case has no outcome.",
            nameof(ReviewCaseOutcome.AllocationRecorded),
            nameof(ReviewCaseOutcome.NoActionRequired),
            nameof(ReviewCaseOutcome.HandledOutside));
        SetStringEnum(
            target,
            "sourceLabel",
            "Optional provenance label. It is not a match result.",
            ReviewCaseSource.Manual.Value,
            ReviewCaseSource.Exact1.Value,
            ReviewCaseSource.Smart1.Value);
        if (TryGetProperty(target, "concurrencyToken", out OpenApiSchema? token))
        {
            token.Description = ConcurrencyTokenDescription;
            token.Type = JsonSchemaType.String;
        }
    }

    private static void SetStringEnum(
        OpenApiSchema schema,
        string name,
        string description,
        params string[] values)
    {
        if (!TryGetProperty(schema, name, out OpenApiSchema? property))
        {
            return;
        }

        property.Description = description;
        property.Type = JsonSchemaType.String;
        property.Enum = values.Select(static value => (JsonNode)JsonValue.Create(value)!).ToList();
    }

    private static bool TryGetProperty(OpenApiSchema schema, string name, out OpenApiSchema property)
    {
        property = null!;
        if (schema.Properties is null)
        {
            return false;
        }

        if (!schema.Properties.TryGetValue(name, out IOpenApiSchema? candidate)
            && !schema.Properties.TryGetValue(char.ToUpperInvariant(name[0]) + name[1..], out candidate))
        {
            return false;
        }

        if (candidate is not OpenApiSchema openApiSchema)
        {
            return false;
        }

        property = openApiSchema;
        return true;
    }

    private static OpenApiSchema CreateOpenRequestSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalPropertiesAllowed = false,
            Required = new HashSet<string> { "reason" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["reason"] = StringEnum(
                    "CompetingMatch requires both financial ids. NeedsInvestigation requires exactly one.",
                    nameof(ReviewCaseReason.CompetingMatch),
                    nameof(ReviewCaseReason.NeedsInvestigation)),
                ["invoiceId"] = Uuid("Optional invoice id. An empty GUID is rejected."),
                ["bankTransactionId"] = Uuid("Optional bank transaction id. An empty GUID is rejected."),
                ["sourceLabel"] = StringEnum(
                    "Optional provenance label.",
                    ReviewCaseSource.Manual.Value,
                    ReviewCaseSource.Exact1.Value,
                    ReviewCaseSource.Smart1.Value),
                ["note"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Optional opening note. Blank becomes null. Maximum length is 500 characters after trim.",
                    MaxLength = ReviewCase.MaxNoteLength,
                },
            },
        };
    }

    private static OpenApiSchema CreateResolveRequestSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalPropertiesAllowed = false,
            Required = new HashSet<string> { "outcome", "concurrencyToken" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["outcome"] = StringEnum(
                    "AllocationRecorded requires allocationId. The other outcomes forbid it.",
                    nameof(ReviewCaseOutcome.AllocationRecorded),
                    nameof(ReviewCaseOutcome.NoActionRequired),
                    nameof(ReviewCaseOutcome.HandledOutside)),
                ["allocationId"] = Uuid("Required only for AllocationRecorded. An empty GUID is rejected."),
                ["resolutionNote"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Optional resolution note. Blank becomes null. Maximum length is 500 characters after trim.",
                    MaxLength = ReviewCase.MaxNoteLength,
                },
                ["concurrencyToken"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = ConcurrencyTokenDescription,
                },
            },
        };
    }

    private static OpenApiSchema CreateDismissRequestSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalPropertiesAllowed = false,
            Required = new HashSet<string> { "concurrencyToken" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["resolutionNote"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Optional dismissal note. Blank becomes null. Maximum length is 500 characters after trim.",
                    MaxLength = ReviewCase.MaxNoteLength,
                },
                ["concurrencyToken"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = ConcurrencyTokenDescription,
                },
            },
        };
    }

    private static OpenApiSchema StringEnum(string? description, params string[] values)
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = description,
            Enum = values.Select(static value => (JsonNode)JsonValue.Create(value)!).ToList(),
        };
    }

    private static OpenApiSchema Uuid(string description)
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Format = "uuid",
            Description = description,
        };
    }

    private static ProblemHttpResult Problem(Error error)
    {
        return TypedResults.Problem(
            detail: error.Message,
            statusCode: StatusCodeFor(error),
            title: TitleFor(error),
            extensions: new Dictionary<string, object?>
            {
                ["code"] = error.Code,
            });
    }

    private static int StatusCodeFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseInvalidConcurrencyToken.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseInvalidPagination.Code
            || error.Code == ReconciliationErrors.ReviewCaseAllocationRequired.Code
            || error.Code == ReconciliationErrors.ReviewCaseAllocationForbidden.Code)
        {
            return StatusCodes.Status400BadRequest;
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseNotFound.Code
            || error.Code == ReconciliationApplicationErrors.InvoiceNotFound.Code
            || error.Code == ReconciliationApplicationErrors.BankTransactionNotFound.Code)
        {
            return StatusCodes.Status404NotFound;
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict.Code
            || error.Code == ReconciliationErrors.ReviewCaseTerminalConflict.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAllocationMismatch.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseSubjectConflict.Code)
        {
            return StatusCodes.Status409Conflict;
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseInconsistentLinkedMoney.Code)
        {
            return StatusCodes.Status500InternalServerError;
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseUnsupportedMediaType.Code)
        {
            return StatusCodes.Status415UnsupportedMediaType;
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseRequestTooLarge.Code)
        {
            return StatusCodes.Status413PayloadTooLarge;
        }

        throw new InvalidOperationException(
            $"Unexpected review case failure '{error.Code}'.");
    }

    private static string TitleFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code)
        {
            return "Review case request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseInvalidConcurrencyToken.Code)
        {
            return "Concurrency token is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseInvalidPagination.Code)
        {
            return "Invalid pagination.";
        }

        if (error.Code == ReconciliationErrors.ReviewCaseAllocationRequired.Code)
        {
            return "An allocation id is required.";
        }

        if (error.Code == ReconciliationErrors.ReviewCaseAllocationForbidden.Code)
        {
            return "This outcome cannot reference an allocation.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseNotFound.Code)
        {
            return "Review case was not found.";
        }

        if (error.Code == ReconciliationApplicationErrors.InvoiceNotFound.Code)
        {
            return "Invoice was not found.";
        }

        if (error.Code == ReconciliationApplicationErrors.BankTransactionNotFound.Code)
        {
            return "Bank transaction was not found.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict.Code)
        {
            return "The review case was modified.";
        }

        if (error.Code == ReconciliationErrors.ReviewCaseTerminalConflict.Code)
        {
            return "The review case already has a terminal decision.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAllocationMismatch.Code)
        {
            return "The allocation does not cover the review case.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseSubjectConflict.Code)
        {
            return "The open review case could not be recovered.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseInconsistentLinkedMoney.Code)
        {
            return "Stored financial amounts are inconsistent.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseUnsupportedMediaType.Code)
        {
            return "Unsupported media type.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseRequestTooLarge.Code)
        {
            return "Review case request is too large.";
        }

        throw new InvalidOperationException(
            $"Unexpected review case failure '{error.Code}'.");
    }

    private readonly record struct JsonRead(
        JsonDocument? Document,
        Dictionary<string, JsonElement>? Properties,
        ProblemHttpResult? Problem)
    {
        public static JsonRead Failed(ProblemHttpResult problem)
        {
            return new JsonRead(null, null, problem);
        }

        public static JsonRead Succeeded(JsonDocument document, Dictionary<string, JsonElement> properties)
        {
            return new JsonRead(document, properties, null);
        }
    }
}