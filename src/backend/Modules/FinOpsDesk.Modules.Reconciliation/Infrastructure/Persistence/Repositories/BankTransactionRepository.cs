using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class BankTransactionRepository : IBankTransactionRepository
{
    private readonly ReconciliationDbContext _dbContext;

    public BankTransactionRepository(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public Task<BankTransaction?> GetByIdAsync(
        OrganizationId organizationId,
        BankTransactionId bankTransactionId,
        CancellationToken cancellationToken)
    {
        return _dbContext.BankTransactions.SingleOrDefaultAsync(
            bankTransaction => bankTransaction.OrganizationId == organizationId
                && bankTransaction.Id == bankTransactionId,
            cancellationToken);
    }
}