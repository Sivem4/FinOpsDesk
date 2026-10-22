using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

internal static class ImportPersistenceData
{
    public static readonly DateTimeOffset CreatedAt =
        new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset CompletedAt = CreatedAt.AddMinutes(5);

    public static SourceIdentity Identity(string sourceSystem, string externalId)
    {
        return new SourceIdentity(
            new SourceSystem(sourceSystem),
            new ExternalId(externalId));
    }

    public static FileHash Hash(char marker)
    {
        if (marker is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new ArgumentOutOfRangeException(
                nameof(marker),
                "File hash marker must be a lowercase hexadecimal character.");
        }

        return new FileHash(new string(marker, FileHash.HexLength));
    }

    public static Invoice ImportedInvoice(
        OrganizationId organizationId,
        SourceIdentity sourceIdentity,
        string invoiceNumber = "INV-001",
        decimal totalAmount = 100m)
    {
        return new Invoice(
            InvoiceId.New(),
            organizationId,
            invoiceNumber,
            new Money(totalAmount, PersistenceTestData.Pln),
            sourceIdentity);
    }

    public static BankTransaction ImportedBankTransaction(
        OrganizationId organizationId,
        SourceIdentity sourceIdentity,
        decimal amount = 100m,
        string? reference = "INV-001")
    {
        return new BankTransaction(
            BankTransactionId.New(),
            organizationId,
            new Money(amount, PersistenceTestData.Pln),
            new DateOnly(2026, 8, 24),
            reference,
            sourceIdentity);
    }

    public static ImportBatch CompletedBatch(
        OrganizationId organizationId,
        ImportType importType,
        string sourceSystem,
        FileHash fileHash,
        string fileName = "import.csv")
    {
        ImportBatch batch = Start(
            organizationId,
            importType,
            sourceSystem,
            fileHash,
            fileName,
            totalRows: 1);
        Succeed(batch.RecordImported(1));
        Succeed(batch.Complete(CompletedAt));
        return batch;
    }

    public static ImportBatch AlreadyExistsBatch(
        OrganizationId organizationId,
        FileHash fileHash)
    {
        ImportBatch batch = Start(
            organizationId,
            ImportType.Invoice,
            "MBANK-ACCT-1",
            fileHash,
            "already-exists.csv",
            totalRows: 2);
        Succeed(batch.RecordAlreadyExists(1));
        Succeed(batch.RecordAlreadyExists(2));
        Succeed(batch.Complete(CompletedAt));
        return batch;
    }

    public static ImportBatch CompletedWithErrorsBatch(
        OrganizationId organizationId,
        FileHash fileHash)
    {
        ImportBatch batch = Start(
            organizationId,
            ImportType.Invoice,
            "MBANK-ACCT-1",
            fileHash,
            "partial.csv",
            totalRows: 2);
        Succeed(batch.RecordImported(1));
        Succeed(batch.RecordRejected(RowError(2, sourceLineNumber: 4, columnName: " Amount ")));
        Succeed(batch.Complete(CompletedAt));
        return batch;
    }

    public static ImportBatch RejectedBatch(
        OrganizationId organizationId,
        FileHash fileHash)
    {
        ImportBatch batch = Start(
            organizationId,
            ImportType.BankTransaction,
            "MBANK-ACCT-1",
            fileHash,
            "rejected.csv",
            totalRows: 2);
        Succeed(batch.RecordRejected(RowError(1, sourceLineNumber: 8, columnName: "Reference")));
        Succeed(batch.RecordRejected(RowError(2)));
        Succeed(batch.Complete(CompletedAt));
        return batch;
    }

    public static ImportRowError RowError(
        int dataRowIndex,
        int? sourceLineNumber = null,
        string? columnName = null)
    {
        return new ImportRowError(
            dataRowIndex,
            ReconciliationErrors.ImportSourceConflict,
            sourceLineNumber,
            columnName);
    }

    private static ImportBatch Start(
        OrganizationId organizationId,
        ImportType importType,
        string sourceSystem,
        FileHash fileHash,
        string fileName,
        int totalRows)
    {
        return ImportBatch.Start(
            ImportBatchId.New(),
            organizationId,
            importType,
            new SourceSystem(sourceSystem),
            fileHash,
            fileName,
            totalRows,
            CreatedAt);
    }

    private static void Succeed(Result result)
    {
        Assert.True(result.IsSuccess, result.Error?.Code);
    }
}