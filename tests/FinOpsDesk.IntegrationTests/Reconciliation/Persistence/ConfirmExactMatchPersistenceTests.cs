using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ConfirmExactMatchPersistenceTests
{
    private static readonly DateTimeOffset ConfirmedAt =
        new(2026, 10, 25, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BookingDate = new(2026, 8, 24);

    private readonly SqlServerFixture _fixture;

    public ConfirmExactMatchPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Confirm_exact_match_persists_the_allocation_key_and_both_balances()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m);
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);

        Result<ConfirmExactMatchResponse> result = await ConfirmAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            idempotencyKey);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.Replayed);
        Assert.Equal(1000m, result.Value.Amount.Amount);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBankTransaction = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);

        Assert.Equal(result.Value.AllocationId, stored.Id.Value);
        Assert.Equal(idempotencyKey, stored.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, stored.IdempotencyOperation);
        Assert.Equal(new Money(1000m, PersistenceTestData.Pln), stored.Amount);
        Assert.Equal(new Money(1000m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(new Money(1000m, PersistenceTestData.Pln), storedBankTransaction.AllocatedAmount);
    }

    [Fact]
    public async Task Replay_after_commit_returns_the_original_allocation_without_changing_balances()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 800m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            800m);
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);

        Result<ConfirmExactMatchResponse> first = await ConfirmAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            idempotencyKey);
        byte[] invoiceVersion = await RowVersionAsync(invoice.Id);
        Result<ConfirmExactMatchResponse> second = await ConfirmAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            idempotencyKey);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotNull(first.Value);
        Assert.NotNull(second.Value);
        Assert.False(first.Value.Replayed);
        Assert.True(second.Value.Replayed);
        Assert.Equal(first.Value.AllocationId, second.Value.AllocationId);
        Assert.Equal(invoiceVersion, await RowVersionAsync(invoice.Id));

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Same_key_with_a_different_pair_does_not_write_a_second_allocation()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m, "INV-001");
        BankTransaction firstBankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m,
            reference: "INV-001");
        BankTransaction secondBankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m,
            reference: "INV-002");
        Invoice secondInvoice = PersistenceTestData.CreateInvoice(organizationId, 100m, "INV-002");
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, firstBankTransaction, secondInvoice, secondBankTransaction);

        Result<ConfirmExactMatchResponse> first = await ConfirmAsync(
            organizationId,
            invoice.Id,
            firstBankTransaction.Id,
            idempotencyKey);
        Result<ConfirmExactMatchResponse> second = await ConfirmAsync(
            organizationId,
            secondInvoice.Id,
            secondBankTransaction.Id,
            idempotencyKey);

        Assert.True(first.IsSuccess);
        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            second.Error?.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        BankTransaction storedSecond = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == secondBankTransaction.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedSecond.AllocatedAmount);
    }

    [Fact]
    public async Task Bank_side_validation_failure_then_save_persists_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            200m);
        await SeedAsync(invoice, bankTransaction);

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ConfirmAllocationHandler handler = CreateAllocationHandler(scope.ServiceProvider, organizationId);
        Result<ConfirmAllocationResult> result = await handler.Handle(
            new ConfirmAllocationCommand(
                invoice.Id,
                bankTransaction.Id,
                new Money(300m, PersistenceTestData.Pln)),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationErrors.BankTransactionAllocationExceedsUnapplied.Code,
            result.Error?.Code);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .SaveChangesAsync(CancellationToken.None);

        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Stale_exact_match_then_save_persists_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            400m);
        await SeedAsync(invoice, bankTransaction);

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ConfirmExactMatchHandler handler = CreateHandler(scope.ServiceProvider, organizationId);
        Result<ConfirmExactMatchResponse> result = await handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ReconciliationErrors.MatchStale.Code, result.Error?.Code);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .SaveChangesAsync(CancellationToken.None);

        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Failed_allocation_insert_rolls_back_the_balance_updates_and_the_key()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m);
        await SeedAsync(invoice, bankTransaction);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER [reconciliation].[TR_Allocations_FailExactConfirm]
            ON [reconciliation].[Allocations]
            AFTER INSERT
            AS
            BEGIN
                THROW 50001, 'FinOpsDesk integration test forced failure', 1;
            END
            """);

        try
        {
            await using ServiceProvider provider = _fixture.CreateServiceProvider();
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            ConfirmExactMatchHandler handler = CreateHandler(scope.ServiceProvider, organizationId);

            await Assert.ThrowsAsync<DbUpdateException>(
                () => handler.Handle(
                    new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, Guid.NewGuid()),
                    CancellationToken.None));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                IF OBJECT_ID(N'[reconciliation].[TR_Allocations_FailExactConfirm]', N'TR') IS NOT NULL
                    DROP TRIGGER [reconciliation].[TR_Allocations_FailExactConfirm];
                """);
        }

        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_confirmations_of_one_invoice_and_two_bank_transactions_persist_one()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m, "INV-001");
        BankTransaction first = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m,
            reference: "INV-001");
        BankTransaction second = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m,
            reference: "INV-001");
        await SeedAsync(invoice, first, second);

        Result<ConfirmExactMatchResponse>[] results = await ConfirmTogetherAsync(
            organizationId,
            (invoice.Id, first.Id, Guid.NewGuid()),
            (invoice.Id, second.Id, Guid.NewGuid()));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(
            results,
            result => result.IsFailure
                && result.Error?.Code == ReconciliationApplicationErrors.ConcurrencyConflict.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedFirst = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == first.Id);
        BankTransaction storedSecond = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == second.Id);
        BankTransaction winner = stored.BankTransactionId == first.Id ? storedFirst : storedSecond;
        BankTransaction loser = stored.BankTransactionId == first.Id ? storedSecond : storedFirst;

        Assert.Equal(new Money(1000m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(new Money(1000m, PersistenceTestData.Pln), winner.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), loser.AllocatedAmount);
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_confirmations_of_one_bank_transaction_and_two_invoices_persist_one()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice first = PersistenceTestData.CreateInvoice(organizationId, 500m, "INV-001");
        Invoice second = PersistenceTestData.CreateInvoice(organizationId, 500m, "INV-001");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            500m,
            reference: "INV-001");
        await SeedAsync(first, second, bankTransaction);

        Result<ConfirmExactMatchResponse>[] results = await ConfirmTogetherAsync(
            organizationId,
            (first.Id, bankTransaction.Id, Guid.NewGuid()),
            (second.Id, bankTransaction.Id, Guid.NewGuid()));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(
            results,
            result => result.IsFailure
                && result.Error?.Code == ReconciliationApplicationErrors.ConcurrencyConflict.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        BankTransaction storedBankTransaction = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Invoice storedFirst = await reload.Invoices.SingleAsync(candidate => candidate.Id == first.Id);
        Invoice storedSecond = await reload.Invoices.SingleAsync(candidate => candidate.Id == second.Id);
        Invoice winner = stored.InvoiceId == first.Id ? storedFirst : storedSecond;
        Invoice loser = stored.InvoiceId == first.Id ? storedSecond : storedFirst;

        Assert.Equal(new Money(500m, PersistenceTestData.Pln), storedBankTransaction.AllocatedAmount);
        Assert.Equal(new Money(500m, PersistenceTestData.Pln), winner.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), loser.AllocatedAmount);
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_confirmations_with_the_same_key_persist_one_allocation()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 250m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            250m);
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);

        Result<ConfirmExactMatchResponse>[] results = await ConfirmTogetherAsync(
            organizationId,
            (invoice.Id, bankTransaction.Id, idempotencyKey),
            (invoice.Id, bankTransaction.Id, idempotencyKey));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Code));
        Assert.Single(results, result => result.Value is { Replayed: false });
        Assert.Single(results, result => result.Value is { Replayed: true });
        ConfirmExactMatchResponse firstResult = results[0].Value!;
        ConfirmExactMatchResponse secondResult = results[1].Value!;
        Assert.Equal(firstResult.AllocationId, secondResult.AllocationId);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Assert.Equal(idempotencyKey, stored.IdempotencyKey);
        Assert.Equal(firstResult.AllocationId, stored.Id.Value);
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_same_key_for_different_pairs_persists_only_the_winner()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoiceA = PersistenceTestData.CreateInvoice(organizationId, 110m, "INV-A");
        BankTransaction bankTransactionA = PersistenceTestData.CreateBankTransaction(
            organizationId,
            110m,
            reference: "INV-A");
        Invoice invoiceB = PersistenceTestData.CreateInvoice(organizationId, 220m, "INV-B");
        BankTransaction bankTransactionB = PersistenceTestData.CreateBankTransaction(
            organizationId,
            220m,
            reference: "INV-B");
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoiceA, bankTransactionA, invoiceB, bankTransactionB);

        Result<ConfirmExactMatchResponse>[] results = await ConfirmTogetherAsync(
            organizationId,
            (invoiceA.Id, bankTransactionA.Id, idempotencyKey),
            (invoiceB.Id, bankTransactionB.Id, idempotencyKey));

        Result<ConfirmExactMatchResponse> winner = Assert.Single(
            results,
            result => result.IsSuccess && result.Value is { Replayed: false });
        Assert.Single(
            results,
            result => result.IsFailure
                && result.Error?.Code == ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code);
        Assert.NotNull(winner.Value);
        ConfirmExactMatchResponse winnerResponse = winner.Value;

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId
                && allocation.IdempotencyKey == idempotencyKey);
        Invoice storedA = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoiceA.Id);
        Invoice storedB = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoiceB.Id);
        BankTransaction storedBankA = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransactionA.Id);
        BankTransaction storedBankB = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransactionB.Id);
        bool firstWon = stored.InvoiceId == invoiceA.Id;

        Assert.Equal(winnerResponse.AllocationId, stored.Id.Value);
        Assert.Equal(winnerResponse.InvoiceId, stored.InvoiceId.Value);
        Assert.Equal(winnerResponse.BankTransactionId, stored.BankTransactionId.Value);
        Assert.Equal(
            firstWon ? new Money(110m, PersistenceTestData.Pln) : Money.Zero(PersistenceTestData.Pln),
            storedA.AllocatedAmount);
        Assert.Equal(
            firstWon ? new Money(110m, PersistenceTestData.Pln) : Money.Zero(PersistenceTestData.Pln),
            storedBankA.AllocatedAmount);
        Assert.Equal(
            firstWon ? Money.Zero(PersistenceTestData.Pln) : new Money(220m, PersistenceTestData.Pln),
            storedB.AllocatedAmount);
        Assert.Equal(
            firstWon ? Money.Zero(PersistenceTestData.Pln) : new Money(220m, PersistenceTestData.Pln),
            storedBankB.AllocatedAmount);
    }

    [Fact(Timeout = 60000)]
    public async Task Different_keys_for_the_same_pair_persist_one_allocation()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 900m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            900m);
        await SeedAsync(invoice, bankTransaction);

        Result<ConfirmExactMatchResponse>[] results = await ConfirmTogetherAsync(
            organizationId,
            (invoice.Id, bankTransaction.Id, Guid.NewGuid()),
            (invoice.Id, bankTransaction.Id, Guid.NewGuid()));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(
            results,
            result => result.IsFailure
                && result.Error?.Code == ReconciliationApplicationErrors.ConcurrencyConflict.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task The_same_key_can_confirm_a_different_organization()
    {
        Guid idempotencyKey = Guid.NewGuid();
        OrganizationId firstOrganization = OrganizationId.New();
        OrganizationId secondOrganization = OrganizationId.New();
        Invoice firstInvoice = PersistenceTestData.CreateInvoice(firstOrganization, 40m);
        BankTransaction firstBankTransaction = PersistenceTestData.CreateBankTransaction(
            firstOrganization,
            40m);
        Invoice secondInvoice = PersistenceTestData.CreateInvoice(secondOrganization, 40m);
        BankTransaction secondBankTransaction = PersistenceTestData.CreateBankTransaction(
            secondOrganization,
            40m);
        await SeedAsync(firstInvoice, firstBankTransaction, secondInvoice, secondBankTransaction);

        Result<ConfirmExactMatchResponse> first = await ConfirmAsync(
            firstOrganization,
            firstInvoice.Id,
            firstBankTransaction.Id,
            idempotencyKey);
        Result<ConfirmExactMatchResponse> second = await ConfirmAsync(
            secondOrganization,
            secondInvoice.Id,
            secondBankTransaction.Id,
            idempotencyKey);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotNull(first.Value);
        Assert.NotNull(second.Value);
        Assert.NotEqual(first.Value.AllocationId, second.Value.AllocationId);
    }

    [Fact]
    public async Task Cross_tenant_ids_are_not_found_and_write_nothing()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId caller = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(owner, 70m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(owner, 70m);
        await SeedAsync(invoice, bankTransaction);

        Result<ConfirmExactMatchResponse> result = await ConfirmAsync(
            caller,
            invoice.Id,
            bankTransaction.Id,
            Guid.NewGuid());

        Assert.Equal(ReconciliationApplicationErrors.InvoiceNotFound.Code, result.Error?.Code);
        await AssertUnchangedAsync(owner, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Duplicate_idempotency_key_is_rejected_by_the_database()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 15m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            15m);
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);
        var amount = new Money(15m, PersistenceTestData.Pln);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Allocations.Add(CreateAllocation(
                organizationId,
                invoice.Id,
                bankTransaction.Id,
                amount,
                idempotencyKey));
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext duplicate = _fixture.CreateContext();
        duplicate.Allocations.Add(CreateAllocation(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            amount,
            idempotencyKey));
        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => duplicate.SaveChangesAsync());

        Assert.True(IsUniqueViolation(exception));
    }

    private async Task<Result<ConfirmExactMatchResponse>> ConfirmAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Guid idempotencyKey)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ConfirmExactMatchHandler handler = CreateHandler(scope.ServiceProvider, organizationId);
        return await handler.Handle(
            new ConfirmExactMatchCommand(invoiceId, bankTransactionId, idempotencyKey),
            CancellationToken.None);
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
                    ConfirmExactMatchHandler handler = CreateHandler(
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

    private async Task AssertUnchangedAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId)
    {
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice invoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        BankTransaction bankTransaction = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransactionId);

        Assert.Equal(Money.Zero(PersistenceTestData.Pln), invoice.AllocatedAmount);
        Assert.Equal(Money.Zero(bankTransaction.Amount.Currency), bankTransaction.AllocatedAmount);
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    private async Task<byte[]> RowVersionAsync(InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        return context.Entry(invoice).Property<byte[]>("RowVersion").CurrentValue.ToArray();
    }

    private static ConfirmExactMatchHandler CreateHandler(
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

    private static ConfirmAllocationHandler CreateAllocationHandler(
        IServiceProvider services,
        OrganizationId organizationId)
    {
        return new ConfirmAllocationHandler(
            new TestCurrentOrganizationContext(organizationId),
            services.GetRequiredService<IInvoiceRepository>(),
            services.GetRequiredService<IBankTransactionRepository>(),
            services.GetRequiredService<IAllocationRepository>(),
            services.GetRequiredService<IUnitOfWork>(),
            new FixedTimeProvider(ConfirmedAt));
    }

    private static Allocation CreateAllocation(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money amount,
        Guid idempotencyKey)
    {
        return new Allocation(
            AllocationId.New(),
            organizationId,
            invoiceId,
            bankTransactionId,
            amount,
            ConfirmedAt,
            idempotencyKey,
            AllocationIdempotencyOperation.ExactMatch);
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException.Number is 2601 or 2627;
            }
        }

        return false;
    }

    private sealed class GatedUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;
        private readonly Barrier _barrier;

        public GatedUnitOfWork(IUnitOfWork inner, Barrier barrier)
        {
            _inner = inner;
            _barrier = barrier;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            _barrier.SignalAndWait(cancellationToken);
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