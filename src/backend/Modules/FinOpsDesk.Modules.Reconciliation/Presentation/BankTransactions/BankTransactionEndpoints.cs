using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.BankTransactions;

internal static class BankTransactionEndpoints
{
    public static RouteGroupBuilder MapBankTransactionEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/bank-transactions", ListBankTransactionsAsync);

        return group;
    }

    private static async Task<Results<Ok<BankTransactionListResponse>, ProblemHttpResult>> ListBankTransactionsAsync(
        ListBankTransactionsHandler handler,
        HttpRequest request,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        Result<BankTransactionListResponse> result = await handler.Handle(
            new ListBankTransactionsQuery(
                page ?? BankTransactionListPagination.DefaultPage,
                pageSize ?? BankTransactionListPagination.DefaultPageSize,
                SuppliedQuery(request.Query, "reference")),
            cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        if (result.Error.Code == ReconciliationApplicationErrors.InvalidBankTransactionPagination.Code)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid pagination.",
                result.Error);
        }

        if (result.Error.Code == ReconciliationApplicationErrors.InvalidBankTransactionReferenceLookup.Code)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid bank transaction reference.",
                result.Error);
        }

        if (result.Error.Code == ReconciliationApplicationErrors.BankTransactionListInconsistentMoney.Code)
        {
            return Problem(
                StatusCodes.Status500InternalServerError,
                "Stored financial amounts are inconsistent.",
                result.Error);
        }

        throw new InvalidOperationException(
            $"Unexpected bank transaction list failure '{result.Error.Code}'.");
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