using System.Globalization;
using System.Text.Json.Nodes;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi;

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
                + "Without pagination and cursor, the response is the offset page: items, page, pageSize, and totalCount as a 64-bit count from a separate statement. "
                + "pagination=cursor selects items, pageSize, nextCursor, and previousCursor. That read does not count the queue. "
                + "A missing cursor reads the first page. page together with pagination=cursor, or cursor without pagination=cursor, is rejected. "
                + "pageSize stays from 1 to 100 and defaults to 50. In cursor mode a non-numeric, empty, repeated, or out-of-range pageSize is invalid pagination. "
                + "The cursor is a position, so a later request may use another page size. "
                + "Ordering is RecordedAt ascending, then dispute id ascending, using SQL Server uniqueidentifier order. "
                + "The cursor is opaque version 1. It stores the boundary time, offset, and dispute id. It does not store the organization. "
                + "nextCursor and previousCursor are strings or JSON null. The opposite cursor is a position hint and can return an empty page.")
            .Produces<AllocationDisputeListResponse>(StatusCodes.Status200OK, "application/json")
            .Produces<AllocationDisputeQueueCursorResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .AddOpenApiOperationTransformer(DocumentQueueOperation)
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

    private static async Task<Results<Ok<AllocationDisputeListResponse>, Ok<AllocationDisputeQueueCursorResponse>, ProblemHttpResult>> QueueAsync(
        HttpRequest httpRequest,
        GetAllocationDisputeQueueHandler offsetHandler,
        GetAllocationDisputeQueueCursorHandler cursorHandler,
        CancellationToken cancellationToken)
    {
        if (!TryBind(
            httpRequest,
            out bool cursorMode,
            out AllocationDisputeQueue queue,
            out int page,
            out int pageSize,
            out AllocationDisputeQueueCursor? cursor,
            out ProblemHttpResult? problem))
        {
            return problem!;
        }

        if (!cursorMode)
        {
            Result<AllocationDisputeListResponse> offset = await offsetHandler.Handle(
                new GetAllocationDisputeQueueQuery(queue, page, pageSize),
                cancellationToken);
            return offset.IsFailure ? Problem(offset.Error) : TypedResults.Ok(offset.Value);
        }

        Result<AllocationDisputeQueueCursorResponse> result = await cursorHandler.Handle(
            new GetAllocationDisputeQueueCursorQuery(queue, pageSize, cursor),
            cancellationToken);
        return result.IsFailure ? Problem(result.Error) : TypedResults.Ok(result.Value);
    }

    private static bool TryBind(
        HttpRequest httpRequest,
        out bool cursorMode,
        out AllocationDisputeQueue queue,
        out int page,
        out int pageSize,
        out AllocationDisputeQueueCursor? cursor,
        out ProblemHttpResult? problem)
    {
        cursorMode = false;
        queue = default;
        page = AllocationDisputeListPagination.DefaultPage;
        pageSize = AllocationDisputeListPagination.DefaultPageSize;
        cursor = null;
        problem = null;
        foreach (string key in httpRequest.Query.Keys)
        {
            if (!IsKnownName(key))
            {
                problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
                return false;
            }
        }

        bool paginationPresent = HasName(httpRequest, "pagination");
        bool cursorPresent = HasName(httpRequest, "cursor");
        if (!paginationPresent && !cursorPresent)
        {
            return TryBindOffset(httpRequest, out queue, out page, out pageSize, out problem);
        }

        if (!paginationPresent
            || Count(httpRequest, "pagination") != 1
            || !string.Equals(Single(httpRequest, "pagination"), "cursor", StringComparison.Ordinal))
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
            return false;
        }

        cursorMode = true;
        return TryBindCursor(httpRequest, out queue, out pageSize, out cursor, out problem);
    }

    private static bool TryBindOffset(
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

        if (!TryInteger(httpRequest, "page", AllocationDisputeListPagination.DefaultPage, out page)
            || !TryInteger(httpRequest, "pageSize", AllocationDisputeListPagination.DefaultPageSize, out pageSize))
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

    private static bool TryBindCursor(
        HttpRequest httpRequest,
        out AllocationDisputeQueue queue,
        out int pageSize,
        out AllocationDisputeQueueCursor? cursor,
        out ProblemHttpResult? problem)
    {
        queue = default;
        pageSize = AllocationDisputeListPagination.DefaultPageSize;
        cursor = null;
        problem = null;
        if (HasName(httpRequest, "page")
            || Count(httpRequest, "queue") != 1
            || Count(httpRequest, "cursor") > 1)
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
            return false;
        }

        if (Count(httpRequest, "pageSize") > 1)
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidPagination);
            return false;
        }

        if (!TryQueue(Single(httpRequest, "queue"), out queue))
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
            return false;
        }

        if (!TryInteger(httpRequest, "pageSize", AllocationDisputeListPagination.DefaultPageSize, out pageSize))
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidPagination);
            return false;
        }

        if (!AllocationDisputeListPagination.IsValidPageSize(pageSize))
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidPagination);
            return false;
        }

        if (!HasName(httpRequest, "cursor"))
        {
            return true;
        }

        string text = Single(httpRequest, "cursor") ?? string.Empty;
        if (!AllocationDisputeQueueCursor.TryDecode(text, queue, out cursor))
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
            return false;
        }

        return true;
    }

    private static bool IsKnownName(string key)
    {
        return string.Equals(key, "queue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "page", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "pageSize", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "pagination", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "cursor", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasName(HttpRequest httpRequest, string name)
    {
        return Count(httpRequest, name) > 0;
    }

    private static int Count(HttpRequest httpRequest, string name)
    {
        foreach (string key in httpRequest.Query.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return httpRequest.Query[key].Count;
            }
        }

        return 0;
    }

    private static string? Single(HttpRequest httpRequest, string name)
    {
        foreach (string key in httpRequest.Query.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return httpRequest.Query[key][0];
            }
        }

        return null;
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

    private static bool TryInteger(HttpRequest httpRequest, string name, int fallback, out int value)
    {
        value = fallback;
        if (!HasName(httpRequest, name))
        {
            return true;
        }

        return int.TryParse(Single(httpRequest, name), NumberStyles.None, CultureInfo.InvariantCulture, out value);
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

    private static Task DocumentQueueOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        operation.Parameters ??= [];
        operation.Parameters.Add(QueryParameter(
            "queue",
            "needs-decision or needs-correction. The name may appear once.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = ["needs-decision", "needs-correction"],
            }));
        operation.Parameters.Add(QueryParameter(
            "page",
            "Offset mode only. Defaults to 1. The page may be greater than 100 when the zero-based offset fits in a 32-bit integer. pageSize, not page, is limited to 100. Rejected when pagination=cursor.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(AllocationDisputeListPagination.DefaultPage),
                Minimum = "1",
            }));
        operation.Parameters.Add(QueryParameter(
            "pageSize",
            "Page size from 1 to 100. Defaults to 50. In cursor mode a non-numeric, empty, repeated, or out-of-range pageSize is invalid pagination. Offset mode keeps a non-numeric pageSize as an invalid request. The cursor does not store this value.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(AllocationDisputeListPagination.DefaultPageSize),
                Minimum = "1",
                Maximum = "100",
            }));
        operation.Parameters.Add(QueryParameter(
            "pagination",
            "Pass cursor to select the keyset response. Omit it, and omit cursor, for the offset response.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = ["cursor"],
            }));
        operation.Parameters.Add(QueryParameter(
            "cursor",
            "Opaque version 1 position. At most 64 Base64Url characters. Requires pagination=cursor. A missing value reads the first page.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                MaxLength = AllocationDisputeQueueCursor.MaxTextLength,
            }));
        if (context.Document is not null)
        {
            AnnotateQueueSchemas(operation, context.Document);
        }

        return Task.CompletedTask;
    }

    private static void AnnotateQueueSchemas(OpenApiOperation operation, OpenApiDocument document)
    {
        AnnotateInteger(document, nameof(AllocationDisputeListResponse), "totalCount");
        AnnotateCursorSchema(document);
        if (operation.Responses is null
            || !operation.Responses.TryGetValue("200", out IOpenApiResponse? response)
            || response is not OpenApiResponse ok
            || ok.Content is null
            || !ok.Content.TryGetValue("application/json", out OpenApiMediaType? media))
        {
            return;
        }

        media.Schema = new OpenApiSchema
        {
            Description = "Offset page or cursor page. The active query selects one of the two schemas.",
            OneOf =
            [
                new OpenApiSchemaReference(nameof(AllocationDisputeListResponse), document),
                new OpenApiSchemaReference(nameof(AllocationDisputeQueueCursorResponse), document),
            ],
        };
    }

    private static void AnnotateInteger(OpenApiDocument document, string schemaName, string propertyName)
    {
        if (!TrySchema(document, schemaName, out OpenApiSchema? schema)
            || !TryProperty(schema, propertyName, out OpenApiSchema? property))
        {
            return;
        }

        property.Type = JsonSchemaType.Integer;
        property.Format = "int64";
        property.Minimum = "0";
    }

    private static void AnnotateCursorSchema(OpenApiDocument document)
    {
        if (!TrySchema(document, nameof(AllocationDisputeQueueCursorResponse), out OpenApiSchema? schema))
        {
            return;
        }

        schema.Required = new HashSet<string> { "items", "pageSize", "nextCursor", "previousCursor" };
        AnnotateNullableString(schema, "nextCursor");
        AnnotateNullableString(schema, "previousCursor");
    }

    private static void AnnotateNullableString(OpenApiSchema schema, string name)
    {
        if (!TryProperty(schema, name, out OpenApiSchema? property))
        {
            return;
        }

        property.Type = JsonSchemaType.String | JsonSchemaType.Null;
    }

    private static bool TrySchema(OpenApiDocument document, string name, out OpenApiSchema schema)
    {
        schema = null!;
        if (document.Components?.Schemas is null
            || !document.Components.Schemas.TryGetValue(name, out IOpenApiSchema? candidate)
            || candidate is not OpenApiSchema openApiSchema)
        {
            return false;
        }

        schema = openApiSchema;
        return true;
    }

    private static bool TryProperty(OpenApiSchema schema, string name, out OpenApiSchema property)
    {
        property = null!;
        if (schema.Properties is null
            || (!schema.Properties.TryGetValue(name, out IOpenApiSchema? candidate)
                && !schema.Properties.TryGetValue(char.ToUpperInvariant(name[0]) + name[1..], out candidate))
            || candidate is not OpenApiSchema openApiSchema)
        {
            return false;
        }

        property = openApiSchema;
        return true;
    }

    private static OpenApiParameter QueryParameter(string name, string description, OpenApiSchema schema)
    {
        return new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = name == "queue",
            Description = description,
            Schema = schema,
        };
    }
}