using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

internal interface IBatchExactMatchItemExecutor
{
    Task<BatchItemAttempt> ExecuteAsync(
        OrganizationId organizationId,
        Guid batchIdempotencyKey,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken);
}