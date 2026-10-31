using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public interface IAllocationHistoryQueries
{
    Task<AllocationHistoryListResponse> ListAsync(
        OrganizationId organizationId,
        int page,
        int pageSize,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId,
        CancellationToken cancellationToken);
}