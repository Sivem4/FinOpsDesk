using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Imports;

public sealed class ImportBatchTests
{
    private const string Digest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt =
        CreatedAt.AddMinutes(5);

    [Fact]
    public void Start_should_create_a_processing_batch()
    {
        var id = new ImportBatchId(
            new Guid("22222222-2222-2222-2222-222222222222"));
        var sourceSystem = new SourceSystem(" erp ");
        var fileHash = new FileHash(Digest);

        ImportBatch batch = ImportBatch.Start(
            id,
            TestOrganizationId,
            ImportType.BankTransaction,
            sourceSystem,
            fileHash,
            "  transactions.csv  ",
            totalRows: 3,
            CreatedAt);

        Assert.Equal(id, batch.Id);
        Assert.Equal(TestOrganizationId, batch.OrganizationId);
        Assert.Equal(ImportType.BankTransaction, batch.ImportType);
        Assert.Equal(sourceSystem, batch.SourceSystem);
        Assert.Equal(fileHash, batch.FileHash);
        Assert.Equal("transactions.csv", batch.FileName);
        Assert.Equal(ImportBatchStatus.Processing, batch.Status);
        Assert.Equal(3, batch.TotalRows);
        Assert.Equal(0, batch.ImportedRows);
        Assert.Equal(0, batch.AlreadyExistsRows);
        Assert.Equal(0, batch.RejectedRows);
        Assert.Equal(CreatedAt, batch.CreatedAt);
        Assert.Null(batch.CompletedAt);
        Assert.Empty(batch.RowErrors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Start_should_reject_an_empty_data_file(int totalRows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StartBatch(totalRows));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("folder\\invoices.csv")]
    [InlineData("folder/invoices.csv")]
    public void Start_should_reject_an_invalid_file_name(string fileName)
    {
        Assert.Throws<ArgumentException>(
            () => ImportBatch.Start(
                ImportBatchId.New(),
                TestOrganizationId,
                ImportType.Invoice,
                new SourceSystem("ERP"),
                new FileHash(Digest),
                fileName,
                totalRows: 1,
                CreatedAt));
    }

    [Fact]
    public void Complete_should_accept_every_row_imported()
    {
        ImportBatch batch = StartBatch(3);

        Assert.True(batch.RecordImported(1).IsSuccess);
        Assert.True(batch.RecordImported(2).IsSuccess);
        Assert.True(batch.RecordImported(3).IsSuccess);

        Result result = batch.Complete(CompletedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(ImportBatchStatus.Completed, batch.Status);
        Assert.Equal(3, batch.ImportedRows);
        Assert.Equal(0, batch.AlreadyExistsRows);
        Assert.Equal(0, batch.RejectedRows);
        Assert.Equal(CompletedAt, batch.CompletedAt);
        Assert.Empty(batch.RowErrors);
    }

    [Fact]
    public void Complete_should_accept_every_row_already_existing()
    {
        ImportBatch batch = StartBatch(2);

        Assert.True(batch.RecordAlreadyExists(2).IsSuccess);
        Assert.True(batch.RecordAlreadyExists(1).IsSuccess);

        Result result = batch.Complete(CreatedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(ImportBatchStatus.Completed, batch.Status);
        Assert.Equal(0, batch.ImportedRows);
        Assert.Equal(2, batch.AlreadyExistsRows);
        Assert.Equal(0, batch.RejectedRows);
    }

    [Fact]
    public void Complete_should_accept_mixed_imported_and_already_existing_rows()
    {
        ImportBatch batch = StartBatch(2);

        batch.RecordImported(1);
        batch.RecordAlreadyExists(2);

        Result result = batch.Complete(CompletedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(ImportBatchStatus.Completed, batch.Status);
        Assert.Equal(1, batch.ImportedRows);
        Assert.Equal(1, batch.AlreadyExistsRows);
        Assert.Equal(0, batch.RejectedRows);
    }

    [Fact]
    public void Complete_should_record_partial_rejection()
    {
        ImportBatch batch = StartBatch(3);
        ImportRowError validationError = ValidationError(2, sourceLineNumber: 9);
        ImportRowError conflictError = ConflictError(3, sourceLineNumber: 40);

        batch.RecordImported(1);
        Assert.True(batch.RecordRejected(validationError).IsSuccess);
        Assert.True(batch.RecordRejected(conflictError).IsSuccess);

        Result result = batch.Complete(CompletedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(ImportBatchStatus.CompletedWithErrors, batch.Status);
        Assert.Equal(1, batch.ImportedRows);
        Assert.Equal(0, batch.AlreadyExistsRows);
        Assert.Equal(2, batch.RejectedRows);
        Assert.Equal(2, batch.RowErrors.Count);
        Assert.Equal(validationError, batch.RowErrors[0]);
        Assert.Equal(conflictError, batch.RowErrors[1]);
        Assert.Equal(9, batch.RowErrors[0].SourceLineNumber);
        Assert.Equal(40, batch.RowErrors[1].SourceLineNumber);
    }

    [Fact]
    public void Complete_should_reject_a_batch_with_no_accepted_rows()
    {
        ImportBatch batch = StartBatch(2);

        batch.RecordRejected(ConflictError(1, sourceLineNumber: null));
        batch.RecordRejected(ValidationError(2, sourceLineNumber: 4));

        Result result = batch.Complete(CompletedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(ImportBatchStatus.Rejected, batch.Status);
        Assert.Equal(0, batch.ImportedRows);
        Assert.Equal(0, batch.AlreadyExistsRows);
        Assert.Equal(2, batch.RejectedRows);
        Assert.Equal(2, batch.RowErrors.Count);
    }

    [Fact]
    public void Record_should_reject_a_duplicate_logical_row_index()
    {
        ImportBatch batch = StartBatch(2);

        Assert.True(batch.RecordImported(1).IsSuccess);

        Result result = batch.RecordImported(1);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.ImportDataRowAlreadyClassified.Code,
            result.Error?.Code);
        Assert.Equal(ImportBatchStatus.Processing, batch.Status);
        Assert.Equal(1, batch.ImportedRows);
        Assert.Equal(0, batch.AlreadyExistsRows);
        Assert.Equal(0, batch.RejectedRows);
        Assert.Empty(batch.RowErrors);

        Assert.True(batch.RecordAlreadyExists(2).IsSuccess);
        Assert.True(batch.Complete(CompletedAt).IsSuccess);
        Assert.Equal(1, batch.ImportedRows);
        Assert.Equal(1, batch.AlreadyExistsRows);
    }

    [Fact]
    public void Record_should_reject_a_different_outcome_for_the_same_row()
    {
        ImportBatch batch = StartBatch(1);
        ImportRowError conflict = ConflictError(1, sourceLineNumber: 2);

        Assert.True(batch.RecordAlreadyExists(1).IsSuccess);

        Result result = batch.RecordRejected(conflict);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.ImportDataRowAlreadyClassified.Code,
            result.Error?.Code);
        Assert.Equal(ImportBatchStatus.Processing, batch.Status);
        Assert.Equal(0, batch.ImportedRows);
        Assert.Equal(1, batch.AlreadyExistsRows);
        Assert.Equal(0, batch.RejectedRows);
        Assert.Empty(batch.RowErrors);
        Assert.Null(batch.CompletedAt);
    }

    [Fact]
    public void Complete_should_reject_missing_row_classification()
    {
        ImportBatch batch = StartBatch(2);

        batch.RecordImported(1);

        Result result = batch.Complete(CompletedAt);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.ImportIncompleteClassification.Code,
            result.Error?.Code);
        Assert.Equal(ImportBatchStatus.Processing, batch.Status);
        Assert.Null(batch.CompletedAt);
        Assert.Equal(1, batch.ImportedRows);

        Assert.True(batch.RecordImported(2).IsSuccess);
        Assert.True(batch.Complete(CompletedAt).IsSuccess);
        Assert.Equal(ImportBatchStatus.Completed, batch.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    public void Record_should_reject_an_out_of_range_row_index(int dataRowIndex)
    {
        ImportBatch batch = StartBatch(2);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => batch.RecordImported(dataRowIndex));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => batch.RecordAlreadyExists(dataRowIndex));

        if (dataRowIndex >= 1)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => batch.RecordRejected(
                    new ImportRowError(
                        dataRowIndex,
                        ReconciliationErrors.ImportSourceConflict)));
        }

        Assert.Equal(ImportBatchStatus.Processing, batch.Status);
        Assert.Equal(0, batch.ImportedRows);
        Assert.Equal(0, batch.AlreadyExistsRows);
        Assert.Equal(0, batch.RejectedRows);
        Assert.Empty(batch.RowErrors);
    }

    [Fact]
    public void Complete_should_reject_a_second_completion()
    {
        ImportBatch batch = StartBatch(1);

        batch.RecordImported(1);
        Assert.True(batch.Complete(CompletedAt).IsSuccess);

        Result result = batch.Complete(CompletedAt.AddMinutes(1));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.ImportBatchNotProcessing.Code,
            result.Error?.Code);
        Assert.Equal(ImportBatchStatus.Completed, batch.Status);
        Assert.Equal(CompletedAt, batch.CompletedAt);
        Assert.Equal(1, batch.ImportedRows);
    }

    [Fact]
    public void Record_should_reject_classification_after_completion()
    {
        ImportBatch batch = StartBatch(1);

        batch.RecordImported(1);
        batch.Complete(CompletedAt);

        Result imported = batch.RecordImported(1);
        Result alreadyExists = batch.RecordAlreadyExists(1);
        Result rejected = batch.RecordRejected(ConflictError(1, sourceLineNumber: 3));
        Result outOfRange = batch.RecordImported(0);

        Assert.Equal(
            ReconciliationErrors.ImportBatchNotProcessing.Code,
            imported.Error?.Code);
        Assert.Equal(
            ReconciliationErrors.ImportBatchNotProcessing.Code,
            alreadyExists.Error?.Code);
        Assert.Equal(
            ReconciliationErrors.ImportBatchNotProcessing.Code,
            rejected.Error?.Code);
        Assert.Equal(
            ReconciliationErrors.ImportBatchNotProcessing.Code,
            outOfRange.Error?.Code);
        Assert.Equal(1, batch.ImportedRows);
        Assert.Equal(0, batch.RejectedRows);
        Assert.Empty(batch.RowErrors);
        Assert.Equal(CompletedAt, batch.CompletedAt);
    }

    [Fact]
    public void Complete_should_reject_a_timestamp_before_creation()
    {
        ImportBatch batch = StartBatch(1);

        batch.RecordImported(1);

        Result result = batch.Complete(CreatedAt.AddMinutes(-1));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.ImportCompletionPrecedesCreation.Code,
            result.Error?.Code);
        Assert.Equal(ImportBatchStatus.Processing, batch.Status);
        Assert.Null(batch.CompletedAt);
        Assert.Equal(1, batch.ImportedRows);

        Assert.True(batch.Complete(CreatedAt).IsSuccess);
        Assert.Equal(CreatedAt, batch.CompletedAt);
    }

    [Fact]
    public void Complete_should_reject_the_default_timestamp()
    {
        ImportBatch batch = StartBatch(1);

        batch.RecordImported(1);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => batch.Complete(default));
        Assert.Equal(ImportBatchStatus.Processing, batch.Status);
        Assert.Null(batch.CompletedAt);
    }

    [Fact]
    public void Rejected_row_should_store_one_error_for_the_logical_index()
    {
        ImportBatch batch = StartBatch(1);
        ImportRowError error = ConflictError(1, sourceLineNumber: 12);

        Assert.True(batch.RecordRejected(error).IsSuccess);

        Result duplicate = batch.RecordRejected(
            ValidationError(1, sourceLineNumber: 99));

        Assert.True(duplicate.IsFailure);
        Assert.Equal(
            ReconciliationErrors.ImportDataRowAlreadyClassified.Code,
            duplicate.Error?.Code);
        Assert.Equal(1, batch.RejectedRows);
        ImportRowError stored = Assert.Single(batch.RowErrors);
        Assert.Equal(error, stored);
        Assert.Equal(1, stored.DataRowIndex);
        Assert.Equal(12, stored.SourceLineNumber);
        Assert.Equal(
            ReconciliationErrors.ImportSourceConflict.Code,
            stored.ErrorCode);
    }

    private static ImportBatch StartBatch(int totalRows)
    {
        return ImportBatch.Start(
            ImportBatchId.New(),
            TestOrganizationId,
            ImportType.Invoice,
            new SourceSystem("ERP"),
            new FileHash(Digest),
            "invoices.csv",
            totalRows,
            CreatedAt);
    }

    private static ImportRowError ConflictError(int dataRowIndex, int? sourceLineNumber)
    {
        return new ImportRowError(
            dataRowIndex,
            ReconciliationErrors.ImportSourceConflict,
            sourceLineNumber);
    }

    private static ImportRowError ValidationError(int dataRowIndex, int? sourceLineNumber)
    {
        return new ImportRowError(
            dataRowIndex,
            new Error(
                "reconciliation.import.row_invalid",
                "The row is invalid."),
            sourceLineNumber);
    }

}