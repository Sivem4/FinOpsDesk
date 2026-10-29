using FinOpsDesk.Modules.Reconciliation;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialAuditConfirmationTests
{
    private static readonly DateTimeOffset ConfirmedAt =
        new(2026, 10, 28, 9, 15, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public FinancialAuditConfirmationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Exact_confirmation_persists_one_audit_and_replay_does_not_add_another()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m, "INV-AUDIT");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m,
            reference: "INV-AUDIT");
        await SeedAsync(invoice, bankTransaction);
        Guid idempotencyKey = Guid.NewGuid();

        Result<ConfirmExactMatchResponse> created = await ConfirmExactAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            idempotencyKey);
        Result<ConfirmExactMatchResponse> replayed = await ConfirmExactAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            idempotencyKey);

        Assert.True(created.IsSuccess);
        Assert.False(created.Value?.Replayed);
        Assert.True(replayed.IsSuccess);
        Assert.True(replayed.Value?.Replayed);
        Assert.Equal(created.Value?.AllocationId, replayed.Value?.AllocationId);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        FinancialAuditEntry audit = await reload.FinancialAuditEntries.SingleAsync(
            entry => entry.OrganizationId == organizationId);
        Allocation allocation = await reload.Allocations.SingleAsync(
            candidate => candidate.OrganizationId == organizationId);
        Assert.Equal(allocation.Id, audit.AllocationId);
        Assert.Equal(invoice.Id, audit.InvoiceId);
        Assert.Equal(bankTransaction.Id, audit.BankTransactionId);
        Assert.Equal(organizationId, audit.OrganizationId);
        Assert.Equal(new Money(100m, PersistenceTestData.Pln), audit.Amount);
        Assert.Equal(TestCurrentActorContext.DefaultActorId, audit.ActorId);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, audit.IdempotencyOperation);
        Assert.Equal(idempotencyKey, audit.IdempotencyKey);
        Assert.Equal(ConfirmedAt, audit.RecordedAt);
        Assert.Null(audit.BatchExecutionId);
        Assert.Equal(FinancialAuditAction.Confirmation, audit.Action);
    }

    [Fact]
    public async Task Historical_exact_replay_does_not_backfill_an_audit_entry()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 25m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 25m);
        Guid idempotencyKey = Guid.NewGuid();
        var historical = new Allocation(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            new Money(25m, PersistenceTestData.Pln),
            ConfirmedAt,
            idempotencyKey,
            AllocationIdempotencyOperation.ExactMatch);
        await SeedAsync(invoice, bankTransaction, historical);

        Result<ConfirmExactMatchResponse> replayed = await ConfirmExactAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            idempotencyKey);

        Assert.True(replayed.IsSuccess);
        Assert.True(replayed.Value?.Replayed);
        Assert.Equal(historical.Id.Value, replayed.Value?.AllocationId);
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            0,
            await reload.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Explicit_partial_confirmation_records_the_confirmed_amount_and_replay_adds_no_audit()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m, "INV-PART");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            80m,
            reference: "OTHER");
        await SeedAsync(invoice, bankTransaction);
        Guid idempotencyKey = Guid.NewGuid();
        var amount = new Money(40m, PersistenceTestData.Pln);

        Result<ConfirmExplicitAmountResponse> created = await ConfirmExplicitAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            amount,
            idempotencyKey);
        Result<ConfirmExplicitAmountResponse> replayed = await ConfirmExplicitAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            amount,
            idempotencyKey);

        Assert.True(created.IsSuccess);
        Assert.False(created.Value?.Replayed);
        Assert.True(replayed.IsSuccess);
        Assert.True(replayed.Value?.Replayed);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        FinancialAuditEntry audit = await reload.FinancialAuditEntries.SingleAsync(
            entry => entry.OrganizationId == organizationId);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, audit.IdempotencyOperation);
        Assert.Equal(idempotencyKey, audit.IdempotencyKey);
        Assert.Equal(amount, audit.Amount);
        Assert.Equal(ConfirmedAt, audit.RecordedAt);
        Assert.Equal(TestCurrentActorContext.DefaultActorId, audit.ActorId);
        Assert.Null(audit.BatchExecutionId);
        Invoice stored = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(amount, stored.AllocatedAmount);
    }

    [Fact]
    public async Task Invalid_explicit_amount_creates_neither_allocation_nor_audit()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 100m);
        await SeedAsync(invoice, bankTransaction);

        Result<ConfirmExplicitAmountResponse> result = await ConfirmExplicitAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            new Money(50m, PersistenceTestData.Pln),
            Guid.NewGuid());

        Assert.Equal(
            ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code,
            result.Error?.Code);
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        Assert.Equal(
            0,
            await reload.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        Invoice stored = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), stored.AllocatedAmount);
    }

    [Fact]
    public async Task Audit_insert_failure_rolls_back_the_financial_changes()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 30m, "INV-ROLLBACK");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            30m,
            reference: "INV-ROLLBACK");
        await SeedAsync(invoice, bankTransaction);

        await using ServiceProvider provider = CreateProvider(duplicateAudit: true);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ConfirmExactMatchHandler handler = CreateExactHandler(scope.ServiceProvider, organizationId);
        Result<ConfirmExactMatchResponse> result = await handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ConcurrencyConflict.Code, result.Error?.Code);
        Assert.False(result.IsSuccess);
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        Assert.Equal(
            0,
            await reload.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBankTransaction = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedBankTransaction.AllocatedAmount);
    }

    [Fact]
    public async Task Financial_write_failure_does_not_commit_an_audit_entry()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 18m, "INV-STALE");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            18m,
            reference: "INV-STALE");
        await SeedAsync(invoice, bankTransaction);

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ConfirmExactMatchHandler handler = CreateExactHandler(
            scope.ServiceProvider,
            organizationId,
            new StaleRowVersionUnitOfWork(
                scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                _fixture.ConnectionString,
                invoice.Id.Value));
        Result<ConfirmExactMatchResponse> result = await handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ConcurrencyConflict.Code, result.Error?.Code);
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        Assert.Equal(
            0,
            await reload.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        Invoice stored = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), stored.AllocatedAmount);
    }

    [Fact]
    public async Task Concurrent_exact_confirmations_leave_one_allocation_and_one_audit()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 44m, "INV-RACE");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            44m,
            reference: "INV-RACE");
        await SeedAsync(invoice, bankTransaction);
        Guid idempotencyKey = Guid.NewGuid();

        Result<ConfirmExactMatchResponse>[] results = await ConfirmTogetherAsync(
            organizationId,
            (invoice.Id, bankTransaction.Id, idempotencyKey),
            (invoice.Id, bankTransaction.Id, idempotencyKey));

        Assert.Equal(2, results.Count(result => result.IsSuccess));
        Assert.Equal(1, results.Count(result => result.Value?.Replayed == false));
        Assert.Equal(1, results.Count(result => result.Value?.Replayed == true));
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        Assert.Equal(
            1,
            await reload.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
    }

    private ServiceProvider CreateProvider(bool duplicateAudit)
    {
        var services = new ServiceCollection();
        services.AddReconciliationModule(_fixture.ConnectionString);
        if (duplicateAudit)
        {
            services.AddScoped<IFinancialAuditWriter, DuplicatingFinancialAuditWriter>();
        }

        return services.BuildServiceProvider();
    }

    private async Task<Result<ConfirmExactMatchResponse>> ConfirmExactAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Guid idempotencyKey)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ConfirmExactMatchHandler handler = CreateExactHandler(scope.ServiceProvider, organizationId);
        return await handler.Handle(
            new ConfirmExactMatchCommand(invoiceId, bankTransactionId, idempotencyKey),
            CancellationToken.None);
    }

    private async Task<Result<ConfirmExplicitAmountResponse>> ConfirmExplicitAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money amount,
        Guid idempotencyKey)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var handler = new ConfirmExplicitAmountHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(),
            scope.ServiceProvider.GetRequiredService<IInvoiceRepository>(),
            scope.ServiceProvider.GetRequiredService<IBankTransactionRepository>(),
            scope.ServiceProvider.GetRequiredService<IAllocationRepository>(),
            scope.ServiceProvider.GetRequiredService<IAllocationIdempotencyLookup>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IFinancialAuditWriter>(),
            scope.ServiceProvider.GetRequiredService<IAllocationIdempotencyConflict>(),
            new FixedTimeProvider(ConfirmedAt));
        return await handler.Handle(
            new ConfirmExplicitAmountCommand(invoiceId, bankTransactionId, amount, idempotencyKey),
            CancellationToken.None);
    }

    private static ConfirmExactMatchHandler CreateExactHandler(
        IServiceProvider services,
        OrganizationId organizationId,
        IUnitOfWork? unitOfWork = null)
    {
        return new ConfirmExactMatchHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(),
            services.GetRequiredService<IInvoiceRepository>(),
            services.GetRequiredService<IBankTransactionRepository>(),
            services.GetRequiredService<IAllocationRepository>(),
            services.GetRequiredService<IAllocationIdempotencyLookup>(),
            unitOfWork ?? services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IFinancialAuditWriter>(),
            services.GetRequiredService<IAllocationIdempotencyConflict>(),
            new FixedTimeProvider(ConfirmedAt));
    }

    private async Task<Result<ConfirmExactMatchResponse>[]> ConfirmTogetherAsync(
        OrganizationId organizationId,
        params (InvoiceId InvoiceId, BankTransactionId BankTransactionId, Guid IdempotencyKey)[] requests)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using var gate = new Barrier(requests.Length);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task<Result<ConfirmExactMatchResponse>> Start(
            (InvoiceId InvoiceId, BankTransactionId BankTransactionId, Guid IdempotencyKey) request)
        {
            return Task.Run(
                async () =>
                {
                    await using AsyncServiceScope scope = provider.CreateAsyncScope();
                    ConfirmExactMatchHandler handler = CreateExactHandler(
                        scope.ServiceProvider,
                        organizationId,
                        new GatedUnitOfWork(
                            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                            gate));
                    return await handler.Handle(
                        new ConfirmExactMatchCommand(
                            request.InvoiceId,
                            request.BankTransactionId,
                            request.IdempotencyKey),
                        timeout.Token);
                },
                timeout.Token);
        }

        return await Task.WhenAll(requests.Select(Start));
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();

        foreach (object entity in entities)
        {
            context.Add(entity);
        }

        await context.SaveChangesAsync();
    }

    private sealed class DuplicatingFinancialAuditWriter : IFinancialAuditWriter
    {
        private readonly ReconciliationDbContext _dbContext;

        public DuplicatingFinancialAuditWriter(ReconciliationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public void AppendConfirmation(
            Allocation allocation,
            string actorId,
            BatchExecutionId? batchExecutionId)
        {
            _dbContext.FinancialAuditEntries.Add(
                FinancialAuditEntry.ForConfirmation(
                    FinancialAuditEntryId.New(),
                    allocation,
                    actorId,
                    batchExecutionId));
            _dbContext.FinancialAuditEntries.Add(
                FinancialAuditEntry.ForConfirmation(
                    FinancialAuditEntryId.New(),
                    allocation,
                    actorId,
                    batchExecutionId));
        }

        public void AppendReversal(
            Allocation allocation,
            string actorId,
            Guid reversalIdempotencyKey)
        {
            throw new InvalidOperationException("This confirmation test double does not record reversals.");
        }
    }

    private sealed class StaleRowVersionUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;
        private readonly string _connectionString;
        private readonly Guid _invoiceId;

        public StaleRowVersionUnitOfWork(
            IUnitOfWork inner,
            string connectionString,
            Guid invoiceId)
        {
            _inner = inner;
            _connectionString = connectionString;
            _invoiceId = invoiceId;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE [reconciliation].[Invoices]
                SET [InvoiceNumber] = [InvoiceNumber]
                WHERE [Id] = @id
                """;
            command.Parameters.Add(new SqlParameter("@id", _invoiceId));
            int updated = await command.ExecuteNonQueryAsync(cancellationToken);
            if (updated != 1)
            {
                throw new InvalidOperationException("Expected exactly one invoice rowversion bump.");
            }

            await _inner.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class GatedUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;
        private readonly Barrier _gate;

        public GatedUnitOfWork(IUnitOfWork inner, Barrier gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            _gate.SignalAndWait(cancellationToken);
            await _inner.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}