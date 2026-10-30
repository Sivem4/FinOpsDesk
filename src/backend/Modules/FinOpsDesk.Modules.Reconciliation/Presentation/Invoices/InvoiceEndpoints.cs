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
        HttpRequest request,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        Result<InvoiceListResponse> result = await handler.Handle(
            new ListInvoicesQuery(
                page ?? InvoiceListPagination.DefaultPage,
                pageSize ?? InvoiceListPagination.DefaultPageSize,
                SuppliedQuery(request.Query, "invoiceNumber")),
            cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        if (result.Error.Code == ReconciliationApplicationErrors.InvalidInvoicePagination.Code)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid pagination.", result.Error);
        }

        if (result.Error.Code == ReconciliationApplicationErrors.InvalidInvoiceNumberLookup.Code)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid invoice number.", result.Error);
        }

        throw new InvalidOperationException(
            $"Unexpected invoice list failure '{result.Error.Code}'.");
    }

    private static string? SuppliedQuery(IQueryCollection query, string name)
    {
        return query.ContainsKey(name) ? query[name].ToString() : null;
    }

    private static ProblemHttpResult Problem(int statusCode, string title, Error error)
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