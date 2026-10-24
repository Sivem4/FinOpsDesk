using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application.Imports;

public sealed record ImportCsvCommand(
    ImportType ImportType,
    string? SourceSystem,
    byte[] Content,
    string? FileName);

public sealed record ParsedCsvRow(
    int DataRowIndex,
    int SourceLineNumber,
    IReadOnlyDictionary<string, string> Fields);

public sealed record ParsedCsvFile(IReadOnlyList<ParsedCsvRow> Rows);

public sealed record InvoiceImportFacts(
    string ExternalId,
    string InvoiceNumber,
    Money TotalAmount);

public sealed record BankTransactionImportFacts(
    string ExternalId,
    Money Amount,
    DateOnly BookingDate,
    string? Reference);

public sealed record PreparedImportRow<TFacts>(
    int DataRowIndex,
    int SourceLineNumber,
    string? ExternalId,
    TFacts? Facts,
    Error? Error,
    string? ColumnName)
    where TFacts : class;

public enum ClassifiedImportKind
{
    Imported = 1,
    AlreadyExists = 2,
    Rejected = 3
}

public sealed record ClassifiedImportRow<TFacts>(
    int DataRowIndex,
    int SourceLineNumber,
    ClassifiedImportKind Kind,
    TFacts? Facts,
    Error? Error,
    string? ColumnName)
    where TFacts : class;

public sealed class ImportCommitWork
{
    public ImportCommitWork(
        ImportBatch batch,
        IReadOnlyList<Invoice> invoices,
        IReadOnlyList<BankTransaction> bankTransactions)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(bankTransactions);

        Batch = batch;
        Invoices = invoices;
        BankTransactions = bankTransactions;
    }

    public ImportBatch Batch { get; }

    public IReadOnlyList<Invoice> Invoices { get; }

    public IReadOnlyList<BankTransaction> BankTransactions { get; }
}

public enum ImportCommitStatus
{
    Committed = 1,
    UniqueConflict = 2,
    Deadlock = 3
}

public sealed record ImportCommitResult(ImportCommitStatus Status);

public sealed record ImportRowErrorResponse(
    int DataRowIndex,
    int? SourceLineNumber,
    string ErrorCode,
    string Message,
    string? ColumnName);

public sealed record ImportCsvResponse(
    Guid ImportBatchId,
    string ImportType,
    string SourceSystem,
    string FileHash,
    string FileName,
    string Status,
    int TotalRows,
    int ImportedRows,
    int AlreadyExistsRows,
    int RejectedRows,
    DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt,
    bool Replayed,
    IReadOnlyList<ImportRowErrorResponse> RowErrors);