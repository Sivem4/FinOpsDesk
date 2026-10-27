using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

public interface IOpenReviewBlockQueries
{
    Task<IReadOnlyList<ReviewBlockSubject>> ListOpenAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewBlockSubject>> ListOpenForPairAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        CancellationToken cancellationToken);
}