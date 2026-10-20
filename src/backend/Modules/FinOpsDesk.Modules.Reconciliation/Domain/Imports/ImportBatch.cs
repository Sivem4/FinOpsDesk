using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Imports;

public sealed class ImportBatch
{
    public const int MaxFileNameLength = 260;

    private readonly List<ImportRowError> _rowErrors = new();
    private readonly IReadOnlyList<ImportRowError> _rowErrorsView;

    // One slot per logical data row. Not persisted.
    private readonly ImportRowOutcome?[] _classifiedRows;

    public ImportBatchId Id { get; }

    public OrganizationId OrganizationId { get; }

    public ImportType ImportType { get; }

    public SourceSystem SourceSystem { get; }

    public FileHash FileHash { get; }

    public string FileName { get; }

    public ImportBatchStatus Status { get; private set; }

    public int TotalRows { get; }

    public int ImportedRows { get; private set; }

    public int AlreadyExistsRows { get; private set; }

    public int RejectedRows { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public IReadOnlyList<ImportRowError> RowErrors => _rowErrorsView;

#pragma warning disable CS8618
    // Required for persistence materialization.
    private ImportBatch()
    {
        _classifiedRows = Array.Empty<ImportRowOutcome?>();
        _rowErrorsView = _rowErrors.AsReadOnly();
    }
#pragma warning restore CS8618

    private ImportBatch(
        ImportBatchId id,
        OrganizationId organizationId,
        ImportType importType,
        SourceSystem sourceSystem,
        FileHash fileHash,
        string fileName,
        int totalRows,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(fileHash);

        if (!Enum.IsDefined(importType))
        {
            throw new ArgumentException(
                "Import type is not defined.",
                nameof(importType));
        }

        if (totalRows < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalRows),
                "Import batch must contain at least one data row.");
        }

        if (createdAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                "Creation time cannot be the default value.");
        }

        Id = id;
        OrganizationId = organizationId;
        ImportType = importType;
        SourceSystem = sourceSystem;
        FileHash = fileHash;
        FileName = NormalizeFileName(fileName);
        TotalRows = totalRows;
        CreatedAt = createdAt;
        Status = ImportBatchStatus.Processing;
        _classifiedRows = new ImportRowOutcome?[totalRows];
        _rowErrorsView = _rowErrors.AsReadOnly();
    }

    public static ImportBatch Start(
        ImportBatchId id,
        OrganizationId organizationId,
        ImportType importType,
        SourceSystem sourceSystem,
        FileHash fileHash,
        string fileName,
        int totalRows,
        DateTimeOffset createdAt)
    {
        return new ImportBatch(
            id,
            organizationId,
            importType,
            sourceSystem,
            fileHash,
            fileName,
            totalRows,
            createdAt);
    }

    public Result RecordImported(int dataRowIndex)
    {
        return Classify(
            dataRowIndex,
            ImportRowOutcome.Imported,
            error: null);
    }

    public Result RecordAlreadyExists(int dataRowIndex)
    {
        return Classify(
            dataRowIndex,
            ImportRowOutcome.AlreadyExists,
            error: null);
    }

    public Result RecordRejected(ImportRowError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return Classify(
            error.DataRowIndex,
            ImportRowOutcome.Rejected,
            error);
    }

    public Result Complete(DateTimeOffset completedAt)
    {
        if (Status != ImportBatchStatus.Processing)
        {
            return Result.Failure(ReconciliationErrors.ImportBatchNotProcessing);
        }

        if (completedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                "Completion time cannot be the default value.");
        }

        if (completedAt < CreatedAt)
        {
            return Result.Failure(
                ReconciliationErrors.ImportCompletionPrecedesCreation);
        }

        if (!IsFullyClassified())
        {
            return Result.Failure(
                ReconciliationErrors.ImportIncompleteClassification);
        }

        Status = ResolveTerminalStatus();
        CompletedAt = completedAt;
        return Result.Success();
    }

    private Result Classify(
        int dataRowIndex,
        ImportRowOutcome outcome,
        ImportRowError? error)
    {
        if (Status != ImportBatchStatus.Processing)
        {
            return Result.Failure(ReconciliationErrors.ImportBatchNotProcessing);
        }

        if (dataRowIndex < 1 || dataRowIndex > TotalRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dataRowIndex),
                "Data row index must be between 1 and the total number of rows.");
        }

        int slotIndex = dataRowIndex - 1;

        if (_classifiedRows[slotIndex] is not null)
        {
            return Result.Failure(
                ReconciliationErrors.ImportDataRowAlreadyClassified);
        }

        switch (outcome)
        {
            case ImportRowOutcome.Imported:
                _classifiedRows[slotIndex] = outcome;
                ImportedRows++;
                break;
            case ImportRowOutcome.AlreadyExists:
                _classifiedRows[slotIndex] = outcome;
                AlreadyExistsRows++;
                break;
            case ImportRowOutcome.Rejected:
                ArgumentNullException.ThrowIfNull(error);
                _classifiedRows[slotIndex] = outcome;
                _rowErrors.Add(error);
                RejectedRows++;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(outcome),
                    "Import row outcome is not defined.");
        }

        return Result.Success();
    }

    private bool IsFullyClassified()
    {
        if (_classifiedRows.Length != TotalRows ||
            RejectedRows != _rowErrors.Count ||
            ImportedRows + AlreadyExistsRows + RejectedRows != TotalRows)
        {
            return false;
        }

        int imported = 0;
        int alreadyExists = 0;
        int rejected = 0;

        foreach (ImportRowOutcome? outcome in _classifiedRows)
        {
            switch (outcome)
            {
                case ImportRowOutcome.Imported:
                    imported++;
                    break;
                case ImportRowOutcome.AlreadyExists:
                    alreadyExists++;
                    break;
                case ImportRowOutcome.Rejected:
                    rejected++;
                    break;
                default:
                    return false;
            }
        }

        return imported == ImportedRows
            && alreadyExists == AlreadyExistsRows
            && rejected == RejectedRows;
    }

    private ImportBatchStatus ResolveTerminalStatus()
    {
        if (RejectedRows == 0)
        {
            return ImportBatchStatus.Completed;
        }

        if (ImportedRows + AlreadyExistsRows > 0)
        {
            return ImportBatchStatus.CompletedWithErrors;
        }

        return ImportBatchStatus.Rejected;
    }

    private static string NormalizeFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string normalized = fileName.Trim();

        if (normalized.Length > MaxFileNameLength)
        {
            throw new ArgumentException(
                $"File name cannot exceed {MaxFileNameLength} characters.",
                nameof(fileName));
        }

        if (normalized.Contains('\\') || normalized.Contains('/'))
        {
            throw new ArgumentException(
                "File name cannot contain a directory separator.",
                nameof(fileName));
        }

        return normalized;
    }
}