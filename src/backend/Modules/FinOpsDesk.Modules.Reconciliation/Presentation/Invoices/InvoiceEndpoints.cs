using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.Invoices;

internal static class InvoiceEndpoints
{
    public static RouteGroupBuilder MapInvoiceEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/invoices", ListInvoicesAsync);

        return group;
    }

    private static async Task<Results<Ok<InvoiceListResponse>, ProblemHttpResult>> ListInvoicesAsync(
        ListInvoicesHandler handler,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        Result<InvoiceListResponse> result = await handler.Handle(
            new ListInvoicesQuery(
                page ?? InvoiceListPagination.DefaultPage,
                pageSize ?? InvoiceListPagination.DefaultPageSize),
            cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        if (result.Error.Code != ReconciliationApplicationErrors.InvalidInvoicePagination.Code)
        {
            throw new InvalidOperationException(
                $"Unexpected invoice list failure '{result.Error.Code}'.");
        }

        return TypedResults.Problem(
            detail: result.Error.Message,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid pagination.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = result.Error.Code,
            });
    }
}