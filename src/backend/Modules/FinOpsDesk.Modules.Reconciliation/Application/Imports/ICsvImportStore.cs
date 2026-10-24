using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Imports;

public interface ICsvImportStore
{
    Task<ImportBatch?> FindBatchByFileIdentityAsync(
        OrganizationId organizationId,
        ImportType importType,
        SourceSystem sourceSystem,
        FileHash fileHash,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, InvoiceImportFacts>> FindInvoicesByExternalIdsAsync(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, BankTransactionImportFacts>> FindBankTransactionsByExternalIdsAsync(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken);

    Task<ImportCommitResult> CommitAsync(
        ImportCommitWork work,
        CancellationToken cancellationToken);
}