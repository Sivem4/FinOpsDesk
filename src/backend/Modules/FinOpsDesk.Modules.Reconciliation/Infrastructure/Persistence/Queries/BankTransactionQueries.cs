using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class BankTransactionQueries : IBankTransactionQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public BankTransactionQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<BankTransactionListRead> ListAsync(
        OrganizationId organizationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        // Count and page are separate statements. They are not one snapshot,
        // and this read does not lock either balance.
        int totalCount = await _dbContext.BankTransactions
            .AsNoTracking()
            .CountAsync(
                bankTransaction => bankTransaction.OrganizationId == organizationId,
                cancellationToken);

        long wideOffset = ((long)page - 1L) * pageSize;
        int offset = checked((int)wideOffset);

        // Currency is selected as text. Materializing BankTransaction would run
        // the currency converter before a controlled inconsistent-money result.
        // UnappliedAmount is ignored by EF and is not selected here.
        List<BankTransactionListColumns> items = await _dbContext.Database
            .SqlQuery<BankTransactionListColumns>(
                $"""
                SELECT
                    [Id] AS [Id],
                    [Reference] AS [Reference],
                    [BookingDate] AS [BookingDate],
                    [Amount] AS [Amount],
                    [Currency] AS [Currency],
                    [AllocatedAmount] AS [AllocatedAmount],
                    [AllocatedCurrency] AS [AllocatedCurrency],
                    [SourceSystem] AS [SourceSystem],
                    [ExternalId] AS [ExternalId]
                FROM [reconciliation].[BankTransactions]
                WHERE [OrganizationId] = {organizationId.Value}
                ORDER BY [Id]
                OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY
                """)
            .ToListAsync(cancellationToken);

        return new BankTransactionListRead(totalCount, items);
    }
}