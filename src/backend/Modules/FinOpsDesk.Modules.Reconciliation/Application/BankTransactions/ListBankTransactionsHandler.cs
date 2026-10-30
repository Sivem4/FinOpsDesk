using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;

public sealed class ListBankTransactionsHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IBankTransactionQueries _bankTransactionQueries;

    public ListBankTransactionsHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IBankTransactionQueries bankTransactionQueries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(bankTransactionQueries);

        _currentOrganizationContext = currentOrganizationContext;
        _bankTransactionQueries = bankTransactionQueries;
    }

    public async Task<Result<BankTransactionListResponse>> Handle(
        ListBankTransactionsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!BankTransactionListPagination.IsValid(query.Page, query.PageSize))
        {
            return Result<BankTransactionListResponse>.Failure(
                ReconciliationApplicationErrors.InvalidBankTransactionPagination);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        BankTransactionListRead read = await _bankTransactionQueries.ListAsync(
            organizationId,
            query.Page,
            query.PageSize,
            cancellationToken);

        List<BankTransactionListItemDto> items = new(read.Items.Count);

        foreach (BankTransactionListColumns row in read.Items)
        {
            if (!AllocationContextAmounts.TryRead(
                    row.Amount,
                    row.Currency,
                    row.AllocatedAmount,
                    row.AllocatedCurrency,
                    out MoneyDto amount,
                    out MoneyDto allocated,
                    out MoneyDto unapplied))
            {
                return Result<BankTransactionListResponse>.Failure(
                    ReconciliationApplicationErrors.BankTransactionListInconsistentMoney);
            }

            items.Add(new BankTransactionListItemDto(
                row.Id,
                row.BookingDate,
                row.Reference,
                amount,
                allocated,
                unapplied,
                row.SourceSystem,
                row.ExternalId));
        }

        return Result<BankTransactionListResponse>.Success(
            new BankTransactionListResponse(items, query.Page, query.PageSize, read.TotalCount));
    }
}