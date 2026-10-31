using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class AllocationHistoryQueries : IAllocationHistoryQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public AllocationHistoryQueries(ReconciliationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AllocationHistoryListResponse> ListAsync(
        OrganizationId organizationId,
        int page,
        int pageSize,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId,
        CancellationToken cancellationToken)
    {
        IQueryable<Allocation> allocations = _dbContext.Allocations
            .AsNoTracking()
            .Where(allocation => allocation.OrganizationId == organizationId);
        if (invoiceId is not null)
        {
            allocations = allocations.Where(allocation => allocation.InvoiceId == invoiceId);
        }

        if (bankTransactionId is not null)
        {
            allocations = allocations.Where(allocation => allocation.BankTransactionId == bankTransactionId);
        }

        var rows =
            from allocation in allocations
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
                allocation.IdempotencyOperation,
            };

        // Count and page share this predicate. They are two statements, not one snapshot.
        int totalCount = await rows.CountAsync(cancellationToken);
        long offset = ((long)page - 1L) * pageSize;
        var pageRows = await rows
            .OrderByDescending(row => row.ConfirmedAt)
            .ThenByDescending(row => row.Id)
            .Skip(checked((int)offset))
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        AllocationHistoryItemResponse[] items = new AllocationHistoryItemResponse[pageRows.Count];
        for (int index = 0; index < pageRows.Count; index++)
        {
            var row = pageRows[index];
            items[index] = new AllocationHistoryItemResponse(
                row.Id.Value,
                row.InvoiceId.Value,
                row.BankTransactionId.Value,
                row.InvoiceNumber,
                row.Reference,
                new MoneyDto(row.Amount, row.Currency),
                StatusName(row.Status),
                row.ConfirmedAt,
                row.ReversedAt,
                row.ReversalReason,
                OperationName(row.IdempotencyOperation));
        }

        return new AllocationHistoryListResponse(items, page, pageSize, totalCount);
    }

    private static string StatusName(AllocationStatus status)
    {
        return status switch
        {
            AllocationStatus.Confirmed => "confirmed",
            AllocationStatus.Reversed => "reversed",
            _ => throw new InvalidOperationException(
                "Allocation status has no history response name."),
        };
    }

    private static string? OperationName(AllocationIdempotencyOperation? operation)
    {
        if (operation is null)
        {
            return null;
        }

        return operation.Value switch
        {
            AllocationIdempotencyOperation.ExactMatch => "exactMatch",
            AllocationIdempotencyOperation.ExplicitAmount => "explicitAmount",
            AllocationIdempotencyOperation.BatchExactMatch => "batchExactMatch",
            _ => throw new InvalidOperationException(
                "Allocation idempotency operation has no history response name."),
        };
    }
}