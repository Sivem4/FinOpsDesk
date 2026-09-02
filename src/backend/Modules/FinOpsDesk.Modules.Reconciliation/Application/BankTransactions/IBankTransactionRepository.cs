using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;

public interface IBankTransactionRepository
{
    Task<BankTransaction?> GetByIdAsync(
        OrganizationId organizationId,
        BankTransactionId bankTransactionId,
        CancellationToken cancellationToken);
}