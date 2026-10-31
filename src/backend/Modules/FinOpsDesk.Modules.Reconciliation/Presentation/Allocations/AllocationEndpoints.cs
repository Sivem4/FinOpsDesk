using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.Allocations;

internal static class AllocationEndpoints
{
    private const int MaxJsonBodyBytes = 4096;

    private static readonly string[] RootPropertyNames = ["invoiceId", "bankTransactionId", "amount"];

    private static readonly string[] AmountPropertyNames = ["amount", "currency"];

    private static readonly string[] AuditQueryNames = ["page", "pageSize"];

    private static readonly string[] HistoryQueryNames =
        ["page", "pageSize", "invoiceId", "bankTransactionId"];

    private static readonly string[] ContextQueryNames = ["invoiceId", "bankTransactionId"];

    public static RouteGroupBuilder MapAllocationEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/allocations/context", GetContextAsync)
            .WithName("GetAllocationContext")
            .WithSummary("Read the current business context of one invoice and one bank transaction.")
            .WithDescription(
                "Requires the TenantMember policy. OrganizationId comes only from the authenticated organization claim. "
                + "A missing JWT sub is allowed. The query requires exactly one invoiceId and one bankTransactionId. "
                + "Both reads are organization-scoped point lookups. They are not one atomic snapshot and they do not lock or reserve either record. "
                + "Outstanding and unapplied amounts are calculated from the stored principal and allocated amounts. "
                + "Different currencies are returned as stored. POST /api/allocations remains responsible for rejecting a cross-currency allocation "
                + "and for checking the balances at commit time.")
            .Produces<AllocationContextResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .AddOpenApiOperationTransformer(DocumentContextOperation);

        group.MapPost("/allocations", AllocateAsync)
            .WithName("AllocateExplicitAmount")
            .AddOpenApiOperationTransformer(DocumentAllocationOperation);

        group.MapPost("/allocations/{allocationId}/reverse", ReverseAsync)
            .WithName("ReverseAllocation")
            .WithSummary("Reverse one confirmed allocation for its full original amount.")
            .WithDescription(
                "Requires a bearer token and the TenantMember policy. OrganizationId comes only from the authenticated organization claim. "
                + "ActorId comes only from the JWT sub claim. A missing, blank, or overlong sub is rejected before replay or financial work. "
                + "The body accepts only reason. Amount, reversedAt, actorId, and organizationId are not accepted from the client. "
                + "Idempotency-Key is a UUID in D format and is scoped to reversal, separate from confirmation. "
                + "A fresh reversal and a matching replay both return HTTP 200 with the persisted reversal values. "
                + "replayed is false for the first commit and true for a matching replay.")
            .Produces<ReverseAllocationResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(DocumentReverseOperation);

        group.MapGet("/allocations", GetHistoryAsync)
            .WithName("ListAllocationHistory")
            .WithSummary("List persisted allocations for the authenticated organization.")
            .WithDescription(
                "Requires the TenantMember policy. OrganizationId comes only from the authenticated organization claim. "
                + "A missing JWT sub is allowed. Optional invoiceId and bankTransactionId filters are exact and combined with AND. "
                + "A missing filter is unfiltered. An empty or malformed filter is rejected. "
                + "An id from another organization returns an empty page, not that organization's rows. "
                + "Results are ordered by ConfirmedAt descending, then allocation id descending. "
                + "Page defaults to 1 and page size defaults to 50, with a maximum of 100. "
                + "A valid filter with no matches returns an empty page. "
                + "The amount is the original allocation amount, including reversed rows. "
                + "Invoice number and bank reference are the current source values, not confirmation-time snapshots. "
                + "The response does not include the actor, the idempotency key, rowversion, or current balances. "
                + "The count and the page are separate statements and are not one snapshot. "
                + "This read does not confirm, reverse, or recover an idempotent POST.")
            .Produces<AllocationHistoryListResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .AddOpenApiOperationTransformer(DocumentHistoryOperation);

        group.MapGet("/allocations/{allocationId}/audit", GetAuditAsync)
            .WithName("GetAllocationAudit")
            .WithSummary("List financial audit entries for one allocation.")
            .WithDescription(
                "Requires the TenantMember policy. OrganizationId comes only from the authenticated organization claim. "
                + "Any TenantMember may read ActorId. Finer audit permissions are future work. "
                + "A missing JWT sub is allowed on this read. "
                + "Results are ordered by RecordedAt ascending, then audit entry id ascending. "
                + "Page defaults to 1 and page size defaults to 50, with a maximum of 100. "
                + "An allocation with no audit rows returns an empty page. "
                + "The response does not include the idempotency key and does not reconstruct a historical actor.")
            .Produces<FinancialAuditListResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AddOpenApiOperationTransformer(DocumentAuditOperation);

        group.MapGet("/allocations/{allocationId}/reversal-context", GetReversalContextAsync)
            .WithName("GetAllocationReversalContext")
            .WithSummary("Read the current reversal context of one allocation.")
            .WithDescription(
                "Requires the TenantMember policy. OrganizationId comes only from the authenticated organization claim. "
                + "A missing JWT sub is allowed. The read returns the current persisted allocation, including reversed rows and rows without a confirmation audit. "
                + "The amount is the original allocation amount. Invoice number and bank reference are current source labels. "
                + "The response does not include rowversion, idempotency keys, actor, audit, or balances. Any query parameter is rejected. "
                + "This read does not lock or reserve the allocation and does not authorize POST /api/allocations/{allocationId}/reverse.")
            .Produces<AllocationReversalContextResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AddOpenApiOperationTransformer(DocumentReversalContextOperation);

        return group;
    }

    private static async Task<Results<Ok<ReverseAllocationResponse>, ProblemHttpResult>> ReverseAsync(
        string allocationId,
        HttpRequest httpRequest,
        ReverseAllocationHandler handler,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(allocationId, out Guid parsedAllocationId) || parsedAllocationId == Guid.Empty)
        {
            return ReversalProblem(ReconciliationApplicationErrors.AllocationReversalInvalidRequest);
        }

        if (!IsJsonContentType(httpRequest.ContentType))
        {
            return UnsupportedMediaType();
        }

        if (!TryReadIdempotencyKey(httpRequest, out Guid idempotencyKey, out Error? keyError))
        {
            return Problem(keyError!, StatusCodeFor(keyError!), TitleFor(keyError!));
        }

        if (httpRequest.Query.Count > 0)
        {
            return ReversalProblem(ReconciliationApplicationErrors.AllocationReversalInvalidRequest);
        }

        byte[]? body = await ReadBoundedBodyAsync(httpRequest.Body, cancellationToken);
        if (body is null)
        {
            return RequestTooLarge();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception) when (exception.InnerException is not OperationCanceledException)
        {
            return ReversalProblem(ReconciliationApplicationErrors.AllocationReversalInvalidRequest);
        }

        using (document)
        {
            if (!TryReadReversalReason(document.RootElement, out string reason, out Error? requestError))
            {
                return ReversalProblem(requestError!);
            }

            Result<ReverseAllocationResponse> result = await handler.Handle(
                new ReverseAllocationCommand(
                    new AllocationId(parsedAllocationId),
                    reason,
                    idempotencyKey),
                cancellationToken);

            if (result.IsFailure)
            {
                return ReversalProblem(result.Error);
            }

            return TypedResults.Ok(result.Value);
        }
    }

    private static async Task<Results<Ok<AllocationHistoryListResponse>, ProblemHttpResult>> GetHistoryAsync(
        HttpRequest httpRequest,
        ListAllocationHistoryHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryBindHistoryQuery(
                httpRequest,
                out int page,
                out int pageSize,
                out Guid? invoiceId,
                out Guid? bankTransactionId,
                out ProblemHttpResult? queryProblem))
        {
            return queryProblem!;
        }

        Result<AllocationHistoryListResponse> result = await handler.Handle(
            new ListAllocationHistoryQuery(
                page,
                pageSize,
                invoiceId is null ? null : new InvoiceId(invoiceId.Value),
                bankTransactionId is null ? null : new BankTransactionId(bankTransactionId.Value)),
            cancellationToken);

        if (result.IsFailure)
        {
            return Problem(result.Error, StatusCodeFor(result.Error), TitleFor(result.Error));
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<FinancialAuditListResponse>, ProblemHttpResult>> GetAuditAsync(
        string allocationId,
        HttpRequest httpRequest,
        GetAllocationAuditHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadAllocationId(allocationId, out Guid parsedAllocationId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (!TryBindAuditQuery(httpRequest, out int page, out int pageSize, out ProblemHttpResult? queryProblem))
        {
            return queryProblem!;
        }

        Result<FinancialAuditListResponse> result = await handler.Handle(
            new GetAllocationAuditQuery(new AllocationId(parsedAllocationId), page, pageSize),
            cancellationToken);

        if (result.IsFailure)
        {
            return Problem(result.Error, StatusCodeFor(result.Error), TitleFor(result.Error));
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<AllocationReversalContextResponse>, ProblemHttpResult>> GetReversalContextAsync(
        string allocationId,
        HttpRequest httpRequest,
        GetAllocationReversalContextHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadReversalContextAllocationId(allocationId, out Guid parsedAllocationId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (httpRequest.Query.Count > 0)
        {
            return Problem(
                ReconciliationApplicationErrors.AllocationReversalContextInvalidRequest,
                StatusCodes.Status400BadRequest,
                "Reversal context request is invalid.");
        }

        Result<AllocationReversalContextResponse> result = await handler.Handle(
            new GetAllocationReversalContextQuery(new AllocationId(parsedAllocationId)),
            cancellationToken);

        if (result.IsFailure)
        {
            return Problem(result.Error, StatusCodeFor(result.Error), TitleFor(result.Error));
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<AllocationContextResponse>, ProblemHttpResult>> GetContextAsync(
        HttpRequest httpRequest,
        GetAllocationContextHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadContextQuery(
                httpRequest,
                out Guid invoiceId,
                out Guid bankTransactionId,
                out ProblemHttpResult? queryProblem))
        {
            return queryProblem!;
        }

        Result<AllocationContextResponse> result = await handler.Handle(
            new GetAllocationContextQuery(new InvoiceId(invoiceId), new BankTransactionId(bankTransactionId)),
            cancellationToken);

        if (result.IsFailure)
        {
            return Problem(result.Error, StatusCodeFor(result.Error), TitleFor(result.Error));
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<JsonHttpResult<ConfirmExplicitAmountResponse>, ProblemHttpResult>> AllocateAsync(
        HttpRequest httpRequest,
        ConfirmExplicitAmountHandler handler,
        CancellationToken cancellationToken)
    {
        if (!IsJsonContentType(httpRequest.ContentType))
        {
            return UnsupportedMediaType();
        }

        if (!TryReadIdempotencyKey(httpRequest, out Guid idempotencyKey, out Error? keyError))
        {
            return Problem(keyError!, StatusCodeFor(keyError!), TitleFor(keyError!));
        }

        byte[]? body = await ReadBoundedBodyAsync(httpRequest.Body, cancellationToken);
        if (body is null)
        {
            return RequestTooLarge();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception) when (exception.InnerException is not OperationCanceledException)
        {
            return InvalidAllocationRequest();
        }

        using (document)
        {
            if (!TryReadRequest(
                    document.RootElement,
                    out Guid invoiceId,
                    out Guid bankTransactionId,
                    out decimal amount,
                    out string? currency,
                    out Error? requestError)
                || string.IsNullOrWhiteSpace(currency))
            {
                Error requestFailure = requestError
                    ?? ReconciliationApplicationErrors.AllocationInvalidRequest;
                return Problem(requestFailure, StatusCodeFor(requestFailure), TitleFor(requestFailure));
            }

            CurrencyCode parsedCurrency;
            try
            {
                parsedCurrency = new CurrencyCode(currency);
            }
            catch (ArgumentException)
            {
                return InvalidAllocationRequest();
            }

            Result<ConfirmExplicitAmountResponse> result = await handler.Handle(
                new ConfirmExplicitAmountCommand(
                    new InvoiceId(invoiceId),
                    new BankTransactionId(bankTransactionId),
                    new Money(amount, parsedCurrency),
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
    }

    private static bool TryReadRequest(
        JsonElement root,
        out Guid invoiceId,
        out Guid bankTransactionId,
        out decimal amount,
        out string? currency,
        out Error? error)
    {
        invoiceId = Guid.Empty;
        bankTransactionId = Guid.Empty;
        amount = 0m;
        currency = null;
        error = ReconciliationApplicationErrors.AllocationInvalidRequest;

        if (root.ValueKind != JsonValueKind.Object
            || !TryReadObject(root, RootPropertyNames, out Dictionary<string, JsonElement> properties, out error))
        {
            return false;
        }

        if (!properties.TryGetValue("invoiceId", out JsonElement invoiceElement)
            || !properties.TryGetValue("bankTransactionId", out JsonElement bankTransactionElement)
            || !properties.TryGetValue("amount", out JsonElement amountElement)
            || !TryReadGuid(invoiceElement, out invoiceId)
            || !TryReadGuid(bankTransactionElement, out bankTransactionId)
            || amountElement.ValueKind != JsonValueKind.Object)
        {
            error = ReconciliationApplicationErrors.AllocationInvalidRequest;
            return false;
        }

        if (!TryReadObject(amountElement, AmountPropertyNames, out Dictionary<string, JsonElement> amountProperties, out error))
        {
            return false;
        }

        if (!amountProperties.TryGetValue("amount", out JsonElement valueElement)
            || !amountProperties.TryGetValue("currency", out JsonElement currencyElement)
            || currencyElement.ValueKind != JsonValueKind.String)
        {
            error = ReconciliationApplicationErrors.AllocationInvalidRequest;
            return false;
        }

        currency = currencyElement.GetString();
        if (string.IsNullOrWhiteSpace(currency))
        {
            error = ReconciliationApplicationErrors.AllocationInvalidRequest;
            return false;
        }

        if (valueElement.ValueKind != JsonValueKind.Number
            || !JsonDecimalLiteral.TryReadExact(valueElement, out amount))
        {
            error = valueElement.ValueKind == JsonValueKind.Number
                ? ReconciliationApplicationErrors.ExplicitAmountInvalid
                : ReconciliationApplicationErrors.AllocationInvalidRequest;
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadObject(
        JsonElement element,
        IReadOnlyList<string> allowedNames,
        out Dictionary<string, JsonElement> properties,
        out Error? error)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowedNames.Any(name => name.Equals(property.Name, StringComparison.OrdinalIgnoreCase))
                || !properties.TryAdd(property.Name, property.Value))
            {
                error = ReconciliationApplicationErrors.AllocationInvalidRequest;
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool TryReadReversalReason(JsonElement root, out string reason, out Error? error)
    {
        reason = string.Empty;
        error = ReconciliationApplicationErrors.AllocationReversalInvalidRequest;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? raw = null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!property.Name.Equals("reason", StringComparison.OrdinalIgnoreCase) || raw is not null)
            {
                return false;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            raw = property.Value.GetString();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        reason = raw.Trim();
        if (reason.Length > Allocation.MaxReversalReasonLength)
        {
            error = ReconciliationErrors.AllocationReversalReasonTooLong;
            return false;
        }

        error = null;
        return true;
    }

    private static ProblemHttpResult ReversalProblem(Error error)
    {
        return Problem(error, StatusCodeForReversal(error), TitleForReversal(error));
    }

    private static int StatusCodeForReversal(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ActorRequired.Code)
        {
            return StatusCodes.Status403Forbidden;
        }

        if (error.Code == ReconciliationApplicationErrors.IdempotencyKeyRequired.Code
            || error.Code == ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code
            || error.Code == ReconciliationApplicationErrors.AllocationReversalInvalidRequest.Code
            || error.Code == ReconciliationErrors.AllocationReversalReasonTooLong.Code)
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

        if (error.Code == ReconciliationApplicationErrors.AllocationNotFound.Code)
        {
            return StatusCodes.Status404NotFound;
        }

        if (error.Code == ReconciliationErrors.AllocationAlreadyReversed.Code
            || error.Code == ReconciliationErrors.AllocationReversalTargetMismatch.Code
            || error.Code == ReconciliationErrors.InvoiceReversalExceedsAllocated.Code
            || error.Code == ReconciliationErrors.BankTransactionReversalExceedsAllocated.Code
            || error.Code == ReconciliationErrors.CurrencyMismatch.Code
            || error.Code == ReconciliationApplicationErrors.AllocationReversalIdempotencyPayloadConflict.Code
            || error.Code == ReconciliationApplicationErrors.AllocationReversalIntegrityConflict.Code
            || error.Code == ReconciliationApplicationErrors.ConcurrencyConflict.Code)
        {
            return StatusCodes.Status409Conflict;
        }

        throw new InvalidOperationException(
            $"Unexpected allocation reversal failure '{error.Code}'.");
    }

    private static string TitleForReversal(Error error)
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

        if (error.Code == ReconciliationApplicationErrors.AllocationReversalInvalidRequest.Code)
        {
            return "Reversal request is invalid.";
        }

        if (error.Code == ReconciliationErrors.AllocationReversalReasonTooLong.Code)
        {
            return "Reversal reason is too long.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationNotFound.Code)
        {
            return "Allocation was not found.";
        }

        if (error.Code == ReconciliationErrors.AllocationAlreadyReversed.Code)
        {
            return "Allocation is already reversed.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationReversalIdempotencyPayloadConflict.Code)
        {
            return "Idempotency key was already used.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationReversalIntegrityConflict.Code)
        {
            return "Stored reversal is inconsistent.";
        }

        if (error.Code == ReconciliationApplicationErrors.ConcurrencyConflict.Code)
        {
            return "The financial records were modified.";
        }

        if (error.Code == ReconciliationErrors.InvoiceReversalExceedsAllocated.Code
            || error.Code == ReconciliationErrors.BankTransactionReversalExceedsAllocated.Code
            || error.Code == ReconciliationErrors.AllocationReversalTargetMismatch.Code
            || error.Code == ReconciliationErrors.CurrencyMismatch.Code)
        {
            return "Reversal conflicts with the stored financial records.";
        }

        throw new InvalidOperationException(
            $"Unexpected allocation reversal failure '{error.Code}'.");
    }

    private static Task DocumentReverseOperation(
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
            Description = "The normalized reason is stored. Leading and trailing whitespace is removed. At most 500 characters.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        AdditionalPropertiesAllowed = false,
                        Required = new HashSet<string> { "reason" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["reason"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                MinLength = 1,
                                MaxLength = Allocation.MaxReversalReasonLength,
                            },
                        },
                    },
                },
            },
        };

        operation.Parameters ??= [];
        foreach (IOpenApiParameter parameter in operation.Parameters)
        {
            if (string.Equals(parameter.Name, "allocationId", StringComparison.OrdinalIgnoreCase)
                && parameter.In == ParameterLocation.Path
                && parameter is OpenApiParameter pathParameter)
            {
                pathParameter.Description = "Allocation id. An empty or malformed GUID is rejected.";
                pathParameter.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "uuid",
                };
            }
        }

        bool headerPresent = false;
        foreach (IOpenApiParameter parameter in operation.Parameters)
        {
            if (string.Equals(parameter.Name, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)
                && parameter.In == ParameterLocation.Header)
            {
                headerPresent = true;
                break;
            }
        }

        if (!headerPresent)
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = true,
                Description = "Exactly one UUID in D format. An empty UUID is rejected. The key is scoped to reversal.",
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "uuid",
                },
            });
        }

        return Task.CompletedTask;
    }

    private static bool TryReadGuid(JsonElement element, out Guid value)
    {
        value = Guid.Empty;
        return element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out value)
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
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        string mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static ProblemHttpResult InvalidAllocationRequest()
    {
        return Problem(
            ReconciliationApplicationErrors.AllocationInvalidRequest,
            StatusCodes.Status400BadRequest,
            "Allocation request is invalid.");
    }

    private static ProblemHttpResult UnsupportedMediaType()
    {
        return Problem(
            ReconciliationApplicationErrors.AllocationUnsupportedMediaType,
            StatusCodes.Status415UnsupportedMediaType,
            "Unsupported media type.");
    }

    private static ProblemHttpResult RequestTooLarge()
    {
        return Problem(
            ReconciliationApplicationErrors.AllocationRequestTooLarge,
            StatusCodes.Status413PayloadTooLarge,
            "Allocation request is too large.");
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
            || error.Code == ReconciliationApplicationErrors.AllocationInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.AllocationAuditInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.AllocationAuditInvalidPagination.Code
            || error.Code == ReconciliationApplicationErrors.AllocationHistoryInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.AllocationHistoryInvalidPagination.Code
            || error.Code == ReconciliationApplicationErrors.AllocationReversalContextInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.AllocationContextInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.ExplicitAmountInvalid.Code)
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

        if (error.Code == ReconciliationApplicationErrors.InvoiceNotFound.Code
            || error.Code == ReconciliationApplicationErrors.BankTransactionNotFound.Code
            || error.Code == ReconciliationApplicationErrors.AllocationNotFound.Code)
        {
            return StatusCodes.Status404NotFound;
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationContextInconsistentMoney.Code)
        {
            return StatusCodes.Status500InternalServerError;
        }

        if (error.Code == ReconciliationErrors.CurrencyMismatch.Code
            || error.Code == ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code
            || error.Code == ReconciliationErrors.BankTransactionAllocationExceedsUnapplied.Code
            || error.Code == ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code
            || error.Code == ReconciliationApplicationErrors.ConcurrencyConflict.Code)
        {
            return StatusCodes.Status409Conflict;
        }

        throw new InvalidOperationException(
            $"Unexpected allocation failure '{error.Code}'.");
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

        if (error.Code == ReconciliationApplicationErrors.AllocationInvalidRequest.Code)
        {
            return "Allocation request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationNotFound.Code)
        {
            return "Allocation was not found.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationAuditInvalidRequest.Code)
        {
            return "Allocation audit request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationContextInvalidRequest.Code)
        {
            return "Allocation context request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationContextInconsistentMoney.Code)
        {
            return "Stored financial amounts are inconsistent.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationAuditInvalidPagination.Code)
        {
            return "Allocation audit page is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationHistoryInvalidRequest.Code)
        {
            return "Allocation history request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationReversalContextInvalidRequest.Code)
        {
            return "Reversal context request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationHistoryInvalidPagination.Code)
        {
            return "Allocation history page is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ExplicitAmountInvalid.Code)
        {
            return "Allocation amount is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationRequestTooLarge.Code)
        {
            return "Allocation request is too large.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationUnsupportedMediaType.Code)
        {
            return "Unsupported media type.";
        }

        if (error.Code == ReconciliationApplicationErrors.InvoiceNotFound.Code)
        {
            return "Invoice was not found.";
        }

        if (error.Code == ReconciliationApplicationErrors.BankTransactionNotFound.Code)
        {
            return "Bank transaction was not found.";
        }

        if (error.Code == ReconciliationErrors.CurrencyMismatch.Code)
        {
            return "Currencies do not match.";
        }

        if (error.Code == ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code)
        {
            return "Allocation exceeds the invoice outstanding amount.";
        }

        if (error.Code == ReconciliationErrors.BankTransactionAllocationExceedsUnapplied.Code)
        {
            return "Allocation exceeds the unapplied bank transaction amount.";
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
            $"Unexpected allocation failure '{error.Code}'.");
    }

    private static Task DocumentAllocationOperation(
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
            Description = "One invoice, one bank transaction, and one explicit amount.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = CreateAllocationRequestSchema(),
                },
            },
        };

        operation.Parameters ??= [];
        bool headerPresent = false;
        foreach (IOpenApiParameter parameter in operation.Parameters)
        {
            if (string.Equals(parameter.Name, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)
                && parameter.In == ParameterLocation.Header)
            {
                headerPresent = true;
                break;
            }
        }

        if (!headerPresent)
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = true,
                Description = "Exactly one UUID in D format. An empty UUID is rejected.",
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "uuid",
                },
            });
        }

        return Task.CompletedTask;
    }

    private static OpenApiSchema CreateAllocationRequestSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalPropertiesAllowed = false,
            Required = new HashSet<string>
            {
                "invoiceId",
                "bankTransactionId",
                "amount",
            },
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
                ["amount"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    AdditionalPropertiesAllowed = false,
                    Required = new HashSet<string>
                    {
                        "amount",
                        "currency",
                    },
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["amount"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Number,
                            Description =
                                "JSON number greater than zero and less than 1000000000000000. It must fit decimal(19,4) without rounding. Trailing zeros and exact exponent notation are allowed.",
                            ExclusiveMinimum = "0",
                            ExclusiveMaximum = "1000000000000000",
                            MultipleOf = 0.0001m,
                        },
                        ["currency"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Description = "Three-letter currency code.",
                            MinLength = 3,
                            MaxLength = 3,
                            Pattern = "^[A-Za-z]{3}$",
                        },
                    },
                },
            },
        };
    }

    private static bool TryReadContextQuery(
        HttpRequest httpRequest,
        out Guid invoiceId,
        out Guid bankTransactionId,
        out ProblemHttpResult? problem)
    {
        invoiceId = Guid.Empty;
        bankTransactionId = Guid.Empty;
        problem = RejectUnexpectedContextQuery(httpRequest);
        if (problem is not null)
        {
            return false;
        }

        if (!TryReadContextGuid(httpRequest, "invoiceId", out invoiceId)
            || !TryReadContextGuid(httpRequest, "bankTransactionId", out bankTransactionId))
        {
            problem = Problem(
                ReconciliationApplicationErrors.AllocationContextInvalidRequest,
                StatusCodes.Status400BadRequest,
                "Allocation context request is invalid.");
            return false;
        }

        return true;
    }

    private static ProblemHttpResult? RejectUnexpectedContextQuery(HttpRequest httpRequest)
    {
        foreach (string key in httpRequest.Query.Keys)
        {
            if (!ContextQueryNames.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return Problem(
                    ReconciliationApplicationErrors.AllocationContextInvalidRequest,
                    StatusCodes.Status400BadRequest,
                    "Allocation context request is invalid.");
            }

            if (httpRequest.Query[key].Count != 1)
            {
                return Problem(
                    ReconciliationApplicationErrors.AllocationContextInvalidRequest,
                    StatusCodes.Status400BadRequest,
                    "Allocation context request is invalid.");
            }
        }

        if (!httpRequest.Query.ContainsKey("invoiceId") || !httpRequest.Query.ContainsKey("bankTransactionId"))
        {
            return Problem(
                ReconciliationApplicationErrors.AllocationContextInvalidRequest,
                StatusCodes.Status400BadRequest,
                "Allocation context request is invalid.");
        }

        return null;
    }

    private static bool TryReadContextGuid(HttpRequest httpRequest, string name, out Guid value)
    {
        string? text = httpRequest.Query[name].ToString();
        return Guid.TryParse(text, out value) && value != Guid.Empty;
    }

    private static Task DocumentContextOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        operation.Parameters ??= [];
        operation.Parameters.Add(ContextQueryParameter("invoiceId", "Invoice id. An empty GUID is rejected."));
        operation.Parameters.Add(ContextQueryParameter(
            "bankTransactionId",
            "Bank transaction id. An empty GUID is rejected."));
        return Task.CompletedTask;
    }

    private static OpenApiParameter ContextQueryParameter(string name, string description)
    {
        return new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = true,
            Description = description,
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            },
        };
    }

    private static bool TryBindHistoryQuery(
        HttpRequest httpRequest,
        out int page,
        out int pageSize,
        out Guid? invoiceId,
        out Guid? bankTransactionId,
        out ProblemHttpResult? problem)
    {
        page = AllocationHistoryListPagination.DefaultPage;
        pageSize = AllocationHistoryListPagination.DefaultPageSize;
        invoiceId = null;
        bankTransactionId = null;
        problem = RejectUnexpectedHistoryQuery(httpRequest);
        if (problem is not null)
        {
            return false;
        }

        if (!TryReadHistoryPage(httpRequest, "page", AllocationHistoryListPagination.DefaultPage, out page, out problem)
            || !TryReadHistoryPage(
                httpRequest,
                "pageSize",
                AllocationHistoryListPagination.DefaultPageSize,
                out pageSize,
                out problem)
            || !TryReadHistoryGuid(httpRequest, "invoiceId", out invoiceId, out problem)
            || !TryReadHistoryGuid(httpRequest, "bankTransactionId", out bankTransactionId, out problem))
        {
            return false;
        }

        if (!AllocationHistoryListPagination.IsValid(page, pageSize))
        {
            problem = Problem(
                ReconciliationApplicationErrors.AllocationHistoryInvalidPagination,
                StatusCodes.Status400BadRequest,
                "Allocation history page is invalid.");
            return false;
        }

        return true;
    }

    private static ProblemHttpResult? RejectUnexpectedHistoryQuery(HttpRequest httpRequest)
    {
        foreach (string key in httpRequest.Query.Keys)
        {
            if (!HistoryQueryNames.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return Problem(
                    ReconciliationApplicationErrors.AllocationHistoryInvalidRequest,
                    StatusCodes.Status400BadRequest,
                    "Allocation history request is invalid.");
            }

            if (httpRequest.Query[key].Count != 1)
            {
                Error error = IsHistoryPaginationName(key)
                    ? ReconciliationApplicationErrors.AllocationHistoryInvalidPagination
                    : ReconciliationApplicationErrors.AllocationHistoryInvalidRequest;
                string title = IsHistoryPaginationName(key)
                    ? "Allocation history page is invalid."
                    : "Allocation history request is invalid.";
                return Problem(error, StatusCodes.Status400BadRequest, title);
            }
        }

        return null;
    }

    private static bool IsHistoryPaginationName(string key)
    {
        return string.Equals(key, "page", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "pageSize", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadHistoryPage(
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
            problem = Problem(
                ReconciliationApplicationErrors.AllocationHistoryInvalidPagination,
                StatusCodes.Status400BadRequest,
                "Allocation history page is invalid.");
            return false;
        }

        return true;
    }

    private static bool TryReadHistoryGuid(
        HttpRequest httpRequest,
        string name,
        out Guid? value,
        out ProblemHttpResult? problem)
    {
        value = null;
        problem = null;
        if (!httpRequest.Query.ContainsKey(name))
        {
            return true;
        }

        StringValues values = httpRequest.Query[name];
        if (values.Count != 1
            || !Guid.TryParse(values[0], out Guid parsed)
            || parsed == Guid.Empty)
        {
            problem = Problem(
                ReconciliationApplicationErrors.AllocationHistoryInvalidRequest,
                StatusCodes.Status400BadRequest,
                "Allocation history request is invalid.");
            return false;
        }

        value = parsed;
        return true;
    }

    private static Task DocumentHistoryOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "page",
            In = ParameterLocation.Query,
            Required = false,
            Description = "Page number. Defaults to 1. The zero-based offset must fit in a 32-bit integer.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(AllocationHistoryListPagination.DefaultPage),
                Minimum = "1",
            },
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "pageSize",
            In = ParameterLocation.Query,
            Required = false,
            Description = "Page size. Defaults to 50. Maximum is 100.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(AllocationHistoryListPagination.DefaultPageSize),
                Minimum = "1",
                Maximum = "100",
            },
        });
        operation.Parameters.Add(HistoryGuidParameter(
            "invoiceId",
            "Optional invoice id. Omitted means no invoice filter. An empty GUID is rejected."));
        operation.Parameters.Add(HistoryGuidParameter(
            "bankTransactionId",
            "Optional bank transaction id. Omitted means no bank-transaction filter. An empty GUID is rejected."));
        return Task.CompletedTask;
    }

    private static OpenApiParameter HistoryGuidParameter(string name, string description)
    {
        return new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = false,
            Description = description,
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            },
        };
    }

    private static bool TryReadAllocationId(
        string allocationId,
        out Guid parsedAllocationId,
        out ProblemHttpResult? problem)
    {
        if (!Guid.TryParse(allocationId, out parsedAllocationId) || parsedAllocationId == Guid.Empty)
        {
            problem = Problem(
                ReconciliationApplicationErrors.AllocationAuditInvalidRequest,
                StatusCodes.Status400BadRequest,
                "Allocation audit request is invalid.");
            return false;
        }

        problem = null;
        return true;
    }

    private static bool TryBindAuditQuery(
        HttpRequest httpRequest,
        out int page,
        out int pageSize,
        out ProblemHttpResult? problem)
    {
        page = FinancialAuditListPagination.DefaultPage;
        pageSize = FinancialAuditListPagination.DefaultPageSize;
        problem = RejectUnexpectedAuditQuery(httpRequest);
        if (problem is not null)
        {
            return false;
        }

        if (!TryReadAuditPage(
                httpRequest,
                "page",
                FinancialAuditListPagination.DefaultPage,
                out page,
                out problem)
            || !TryReadAuditPage(
                httpRequest,
                "pageSize",
                FinancialAuditListPagination.DefaultPageSize,
                out pageSize,
                out problem))
        {
            return false;
        }

        return true;
    }

    private static ProblemHttpResult? RejectUnexpectedAuditQuery(HttpRequest httpRequest)
    {
        foreach (string key in httpRequest.Query.Keys)
        {
            if (!AuditQueryNames.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return Problem(
                    ReconciliationApplicationErrors.AllocationAuditInvalidRequest,
                    StatusCodes.Status400BadRequest,
                    "Allocation audit request is invalid.");
            }

            if (httpRequest.Query[key].Count != 1)
            {
                return Problem(
                    ReconciliationApplicationErrors.AllocationAuditInvalidPagination,
                    StatusCodes.Status400BadRequest,
                    "Allocation audit page is invalid.");
            }
        }

        return null;
    }

    private static bool TryReadAuditPage(
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
            problem = Problem(
                ReconciliationApplicationErrors.AllocationAuditInvalidPagination,
                StatusCodes.Status400BadRequest,
                "Allocation audit page is invalid.");
            return false;
        }

        return true;
    }

    private static Task DocumentAuditOperation(
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
            if (string.Equals(parameter.Name, "allocationId", StringComparison.OrdinalIgnoreCase)
                && parameter.In == ParameterLocation.Path
                && parameter is OpenApiParameter pathParameter)
            {
                pathParameter.Description = "Allocation id. An empty or malformed GUID is rejected.";
                pathParameter.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "uuid",
                };
            }
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "page",
            In = ParameterLocation.Query,
            Required = false,
            Description = "Page number. Defaults to 1. The zero-based offset must fit in a 32-bit integer.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(FinancialAuditListPagination.DefaultPage),
                Minimum = "1",
            },
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "pageSize",
            In = ParameterLocation.Query,
            Required = false,
            Description = "Page size. Defaults to 50. Maximum is 100.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(FinancialAuditListPagination.DefaultPageSize),
                Minimum = "1",
                Maximum = "100",
            },
        });
        return Task.CompletedTask;
    }

    private static bool TryReadReversalContextAllocationId(
        string allocationId,
        out Guid parsedAllocationId,
        out ProblemHttpResult? problem)
    {
        if (!Guid.TryParse(allocationId, out parsedAllocationId) || parsedAllocationId == Guid.Empty)
        {
            problem = Problem(
                ReconciliationApplicationErrors.AllocationReversalContextInvalidRequest,
                StatusCodes.Status400BadRequest,
                "Reversal context request is invalid.");
            return false;
        }

        problem = null;
        return true;
    }

    private static Task DocumentReversalContextOperation(
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
            if (string.Equals(parameter.Name, "allocationId", StringComparison.OrdinalIgnoreCase)
                && parameter.In == ParameterLocation.Path
                && parameter is OpenApiParameter pathParameter)
            {
                pathParameter.Description = "Allocation id. An empty or malformed GUID is rejected.";
                pathParameter.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "uuid",
                };
            }
        }

        return Task.CompletedTask;
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