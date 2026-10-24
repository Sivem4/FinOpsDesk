using System.Text;

using FinOpsDesk.Modules.Reconciliation;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Imports;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class CsvImportWorkflowTests
{
    private readonly SqlServerFixture _fixture;

    public CsvImportWorkflowTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Invoice_import_persists_partial_success_in_one_batch()
    {
        OrganizationId organizationId = OrganizationId.New();
        string csv =
            """
            ExternalId,InvoiceNumber,Amount,Currency
            INV-1,FV/1,10.00,PLN
            INV-2,FV/2,0,PLN
            INV-3,FV/3,30.0000,PLN
            """;

        await using ImportHarness harness = CreateHarness(organizationId);
        ImportCsvResponse response = await harness.ImportAsync(ImportType.Invoice, csv);

        Assert.False(response.Replayed);
        Assert.Equal("CompletedWithErrors", response.Status);
        Assert.Equal(3, response.TotalRows);
        Assert.Equal(2, response.ImportedRows);
        Assert.Equal(0, response.AlreadyExistsRows);
        Assert.Equal(1, response.RejectedRows);
        ImportRowErrorResponse error = Assert.Single(response.RowErrors);
        Assert.Equal(2, error.DataRowIndex);
        Assert.Equal(3, error.SourceLineNumber);
        Assert.Equal("Amount", error.ColumnName);
        Assert.Equal(ImportRowErrors.InvalidAmount.Code, error.ErrorCode);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        int invoices = await context.Invoices.CountAsync(invoice =>
            invoice.OrganizationId == organizationId);
        ImportBatch batch = await context.ImportBatches
            .Include(stored => stored.RowErrors)
            .SingleAsync(stored => stored.Id == new ImportBatchId(response.ImportBatchId));

        Assert.Equal(2, invoices);
        Assert.Equal(ImportBatchStatus.CompletedWithErrors, batch.Status);
        Assert.Equal(response.FileHash, batch.FileHash.Value);
        Assert.Single(batch.RowErrors);
    }

    [Fact]
    public async Task Bank_transaction_import_persists_the_batch_and_transaction()
    {
        OrganizationId organizationId = OrganizationId.New();
        string csv =
            """
            ExternalId,BookingDate,Amount,Currency,Reference
            TXN-1,2026-08-24,10.00,PLN, INV-1 
            """;

        await using ImportHarness harness = CreateHarness(organizationId);
        ImportCsvResponse response = await harness.ImportAsync(ImportType.BankTransaction, csv);

        Assert.Equal("Completed", response.Status);
        Assert.Equal(1, response.ImportedRows);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        BankTransaction transaction = await context.BankTransactions.SingleAsync(stored =>
            stored.OrganizationId == organizationId);

        Assert.Equal("INV-1", transaction.Reference);
        Assert.Equal(new DateOnly(2026, 8, 24), transaction.BookingDate);
    }

    [Fact]
    public async Task All_rows_rejected_persists_a_rejected_batch_and_no_financial_records()
    {
        OrganizationId organizationId = OrganizationId.New();

        await using ImportHarness harness = CreateHarness(organizationId);
        ImportCsvResponse response = await harness.ImportAsync(
            ImportType.Invoice,
            "ExternalId,InvoiceNumber,Amount,Currency\n,,,\n");

        Assert.Equal("Rejected", response.Status);
        Assert.Equal(0, response.ImportedRows);
        Assert.Equal(1, response.RejectedRows);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(0, await context.Invoices.CountAsync(invoice => invoice.OrganizationId == organizationId));
        Assert.Equal(
            ImportBatchStatus.Rejected,
            (await context.ImportBatches.SingleAsync(batch => batch.OrganizationId == organizationId)).Status);
    }

    [Fact]
    public async Task Existing_equivalent_and_conflicting_records_are_classified_without_upsert()
    {
        OrganizationId organizationId = OrganizationId.New();
        string first =
            """
            ExternalId,InvoiceNumber,Amount,Currency
            INV-1,FV/1,10.00,PLN
            """;
        string second =
            """
            ExternalId,InvoiceNumber,Amount,Currency
            INV-1,FV/1,10.00,PLN
            INV-1,FV/1,12.00,PLN
            INV-2,FV/2,20.00,PLN
            """;

        await using ImportHarness harness = CreateHarness(organizationId);
        await harness.ImportAsync(ImportType.Invoice, first);

        await using ReconciliationDbContext allocationContext = _fixture.CreateContext();
        Invoice stored = await allocationContext.Invoices.SingleAsync(invoice =>
            invoice.OrganizationId == organizationId);
        Assert.True(stored.Allocate(new Money(1m, new CurrencyCode("PLN"))).IsSuccess);
        await allocationContext.SaveChangesAsync();

        ImportCsvResponse response = await harness.ImportAsync(ImportType.Invoice, second);

        Assert.Equal(1, response.AlreadyExistsRows);
        Assert.Equal(1, response.RejectedRows);
        Assert.Equal(1, response.ImportedRows);
        Assert.Equal(ImportRowErrors.SourceConflict.Code, response.RowErrors.Single().ErrorCode);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice unchanged = await context.Invoices.SingleAsync(invoice =>
            invoice.OrganizationId == organizationId
            && invoice.InvoiceNumber == "FV/1");

        Assert.Equal(10m, unchanged.TotalAmount.Amount);
        Assert.Equal(1m, unchanged.AllocatedAmount.Amount);
        Assert.Equal(2, await context.Invoices.CountAsync(invoice => invoice.OrganizationId == organizationId));
    }

    [Fact]
    public async Task In_file_duplicates_and_an_invalid_first_occurrence_follow_the_ledger()
    {
        OrganizationId organizationId = OrganizationId.New();
        string csv =
            """
            ExternalId,InvoiceNumber,Amount,Currency
            INV-1,,10.00,PLN
            INV-1,FV/1,10.00,PLN
            INV-1,FV/1,10.00,PLN
            INV-1,FV/1,12.00,PLN
            """;

        await using ImportHarness harness = CreateHarness(organizationId);
        ImportCsvResponse response = await harness.ImportAsync(ImportType.Invoice, csv);

        Assert.Equal(1, response.ImportedRows);
        Assert.Equal(1, response.AlreadyExistsRows);
        Assert.Equal(2, response.RejectedRows);
        Assert.Equal(
            [ImportRowErrors.InvalidInvoiceNumber.Code, ImportRowErrors.SourceConflict.Code],
            response.RowErrors.Select(error => error.ErrorCode).ToArray());
    }

    [Fact]
    public async Task Exact_file_replay_returns_the_original_batch()
    {
        OrganizationId organizationId = OrganizationId.New();
        const string csv = "ExternalId,InvoiceNumber,Amount,Currency\nINV-1,FV/1,10.00,PLN\n";

        await using ImportHarness harness = CreateHarness(organizationId);
        ImportCsvResponse created = await harness.ImportAsync(ImportType.Invoice, csv);
        ImportCsvResponse replayed = await harness.ImportAsync(ImportType.Invoice, csv);

        Assert.False(created.Replayed);
        Assert.True(replayed.Replayed);
        Assert.Equal(created.ImportBatchId, replayed.ImportBatchId);
        Assert.Equal(created.FileHash, replayed.FileHash);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(1, await context.Invoices.CountAsync(invoice => invoice.OrganizationId == organizationId));
        Assert.Equal(1, await context.ImportBatches.CountAsync(batch => batch.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Byte_different_file_with_the_same_records_creates_another_batch()
    {
        OrganizationId organizationId = OrganizationId.New();
        const string lf = "ExternalId,InvoiceNumber,Amount,Currency\nINV-1,FV/1,10.00,PLN\n";
        const string crlf = "ExternalId,InvoiceNumber,Amount,Currency\r\nINV-1,FV/1,10.00,PLN\r\n";

        await using ImportHarness harness = CreateHarness(organizationId);
        ImportCsvResponse first = await harness.ImportAsync(ImportType.Invoice, lf);
        ImportCsvResponse second = await harness.ImportAsync(ImportType.Invoice, crlf);

        Assert.False(second.Replayed);
        Assert.NotEqual(first.FileHash, second.FileHash);
        Assert.Equal(1, second.AlreadyExistsRows);
        Assert.Equal(0, second.ImportedRows);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(1, await context.Invoices.CountAsync(invoice => invoice.OrganizationId == organizationId));
        Assert.Equal(2, await context.ImportBatches.CountAsync(batch => batch.OrganizationId == organizationId));
    }

    [Fact]
    public async Task External_id_imports_are_case_sensitive_and_tenant_scoped()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        const string csv =
            """
            ExternalId,InvoiceNumber,Amount,Currency
            abc,FV/1,10.00,PLN
            ABC,FV/2,20.00,PLN
            """;

        await using ImportHarness harness = CreateHarness(organizationId);
        await using ImportHarness other = CreateHarness(otherOrganizationId);
        ImportCsvResponse response = await harness.ImportAsync(ImportType.Invoice, csv);
        await other.ImportAsync(ImportType.Invoice, csv);

        Assert.Equal(2, response.ImportedRows);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        List<string> externalIds = await context.Invoices
            .Where(invoice => invoice.OrganizationId == organizationId)
            .Select(invoice => invoice.SourceIdentity!.ExternalId.Value)
            .ToListAsync();

        Assert.Equal(2, externalIds.Count);
        Assert.Contains("abc", externalIds);
        Assert.Contains("ABC", externalIds);
        Assert.Equal(2, await context.Invoices.CountAsync(invoice => invoice.OrganizationId == otherOrganizationId));
    }

    [Fact]
    public async Task Chunked_source_identity_lookup_runs_on_sql_server()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        SourceSystem sourceSystem = new("MBANK");
        Invoice match = ImportPersistenceData.ImportedInvoice(
            organizationId,
            new SourceIdentity(sourceSystem, new ExternalId("abc")),
            "INV-MATCH");
        Invoice otherCase = ImportPersistenceData.ImportedInvoice(
            organizationId,
            new SourceIdentity(sourceSystem, new ExternalId("ABC")),
            "INV-CASE");
        Invoice otherTenant = ImportPersistenceData.ImportedInvoice(
            otherOrganizationId,
            new SourceIdentity(sourceSystem, new ExternalId("abc")),
            "INV-OTHER");

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.AddRange(match, otherCase, otherTenant);
            await context.SaveChangesAsync();

            ExternalId[] requested =
            [
                new ExternalId("abc"),
                new ExternalId("ABC"),
                new ExternalId("missing")
            ];
            IQueryable<Invoice> query = ImportSourceQueries.MatchingInvoices(
                context.Invoices.AsNoTracking(),
                organizationId,
                sourceSystem,
                requested);
            string sql = query.ToQueryString();

            Assert.Contains("[OrganizationId]", sql, StringComparison.Ordinal);
            Assert.Contains("[SourceSystem]", sql, StringComparison.Ordinal);
            Assert.Contains("[ExternalId]", sql, StringComparison.Ordinal);
            Assert.Equal(2, await query.CountAsync());
        }

        await using ImportHarness harness = CreateHarness(organizationId);
        string[] requestedIds = Enumerable.Range(0, ImportLimits.LookupChunkSize + 1)
            .Select(index => $"FILL-{index:D4}")
            .Append("abc")
            .ToArray();
        IReadOnlyDictionary<string, InvoiceImportFacts> facts =
            await harness.Store.FindInvoicesByExternalIdsAsync(
                organizationId,
                sourceSystem,
                requestedIds,
                CancellationToken.None);

        Assert.True(facts.ContainsKey("abc"));
        Assert.False(facts.ContainsKey("ABC"));
        Assert.Equal("INV-MATCH", facts["abc"].InvoiceNumber);
    }

    [Fact]
    public async Task Sql_failure_rolls_back_the_batch_and_imported_rows()
    {
        OrganizationId organizationId = OrganizationId.New();

        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER [reconciliation].[TR_Invoices_FailCsvImport]
            ON [reconciliation].[Invoices]
            AFTER INSERT
            AS
            BEGIN
                THROW 50001, 'FinOpsDesk integration test forced failure', 1;
            END
            """);

        try
        {
            await using ImportHarness harness = CreateHarness(organizationId);

            await Assert.ThrowsAsync<DbUpdateException>(() => harness.ImportAsync(
                ImportType.Invoice,
                "ExternalId,InvoiceNumber,Amount,Currency\nINV-1,FV/1,10.00,PLN\n"));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                IF OBJECT_ID(N'[reconciliation].[TR_Invoices_FailCsvImport]', N'TR') IS NOT NULL
                    DROP TRIGGER [reconciliation].[TR_Invoices_FailCsvImport];
                """);
        }

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(0, await reload.Invoices.CountAsync(invoice => invoice.OrganizationId == organizationId));
        Assert.Equal(0, await reload.ImportBatches.CountAsync(batch => batch.OrganizationId == organizationId));
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_identical_imports_recover_to_one_batch()
    {
        OrganizationId organizationId = OrganizationId.New();
        byte[] csv = Encoding.UTF8.GetBytes(
            "ExternalId,InvoiceNumber,Amount,Currency\nINV-1,FV/1,10.00,PLN\n");

        await using ImportHarness harness = CreateHarness(organizationId);
        using var gate = new Barrier(2);
        var store = new GatedStore(harness.Store, gate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ImportCsvHandler handler = harness.CreateHandler(store);
        var command = new ImportCsvCommand(ImportType.Invoice, "mBank", csv, "invoices.csv");

        Result<ImportCsvResponse>[] results = await Task.WhenAll(
            handler.Handle(command, timeout.Token),
            handler.Handle(command, timeout.Token));

        ImportCsvResponse[] responses = Require(results);

        Assert.Equal(1, responses.Count(response => response.Replayed));
        Assert.Equal(responses[0].ImportBatchId, responses[1].ImportBatchId);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(1, await context.Invoices.CountAsync(invoice => invoice.OrganizationId == organizationId));
        Assert.Equal(1, await context.ImportBatches.CountAsync(batch => batch.OrganizationId == organizationId));
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_equivalent_files_recover_without_duplicating_the_invoice()
    {
        OrganizationId organizationId = OrganizationId.New();
        byte[] first = Encoding.UTF8.GetBytes(
            "ExternalId,InvoiceNumber,Amount,Currency\nINV-1,FV/1,10.00,PLN\n");
        byte[] second = Encoding.UTF8.GetBytes(
            "ExternalId,InvoiceNumber,Amount,Currency\r\nINV-1,FV/1,10.00,PLN\r\n");

        await using ImportHarness harness = CreateHarness(organizationId);
        using var gate = new Barrier(2);
        var store = new GatedStore(harness.Store, gate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ImportCsvHandler handler = harness.CreateHandler(store);

        Result<ImportCsvResponse>[] results = await Task.WhenAll(
            handler.Handle(
                new ImportCsvCommand(ImportType.Invoice, "mBank", first, "a.csv"),
                timeout.Token),
            handler.Handle(
                new ImportCsvCommand(ImportType.Invoice, "mBank", second, "b.csv"),
                timeout.Token));

        ImportCsvResponse[] responses = Require(results);

        Assert.Equal(2, responses.Select(response => response.ImportBatchId).Distinct().Count());
        Assert.Equal(1, responses.Sum(response => response.ImportedRows));
        Assert.Equal(1, responses.Sum(response => response.AlreadyExistsRows));
        Assert.DoesNotContain(responses, response => response.Replayed);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(1, await context.Invoices.CountAsync(invoice => invoice.OrganizationId == organizationId));
        Assert.Equal(2, await context.ImportBatches.CountAsync(batch => batch.OrganizationId == organizationId));
    }

    private static ImportCsvResponse[] Require(Result<ImportCsvResponse>[] results)
    {
        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Code));

        return results.Select(result => result.Value!).ToArray();
    }

    private ImportHarness CreateHarness(OrganizationId organizationId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReconciliationModule(_fixture.ConnectionString);

        return new ImportHarness(services.BuildServiceProvider(), organizationId);
    }

    private sealed class ImportHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly OrganizationId _organizationId;

        public ImportHarness(ServiceProvider provider, OrganizationId organizationId)
        {
            _provider = provider;
            _organizationId = organizationId;
            Store = provider.GetRequiredService<ICsvImportStore>();
        }

        public ICsvImportStore Store { get; }

        public ImportCsvHandler CreateHandler(ICsvImportStore store)
        {
            return new ImportCsvHandler(
                new FixedOrganization(_organizationId),
                store,
                _provider.GetRequiredService<IImportFileReader>(),
                TimeProvider.System,
                NullLogger<ImportCsvHandler>.Instance);
        }

        public async Task<ImportCsvResponse> ImportAsync(ImportType importType, string csv)
        {
            Result<ImportCsvResponse> result = await CreateHandler(Store).Handle(
                new ImportCsvCommand(
                    importType,
                    "mBank",
                    Encoding.UTF8.GetBytes(csv),
                    "import.csv"),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Code);

            return result.Value;
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
        }

        private sealed class FixedOrganization(OrganizationId organizationId) : ICurrentOrganizationContext
        {
            public OrganizationId OrganizationId => organizationId;
        }
    }

    private sealed class GatedStore : ICsvImportStore
    {
        private readonly ICsvImportStore _inner;
        private readonly Barrier _barrier;
        private int _commits;

        public GatedStore(ICsvImportStore inner, Barrier barrier)
        {
            _inner = inner;
            _barrier = barrier;
        }

        public Task<ImportBatch?> FindBatchByFileIdentityAsync(
            OrganizationId organizationId,
            ImportType importType,
            SourceSystem sourceSystem,
            FileHash fileHash,
            CancellationToken cancellationToken)
        {
            return _inner.FindBatchByFileIdentityAsync(
                organizationId,
                importType,
                sourceSystem,
                fileHash,
                cancellationToken);
        }

        public Task<IReadOnlyDictionary<string, InvoiceImportFacts>> FindInvoicesByExternalIdsAsync(
            OrganizationId organizationId,
            SourceSystem sourceSystem,
            IReadOnlyCollection<string> externalIds,
            CancellationToken cancellationToken)
        {
            return _inner.FindInvoicesByExternalIdsAsync(
                organizationId,
                sourceSystem,
                externalIds,
                cancellationToken);
        }

        public Task<IReadOnlyDictionary<string, BankTransactionImportFacts>> FindBankTransactionsByExternalIdsAsync(
            OrganizationId organizationId,
            SourceSystem sourceSystem,
            IReadOnlyCollection<string> externalIds,
            CancellationToken cancellationToken)
        {
            return _inner.FindBankTransactionsByExternalIdsAsync(
                organizationId,
                sourceSystem,
                externalIds,
                cancellationToken);
        }

        public async Task<ImportCommitResult> CommitAsync(
            ImportCommitWork work,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _commits) <= 2)
            {
                _barrier.SignalAndWait(cancellationToken);
            }

            return await _inner.CommitAsync(work, cancellationToken);
        }
    }
}