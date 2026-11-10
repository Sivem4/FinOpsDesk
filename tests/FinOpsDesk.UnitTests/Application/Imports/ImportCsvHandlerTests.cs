using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Extensions.Logging.Abstractions;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.Imports;

public sealed class ImportCsvHandlerTests
{
    private static readonly OrganizationId Organization = OrganizationId.New();

    [Fact]
    public async Task Invalid_source_system_does_not_read_or_commit()
    {
        var store = new FakeStore();
        var reader = new FakeReader();
        ImportCsvHandler handler = CreateHandler(store, reader);

        Result<ImportCsvResponse> result = await handler.Handle(
            new ImportCsvCommand(ImportType.Invoice, " ", [1], "invoices.csv"),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ImportInvalidSourceSystem.Code, result.Error!.Code);
        Assert.Equal(0, store.FindCalls);
        Assert.Equal(0, reader.Calls);
        Assert.Equal(0, store.CommitCalls);
    }

    [Fact]
    public async Task Structurally_invalid_file_does_not_commit()
    {
        var store = new FakeStore();
        var reader = new FakeReader
        {
            Result = Result<ParsedCsvFile>.Failure(ReconciliationApplicationErrors.ImportInvalidFile)
        };
        ImportCsvHandler handler = CreateHandler(store, reader);

        Result<ImportCsvResponse> result = await handler.Handle(
            Command("ExternalId,InvoiceNumber,Amount,Currency\n"),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ImportInvalidFile.Code, result.Error!.Code);
        Assert.Equal(1, store.FindCalls);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(0, store.CommitCalls);
    }

    [Fact]
    public async Task Exact_file_replay_returns_the_stored_batch_without_parsing()
    {
        ImportBatch stored = CompletedBatch();
        var store = new FakeStore { Stored = stored };
        var reader = new FakeReader();
        ImportCsvHandler handler = CreateHandler(store, reader);

        Result<ImportCsvResponse> result = await handler.Handle(
            Command("ExternalId,InvoiceNumber,Amount,Currency\nINV-1,FV,1.00,PLN\n"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.True(result.Value!.Replayed);
        Assert.Equal(stored.Id.Value, result.Value.ImportBatchId);
        Assert.Equal(0, reader.Calls);
        Assert.Equal(0, store.CommitCalls);
    }

    [Fact]
    public async Task Unique_conflict_reloads_the_committed_batch_instead_of_retrying_the_same_work()
    {
        var store = new FakeStore { FirstCommitFindsStoredBatch = true };
        var reader = new FakeReader
        {
            Result = Result<ParsedCsvFile>.Success(new ParsedCsvFile(
            [
                new ParsedCsvRow(
                    1,
                    2,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ExternalId"] = "INV-1",
                        ["InvoiceNumber"] = "FV",
                        ["Amount"] = "1.00",
                        ["Currency"] = "PLN"
                    })
            ]))
        };
        ImportCsvHandler handler = CreateHandler(store, reader);

        Result<ImportCsvResponse> result = await handler.Handle(
            Command("ignored"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.True(result.Value!.Replayed);
        Assert.Equal(1, store.CommitCalls);
        Assert.NotNull(store.CommittedWork);
    }

    private static ImportCsvHandler CreateHandler(FakeStore store, FakeReader reader)
    {
        return new ImportCsvHandler(
            new FixedOrganization(Organization),
            store,
            reader,
            new FixedTimeProvider(),
            NullLogger<ImportCsvHandler>.Instance,
            NoFinancialHandlerObservation.Instance);
    }

    private static ImportCsvCommand Command(string csv)
    {
        return new ImportCsvCommand(
            ImportType.Invoice,
            "mBank",
            System.Text.Encoding.UTF8.GetBytes(csv),
            "invoices.csv");
    }

    private static ImportBatch CompletedBatch()
    {
        ImportBatch batch = ImportBatch.Start(
            ImportBatchId.New(),
            Organization,
            ImportType.Invoice,
            new SourceSystem("mBank"),
            new FileHash(new string('a', FileHash.HexLength)),
            "invoices.csv",
            1,
            new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));
        Assert.True(batch.RecordImported(1).IsSuccess);
        Assert.True(batch.Complete(batch.CreatedAt).IsSuccess);

        return batch;
    }

    private sealed class FixedOrganization(OrganizationId organizationId) : ICurrentOrganizationContext
    {
        public OrganizationId OrganizationId => organizationId;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        }
    }

    private sealed class FakeReader : IImportFileReader
    {
        public int Calls { get; private set; }

        public Result<ParsedCsvFile> Result { get; set; } =
            Result<ParsedCsvFile>.Failure(ReconciliationApplicationErrors.ImportInvalidFile);

        public Result<ParsedCsvFile> Read(
            ImportType importType,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            Calls++;

            return Result;
        }
    }

    private sealed class FakeStore : ICsvImportStore
    {
        public ImportBatch? Stored { get; set; }

        public int FindCalls { get; private set; }

        public int CommitCalls { get; private set; }

        public bool FirstCommitFindsStoredBatch { get; set; }

        public ImportCommitWork? CommittedWork { get; private set; }

        public Task<ImportBatch?> FindBatchByFileIdentityAsync(
            OrganizationId organizationId,
            ImportType importType,
            SourceSystem sourceSystem,
            FileHash fileHash,
            CancellationToken cancellationToken)
        {
            FindCalls++;

            return Task.FromResult(Stored);
        }

        public Task<IReadOnlyDictionary<string, InvoiceImportFacts>> FindInvoicesByExternalIdsAsync(
            OrganizationId organizationId,
            SourceSystem sourceSystem,
            IReadOnlyCollection<string> externalIds,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyDictionary<string, InvoiceImportFacts>>(
                new Dictionary<string, InvoiceImportFacts>(StringComparer.Ordinal));
        }

        public Task<IReadOnlyDictionary<string, BankTransactionImportFacts>> FindBankTransactionsByExternalIdsAsync(
            OrganizationId organizationId,
            SourceSystem sourceSystem,
            IReadOnlyCollection<string> externalIds,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyDictionary<string, BankTransactionImportFacts>>(
                new Dictionary<string, BankTransactionImportFacts>(StringComparer.Ordinal));
        }

        public Task<ImportCommitResult> CommitAsync(
            ImportCommitWork work,
            CancellationToken cancellationToken)
        {
            CommitCalls++;
            CommittedWork = work;

            if (FirstCommitFindsStoredBatch)
            {
                Stored = work.Batch;
            }

            return Task.FromResult(new ImportCommitResult(
                FirstCommitFindsStoredBatch
                    ? ImportCommitStatus.UniqueConflict
                    : ImportCommitStatus.Committed));
        }
    }
}