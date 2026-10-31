using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class AllocationReversalContextQueries : IAllocationReversalContextQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public AllocationReversalContextQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<AllocationReversalContextResponse?> FindAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationId);

        var row = await (
            from allocation in _dbContext.Allocations.AsNoTracking()
            where allocation.OrganizationId == organizationId && allocation.Id == allocationId
            join invoice in _dbContext.Invoices.AsNoTracking()
                on new { allocation.OrganizationId, allocation.InvoiceId }
                equals new { invoice.OrganizationId, InvoiceId = invoice.Id }
            join bankTransaction in _dbContext.BankTransactions.AsNoTracking()
                on new { allocation.OrganizationId, allocation.BankTransactionId }
                equals new { bankTransaction.OrganizationId, BankTransactionId = bankTransaction.Id }
            select new
            {
                allocation.Id,
                allocation.InvoiceId,
                allocation.BankTransactionId,
                invoice.InvoiceNumber,
                bankTransaction.Reference,
                Amount = allocation.Amount.Amount,
                Currency = allocation.Amount.Currency.Value,
                allocation.Status,
                allocation.ConfirmedAt,
                allocation.ReversedAt,
                allocation.ReversalReason,
            }).SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        return new AllocationReversalContextResponse(
            row.Id.Value,
            row.InvoiceId.Value,
            row.BankTransactionId.Value,
            row.InvoiceNumber,
            row.Reference,
            new MoneyDto(row.Amount, row.Currency),
            StatusName(row.Status),
            row.ConfirmedAt,
            row.ReversedAt,
            row.ReversalReason);
    }

    private static string StatusName(AllocationStatus status)
    {
        return status switch
        {
            AllocationStatus.Confirmed => "confirmed",
            AllocationStatus.Reversed => "reversed",
            _ => throw new InvalidOperationException(
                "Allocation status has no reversal-context response name."),
        };
    }
}