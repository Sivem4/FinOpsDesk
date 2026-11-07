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

internal static class AllocationDisputeEndpoints
{
    private static readonly string[] ListQueryNames = ["page", "pageSize", "allocationId"];

    public static RouteGroupBuilder MapAllocationDisputeEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/allocation-disputes", ListAsync)
            .WithName("ListAllocationDisputes")
            .WithSummary("List stored allocation dispute registrations for the authenticated organization.")
            .WithDescription(
                "Requires the TenantMember policy. The effective OrganizationId comes from the host organization context. "
                + "A client-supplied organization id is not the tenant. A missing actor id is allowed on this read. "
                + "The response is the stored registration: disputeId, allocationId, statement, actorSource, actorId, and recordedAt. "
                + "It does not include the organization id, the idempotency key, the current allocation state, or a resolution. "
                + "After reversal the stored registration remains. A join to the allocation is possible and is not used, because this read shows the registration rather than later financial state. "
                + "Optional allocationId filters stored rows and does not check that the allocation exists. A foreign or unknown allocation id returns an empty page. "
                + "page, pageSize, and allocationId may each appear once. An unknown query name is rejected. "
                + "Page defaults to 1 and page size defaults to 50, with a maximum of 100. The zero-based offset is calculated as a 64-bit value and must fit in a 32-bit integer. "
                + "Ordering is RecordedAt ascending, then dispute id ascending. totalCount is a 64-bit count. "
                + "The count and the page are separate statements, so a dispute committed between them can make totalCount differ from items. "
                + "The read does not open a transaction and does not change rows.")
            .Produces<AllocationDisputeListResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .AddOpenApiOperationTransformer(DocumentListOperation)
            .WithMetadata(FinancialEndpointClassification.Read("allocation-disputes.read"));

        group.MapGet("/allocation-disputes/{disputeId}", GetAsync)
            .WithName("GetAllocationDispute")
            .WithSummary("Read one stored allocation dispute registration.")
            .WithDescription(
                "Requires the TenantMember policy. The effective OrganizationId comes from the host organization context. "
                + "A client-supplied organization id is not the tenant. A missing actor id is allowed on this read. "
                + "Any query parameter is rejected. A malformed dispute id is rejected. "
                + "An unknown dispute and a dispute owned by another organization both return the same not-found result. "
                + "The response is the stored registration: disputeId, allocationId, statement, actorSource, actorId, and recordedAt. "
                + "It does not include the organization id, the idempotency key, the current allocation state, or a resolution. "
                + "After reversal the stored registration remains. "
                + "The read does not open a transaction and does not change rows.")
            .Produces<AllocationDisputeResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AddOpenApiOperationTransformer(DocumentDetailOperation)
            .WithMetadata(FinancialEndpointClassification.Read("allocation-disputes.read"));

        return group;
    }

    private static async Task<Results<Ok<AllocationDisputeListResponse>, ProblemHttpResult>> ListAsync(
        HttpRequest httpRequest,
        GetAllocationDisputesHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryBindListQuery(httpRequest, out int page, out int pageSize, out Guid? allocationId, out ProblemHttpResult? problem))
        {
            return problem!;
        }

        Result<AllocationDisputeListResponse> result = await handler.Handle(
            new GetAllocationDisputesQuery(page, pageSize, allocationId),
            cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<AllocationDisputeResponse>, ProblemHttpResult>> GetAsync(
        string disputeId,
        HttpRequest httpRequest,
        GetAllocationDisputeHandler handler,
        CancellationToken cancellationToken)
    {
        if (httpRequest.Query.Count > 0)
        {
            return Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
        }

        if (!Guid.TryParse(disputeId, out Guid parsedDisputeId) || parsedDisputeId == Guid.Empty)
        {
            return Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
        }

        Result<AllocationDisputeResponse> result = await handler.Handle(
            new GetAllocationDisputeQuery(parsedDisputeId),
            cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static bool TryBindListQuery(
        HttpRequest httpRequest,
        out int page,
        out int pageSize,
        out Guid? allocationId,
        out ProblemHttpResult? problem)
    {
        page = AllocationDisputeListPagination.DefaultPage;
        pageSize = AllocationDisputeListPagination.DefaultPageSize;
        allocationId = null;
        problem = RejectUnexpectedQuery(httpRequest);
        if (problem is not null)
        {
            return false;
        }

        if (!TryReadPage(httpRequest, "page", AllocationDisputeListPagination.DefaultPage, out page, out problem)
            || !TryReadPage(httpRequest, "pageSize", AllocationDisputeListPagination.DefaultPageSize, out pageSize, out problem)
            || !TryReadAllocationId(httpRequest, out allocationId, out problem))
        {
            return false;
        }

        if (!AllocationDisputeListPagination.IsValid(page, pageSize))
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidPagination);
            return false;
        }

        return true;
    }

    private static ProblemHttpResult? RejectUnexpectedQuery(HttpRequest httpRequest)
    {
        foreach (string key in httpRequest.Query.Keys)
        {
            if (!ListQueryNames.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
            }

            if (httpRequest.Query[key].Count != 1)
            {
                return Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
            }
        }

        return null;
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
        if (!TryGetSingle(httpRequest, name, out string? raw))
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out page))
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidPagination);
            return false;
        }

        return true;
    }

    private static bool TryReadAllocationId(
        HttpRequest httpRequest,
        out Guid? allocationId,
        out ProblemHttpResult? problem)
    {
        allocationId = null;
        problem = null;
        if (!TryGetSingle(httpRequest, "allocationId", out string? raw))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(raw)
            || !Guid.TryParse(raw, out Guid parsed)
            || parsed == Guid.Empty)
        {
            problem = Problem(ReconciliationApplicationErrors.AllocationDisputeInvalidRequest);
            return false;
        }

        allocationId = parsed;
        return true;
    }

    private static bool TryGetSingle(HttpRequest httpRequest, string name, out string? raw)
    {
        raw = null;
        foreach (string key in httpRequest.Query.Keys)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            StringValues values = httpRequest.Query[key];
            if (values.Count != 1)
            {
                return false;
            }

            raw = values[0];
            return true;
        }

        return false;
    }

    private static ProblemHttpResult Problem(Error error)
    {
        int statusCode = StatusCodes.Status500InternalServerError;
        string title = "Allocation dispute read failed";
        if (error.Code == ReconciliationApplicationErrors.AllocationDisputeInvalidRequest.Code)
        {
            statusCode = StatusCodes.Status400BadRequest;
            title = "Invalid allocation dispute request";
        }
        else if (error.Code == ReconciliationApplicationErrors.AllocationDisputeInvalidPagination.Code)
        {
            statusCode = StatusCodes.Status400BadRequest;
            title = "Invalid allocation dispute pagination";
        }
        else if (error.Code == ReconciliationApplicationErrors.AllocationDisputeNotFound.Code)
        {
            statusCode = StatusCodes.Status404NotFound;
            title = "Allocation dispute not found";
        }

        return TypedResults.Problem(
            detail: error.Message,
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = error.Code,
            });
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
            "page",
            "Page number. Defaults to 1. The zero-based offset must fit in a 32-bit integer. The name may appear once.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(AllocationDisputeListPagination.DefaultPage),
                Minimum = "1",
            }));
        operation.Parameters.Add(QueryParameter(
            "pageSize",
            "Page size. Defaults to 50. Maximum is 100. The name may appear once.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(AllocationDisputeListPagination.DefaultPageSize),
                Minimum = "1",
                Maximum = "100",
            }));
        operation.Parameters.Add(QueryParameter(
            "allocationId",
            "Optional allocation id. It filters stored disputes and does not check that the allocation exists. The name may appear once.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            }));
        AnnotateListSchema(context);
        return Task.CompletedTask;
    }

    private static Task DocumentDetailOperation(
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

    private static void AnnotateListSchema(OpenApiOperationTransformerContext context)
    {
        if (context.Document?.Components?.Schemas is null
            || !context.Document.Components.Schemas.TryGetValue(
                nameof(AllocationDisputeListResponse),
                out IOpenApiSchema? schema)
            || schema is not OpenApiSchema target)
        {
            return;
        }

        if (!TryGetProperty(target, "totalCount", out OpenApiSchema? totalCount))
        {
            return;
        }

        totalCount.Type = JsonSchemaType.Integer;
        totalCount.Format = "int64";
        totalCount.Minimum = "0";
        totalCount.Description =
            "64-bit count from a statement separate from the page. It can differ from items when a dispute is committed between the two reads.";
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
}