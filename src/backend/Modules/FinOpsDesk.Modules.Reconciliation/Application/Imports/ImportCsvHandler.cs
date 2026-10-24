using System.Security.Cryptography;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Extensions.Logging;

namespace FinOpsDesk.Modules.Reconciliation.Application.Imports;

public sealed class ImportCsvHandler
{
    public const int MaxCommitAttempts = 3;

    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly ICsvImportStore _importStore;
    private readonly IImportFileReader _fileReader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImportCsvHandler> _logger;

    public ImportCsvHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        ICsvImportStore importStore,
        IImportFileReader fileReader,
        TimeProvider timeProvider,
        ILogger<ImportCsvHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(importStore);
        ArgumentNullException.ThrowIfNull(fileReader);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _currentOrganizationContext = currentOrganizationContext;
        _importStore = importStore;
        _fileReader = fileReader;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<Result<ImportCsvResponse>> Handle(
        ImportCsvCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Enum.IsDefined(command.ImportType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "Import type is not defined.");
        }

        SourceSystem sourceSystem;

        try
        {
            sourceSystem = new SourceSystem(command.SourceSystem ?? string.Empty);
        }
        catch (ArgumentException)
        {
            return Result<ImportCsvResponse>.Failure(
                ReconciliationApplicationErrors.ImportInvalidSourceSystem);
        }

        if (command.Content.Length == 0)
        {
            return Result<ImportCsvResponse>.Failure(
                ReconciliationApplicationErrors.ImportInvalidFile);
        }

        if (command.Content.Length > ImportLimits.MaxFileBytes)
        {
            return Result<ImportCsvResponse>.Failure(
                ReconciliationApplicationErrors.ImportFileTooLarge);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        FileHash fileHash = Hash(command.Content);
        string fileName = ImportFileNames.Sanitize(command.FileName);

        ImportBatch? stored = await _importStore.FindBatchByFileIdentityAsync(
            organizationId,
            command.ImportType,
            sourceSystem,
            fileHash,
            cancellationToken);

        if (stored is not null)
        {
            return Result<ImportCsvResponse>.Success(Map(stored, replayed: true));
        }

        Result<ParsedCsvFile> parsed = _fileReader.Read(
            command.ImportType,
            command.Content,
            cancellationToken);

        if (parsed.IsFailure)
        {
            return Result<ImportCsvResponse>.Failure(parsed.Error);
        }

        for (int attempt = 1; attempt <= MaxCommitAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ImportCommitWork work = command.ImportType == ImportType.Invoice
                ? await BuildInvoiceWorkAsync(
                    organizationId,
                    sourceSystem,
                    fileHash,
                    fileName,
                    parsed.Value,
                    cancellationToken)
                : await BuildBankTransactionWorkAsync(
                    organizationId,
                    sourceSystem,
                    fileHash,
                    fileName,
                    parsed.Value,
                    cancellationToken);

            ImportCommitResult commit = await _importStore.CommitAsync(work, cancellationToken);

            if (commit.Status == ImportCommitStatus.Committed)
            {
                _logger.LogInformation(
                    "Committed import batch {ImportBatchId} with status {Status}.",
                    work.Batch.Id.Value,
                    work.Batch.Status);

                return Result<ImportCsvResponse>.Success(Map(work.Batch, replayed: false));
            }

            stored = await _importStore.FindBatchByFileIdentityAsync(
                organizationId,
                command.ImportType,
                sourceSystem,
                fileHash,
                cancellationToken);

            if (stored is not null)
            {
                return Result<ImportCsvResponse>.Success(Map(stored, replayed: true));
            }

            if (attempt == MaxCommitAttempts || commit.Status != ImportCommitStatus.UniqueConflict
                && commit.Status != ImportCommitStatus.Deadlock)
            {
                if (commit.Status == ImportCommitStatus.UniqueConflict)
                {
                    return Result<ImportCsvResponse>.Failure(
                        ReconciliationApplicationErrors.ImportConflictUnresolved);
                }

                throw new InvalidOperationException(
                    "Import commit failed for a reason other than a unique key conflict.");
            }

            _logger.LogWarning(
                "Retrying import commit attempt {Attempt} after status {CommitStatus}.",
                attempt,
                commit.Status);
        }

        return Result<ImportCsvResponse>.Failure(
            ReconciliationApplicationErrors.ImportConflictUnresolved);
    }

    private async Task<ImportCommitWork> BuildInvoiceWorkAsync(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        FileHash fileHash,
        string fileName,
        ParsedCsvFile file,
        CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedImportRow<InvoiceImportFacts>>(file.Rows.Count);

        foreach (ParsedCsvRow row in file.Rows)
        {
            prepared.Add(InvoiceImportRowMapper.Map(row));
        }

        string[] externalIds = prepared
            .Where(row => row.ExternalId is not null)
            .Select(row => row.ExternalId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyDictionary<string, InvoiceImportFacts> existing =
            await _importStore.FindInvoicesByExternalIdsAsync(
                organizationId,
                sourceSystem,
                externalIds,
                cancellationToken);
        IReadOnlyList<ClassifiedImportRow<InvoiceImportFacts>> classified =
            ImportClassifier.ClassifyInvoices(
                organizationId,
                sourceSystem,
                prepared,
                existing);
        ImportBatch batch = StartBatch(
            organizationId,
            ImportType.Invoice,
            sourceSystem,
            fileHash,
            fileName,
            classified);
        var invoices = new List<Invoice>();

        foreach (ClassifiedImportRow<InvoiceImportFacts> row in classified)
        {
            if (row.Kind == ClassifiedImportKind.Imported)
            {
                invoices.Add(ImportClassifier.CreateInvoice(
                    organizationId,
                    sourceSystem,
                    row.Facts!));
            }
        }

        return new ImportCommitWork(batch, invoices, []);
    }

    private async Task<ImportCommitWork> BuildBankTransactionWorkAsync(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        FileHash fileHash,
        string fileName,
        ParsedCsvFile file,
        CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedImportRow<BankTransactionImportFacts>>(file.Rows.Count);

        foreach (ParsedCsvRow row in file.Rows)
        {
            prepared.Add(BankTransactionImportRowMapper.Map(row));
        }

        string[] externalIds = prepared
            .Where(row => row.ExternalId is not null)
            .Select(row => row.ExternalId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyDictionary<string, BankTransactionImportFacts> existing =
            await _importStore.FindBankTransactionsByExternalIdsAsync(
                organizationId,
                sourceSystem,
                externalIds,
                cancellationToken);
        IReadOnlyList<ClassifiedImportRow<BankTransactionImportFacts>> classified =
            ImportClassifier.ClassifyBankTransactions(
                organizationId,
                sourceSystem,
                prepared,
                existing);
        ImportBatch batch = StartBatch(
            organizationId,
            ImportType.BankTransaction,
            sourceSystem,
            fileHash,
            fileName,
            classified);
        var transactions = new List<BankTransaction>();

        foreach (ClassifiedImportRow<BankTransactionImportFacts> row in classified)
        {
            if (row.Kind == ClassifiedImportKind.Imported)
            {
                transactions.Add(ImportClassifier.CreateBankTransaction(
                    organizationId,
                    sourceSystem,
                    row.Facts!));
            }
        }

        return new ImportCommitWork(batch, [], transactions);
    }

    private ImportBatch StartBatch<TFacts>(
        OrganizationId organizationId,
        ImportType importType,
        SourceSystem sourceSystem,
        FileHash fileHash,
        string fileName,
        IReadOnlyList<ClassifiedImportRow<TFacts>> classified)
        where TFacts : class
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        ImportBatch batch = ImportBatch.Start(
            ImportBatchId.New(),
            organizationId,
            importType,
            sourceSystem,
            fileHash,
            fileName,
            classified.Count,
            now);

        foreach (ClassifiedImportRow<TFacts> row in classified)
        {
            Result result = row.Kind switch
            {
                ClassifiedImportKind.Imported => batch.RecordImported(row.DataRowIndex),
                ClassifiedImportKind.AlreadyExists => batch.RecordAlreadyExists(row.DataRowIndex),
                ClassifiedImportKind.Rejected => batch.RecordRejected(new ImportRowError(
                    row.DataRowIndex,
                    row.Error!,
                    row.SourceLineNumber,
                    row.ColumnName)),
                _ => throw new InvalidOperationException("Import row kind is not defined.")
            };

            if (result.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Import row {row.DataRowIndex} could not be classified ({result.Error.Code}).");
            }
        }

        Result completion = batch.Complete(now);

        if (completion.IsFailure)
        {
            throw new InvalidOperationException(
                $"Import batch could not be completed ({completion.Error.Code}).");
        }

        return batch;
    }

    private static FileHash Hash(byte[] content)
    {
        string hex = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        return new FileHash(hex);
    }

    private static ImportCsvResponse Map(ImportBatch batch, bool replayed)
    {
        if (batch.CompletedAt is null)
        {
            throw new InvalidOperationException("A stored import batch is missing its completion time.");
        }

        var errors = new List<ImportRowErrorResponse>(batch.RowErrors.Count);

        foreach (ImportRowError error in batch.RowErrors.OrderBy(error => error.DataRowIndex))
        {
            errors.Add(new ImportRowErrorResponse(
                error.DataRowIndex,
                error.SourceLineNumber,
                error.ErrorCode,
                error.Message,
                error.ColumnName));
        }

        return new ImportCsvResponse(
            batch.Id.Value,
            batch.ImportType.ToString(),
            batch.SourceSystem.Value,
            batch.FileHash.Value,
            batch.FileName,
            batch.Status.ToString(),
            batch.TotalRows,
            batch.ImportedRows,
            batch.AlreadyExistsRows,
            batch.RejectedRows,
            batch.CreatedAt,
            batch.CompletedAt.Value,
            replayed,
            errors);
    }
}