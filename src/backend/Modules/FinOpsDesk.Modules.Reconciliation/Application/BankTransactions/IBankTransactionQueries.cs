using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;

public interface IBankTransactionQueries
{
    Task<BankTransactionListRead> ListAsync(
        OrganizationId organizationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        string? reference = null);
}